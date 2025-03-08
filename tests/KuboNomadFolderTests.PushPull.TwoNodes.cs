using CommunityToolkit.Diagnostics;
using Ipfs;
using OwlCore.Kubo;
using OwlCore.Nomad.Kubo;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.Nomad.Storage.Kubo.Tests.Extensions;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

public partial class NomadKuboFolderTests
{
    [DataRow(1000, 3)]
    [DataRow(10000, 3)]
    [DataRow(1000000, 2)]
    [DataRow(10000000, 1)]
    [DataRow(int.MaxValue + 1000L, 1)]
    [TestMethod]
    public async Task PushPullPairedTwoNodeTestAsync(long numberOfBytes, int fileCount)
    {
        Logger.MessageReceived += LoggerOnMessageReceived;
        var cancellationToken = CancellationToken.None;

        var temp = new SystemFolder(Path.GetTempPath());
        var folderId = $"{nameof(NomadKuboFolderTests)}.{nameof(PushPullPairedTwoNodeTestAsync)}.{fileCount}.{numberOfBytes}";
        var testTempFolder = await SafeCreateFolderAsync(temp, folderId, cancellationToken);
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
            await foreach (var file in sourceFolder.CreateFilesAsync(fileCount, i => $"pushedFromA.{i}", cancellationToken))
                await file.WriteRandomBytes(numberOfBytes, 4096, cancellationToken);

            var roamingKeyName = $"Nomad.Storage.Roaming.{folderId}";
            var localKeyName = $"Nomad.Storage.Local.{folderId}";

            var mfsRootA = new MfsFolder("/", clientA);
            var cacheFolderA = (IModifiableFolder)await mfsRootA.CreateFolderAsync(".cache", cancellationToken: cancellationToken);
            cacheFolderA = (IModifiableFolder)await cacheFolderA.CreateFolderAsync("nomad", cancellationToken: cancellationToken);
            cacheFolderA = (IModifiableFolder)await cacheFolderA.CreateFolderAsync(folderId, cancellationToken: cancellationToken);

            var mfsRootB = new MfsFolder("/", clientB);
            var cacheFolderB = (IModifiableFolder)await mfsRootB.CreateFolderAsync(".cache", cancellationToken: cancellationToken);
            cacheFolderB = (IModifiableFolder)await cacheFolderB.CreateFolderAsync("nomad", cancellationToken: cancellationToken);
            cacheFolderB = (IModifiableFolder)await cacheFolderB.CreateFolderAsync(folderId, cancellationToken: cancellationToken);

            var localARepo = StorageRepoFactory.GetFolderRepository(roamingKeyName, localKeyName, folderId, cacheFolderA, clientA, kuboOptions);
            var nomadFolderAHandlerConfig = await localARepo.GetEventStreamHandlerConfigAsync(null, cancellationToken);
            var nomadFolderA = await localARepo.CreateAsync(cancellationToken);

            // Default value validation.
            // local should be a source on roaming.
            Guard.IsNotNull(nomadFolderA.Inner.Sources.FirstOrDefault(x => x == nomadFolderA.LocalEventStreamKey.Id));

            await nomadFolderA.FlushAsync(cancellationToken);

            // Only create local key for nodeB, roaming key will be imported from nodeA.
            // During pairing, roamingA will be exported to nodeB, and localB will be added to the event stream for localA.
            var localB = await NomadKeyGen.GetOrCreateLocalAsync(localKeyName, folderId, kuboOptions, clientB, cancellationToken);

            // Execute pairing
            {
                // Generate pairing code
                var pairingCode = Guid.NewGuid().ToString().Split('-')[0];
                Guard.HasSizeGreaterThanOrEqualTo(pairingCode, 8);

                // Split 8 digits into 2x4, room name and password.
                var roomName = string.Join(null, pairingCode.Take(4));
                var password = string.Join(null, pairingCode.Skip(4));

                // Initiate pairing from node a and follow up on nodeB
                var nodeAPairingTask = KeyExchange.PairWithEncryptedPubSubAsync(kuboA, kuboOptions, clientA, kuboA.Client, (_, _) => Task.FromResult(nomadFolderA.LocalEventStreamKey), isRoamingReceiver: false, roamingKeyName, roomName, password, cancellationToken);
                var nodeBPairingTask = KeyExchange.PairWithEncryptedPubSubAsync(kuboB, kuboOptions, clientB, kuboB.Client, (_, _) => Task.FromResult(localB.Key), isRoamingReceiver: true, roamingKeyName, roomName, password, cancellationToken);

                await Task.WhenAll(nodeAPairingTask, nodeBPairingTask);

                // Reload updated local data
                var (publishedLocalAOnA, _) = await clientA.ResolveDagCidAsync<EventStream<DagCid>>(nomadFolderA.LocalEventStreamKey.Id, !kuboOptions.UseCache, cancellationToken);
                Guard.IsNotNull(publishedLocalAOnA);
                nomadFolderA.LocalEventStream = publishedLocalAOnA;
                nomadFolderAHandlerConfig.LocalValue = publishedLocalAOnA;
            }

            {
                var enumerableKeysB = await clientB.Key.ListAsync(cancellationToken);
                var keysB = enumerableKeysB as IKey[] ?? enumerableKeysB.ToArray();

                // roamingA should be imported and present in keysB
                var roamingBKey = keysB.FirstOrDefault(x => x.Id == nomadFolderA.RoamingKey.Id);
                Guard.IsNotNull(roamingBKey);
                var publishedRoamingBOnB = await ResolveAndValidatePublishedRoamingSeedAsync(clientB, roamingBKey, kuboOptions, cancellationToken);

                var localBRepo = StorageRepoFactory.GetFolderRepository(roamingKeyName, localKeyName, folderId, cacheFolderB, clientB, kuboOptions);
                var nomadFolderB = await localBRepo.CreateAsync(cancellationToken);
                Guard.IsNotNull(nomadFolderB.ResolvedEventStreamEntries);

                // Push to A
                await sourceFolder.CopyToAsync(nomadFolderA, storable => GetLastWriteTimeFor(storable, nomadFolderA.ResolvedEventStreamEntries), cancellationToken);

                // Publish A
                await nomadFolderA.FlushAsync(cancellationToken);
    
                nomadFolderB = await localBRepo.CreateAsync(cancellationToken);
                
                // Pull to B
                await nomadFolderB.CopyToAsync(destinationFolder, storable => GetLastWriteTimeFor(storable, nomadFolderB.ResolvedEventStreamEntries), cancellationToken);
                
                // Verify pushed content was pulled
                await VerifyFolderContents(sourceFolder, destinationFolder, cancellationToken);

                // Reverse direction: Push to B, pull to A
                // Push via B
                {
                    // Add new content to dest, to be pushed via B and pulled via A
                    await foreach (var file in destinationFolder.CreateFilesAsync(fileCount, i => $"pushedFromB.{i}", cancellationToken))
                        await file.WriteRandomBytes(numberOfBytes: numberOfBytes, bufferSize: 4096, cancellationToken);

                    await destinationFolder.CopyToAsync(nomadFolderB, storable => GetLastWriteTimeFor(storable, nomadFolderB.ResolvedEventStreamEntries), cancellationToken);

                    // Publish changes
                    await nomadFolderB.FlushAsync(cancellationToken);
                }

                // Pull via A
                {
                    nomadFolderA = await localARepo.CreateAsync(cancellationToken);

                    Guard.IsNotNull(nomadFolderA.Sources.FirstOrDefault(x => x == localB.Key.Id));
                    Guard.IsNotNull(nomadFolderA.Sources.FirstOrDefault(x => x == nomadFolderA.LocalEventStreamKey.Id));
                    Guard.IsEqualTo(nomadFolderA.Sources.Count, 2);
                    Guard.IsNotNull(nomadFolderA.ResolvedEventStreamEntries);

                    await nomadFolderA.CopyToAsync(sourceFolder, storable => GetLastWriteTimeFor(storable, nomadFolderA.ResolvedEventStreamEntries), cancellationToken);
                }

                // Verify folder contents
                await VerifyFolderContents(sourceFolder, destinationFolder, cancellationToken);
            }
        }
    }
}