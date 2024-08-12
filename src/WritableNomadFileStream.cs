using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Common;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Nomad.Storage.Kubo.Models;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// Handles opening and writing a writable file stream for a <see cref="KuboNomadFile"/>.
/// </summary>
public class WritableNomadFileStream : WritableLazySeekStream
{
    /// <summary>
    /// Creates an instance of <see cref="WritableNomadFileStream"/>.
    /// </summary>
    /// <param name="kuboNomadFile">The file that this stream represents.</param>
    /// <param name="sourceStream"></param>
    public WritableNomadFileStream(KuboNomadFile kuboNomadFile, Stream sourceStream)
        : base(sourceStream, destinationStream: new MemoryStream())
    {
        KuboNomadFile = kuboNomadFile;
    }

    /// <summary>
    /// The file that this stream represents.
    /// </summary>
    public KuboNomadFile KuboNomadFile { get; }

    /// <inheritdoc/>
    public override void Flush()
    {
        FlushAsync().GetResultOrDefault();
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (DestinationStream.Position != 0)
            DestinationStream.Seek(0, SeekOrigin.Begin);

        // Seek to end to ensure full memory stream is loaded
        if (Position != Length)
            Seek(0, SeekOrigin.End);

        // Copy memory stream to destination.
        // Will include any writes done below.
        await MemoryStream.CopyToAsync(DestinationStream);
        
        if (DestinationStream.Position != 0)
            DestinationStream.Position = 0;

        var added = await KuboNomadFile.Client.FileSystem.AddAsync(DestinationStream, KuboNomadFile.Name, new AddFileOptions { Pin = KuboNomadFile.KuboOptions.ShouldPin }, cancel: cancellationToken);

        var fileUpdateEvent = new FileUpdateEvent(KuboNomadFile.Id, added.Id);
        var appendedEvent = await KuboNomadFile.AppendNewEntryAsync(fileUpdateEvent, cancellationToken);
        
        await KuboNomadFile.ApplyEntryUpdateAsync(fileUpdateEvent, cancellationToken);
        KuboNomadFile.EventStreamPosition = appendedEvent;
    }
}
