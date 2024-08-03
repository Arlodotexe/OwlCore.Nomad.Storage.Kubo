using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Kubo;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Kubo.Extensions;
using OwlCore.Nomad.Storage.Models;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class ReadOnlyKuboNomadFile : ReadOnlyNomadFile<Cid, EventStream<Cid>, EventStreamEntry<Cid>>, IReadOnlyKuboBasedNomadFile
{
    /// <summary>
    /// Creates a new instance of <see cref="ReadOnlyKuboNomadFile"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known nomad event streams participating in event seeking.</param>
    public ReadOnlyKuboNomadFile(ICollection<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> listeningEventStreamHandlers)
        : base(listeningEventStreamHandlers)
    {
    }

    /// <inheritdoc/>
    public required IKuboOptions KuboOptions { get; set; }

    /// <inheritdoc/>
    public required ICoreApi Client { get; set; }

    /// <inheritdoc />
    public override async Task<Stream> OpenStreamAsync(FileAccess accessMode = FileAccess.Read, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (accessMode.HasFlag(FileAccess.Write))
            throw new ArgumentException($"{nameof(ReadOnlyKuboNomadFile)} doesn't support writing. Use {nameof(KuboNomadFile)} instead.");

        var contentId = Inner.ContentId;
        if (contentId is null)
            return new MemoryStream();

        Guard.IsNotNull(contentId);

        var backingFile = new IpfsFile(contentId, Client);
        var sourceStream = await backingFile.OpenStreamAsync(accessMode, cancellationToken);

        return sourceStream;
    }

    /// <inheritdoc />
    public override Task TryAdvanceEventStreamAsync(EventStreamEntry<Cid> streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.TryAdvanceEventStreamAsync(this, streamEntry, cancellationToken);
    }

    /// <inheritdoc />
    public override Task ResetEventStreamPositionAsync(CancellationToken cancellationToken)
    {
        Inner.ContentId = null;
        return base.ResetEventStreamPositionAsync(cancellationToken);
    }

    /// <summary>
    /// Applies the provided storage update event without external side effects.
    /// </summary>
    /// <param name="updateEventContent">The event to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public override Task ApplyEntryUpdateAsync(StorageUpdateEvent updateEventContent, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.ApplyEntryUpdateAsync(this, updateEventContent, cancellationToken);
    }
}