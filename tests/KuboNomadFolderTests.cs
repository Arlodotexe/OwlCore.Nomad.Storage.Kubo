using System.Diagnostics;
using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.Kubo;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

[TestClass]
public partial class NomadKuboFolderTests
{
    private void LoggerOnMessageReceived(object? sender, LoggerMessageEventArgs args) => Debug.WriteLine(args.Message);

    private async Task VerifyFolderContents(SystemFolder sourceFolder, SystemFolder destinationFolder, CancellationToken cancellationToken)
    {
        var pushedFiles = await sourceFolder.GetFilesAsync(cancellationToken: cancellationToken).OrderBy(x => x.Name).ToListAsync(cancellationToken: cancellationToken);
        var pulledFiles = await destinationFolder.GetFilesAsync(cancellationToken: cancellationToken).OrderBy(x => x.Name).ToListAsync(cancellationToken: cancellationToken);

        Guard.IsGreaterThan(pushedFiles.Count, 0);
        Guard.IsGreaterThan(pulledFiles.Count, 0);
        Guard.IsEqualTo(pushedFiles.Count, pulledFiles.Count);

        foreach (var pushedFile in pushedFiles)
        {
            var pulledFile = pulledFiles.First(x => x.Name == pushedFile.Name);

            await using var pushedFileStream = await pushedFile.OpenReadAsync(cancellationToken);
            await using var pulledFileStream = await pulledFile.OpenReadAsync(cancellationToken);
            
            Guard.IsEqualTo(pushedFileStream.Position, 0);
            Guard.IsEqualTo(pulledFileStream.Position, 0);
            Assert.AreEqual(pushedFileStream.Length, pulledFileStream.Length);
            
            await AssertStreamEqualAsync(pushedFileStream, pulledFileStream, 81920, cancellationToken);
        }
    }

    private static async Task AssertStreamEqualAsync(Stream srcStream, Stream destStream, int bufferSize, CancellationToken cancellationToken)
    {
        Assert.AreEqual(srcStream.Length, destStream.Length);

        var totalBytes = srcStream.Length;
        var bytesChecked = 0L;

        var srcBuffer = new byte[bufferSize];
        var destBuffer = new byte[bufferSize];

        // Fill each buffer until bufferSize is reached.
        // Each stream must fill the buffer until it is full,
        // except if no bytes are left.
        while (bytesChecked < totalBytes)
        {
            var srcBytesRead = 0;
            while (srcBytesRead < srcBuffer.Length)
            {
                var srcBytesReadInternal = await srcStream.ReadAsync(srcBuffer, offset: srcBytesRead, count: srcBuffer.Length - srcBytesRead, cancellationToken);
                if (srcBytesReadInternal == 0)
                    break;

                srcBytesRead += srcBytesReadInternal;
            }

            var destBytesRead = 0;
            while (destBytesRead < destBuffer.Length)
            {
                var destBytesReadInternal = await destStream.ReadAsync(destBuffer, offset: destBytesRead, count: destBuffer.Length - destBytesRead, cancellationToken);
                if (destBytesReadInternal == 0)
                    break;

                destBytesRead += destBytesReadInternal;
            }

            if (srcBytesRead != destBytesRead)
            {
                throw new InvalidOperationException($"Mismatch in bytes read between source and destination streams: {destBytesRead} and {srcBytesRead}.");
            }

            // When buffers are full, compare and continue.
            CollectionAssert.AreEqual(destBuffer, srcBuffer);
            bytesChecked += srcBytesRead;
        }
    }

