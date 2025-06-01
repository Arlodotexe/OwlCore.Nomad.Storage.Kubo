using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TImmutableContent, TMutableContent, TEventStream, TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/>.
/// </summary>
public class NomadKuboFolder : NomadFolder<DagCid, Cid, EventStream<DagCid>, EventStreamEntry<DagCid>>, IModifiableNomadKuboFolder, ICreateCopyOf, IFlushable
{
    /// <summary>
    /// Creates a new instance of <see cref="NomadKuboFolder"/> from the specified handler configuration.
    /// </summary>
    /// <param name="handlerConfig">The handler configuration to use.</param>
    /// <param name="tempCacheFolder">The folder to use for caching reads and writes.</param>
    /// <param name="kuboOptions">The options used to read and write data to and from Kubo.</param>
    /// <param name="client">The IPFS client used to interact with the network.</param>
    /// <returns>A new instance of <see cref="NomadKuboFolder"/>.</returns>
    public static NomadKuboFolder FromHandlerConfig(NomadKuboEventStreamHandlerConfig<NomadFolderData<DagCid, Cid>> handlerConfig, IModifiableFolder tempCacheFolder, IKuboOptions kuboOptions, ICoreApi client)
    {
        Guard.IsNotNull(handlerConfig.RoamingValue);
        Guard.IsNotNull(handlerConfig.RoamingKey);
        Guard.IsNotNull(handlerConfig.LocalValue);
        Guard.IsNotNull(handlerConfig.LocalKey);

        return new NomadKuboFolder
        {
            // Only a root-level event stream handler can be created from a config.
            Parent = null,
            TempCacheFolder = tempCacheFolder,
            EventStreamHandlerId = handlerConfig.RoamingKey.Id,
            Inner = handlerConfig.RoamingValue,
            RoamingKey = handlerConfig.RoamingKey,
            Sources = handlerConfig.Sources,
            LocalEventStreamKey = handlerConfig.LocalKey,
            LocalEventStream = handlerConfig.LocalValue,
            ResolvedEventStreamEntries = handlerConfig.ResolvedEventStreamEntries,
            KuboOptions = kuboOptions,
            Client = client,
        };
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
    /// A temp folder for caching during read and persisting writes during flush.  
    /// </summary>
    public required IModifiableFolder TempCacheFolder { get; init; }

    /// <summary>
    /// The resolved event stream entries.
    /// </summary>
    public ICollection<EventStreamEntry<DagCid>>? ResolvedEventStreamEntries { get; set; } = [];

    /// <summary>
    /// The interval that IPNS should be checked for updates.
    /// </summary>
    public TimeSpan UpdateCheckInterval { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc cref="INomadKuboEventStreamHandler{TEventEntryContent}.AppendNewEntryAsync" />
    public override async Task<EventStreamEntry<DagCid>> AppendNewEntryAsync(string targetId, string eventId, FolderUpdateEvent updateEvent, DateTime? timestampUtc = null, CancellationToken cancellationToken = default)
    {
        // Use extension method for code deduplication (can't use inheritance).
        var localUpdateEventCid = await Client.Dag.PutAsync(updateEvent, pin: KuboOptions.ShouldPin, cancel: cancellationToken);
        var newEntry = await this.AppendEventStreamEntryAsync((DagCid)localUpdateEventCid, updateEvent.EventId, targetId: Id, cancellationToken);
        return newEntry;
    }

    /// <inheritdoc cref="NomadFolder{TImmutablePointer,TMutablePointer,TEventStreamSource,TEventStreamEntry}.ApplyEntryUpdateAsync" />
    public override Task ApplyEntryUpdateAsync(EventStreamEntry<DagCid> eventStreamEntry, FolderUpdateEvent eventEntryContent, CancellationToken cancellationToken)
    {
        return eventEntryContent switch
        {
            CreateFileInFolderEvent createFileInFolderEvent => ApplyFolderUpdateAsync(createFileInFolderEvent, cancellationToken),
            CreateFolderInFolderEvent createFolderInFolderEvent => ApplyFolderUpdateAsync(createFolderInFolderEvent, cancellationToken),
            DeleteFromFolderEvent deleteFromFolderEvent => ApplyFolderUpdateAsync(deleteFromFolderEvent, cancellationToken),
            _ => throw new ArgumentOutOfRangeException($"Unhandled {nameof(FolderUpdateEvent)} type {eventEntryContent.GetType()}."),
        };
    }

    /// <inheritdoc />
    public override Task AdvanceEventStreamAsync(EventStreamEntry<DagCid> streamEntry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this.TryAdvanceEventStreamAsync(streamEntry, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<IFolderWatcher> GetFolderWatcherAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderWatcher>(new TimerBasedNomadFolderWatcher(this, UpdateCheckInterval));

    /// <inheritdoc />
    public async Task<IChildFile> CreateCopyOfAsync(IFile fileToCopy, bool overwrite, CancellationToken cancellationToken, CreateCopyOfDelegate fallback)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // If the destination file exists and overwrite is false, it shouldn't be overwritten or returned as-is. Throw an exception instead.
        if (!overwrite)
        {
            try
            {
                var existing = await this.GetFirstByNameAsync(fileToCopy.Name, cancellationToken);
                if (existing is not null)
                    throw new FileAlreadyExistsException(fileToCopy.Name);
            }
            catch (FileNotFoundException) { }
        }

        // Create the destination file.
        var storageUpdateEvent = new CreateFileInFolderEvent(Id, $"{Id}/{fileToCopy.Name}", fileToCopy.Name, overwrite);

        var createdFileData = await ApplyFolderUpdateAsync(storageUpdateEvent, cancellationToken);
        EventStreamPosition = await AppendNewEntryAsync(Id, nameof(CreateFileInFolderEvent), storageUpdateEvent, DateTime.UtcNow, cancellationToken);
        Guard.IsNotNull(createdFileData);

        var newFile = (NomadKuboFile)await FileDataToInstanceAsync(createdFileData, cancellationToken);

        // Populate file cid.
        var fileToCopyCid = await fileToCopy.GetCidAsync(Client, new AddFileOptions { Pin = KuboOptions.ShouldPin, OnlyHash = false }, cancellationToken);

        // Apply and append update event
        var fileUpdateEvent = new FileUpdateEvent(newFile.Id, (DagCid)fileToCopyCid);
        await newFile.ApplyFileUpdateAsync(fileUpdateEvent, cancellationToken);
        newFile.EventStreamPosition = await newFile.AppendNewEntryAsync(fileUpdateEvent.StorableItemId, fileUpdateEvent.EventId, fileUpdateEvent, DateTime.UtcNow, cancellationToken);

        Guard.IsTrue(newFile.Inner.ContentId == (DagCid)fileToCopyCid);
        return newFile;
    }

    /// <inheritdoc />
    protected override async Task<NomadFile<DagCid, Cid, EventStream<DagCid>, EventStreamEntry<DagCid>>> FileDataToInstanceAsync(NomadFileData<DagCid> fileData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheFile = await TempCacheFolder.CreateFileAsync(fileData.StorableItemName, overwrite: false, cancellationToken);

        var file = new NomadKuboFile
        {
            TempCacheFile = cacheFile,
            Client = Client,
            KuboOptions = KuboOptions,
            Parent = this,
            Sources = Sources,
            Inner = fileData,
            LocalEventStreamKey = LocalEventStreamKey,
            RoamingKey = RoamingKey,
            EventStreamHandlerId = EventStreamHandlerId,
            LocalEventStream = LocalEventStream,
        };

        Guard.IsNotNull(EventStreamPosition?.TimestampUtc);
        Guard.IsNotNull(ResolvedEventStreamEntries);

        // Modifiable data cannot read remote changes from the roaming snapshot.
        // Event stream must be advanced using known sources.
        // Resolved event stream entries are passed down the same as sources are.
        foreach (var entry in ResolvedEventStreamEntries.ToArray().OrderBy(x => x.TimestampUtc).Where(x => x.TargetId == file.Id))
            await file.AdvanceEventStreamAsync(entry, cancellationToken);

        return file;
    }

    /// <inheritdoc />
    protected override async Task<NomadFolder<DagCid, Cid, EventStream<DagCid>, EventStreamEntry<DagCid>>> FolderDataToInstanceAsync(NomadFolderData<DagCid, Cid> folderData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheFolder = (IModifiableFolder)await TempCacheFolder.CreateFolderAsync(folderData.StorableItemName, overwrite: false, cancellationToken);

        var folder = new NomadKuboFolder
        {
            TempCacheFolder = cacheFolder,
            Client = Client,
            KuboOptions = KuboOptions,
            Parent = this,
            Sources = Sources,
            Inner = folderData,
            LocalEventStreamKey = LocalEventStreamKey,
            RoamingKey = RoamingKey,
            ResolvedEventStreamEntries = ResolvedEventStreamEntries,
            EventStreamHandlerId = EventStreamHandlerId,
            LocalEventStream = LocalEventStream,
        };

        Guard.IsNotNull(EventStreamPosition?.TimestampUtc);
        Guard.IsNotNull(ResolvedEventStreamEntries);

        // Modifiable data cannot read remote changes from the roaming snapshot.
        // Event stream must be advanced using known sources.
        // Resolved event stream entries are passed down the same as sources are.
        foreach (var entry in ResolvedEventStreamEntries.ToArray().OrderBy(x => x.TimestampUtc).Where(x => x.TargetId == folder.Id))
            await folder.AdvanceEventStreamAsync(entry, cancellationToken);

        return folder;
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // If this object is not root, get root.
        var root = await GetRootAsync(cancellationToken);
        if (root is NomadKuboFolder target)
        {
            await target.FlushAsync(cancellationToken);
            return;
        }

        // Publish this as local/roaming data root.
        await this.PublishLocalAsync<NomadKuboFolder, FolderUpdateEvent>(cancellationToken);
        await this.PublishRoamingAsync<NomadKuboFolder, FolderUpdateEvent, NomadFolderData<DagCid, Cid>>(cancellationToken);
    }
}