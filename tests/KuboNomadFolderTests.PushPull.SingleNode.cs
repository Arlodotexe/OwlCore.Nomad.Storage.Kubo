using CommunityToolkit.Diagnostics;
using Ipfs;
using OwlCore.Nomad.Kubo;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.Nomad.Storage.Kubo.Tests.Extensions;
using OwlCore.Storage;
using OwlCore.Kubo;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

public partial class NomadKuboFolderTests
{
    [DataRow(1000, 3)]
    [DataRow(10000, 3)]
    [DataRow(1000000, 2)]
    [DataRow(10000000, 1)]
    [DataRow(int.MaxValue + 1000L, 1)]
    [TestMethod]
    public async Task PushPullSingleNodeTestAsync(long numberOfBytes, int fileCount)
    {
        Logger.MessageReceived += LoggerOnMessageReceived;
        var cancellationToken = CancellationToken.None;

        var temp = new SystemFolder(Path.GetTempPath());

        var folderId = $"{nameof(NomadKuboFolderTests)}.{nameof(PushPullSingleNodeTestAsync)}.{fileCount}.{numberOfBytes}";
        var roamingKeyName = $"Nomad.Storage.Roaming.{folderId}";
        var localKeyName = $"Nomad.Storage.Local.{folderId}";

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
            var mfsRoot = new MfsFolder("/", client);
            var cacheFolder = (IModifiableFolder)await mfsRoot.CreateFolderAsync(".cache", cancellationToken: cancellationToken);
            cacheFolder = (IModifiableFolder)await cacheFolder.CreateFolderAsync("nomad", cancellationToken: cancellationToken);
            cacheFolder = (IModifiableFolder)await cacheFolder.CreateFolderAsync(folderId, cancellationToken: cancellationToken);

            var nodeAKeys = await client.Key.ListAsync(cancellationToken);
            var localARepo = new RoamingFolderRepository
            {
                Client = client,
                KuboOptions = kuboOptions,
                TempCacheFolder = cacheFolder,
                KeyNamePrefix = "Nomad.Storage",
                ManagedKeys = nodeAKeys.ToList(),
            };
            
            var nomadFolder = await localARepo.CreateAsync(folderId, cancellationToken);

            Guard.IsNotNull(nomadFolder.ResolvedEventStreamEntries);

            await folderToPush.CopyToAsync(nomadFolder, storable => GetLastWriteTimeFor(storable, nomadFolder.ResolvedEventStreamEntries), cancellationToken);
            await nomadFolder.CopyToAsync(folderToPull, storable => GetLastWriteTimeFor(storable, nomadFolder.ResolvedEventStreamEntries), cancellationToken);

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