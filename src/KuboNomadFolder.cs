using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Kubo.Extensions;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TContentPointer, TEventStream, TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class KuboNomadFolder : NomadFolder<Cid, EventStream<Cid>, EventStreamEntry<Cid>>, IModifiableKuboNomadFolder
{
    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFolder"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known nomad event streams participating in event seeking.</param>
    public KuboNomadFolder(ICollection<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> listeningEventStreamHandlers)
        : base(listeningEventStreamHandlers)
    {
    }

    /// <inheritdoc/>
    public required IKuboOptions KuboOptions { get; set; }

    /// <inheritdoc/>
    public required ICoreApi Client { get; set; }

    /// <inheritdoc />
    public required IKey LocalEventStreamKey { get; init; }

    /// <inheritdoc />
    public required IKey RoamingKey { get; init; }

    /// <summary>
    /// The interval that IPNS should be checked for updates.
    /// </summary>
    public TimeSpan UpdateCheckInterval { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc cref="INomadKuboEventStreamHandler{TEventEntryContent}.AppendNewEntryAsync" />
    public override async Task<EventStreamEntry<Cid>> AppendNewEntryAsync(FolderUpdateEvent updateEvent, CancellationToken cancellationToken = default)
    {
        // Use extension method for code deduplication (can't use inheritance).
        var localUpdateEventCid = await Client.Dag.PutAsync(updateEvent, pin: KuboOptions.ShouldPin, cancel: cancellationToken);
        var newEntry = await this.AppendEventStreamEntryAsync(localUpdateEventCid, updateEvent.EventId, targetId: Id, cancellationToken);
        return newEntry;
    }

    /// <inheritdoc cref="NomadFolder{TContentPointer,TEventStreamSource,TEventStreamEntry}.ApplyEntryUpdateAsync" />
    public override Task ApplyEntryUpdateAsync(FolderUpdateEvent updateEventContent, CancellationToken cancellationToken)
    {
        // Use extension methods for code deduplication (can't use inheritance).
        return updateEventContent switch
        {
            CreateFileInFolderEvent createFileInFolderEvent => this.ApplyFolderUpdateAsync(createFileInFolderEvent, cancellationToken),
            CreateFolderInFolderEvent createFolderInFolderEvent => this.ApplyFolderUpdateAsync(createFolderInFolderEvent, cancellationToken),
            DeleteFromFolderEvent deleteFromFolderEvent => this.ApplyFolderUpdateAsync(deleteFromFolderEvent, cancellationToken),
            _ => throw new ArgumentOutOfRangeException($"Unhandled {nameof(FolderUpdateEvent)} type {updateEventContent.GetType()}."),
        };
    }

    /// <inheritdoc />
    public override Task AdvanceEventStreamAsync(EventStreamEntry<Cid> streamEntry, CancellationToken cancellationToken)
    {
        return this.TryAdvanceEventStreamAsync(streamEntry, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<IFolderWatcher> GetFolderWatcherAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderWatcher>(new TimerBasedNomadFolderWatcher(this, UpdateCheckInterval));

    /// <inheritdoc />
    protected override async Task<NomadFile<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> FileDataToInstanceAsync(NomadFileData<Cid> fileData, CancellationToken cancellationToken)
    {
        var file = new KuboNomadFile(ListeningEventStreamHandlers)
        {
            Client = Client,
            KuboOptions = KuboOptions,
            Parent = this,
            Sources = Sources,
            Inner = fileData,
            LocalEventStreamKey = LocalEventStreamKey,
            RoamingKey = RoamingKey,
            EventStreamHandlerId = EventStreamHandlerId,
            AllEventStreamEntries = AllEventStreamEntries,
            LocalEventStream = LocalEventStream,
        };
        
        Guard.IsNotNull(EventStreamPosition?.TimestampUtc);

        // Modifiable data cannot read remote changes from the roaming snapshot.
        // Event stream must be advanced using known sources.
        // Resolved event stream entries are passed down the same as sources are.
        foreach (var entry in AllEventStreamEntries.ToArray().OrderBy(x => x.TimestampUtc).Where(x=> x.TargetId == file.Id))
            await file.AdvanceEventStreamAsync(entry, cancellationToken);

        return file;
    }

    /// <inheritdoc />
    protected override async Task<NomadFolder<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> FolderDataToInstanceAsync(NomadFolderData<Cid> folderData, CancellationToken cancellationToken)
    {
        var folder = new KuboNomadFolder(ListeningEventStreamHandlers)
        {
            Client = Client,
            KuboOptions = KuboOptions,
            Parent = this,
            Sources = Sources,
            Inner = folderData,
            LocalEventStreamKey = LocalEventStreamKey,
            RoamingKey = RoamingKey,
            AllEventStreamEntries = AllEventStreamEntries,
            EventStreamHandlerId = EventStreamHandlerId,
            LocalEventStream = LocalEventStream,
        };
        
        Guard.IsNotNull(EventStreamPosition?.TimestampUtc);

        // Modifiable data cannot read remote changes from the roaming snapshot.
        // Event stream must be advanced using known sources.
        // Resolved event stream entries are passed down the same as sources are.
        foreach (var entry in AllEventStreamEntries.ToArray().OrderBy(x => x.TimestampUtc).Where(x=> x.TargetId == folder.Id))
            await folder.AdvanceEventStreamAsync(entry, cancellationToken);

        return folder;
    }
}