    private static async Task AddPeerToSwarmAsync(IGenericApi clientA, ICoreApi clientB, CancellationToken cancellationToken)
    {
        var peerB = await clientA.IdAsync(cancel: cancellationToken);
        foreach (var address in peerB.Addresses)
        {
            try
            {
                await clientB.Swarm.ConnectAsync(address, cancellationToken);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static async Task<NomadFolderData<DagCid, Cid>> ResolveAndValidatePublishedRoamingSeedAsync(ICoreApi client, IKey roamingKey, KuboOptions kuboOptions, CancellationToken cancellationToken)
    {
        var (publishedRoaming, _) = await client.ResolveDagCidAsync<NomadFolderData<DagCid, Cid>>(roamingKey.Id, nocache: !kuboOptions.UseCache, cancellationToken);
        Guard.IsNotNull(publishedRoaming);
        {
            Guard.IsNotEmpty(publishedRoaming.Sources);
            Guard.IsNotNullOrWhiteSpace(publishedRoaming.StorableItemId);
            Guard.IsNotNullOrWhiteSpace(publishedRoaming.StorableItemName);
            Guard.IsEmpty(publishedRoaming.Files);
            Guard.IsEmpty(publishedRoaming.Folders);
        }
        return publishedRoaming;
    }

    public static DateTime GetLastWriteTimeFor(IStorable storable, IEnumerable<EventStreamEntry<DagCid>> eventStreamEntries)
    {
        return storable switch
        {
            NomadKuboFile nFile => eventStreamEntries.Where(x => x.TargetId == nFile.Id).Max(x => x.TimestampUtc) ?? ThrowHelper.ThrowNotSupportedException<DateTime>("Unhandled code path"),
            NomadKuboFolder nFolder => eventStreamEntries.Where(x => x.TargetId == nFolder.Id).Max(x => x.TimestampUtc) ?? ThrowHelper.ThrowNotSupportedException<DateTime>("Unhandled code path"),
            ReadOnlyNomadKuboFile nFile => eventStreamEntries.Where(x => x.TargetId == nFile.Id).Max(x => x.TimestampUtc) ?? ThrowHelper.ThrowNotSupportedException<DateTime>("Unhandled code path"),
            ReadOnlyNomadKuboFolder nFolder => eventStreamEntries.Where(x => x.TargetId == nFolder.Id).Max(x => x.TimestampUtc) ?? ThrowHelper.ThrowNotSupportedException<DateTime>("Unhandled code path"),
            SystemFolder systemFolder => systemFolder.Info.LastWriteTimeUtc,
            SystemFile systemFile => systemFile.Info.LastWriteTimeUtc,
            _ => throw new ArgumentOutOfRangeException(nameof(storable), storable, null)
        };
    }
    
    /// <summary>
    /// Creates a temp folder for the test fixture to work in, safely unlocking and removing existing files if needed.
    /// </summary>
    /// <returns>The folder that was created.</returns>
    public static async Task<SystemFolder> SafeCreateFolderAsync(SystemFolder rootFolder, string name, CancellationToken cancellationToken)
    {
        // When Kubo is stopped unexpectedly, it may leave some files with a ReadOnly attribute.
        // Since this folder is created every time tests are run, we need to clean up any files leftover from prior runs.
        // To do that, we need to remove the ReadOnly file attribute.
        var testTempRoot = (SystemFolder)await rootFolder.CreateFolderAsync(name, overwrite: false, cancellationToken: cancellationToken);
        await SetAllFileAttributesRecursive(testTempRoot, attributes => attributes & ~FileAttributes.ReadOnly);

        // Delete and recreate the folder.
        return (SystemFolder)await rootFolder.CreateFolderAsync(name, overwrite: true, cancellationToken: cancellationToken);
    }
    
    /// <summary>
    /// Changes the file attributes of all files in all subfolders of the provided <see cref="SystemFolder"/>.
    /// </summary>
    /// <param name="rootFolder">The folder to set file permissions in.</param>
    /// <param name="transform">This function is provided the current file attributes, and should return the new file attributes.</param>
    public static async Task SetAllFileAttributesRecursive(SystemFolder rootFolder, Func<FileAttributes, FileAttributes> transform)
    {
        await foreach (var childFile in rootFolder.GetFilesAsync())
        {
            var file = (SystemFile)childFile;
            file.Info.Attributes = transform(file.Info.Attributes);
        }

        await foreach (var childFolder in rootFolder.GetFoldersAsync())
        {
            var folder = (SystemFolder)childFolder;
            await SetAllFileAttributesRecursive(folder, transform);
        }
    }
    
    private static async Task<KuboBootstrapper> BootstrapKuboAsync(SystemFolder kuboRepo, int apiPort, int gatewayPort, CancellationToken cancellationToken)
    {
        var kubo = new KuboBootstrapper(kuboRepo.Path)
        {
            ApiUri = new Uri($"http://127.0.0.1:{apiPort}"),
            GatewayUri = new Uri($"http://127.0.0.1:{gatewayPort}"),
            RoutingMode = DhtRoutingMode.None,
            LaunchConflictMode = BootstrapLaunchConflictMode.Attach,
            ApiUriMode = ConfigMode.OverwriteExisting,
            GatewayUriMode = ConfigMode.OverwriteExisting,
        };
                
        await kubo.StartAsync(cancellationToken);
        return kubo;
    }
}