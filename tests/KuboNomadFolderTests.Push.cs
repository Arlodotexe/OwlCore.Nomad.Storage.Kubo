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
    [TestMethod]
    public async Task PushTestAsync()
    {
        Logger.MessageReceived += LoggerOnMessageReceived;
        var cancellationToken = CancellationToken.None;

        var temp = new SystemFolder(Path.GetTempPath());
        var testTempFolder = await SafeCreateFolderAsync(temp, $"{nameof(NomadKuboFolderTests)}.{nameof(PushTestAsync)}", cancellationToken);

        var kubo = await BootstrapKuboAsync(testTempFolder, 5012, 8012, cancellationToken);
        var kuboOptions = new KuboOptions
        {
            IpnsLifetime = TimeSpan.FromDays(1),
            ShouldPin = false,
            UseCache = false,
        };

        {
            var folderToPush = (SystemFolder)await testTempFolder.CreateFolderAsync("in", cancellationToken: cancellationToken);
            await foreach (var file in folderToPush.CreateFilesAsync(5, i => $"{i}", cancellationToken))
                await file.WriteRandomBytes(numberOfBytes: 4096, bufferSize: 4096, cancellationToken);

            var folderId = nameof(PushTestAsync);
            var roamingKeyName = $"Nomad.Storage.Roaming.{folderId}";
            var localKeyName = $"Nomad.Storage.Local.{folderId}";

            var client = kubo.Client;
            var mfsRoot = new MfsFolder("/", client);
            var cacheFolder = (IModifiableFolder)await mfsRoot.CreateFolderAsync(".cache", cancellationToken: cancellationToken);
                cacheFolder = (IModifiableFolder)await cacheFolder.CreateFolderAsync("nomad", cancellationToken: cancellationToken);
                cacheFolder = (IModifiableFolder)await cacheFolder.CreateFolderAsync(folderId, cancellationToken: cancellationToken);

            var localARepo = StorageRepoFactory.GetFolderRepository(roamingKeyName, localKeyName, folderId, cacheFolder, client, kuboOptions);
            var nomadFolder = await localARepo.CreateAsync(cancellationToken);

            {
                // Push (copy)
                await folderToPush.CopyToAsync(nomadFolder, storable => GetLastWriteTimeFor(storable, nomadFolder.ResolvedEventStreamEntries ?? throw new InvalidOperationException()), cancellationToken);

                // Publish local/roaming data.
                await nomadFolder.FlushAsync(cancellationToken);

                // Cleanup and reload
                {
                    await nomadFolder.ResetEventStreamPositionAsync(cancellationToken);
                    nomadFolder.ResolvedEventStreamEntries?.Clear();
                    nomadFolder.ResolvedEventStreamEntries ??= [];

                    // Roaming keys are published from multiple nodes.
                    // If we're publishing this roaming key, we cannot read the latest published by another node, we must build from sources.
                    await foreach (var entry in nomadFolder.ResolveEventStreamEntriesAsync(cancellationToken).OrderBy(x => x.TimestampUtc))
                    {
                        Guard.IsNotNull(entry.TimestampUtc);
                        nomadFolder.ResolvedEventStreamEntries.Add(entry);

                        // Advance stream handler.
                        if (nomadFolder.Id == entry.TargetId)
                            await nomadFolder.AdvanceEventStreamAsync(entry, cancellationToken);
                    }
                }

                // Verify folder contents
                {
                    Guard.IsNotEmpty(nomadFolder.ResolvedEventStreamEntries);
                    Guard.IsNotEmpty(nomadFolder.LocalEventStream.Entries);
                    var sourceFiles = await folderToPush.GetFilesAsync(cancellationToken: cancellationToken).OrderBy(x => x.Name).ToListAsync(cancellationToken);
                    var pushedFiles = await nomadFolder.GetFilesAsync(cancellationToken: cancellationToken).OrderBy(x => x.Name).ToListAsync(cancellationToken);

                    Guard.IsGreaterThan(sourceFiles.Count, 0);
                    Guard.IsGreaterThan(pushedFiles.Count, 0);

                    foreach (var pushedFile in pushedFiles)
                    {
                        var sourceFile = sourceFiles.First(x => x.Name == pushedFile.Name);

                        var pushedFileBytes = await pushedFile.ReadBytesAsync(cancellationToken);
                        var sourceFileBytes = await sourceFile.ReadBytesAsync(cancellationToken);
                        CollectionAssert.AreEqual(pushedFileBytes, sourceFileBytes);
                    }
                }
            }
        }

        await kubo.Client.ShutdownAsync();
        kubo.Dispose();
        await SetAllFileAttributesRecursive(testTempFolder, attributes => attributes & ~FileAttributes.ReadOnly);
        await temp.DeleteAsync(testTempFolder, cancellationToken);
        Logger.MessageReceived -= LoggerOnMessageReceived;
    }
}