using System.Diagnostics;
using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.Kubo;
using OwlCore.Kubo.Cache;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.Extensions;
using OwlCore.Nomad.Kubo.Events;
using OwlCore.Nomad.Storage.Kubo.Tests.Extensions;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

public partial class KuboNomadFolderTests
{
    [TestMethod]
    public async Task PairingTestAsync()
    {
        Logger.MessageReceived += LoggerOnMessageReceived;
        var cancellationToken = CancellationToken.None;

        var temp = new SystemFolder(Path.GetTempPath());
        var testTempFolder = await SafeCreateFolderAsync(temp, $"{nameof(KuboNomadFolderTests)}.{nameof(PairingTestAsync)}", cancellationToken);
        var kuboOptions = new KuboOptions
        {
            IpnsLifetime = TimeSpan.FromDays(1),
            ShouldPin = false,
            UseCache = false,
        };
        
        // Spawn kubo nodes
        var nodeAFolder = (SystemFolder)await testTempFolder.CreateFolderAsync("node-a", overwrite: true, cancellationToken); 
        var kuboA = await BootstrapKuboAsync(nodeAFolder, 5014, 8014, cancellationToken);
        
        var nodeBFolder = (SystemFolder)await testTempFolder.CreateFolderAsync("node-b", overwrite: true, cancellationToken);
        var kuboB = await BootstrapKuboAsync(nodeBFolder, 5015, 8015, cancellationToken);
        
        // Connect kubo nodes in swarm
        await AddPeerToSwarmAsync(kuboA.Client, kuboB.Client, cancellationToken);
        await AddPeerToSwarmAsync(kuboB.Client, kuboA.Client, cancellationToken);

        var clientA = kuboA.Client;
        var clientB = kuboB.Client;
        {
            var folderId = nameof(PairingTestAsync);
            var roamingKeyName = $"Nomad.Storage.Roaming.{folderId}";
            var localKeyName = $"Nomad.Storage.Local.{folderId}";
            
            // Create local AND roaming keys for nodeA.
            // During pairing, roamingA will be exported to nodeB, and localB will be added to the event stream for localA.
            var (localA, roamingA) = await NomadStorageKeys.CreateStorageKeysAsync(localKeyName, roamingKeyName, folderId, folderId, clientA, cancellationToken);
            {
                // Default value validation.
                // roaming should be the TargetId on local,
                // local should be a source on roaming.
                Guard.IsEqualTo(localA.Value.TargetId, $"{roamingA.Key.Id}");
                Guard.IsNotNull(roamingA.Value.Sources.FirstOrDefault(x=> x == localA.Key.Id));
            }
            {
                // Publish provided default values to created keys
                var localACid = await clientA.Dag.PutAsync(localA.Value, cancel: cancellationToken, pin: kuboOptions.ShouldPin);
                _ = await clientA.Name.PublishAsync(localACid, localA.Key.Name, lifetime: kuboOptions.IpnsLifetime, cancellationToken);
                
                var roamingACid = await clientA.Dag.PutAsync(roamingA.Value, cancel: cancellationToken, pin: kuboOptions.ShouldPin);
                _ = await clientA.Name.PublishAsync(roamingACid, roamingA.Key.Name, lifetime: kuboOptions.IpnsLifetime, cancellationToken);
            }
            
            // Only create local key for nodeB, roaming key will be imported from nodeA.
            var localB = await NomadStorageKeys.GetOrCreateLocalStorageKeyAsyc(localKeyName, folderId, roamingKey: roamingA.Key, kuboOptions, clientB, cancellationToken);
            {
                // Default value validation
                Guard.IsEqualTo(localB.Value.TargetId, $"{roamingA.Key.Id}");
            }

            // Execute pairing
            {
                // Generate pairing code
                var pairingCode = Guid.NewGuid().ToString().Split('-')[0];
                Guard.HasSizeGreaterThanOrEqualTo(pairingCode, 8);
                
                // Split 8 digits into 2x4, room name and password.
                var roomName = string.Join(null, pairingCode.Take(4));
                var password = string.Join(null, pairingCode.Skip(4));
                
                // Initiate pairing from node a and follow up on nodeB
                var nodeAPairingTask = KeyExchange.PairWithEncryptedPubSubAsync(kuboA, kuboOptions, clientA, kuboA.Client, (_, _) => Task.FromResult(localA.Key), isRoamingReceiver: false, roamingKeyName, roomName, password, cancellationToken); 
                var nodeBPairingTask = KeyExchange.PairWithEncryptedPubSubAsync(kuboB, kuboOptions, clientB, kuboB.Client, (_, _) => Task.FromResult(localB.Key), isRoamingReceiver: true, roamingKeyName, roomName, password, cancellationToken);
            
                await Task.WhenAll(nodeAPairingTask, nodeBPairingTask);
            }
            
            // Verify published roaming keys
            {
                var enumerableKeysB = await clientB.Key.ListAsync(cancellationToken);
                var keysB = enumerableKeysB as IKey[] ?? enumerableKeysB.ToArray();
                
                // roamingA should be imported and present in keysB
                var roamingBKey = keysB.FirstOrDefault(x => x.Id == roamingA.Key.Id);
                Guard.IsNotNull(roamingBKey);
                
                // Verify published roaming data from perspective of both A and B.
                var publishedRoamingAOnA = await ResolveAndValidatePublishedRoamingSeedAsync(clientA, roamingA.Key, kuboOptions, cancellationToken);
                var publishedRoamingAOnB = await ResolveAndValidatePublishedRoamingSeedAsync(clientB, roamingA.Key, kuboOptions, cancellationToken);
                
                var publishedRoamingBOnA = await ResolveAndValidatePublishedRoamingSeedAsync(clientA, roamingBKey, kuboOptions, cancellationToken);
                var publishedRoamingBOnB = await ResolveAndValidatePublishedRoamingSeedAsync(clientB, roamingBKey, kuboOptions, cancellationToken);
                
                // Roaming was just imported from A to B, all data should be identical.
                Guard.IsEqualTo(publishedRoamingAOnA.StorableItemId, publishedRoamingAOnB.StorableItemId);
                Guard.IsEqualTo(publishedRoamingAOnA.StorableItemName, publishedRoamingAOnB.StorableItemName);
                Guard.IsTrue(publishedRoamingAOnA.Sources.SequenceEqual(publishedRoamingAOnB.Sources));
                
                Guard.IsEqualTo(publishedRoamingBOnA.StorableItemId, publishedRoamingBOnB.StorableItemId);
                Guard.IsEqualTo(publishedRoamingBOnA.StorableItemName, publishedRoamingBOnB.StorableItemName);
                Guard.IsTrue(publishedRoamingBOnA.Sources.SequenceEqual(publishedRoamingBOnB.Sources));
                
                // Given above grid of checks
                // (AOnA, AOnB)
                // (BOnA, BOnB)
                // Cross 1,1 with 2,2 (AOnA, BOnB)
                Guard.IsEqualTo(publishedRoamingAOnA.StorableItemId, publishedRoamingBOnB.StorableItemId);
                Guard.IsEqualTo(publishedRoamingAOnA.StorableItemName, publishedRoamingBOnB.StorableItemName);
                Guard.IsTrue(publishedRoamingAOnA.Sources.SequenceEqual(publishedRoamingBOnB.Sources));
                
                // Cross 2,1 with 1,2 (BOnA, AOnB)
                Guard.IsEqualTo(publishedRoamingBOnA.StorableItemId, publishedRoamingAOnB.StorableItemId);
                Guard.IsEqualTo(publishedRoamingBOnA.StorableItemName, publishedRoamingAOnB.StorableItemName);
                Guard.IsTrue(publishedRoamingBOnA.Sources.SequenceEqual(publishedRoamingAOnB.Sources));
            }
            
            // Verify published local data
            var (publishedLocalAOnA, _) = await clientA.ResolveDagCidAsync<EventStream<Cid>>(localA.Key.Id, !kuboOptions.UseCache, cancellationToken);
            Guard.IsNotNull(publishedLocalAOnA);
            {
                // Load event stream entries
                (EventStreamEntry<Cid>? eventStreamEntry, Cid eventStreamEntryCid)[] eventStreamEntries = await publishedLocalAOnA.Entries.InParallel(x => clientA.ResolveDagCidAsync<EventStreamEntry<Cid>>(x, nocache: !kuboOptions.UseCache, cancellationToken));
                Guard.IsNotEmpty(eventStreamEntries);
                
                var sourceAddEventStreamEntries = eventStreamEntries
                    .Where(x => x.eventStreamEntry?.EventId == nameof(SourceAddEvent))
                    .Where(x=> x.eventStreamEntry is not null)
                    .Cast<(EventStreamEntry<Cid> eventStreamEntry, Cid eventStreamEntryCid)>()
                    .ToArray();
                Guard.IsNotEmpty(sourceAddEventStreamEntries);
                
                var sourceAddEventUpdates = await sourceAddEventStreamEntries.InParallel(x => clientA.ResolveDagCidAsync<SourceAddEvent>(x.eventStreamEntry.Content, nocache: !kuboOptions.UseCache, cancellationToken));
                Guard.IsNotEmpty(sourceAddEventUpdates);
                
                // Ensure localB is added to localA's event stream in a SourceAddEvent
                var localBSourceAddEventUpdate = sourceAddEventUpdates
                    .Where(x=> x.Result is not null)
                    .Cast<(SourceAddEvent SourceAddEvent, Cid SourceAddEventCid)>()
                    .FirstOrDefault(x=> x.SourceAddEvent.AddedSourcePointer == localB.Key.Id);
                
                Guard.IsNotNull(localBSourceAddEventUpdate.SourceAddEvent);
            }
        }
        
        await kuboA.Client.ShutdownAsync();
        kuboA.Dispose();
        
        await kuboB.Client.ShutdownAsync();
        kuboB.Dispose();
        
        Guard.IsTrue(kuboA.Process?.HasExited ?? true);
        Guard.IsTrue(kuboB.Process?.HasExited ?? true);
        Logger.MessageReceived -= LoggerOnMessageReceived;
    }
}