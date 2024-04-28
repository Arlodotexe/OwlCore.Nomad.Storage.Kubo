using CommunityToolkit.Common;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OwlCore.Kubo.Nomad.Storage;

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
        // After flushing the base, the DestinationStream should contain the contents of the disposed MemoryStream.
        await base.FlushAsync(cancellationToken);

        if (DestinationStream.Position != 0)
            DestinationStream.Seek(0, SeekOrigin.Begin);

        var added = await KuboNomadFile.Client.FileSystem.AddAsync(DestinationStream, KuboNomadFile.Name, new AddFileOptions { Pin = KuboNomadFile.ShouldPin }, cancel: cancellationToken);

        var fileUpdateEvent = new FileUpdateEvent(KuboNomadFile.Id, added.Id);
        await KuboNomadFile.AppendNewEntryAsync(fileUpdateEvent, cancellationToken);
    }
}
