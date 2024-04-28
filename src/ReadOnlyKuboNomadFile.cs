using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.ComponentModel.Nomad;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OwlCore.Nomad.Storage.Models;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class ReadOnlyKuboNomadFile : ReadOnlyNomadFile<Cid, NomadEventStream, NomadEventStreamEntry>, IReadOnlyKuboBasedNomadFile
{
    /// <summary>
    /// Creates a new instance of <see cref="ReadOnlyKuboNomadFile"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known nomad event streams participating in event seeking.</param>
    public ReadOnlyKuboNomadFile(ICollection<ISharedEventStreamHandler<Cid, NomadEventStream, NomadEventStreamEntry>> listeningEventStreamHandlers)
        : base(listeningEventStreamHandlers)
    {
    }

    /// <summary>
    /// The client to use for communicating with Ipfs.
    /// </summary>
    public required ICoreApi Client { get; set; }

    /// <summary>
    /// Whether to use the cache when resolving Ipns Cids.
    /// </summary>
    public bool UseCache { get; set; }

    /// <summary>
    /// The current Cid of the backing resource for this file. If null, an empty stream will be returned. Writing is not supported.
    /// </summary>
    /// <remarks>
    /// Should be immutable -- the same string should always return the same content, and a different string should always return different content.
    /// </remarks>
    public required Cid? CurrentContentId { get; set; }

    /// <inheritdoc />
    public override async Task<Stream> OpenStreamAsync(FileAccess accessMode = FileAccess.Read, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (accessMode.HasFlag(FileAccess.Write))
            throw new ArgumentException($"{nameof(ReadOnlyKuboNomadFile)} doesn't support writing. Use {nameof(KuboNomadFile)} instead.");

        var contentId = CurrentContentId;
        if (CurrentContentId is null)
            return new MemoryStream();

        Guard.IsNotNull(contentId);

        var backingFile = new IpfsFile(contentId, Client);
        var sourceStream = await backingFile.OpenStreamAsync(accessMode, cancellationToken);

        return sourceStream;
    }

    /// <inheritdoc />
    public override Task TryAdvanceEventStreamAsync(NomadEventStreamEntry streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.TryAdvanceEventStreamAsync(this, streamEntry, cancellationToken);
    }

    /// <inheritdoc />
    public override Task ResetEventStreamPositionAsync(CancellationToken cancellationToken)
    {
        CurrentContentId = null;
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