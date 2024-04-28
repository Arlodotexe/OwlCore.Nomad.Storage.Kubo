using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.ComponentModel.Nomad;
using OwlCore.Storage;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OwlCore.Nomad.Storage.Models;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class ReadOnlyKuboNomadFolder : ReadOnlyNomadFolder<Cid, NomadEventStream, NomadEventStreamEntry>, IReadOnlyKuboBasedNomadFolder
{
    /// <summary>
    /// Creates a new instance of <see cref="ReadOnlyKuboNomadFolder"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known nomad event streams participating in event seeking.</param>
    public ReadOnlyKuboNomadFolder(ICollection<ISharedEventStreamHandler<Cid, NomadEventStream, NomadEventStreamEntry>> listeningEventStreamHandlers)
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
    /// The interval that IPNS should be checked for updates.
    /// </summary>
    public TimeSpan UpdateCheckInterval { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public override Task TryAdvanceEventStreamAsync(NomadEventStreamEntry streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.TryAdvanceEventStreamAsync(this, streamEntry, cancellationToken);
    }

    /// <summary>
    /// Applies the provided storage update event without external side effects.
    /// </summary>
    /// <param name="updateEventContent">The event to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public Task ApplyEntryUpdateAsync(StorageUpdateEvent updateEventContent, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.ApplyEntryUpdateAsync(this, updateEventContent, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<IFolderWatcher> GetFolderWatcherAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderWatcher>(new TimerBasedNomadFolderWatcher(this, UpdateCheckInterval));
}