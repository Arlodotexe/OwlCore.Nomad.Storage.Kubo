using CommunityToolkit.Diagnostics;
using Ipfs;
using OwlCore.Nomad.Kubo;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.Nomad.Storage.Kubo.Tests.Extensions;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

public partial class KuboNomadFolderTests
{
    [DataRow(1000, 3)]
    [DataRow(10000, 3)]
    [DataRow(1000000, 2)]
    [DataRow(10000000, 1)]
    [DataRow(int.MaxValue - 1000L, 1)]
    [DataRow((long)int.MaxValue, 1)]
    [DataRow(int.MaxValue + 1000L, 1)]
    [TestMethod]
    public async Task PushPullSingleNodeTestAsync(long numberOfBytes, int fileCount)
    {
        Logger.MessageReceived += LoggerOnMessageReceived;
        var cancellationToken = CancellationToken.None;

        var folderId = $"{nameof(KuboNomadFolderTests)}.{nameof(PushPullSingleNodeTestAsync)}.{fileCount}.{numberOfBytes}";
        var localKeyName = $"Nomad.Storage.Local.{folderId}";
        var roamingKeyName = $"Nomad.Storage.Roaming.{folderId}";

        var temp = new SystemFolder(Path.GetTempPath());
        var testTempFolder = await SafeCreateFolderAsync(temp, folderId, cancellationToken);
        var kubo = await BootstrapKuboAsync(testTempFolder, 5013, 8013, cancellationToken);
        var kuboOptions = new KuboOptions
        {
            IpnsLifetime = TimeSpan.FromDays(1),
            ShouldPin = false,
            UseCache = false,
        };

        {
            var folderToPush = (SystemFolder)await testTempFolder.CreateFolderAsync("in", cancellationToken: cancellationToken);
            var folderToPull = (SystemFolder)await testTempFolder.CreateFolderAsync("out", cancellationToken: cancellationToken);

            await foreach (var file in folderToPush.CreateFilesAsync(fileCount, i => $"{i}", cancellationToken))
                await file.WriteRandomBytes(numberOfBytes, 4096, cancellationToken);

            var client = kubo.Client;
            var (local, roaming) = await NomadStorageKeys.CreateStorageKeysAsync(localKeyName, roamingKeyName, folderId,folderId, client, cancellationToken);
            {
                // Default value validation.
                // roaming should be the TargetId on local,
                // local should be a source on roaming.
                Guard.IsEqualTo(local.Value.TargetId, $"{roaming.Key.Id}");
                Guard.IsNotNull(roaming.Value.Sources.FirstOrDefault(x => x == local.Key.Id));
            }
            {
                // Publish provided default values to created keys
                var localACid = await client.Dag.PutAsync(local.Value, cancel: cancellationToken, pin: kuboOptions.ShouldPin);
                _ = await client.Name.PublishAsync(localACid, local.Key.Name, lifetime: kuboOptions.IpnsLifetime, cancellationToken);

                var roamingACid = await client.Dag.PutAsync(roaming.Value, cancel: cancellationToken, pin: kuboOptions.ShouldPin);
                _ = await client.Name.PublishAsync(roamingACid, roaming.Key.Name, lifetime: kuboOptions.IpnsLifetime, cancellationToken);
            }

            var cacheFolder = (IModifiableFolder)await testTempFolder.CreateFolderAsync(".cache", cancellationToken: cancellationToken);

            // Roaming keys are published from multiple nodes.
            // If we're publishing this roaming key, we cannot read the latest published by another node, we must build from sources.
            var sharedEventStreamHandlers = new List<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>>();
            var nomadFolder = new KuboNomadFolder(sharedEventStreamHandlers)
            {
                TempCacheFolder = cacheFolder,
                Inner = roaming.Value,
                AllEventStreamEntries = [], // Must call ResolveEventStreamEntriesAsync to populate all entries
                EventStreamHandlerId = roaming.Key.Id,
                RoamingKey = roaming.Key,
                LocalEventStreamKey = local.Key,
                LocalEventStream = local.Value,
                Sources = roaming.Value.Sources,
                Client = client,
                KuboOptions = kuboOptions,
                Parent = null,
            };

            await folderToPush.CopyToAsync(nomadFolder, storable => GetLastWriteTimeFor(storable, nomadFolder.AllEventStreamEntries), cancellationToken);
            await nomadFolder.CopyToAsync(folderToPull, storable => GetLastWriteTimeFor(storable, nomadFolder.AllEventStreamEntries), cancellationToken);

            // Verify folder contents
            await VerifyFolderContents(folderToPush, folderToPull, cancellationToken);
        }

        await kubo.Client.ShutdownAsync();
        kubo.Dispose();
        await SetAllFileAttributesRecursive(testTempFolder, attributes => attributes & ~FileAttributes.ReadOnly);
        await temp.DeleteAsync(testTempFolder, cancellationToken);
        Logger.MessageReceived -= LoggerOnMessageReceived;
    }
}