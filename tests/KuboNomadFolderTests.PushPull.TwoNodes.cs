using CommunityToolkit.Diagnostics;
using Ipfs;
using OwlCore.Kubo;
using OwlCore.Nomad.Kubo;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.Nomad.Storage.Kubo.Tests.Extensions;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

public partial class KuboNomadFolderTests
{
    [TestMethod]
    public async Task PushPullPairedTwoNodeTestAsync()
    {
        Logger.MessageReceived += LoggerOnMessageReceived;
        var cancellationToken = CancellationToken.None;

        var temp = new SystemFolder(Path.GetTempPath());
        var testTempFolder = await SafeCreateFolderAsync(temp, $"{nameof(KuboNomadFolderTests)}.{nameof(PushPullPairedTwoNodeTestAsync)}", cancellationToken);
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
            var sourceFolder = (SystemFolder)await testTempFolder.CreateFolderAsync("in", cancellationToken: cancellationToken);
            var destinationFolder = (SystemFolder)await testTempFolder.CreateFolderAsync("out", cancellationToken: cancellationToken);
            
            // Add new content to source, to be pushed via A and pulled via B 
            await foreach (var file in sourceFolder.CreateFilesAsync(5, i => $"pushedFromA.{i}", cancellationToken))
                await file.WriteRandomBytes(numberOfBytes: 4096, cancellationToken);
            
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
                Guard.IsNotNull(roamingA.Value.Sources.FirstOrDefault(x => x == localA.Key.Id));
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
                var nodeAPairingTask = KeyExchange.PairWithEncryptedPubSubAsync(kuboA, kuboOptions, clientA, kuboA.Client, localA.Key, isRoamingReceiver: false, roamingKeyName, roomName, password, cancellationToken);
                var nodeBPairingTask = KeyExchange.PairWithEncryptedPubSubAsync(kuboB, kuboOptions, clientB, kuboB.Client, localB.Key, isRoamingReceiver: true, roamingKeyName, roomName, password, cancellationToken);

                await Task.WhenAll(nodeAPairingTask, nodeBPairingTask);
            
                // Reload updated local data
                var (publishedLocalAOnA, _) = await clientA.ResolveDagCidAsync<EventStream<Cid>>(localA.Key.Id, !kuboOptions.UseCache, cancellationToken);
                Guard.IsNotNull(publishedLocalAOnA);
                localA.Value = publishedLocalAOnA;
            }
            
