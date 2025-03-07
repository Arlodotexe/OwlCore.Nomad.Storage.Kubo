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
using OwlCore.Nomad.Storage.Kubo.Models;
using OwlCore.Storage;
using SharpCompress.IO;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TImmutablePointer, TMutablePointer, TEventStreamSource, TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{TMutablePointer}.Sources"/> 
/// </summary>
public class NomadKuboFile : NomadFile<DagCid, Cid, EventStream<DagCid>, EventStreamEntry<DagCid>>, IModifiableNomadKuboFile, IFlushable, IGetCid
{
    /// <inheritdoc/>
    public required IKuboOptions KuboOptions { get; set; }

    /// <inheritdoc/>
    public required ICoreApi Client { get; set; }

    /// <inheritdoc/>
    public required IKey LocalEventStreamKey { get; init; }

    /// <inheritdoc/>
    public required IKey RoamingKey { get; init; }

    /// <summary>
    /// A file to use for seeking reads and holding writes until flush. 
    /// </summary>
    public required IFile TempCacheFile { get; init; }

    /// <summary>
    /// The resolved event stream entries.
    /// </summary>
    public ICollection<EventStreamEntry<DagCid>>? ResolvedEventStreamEntries { get; set; } = [];

    /// <inheritdoc />
    public override async Task<Stream> OpenStreamAsync(FileAccess accessMode = FileAccess.Read, CancellationToken cancellationToken = default)
    {
        if (this is IModifiableNomadKuboFile { TempCacheFile: MfsFile mfsFile, Client: { } client })
        {
            var mfsFolder = (MfsFolder?)await mfsFile.GetParentAsync(cancellationToken);
            Guard.IsNotNull(mfsFolder);

            if (Inner.ContentId is null)
            {
                await client.Mfs.WriteAsync(mfsFile.Path, string.Empty, new MfsWriteOptions { Create = true, Count = 0, Flush = true, Truncate = true }, cancellationToken);
            }
            
            if (Inner.ContentId is not null)
            {
                await client.Mfs.RemoveAsync(mfsFile.Path, force: true, cancel: cancellationToken);
                await client.Mfs.CopyAsync($"/ipfs/{Inner.ContentId}", mfsFile.Path, parents: false, cancellationToken);

                await foreach (var file in mfsFolder.GetItemsAsync(cancellationToken: cancellationToken))
                {
                    var currentCid = await file.GetCidAsync(Client, new(), cancellationToken);
                    if (file.Id == mfsFile.Id)
                        Guard.IsEqualTo(currentCid, Inner.ContentId);
                }
            }
        }

        var backingStream = await TempCacheFile.OpenReadWriteAsync(cancellationToken);
        
        var dispose = new DisposableDelegate { Inner = () =>
            {
                backingStream.Dispose();
                FlushAsync(cancellationToken).Wait(cancellationToken);
            }
        };
        
        var flushToNomadOnDisposeStream = new DelegatedDisposalStream(NonDisposingStream.Create(backingStream)) { Inner = dispose };

        // Mfs can seek normally instead of lazy seeking, and supports writes.
        // Reads and writes happen with MfsStream directly,
        // so the file should have the correct cid set on open.
        if (TempCacheFile is MfsFile)
        {
            return flushToNomadOnDisposeStream;
        }
        
        // Non-mfs (e.g., SystemFolder) backing needs to lazy read from ipfs and flush backing to nomad on dispose
        Stream? sourceStream;
        if (Inner.ContentId is null)
        {
            // LazySeekStream is able to write to backing beyond the max length of source.
            sourceStream = new MemoryStream();
        }
        else
        { 
            var sourceFile = new IpfsFile(Inner.ContentId, Client);
            sourceStream = await sourceFile.OpenStreamAsync(FileAccess.Read, cancellationToken);
        }

        var lazySeekStream = new LazySeekStream(sourceStream, flushToNomadOnDisposeStream);
        return lazySeekStream;
    }

    /// <inheritdoc />
    public override Task AdvanceEventStreamAsync(EventStreamEntry<DagCid> streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return this.TryAdvanceEventStreamAsync(streamEntry, cancellationToken);
    }

    /// <summary>
    /// Appends a new <paramref name="eventEntryContent"/> to the local event stream and updates the current folder.
    /// </summary>
    /// <param name="targetId">A unique identifier that represents the scope the applied event occurred within.</param>
    /// <param name="eventId">A unique identifier for the event that occured within this <paramref name="targetId"/>.</param>
    /// <param name="eventEntryContent">The update event data to apply and persist.</param>
    /// <param name="timestampUtc">The recorded UTC timestamp when this event entry was applied.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns>A task containing the event stream entry that was applied from the update.</returns>
    public async Task<EventStreamEntry<DagCid>> AppendNewEntryAsync(string targetId, string eventId, FileUpdateEvent eventEntryContent, DateTime? timestampUtc = null, CancellationToken cancellationToken = default)
    {
        // Use extension method for code deduplication (can't use inheritance).
        var localUpdateEventCid = await Client.Dag.PutAsync(eventEntryContent, pin: KuboOptions.ShouldPin, cancel: cancellationToken);

        var newEntry = await this.AppendEventStreamEntryAsync((DagCid)localUpdateEventCid, eventEntryContent.EventId, targetId: Id, cancellationToken);
        return newEntry;
    }

    /// <summary>
    /// Applies the given event entry
    /// </summary>
    /// <param name="eventStreamEntry">The event stream entry to apply.</param>
    /// <param name="eventEntryContent">The resolved <see cref="EventStreamEntry{T}.Content"/> to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">Supplied event stream entry doesn't target this object.</exception>
    public Task ApplyEntryUpdateAsync(EventStreamEntry<DagCid> eventStreamEntry, FileUpdateEvent eventEntryContent, CancellationToken cancellationToken)
    {
        // Prevent updates intended for other files.
        if (eventEntryContent.StorableItemId != Id)
            throw new InvalidOperationException($"The provided {nameof(eventEntryContent)} isn't designated for this folder and can't be applied.");

        // Apply file updates
        Inner.ContentId = eventEntryContent.NewContentId;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Flushes the contents of or changes to this file to the underlying datastore. 
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        Cid newCid = await TempCacheFile.GetCidAsync(Client, new AddFileOptions { Pin = KuboOptions.ShouldPin, OnlyHash = false }, cancellationToken);
        
        var fileUpdateEvent = new FileUpdateEvent(Id, (DagCid)newCid);
        var appendedEvent = await AppendNewEntryAsync(fileUpdateEvent.StorableItemId, fileUpdateEvent.EventId, fileUpdateEvent, DateTime.UtcNow, cancellationToken);
        await ApplyEntryUpdateAsync(appendedEvent, fileUpdateEvent, cancellationToken);

        Guard.IsNotNull(Inner.ContentId);
        Guard.IsEqualTo(Inner.ContentId, fileUpdateEvent.NewContentId);
        EventStreamPosition = appendedEvent;
    }

    /// <inheritdoc />
    public Task<Cid> GetCidAsync(CancellationToken cancellationToken)
    {
        Guard.IsNotNull(Inner.ContentId);
        return Task.FromResult((Cid)Inner.ContentId);
    }
}