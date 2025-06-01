using CommunityToolkit.Diagnostics;
using Ipfs;
using OwlCore.Kubo;
using OwlCore.Nomad.Kubo;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.Extensions;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

public partial class NomadKuboFolderTests
{
    [TestMethod]
    public async Task PairingTestAsync()
    {
        Logger.MessageReceived += LoggerOnMessageReceived;
        var cancellationToken = CancellationToken.None;

        var temp = new SystemFolder(Path.GetTempPath());
        var testTempFolder = await SafeCreateFolderAsync(temp, $"{nameof(NomadKuboFolderTests)}.{nameof(PairingTestAsync)}", cancellationToken);
        var kuboOptions = new KuboOptions
        {
            IpnsLifetime = TimeSpan.FromDays(1),
            ShouldPin = false,
            UseCache = false,
        };
        
        // Spawn kubo nodes
        var nodeAFolder = (SystemFolder)await testTempFolder.CreateFolderAsync("node-a", overwrite: true, cancellationToken); 
        var nodeACacheFolder = (SystemFolder)await nodeAFolder.CreateFolderAsync(".cache", overwrite: true, cancellationToken);
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
            
            // Create local AND roaming keys for nodeA.
            // During pairing, roamingA will be exported to nodeB, and localB will be added to the event stream for localA.
            var nodeAKeys = await clientA.Key.ListAsync(cancellationToken);
            var localARepo = new RoamingFolderRepository
            {
                Client = clientA,
                KuboOptions = kuboOptions,
                TempCacheFolder = nodeACacheFolder,
                KeyNamePrefix = "Nomad.Storage",
                ManagedKeys = nodeAKeys.Select(k => new Key(k)).ToList(),
                ManagedConfigs = [],
            };
            
            var folderA = await localARepo.CreateAsync(folderId, cancellationToken);
            await folderA.FlushAsync(cancellationToken);
            
            // Only create local key for nodeB, roaming key will be imported from nodeA.
            // Reuse local key name from folder A.
            var localB = await NomadKeyGen.GetOrCreateLocalAsync(folderA.LocalEventStreamKey.Name, folderId, kuboOptions, clientB, cancellationToken);

            // Execute pairing
            {
                // Generate pairing code
                var pairingCode = Guid.NewGuid().ToString().Split('-')[0];
                Guard.HasSizeGreaterThanOrEqualTo(pairingCode, 8);
                
                // Split 8 digits into 2x4, room name and password.
                var roomName = string.Join(null, pairingCode.Take(4));
                var password = string.Join(null, pairingCode.Skip(4));
                
                // Initiate pairing from node a and follow up on nodeB
                // Use existing roaming key name from folder A
                var roamingKeyName = folderA.RoamingKey.Name;
                var nodeAPairingTask = KeyExchange.PairWithEncryptedPubSubAsync(kuboA, kuboOptions, clientA, kuboA.Client, (_, _) => Task.FromResult(folderA.LocalEventStreamKey), isRoamingReceiver: false, roamingKeyName, roomName, password, cancellationToken); 
                var nodeBPairingTask = KeyExchange.PairWithEncryptedPubSubAsync(kuboB, kuboOptions, clientB, kuboB.Client, (_, _) => Task.FromResult(localB.Key), isRoamingReceiver: true, roamingKeyName, roomName, password, cancellationToken);
            
                await Task.WhenAll(nodeAPairingTask, nodeBPairingTask);
            }
            
            // Verify published roaming keys
            {
                var enumerableKeysB = await clientB.Key.ListAsync(cancellationToken);
                var keysB = enumerableKeysB as IKey[] ?? enumerableKeysB.ToArray();
                
                // roamingA should be imported and present in keysB
                var roamingBKey = keysB.FirstOrDefault(x => x.Id == folderA.RoamingKey.Id);
                Guard.IsNotNull(roamingBKey);
                
                // Verify published roaming data from perspective of both A and B.
                var publishedRoamingAOnA = await ResolveAndValidatePublishedRoamingSeedAsync(clientA, folderA.RoamingKey, kuboOptions, cancellationToken);
                var publishedRoamingAOnB = await ResolveAndValidatePublishedRoamingSeedAsync(clientB, folderA.RoamingKey, kuboOptions, cancellationToken);
                
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
            var (publishedLocalAOnA, _) = await clientA.ResolveDagCidAsync<EventStream<DagCid>>(folderA.LocalEventStreamKey.Id, !kuboOptions.UseCache, cancellationToken);
            Guard.IsNotNull(publishedLocalAOnA);
            {
                // Load event stream entries
                (EventStreamEntry<DagCid>? eventStreamEntry, Cid eventStreamEntryCid)[] eventStreamEntries = await publishedLocalAOnA.Entries.InParallel(x => clientA.ResolveDagCidAsync<EventStreamEntry<DagCid>>(x, nocache: !kuboOptions.UseCache, cancellationToken));
                Guard.IsNotEmpty(eventStreamEntries);
                
                var sourceAddEventStreamEntries = eventStreamEntries
                    .Where(x => x.eventStreamEntry?.EventId == ReservedEventIds.NomadEventStreamSourceAddEvent)
                    .Where(x=> x.eventStreamEntry is not null)
                    .Cast<(EventStreamEntry<DagCid> eventStreamEntry, Cid eventStreamEntryCid)>()
                    .ToArray();
                Guard.IsNotEmpty(sourceAddEventStreamEntries);
                
                var sourceAddEventUpdates = await sourceAddEventStreamEntries.InParallel(x => clientA.ResolveDagCidAsync<Cid>(x.eventStreamEntry.Content, nocache: !kuboOptions.UseCache, cancellationToken));
                Guard.IsNotEmpty(sourceAddEventUpdates);
                
                // Ensure localB is added to localA's event stream in a SourceAddEvent
                var localBSourceAddEventUpdate = sourceAddEventUpdates
                    .Where(x=> x.Result is not null)
                    .FirstOrDefault(x => x.Result == localB.Key.Id);
                
                Guard.IsNotNull(localBSourceAddEventUpdate.Result);
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