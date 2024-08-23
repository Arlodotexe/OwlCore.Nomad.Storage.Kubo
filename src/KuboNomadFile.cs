using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Kubo;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Kubo.Extensions;
using OwlCore.Nomad.Storage.Kubo.Models;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class KuboNomadFile : NomadFile<Cid, EventStream<Cid>, EventStreamEntry<Cid>>, IModifiableKuboNomadFile
{
    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFile"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known event stream targets participating in event seeking.</param>
    public KuboNomadFile(ICollection<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> listeningEventStreamHandlers)
        : base(listeningEventStreamHandlers)
    {
    }

    /// <inheritdoc/>
    public required IKuboOptions KuboOptions { get; set; }

    /// <inheritdoc/>
    public required ICoreApi Client { get; set; }

    /// <inheritdoc/>
    public required IKey LocalEventStreamKey { get; init; }

    /// <inheritdoc/>
    public required IKey RoamingKey { get; init; }

    /// <inheritdoc />
    public override async Task<Stream> OpenStreamAsync(FileAccess accessMode = FileAccess.Read, CancellationToken cancellationToken = default)
    {
        // Handle empty/new file.
        if (Inner.ContentId is null)
            return new WritableNomadFileStream(this, new MemoryStream());
        
        var backingFile = new IpfsFile(Inner.ContentId, Client);
        var sourceStream = await backingFile.OpenStreamAsync(FileAccess.Read, cancellationToken);

        return new WritableNomadFileStream(this, sourceStream);
    }

    /// <inheritdoc />
    public override Task AdvanceEventStreamAsync(EventStreamEntry<Cid> streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return this.TryAdvanceEventStreamAsync(streamEntry, cancellationToken);
    }

    /// <summary>
    /// Appends a new <paramref name="updateEvent"/> to the local event stream and updates the current folder.
    /// </summary>
    /// <param name="updateEvent">The storage event to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public async Task<EventStreamEntry<Cid>> AppendNewEntryAsync(FileUpdateEvent updateEvent, CancellationToken cancellationToken = default)
    {
        // Use extension method for code deduplication (can't use inheritance).
        var localUpdateEventCid = await Client.Dag.PutAsync(updateEvent, pin: KuboOptions.ShouldPin, cancel: cancellationToken);

        var newEntry = await this.AppendEventStreamEntryAsync(localUpdateEventCid, updateEvent.EventId, targetId: Id, cancellationToken);
        return newEntry;
    }

    /// <summary>
    /// Applies the given event entry
    /// </summary>
    /// <param name="updateEvent"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public Task ApplyEntryUpdateAsync(FileUpdateEvent updateEvent, CancellationToken cancellationToken)
    {
        // Prevent updates intended for other files.
        if (updateEvent.StorableItemId != Id)
            throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't designated for this folder and can't be applied.");

        // Apply file updates
        Inner.ContentId = updateEvent.NewContentId;
        return Task.CompletedTask;
    }
}