            {
                var enumerableKeysB = await clientB.Key.ListAsync(cancellationToken);
                var keysB = enumerableKeysB as IKey[] ?? enumerableKeysB.ToArray();
                    
                // roamingA should be imported and present in keysB
                var roamingBKey = keysB.FirstOrDefault(x => x.Id == roamingA.Key.Id);
                Guard.IsNotNull(roamingBKey);
                var publishedRoamingBOnB = await ResolveAndValidatePublishedRoamingSeedAsync(clientB, roamingBKey, kuboOptions, cancellationToken);

                var sharedEventStreamHandlersA = new List<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>>();
                var nomadFolderA = new KuboNomadFolder(sharedEventStreamHandlersA)
                {
                    Inner = roamingA.Value,
                    AllEventStreamEntries = [], // Must call ResolveEventStreamEntriesAsync to populate all entries
                    EventStreamHandlerId = roamingA.Key.Id,
                    RoamingKey = roamingA.Key,
                    LocalEventStreamKey = localA.Key,
                    LocalEventStream = localA.Value,
                    Sources = roamingA.Value.Sources,
                    Client = clientA,
                    KuboOptions = kuboOptions,
                    Parent = null,
                };
                
                var sharedEventStreamHandlersB = new List<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>>();
                var nomadFolderB = new KuboNomadFolder(sharedEventStreamHandlersB)
                {
                    Inner = publishedRoamingBOnB,
                    AllEventStreamEntries = [], // Must call ResolveEventStreamEntriesAsync to populate all entries
                    EventStreamHandlerId = roamingBKey.Id,
                    RoamingKey = roamingBKey,
                    LocalEventStreamKey = localB.Key,
                    LocalEventStream = localB.Value,
                    Sources = publishedRoamingBOnB.Sources,
                    Client = clientB,
                    KuboOptions = kuboOptions,
                    Parent = null,
                };
                
                // Push via A
                {
                    await sourceFolder.CopyToAsync(nomadFolderA, storable => GetLastWriteTimeFor(storable, nomadFolderA.AllEventStreamEntries), cancellationToken);
                        
                    // Publish changes
                    var localACid = await clientA.Dag.PutAsync(localA.Value, cancel: cancellationToken, pin: kuboOptions.ShouldPin);
                    _ = await clientA.Name.PublishAsync(localACid, localA.Key.Name, lifetime: kuboOptions.IpnsLifetime, cancellationToken);
                }
                
                // Pull via B
                {
                    // Roaming keys are published from multiple nodes.
                    // If we're publishing this roaming key, we cannot read the latest published by another node, we must build from sources.
                    await foreach (var entry in nomadFolderB.ResolveEventStreamEntriesAsync(cancellationToken).OrderBy(x => x.TimestampUtc))
                    {
                        Guard.IsNotNull(entry.TimestampUtc);
                        nomadFolderB.AllEventStreamEntries.Add(entry);
                 
                        // Advance stream handler.
                        if (nomadFolderB.Id == entry.TargetId)
                            await nomadFolderB.AdvanceEventStreamAsync(entry, cancellationToken);
                    }
            
                    await nomadFolderB.CopyToAsync(destinationFolder, storable => GetLastWriteTimeFor(storable, nomadFolderB.AllEventStreamEntries), cancellationToken);
                }
            
                // Verify folder contents
                await VerifyFolderContents(sourceFolder, destinationFolder, cancellationToken);
                
                // Push via B
                {
                    // Add new content to dest, to be pushed via B and pulled via A
                    await foreach (var file in destinationFolder.CreateFilesAsync(5, i => $"pushedFromB.{i}", cancellationToken))
                        await file.WriteRandomBytes(numberOfBytes: 4096, cancellationToken);
                    
                    await destinationFolder.CopyToAsync(nomadFolderB, storable => GetLastWriteTimeFor(storable, nomadFolderB.AllEventStreamEntries), cancellationToken);
                        
                    // Publish changes
                    var localBCid = await clientB.Dag.PutAsync(localB.Value, cancel: cancellationToken, pin: kuboOptions.ShouldPin);
                    _ = await clientB.Name.PublishAsync(localBCid, localB.Key.Name, lifetime: kuboOptions.IpnsLifetime, cancellationToken);
                }
                
                // Pull via A
                {
                    // Roaming keys are published from multiple nodes.
                    // If we're publishing this roaming key, we cannot read the latest published by another node, we must build from sources.
                    await foreach (var entry in nomadFolderA.ResolveEventStreamEntriesAsync(cancellationToken).OrderBy(x => x.TimestampUtc))
                    {
                        Guard.IsNotNull(entry.TimestampUtc);
                        nomadFolderA.AllEventStreamEntries.Add(entry);
                 
                        // Advance stream handler.
                        if (nomadFolderA.Id == entry.TargetId)
                            await nomadFolderA.AdvanceEventStreamAsync(entry, cancellationToken);
                    }
                    
                    Guard.IsNotNull(nomadFolderA.Sources.FirstOrDefault(x => x == localB.Key.Id));
                    Guard.IsNotNull(nomadFolderA.Sources.FirstOrDefault(x => x == localA.Key.Id));
                    Guard.IsEqualTo(nomadFolderA.Sources.Count, 2);
            
                    await nomadFolderA.CopyToAsync(sourceFolder, storable => GetLastWriteTimeFor(storable, nomadFolderA.AllEventStreamEntries), cancellationToken);
                }
            
                // Verify folder contents
                await VerifyFolderContents(sourceFolder, destinationFolder, cancellationToken);
            }
        }
    }
}