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
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TContentPointer, TEventStream, TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class KuboNomadFolder : NomadFolder<Cid, EventStream<Cid>, EventStreamEntry<Cid>>, IModifiableKuboNomadFolder, ICreateCopyOf
{
    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFolder"/> from the specified handler configuration.
    /// </summary>
    /// <param name="handlerConfig">The handler configuration to use.</param>
    /// <param name="tempCacheFolder">A temp folder for caching during read and persisting writes during flush.  </param>
    /// <param name="parent">The parent of this folder, if any.</param>
    /// <param name="kuboOptions">The options used to read and write data to and from Kubo.</param>
    /// <param name="client">The IPFS client used to interact with the network.</param>
    /// <returns>A new instance of <see cref="KuboNomadFolder"/>.</returns>
    public static KuboNomadFolder FromHandlerConfig(NomadKuboEventStreamHandlerConfig<NomadFolderData<Cid>> handlerConfig, IModifiableFolder tempCacheFolder, IFolder? parent, IKuboOptions kuboOptions, ICoreApi client)
    {
        Guard.IsNotNull(handlerConfig.RoamingValue);
        Guard.IsNotNull(handlerConfig.RoamingKey);
        Guard.IsNotNull(handlerConfig.LocalValue);
        Guard.IsNotNull(handlerConfig.LocalKey);

        return new KuboNomadFolder(handlerConfig.ListeningEventStreamHandlers)
        {
            Parent = parent,
            TempCacheFolder = tempCacheFolder,
            EventStreamHandlerId = handlerConfig.RoamingKey.Id,
            Inner = new()
            {
                StorableItemId = handlerConfig.RoamingValue.StorableItemId, 
                StorableItemName = handlerConfig.RoamingValue.StorableItemName,
                Sources = handlerConfig.RoamingValue.Sources
            },
            LocalEventStream = handlerConfig.LocalValue,
            RoamingKey = handlerConfig.RoamingKey,
            LocalEventStreamKey = handlerConfig.LocalKey,
            AllEventStreamEntries = handlerConfig.AllEventStreamEntries,
            Sources = handlerConfig.RoamingValue.Sources,
            KuboOptions = kuboOptions,
            Client = client,
        };
    }
    
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
    /// A temp folder for caching during read and persisting writes during flush.  
    /// </summary>
    public required IModifiableFolder TempCacheFolder { get; init; }

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
        EventStreamPosition = await AppendNewEntryAsync(storageUpdateEvent, cancellationToken);
        Guard.IsNotNull(createdFileData);
        
        var newFile = (KuboNomadFile)await FileDataToInstanceAsync(createdFileData, cancellationToken);
        
        // Populate file cid.
        var fileToCopyCid = await fileToCopy.GetCidAsync(Client, new AddFileOptions { Pin = KuboOptions.ShouldPin, OnlyHash = false }, cancellationToken);
        
        // Apply and append update event
        var fileUpdateEvent = new FileUpdateEvent(newFile.Id, fileToCopyCid);
        await newFile.ApplyFileUpdateAsync(fileUpdateEvent, cancellationToken);
        newFile.EventStreamPosition = await newFile.AppendNewEntryAsync(fileUpdateEvent, cancellationToken);
        
        Guard.IsTrue(newFile.Inner.ContentId == fileToCopyCid);
        return newFile;
    }

    /// <inheritdoc />
    protected override async Task<NomadFile<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> FileDataToInstanceAsync(NomadFileData<Cid> fileData, CancellationToken cancellationToken)
    {
        var cacheFile = await TempCacheFolder.CreateFileAsync(fileData.StorableItemName, overwrite: false, cancellationToken);
        
        var file = new KuboNomadFile(ListeningEventStreamHandlers)
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
        var cacheFolder = (IModifiableFolder)await TempCacheFolder.CreateFolderAsync(folderData.StorableItemName, overwrite: false, cancellationToken);
        
        var folder = new KuboNomadFolder(ListeningEventStreamHandlers)
        {
            TempCacheFolder = cacheFolder,
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