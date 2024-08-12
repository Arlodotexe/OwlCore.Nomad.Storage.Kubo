using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Diagnostics;
using OwlCore.Kubo;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Kubo.Extensions;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class KuboNomadFolder : NomadFolder<Cid, EventStream<Cid>, EventStreamEntry<Cid>>, IModifiableKuboBasedNomadFolder, IFlushable, ICreateCopyOf
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
    public required string LocalEventStreamKeyName { get; init; }

    /// <inheritdoc />
    public required string RoamingKeyName { get; init; }

    /// <summary>
    /// The resolved event stream entries to use when constructing child files and folders.
    /// </summary>
    public ICollection<EventStreamEntry<Cid>> EventStreamEntries { get; init; } = [];

    /// <summary>
    /// The interval that IPNS should be checked for updates.
    /// </summary>
    public TimeSpan UpdateCheckInterval { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    public override async Task DeleteAsync(IStorableChild item, CancellationToken cancellationToken = default)
    {
        var storageUpdateEvent = new DeleteFromFolderEvent(Id, item.Id, item.Name);
        await ApplyEntryUpdateAsync(storageUpdateEvent, cancellationToken);
        EventStreamPosition = await AppendNewEntryAsync(storageUpdateEvent, cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<IChildFolder> CreateFolderAsync(string name, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var existing = Inner.Folders.FirstOrDefault(x => x.StorableItemName == name);
        if (!overwrite && existing is not null)
            return await FolderDataToInstanceAsync(existing, cancellationToken);

        var storageUpdateEvent = new CreateFolderInFolderEvent(Id, $"{Id}/{name}", name, overwrite);
        await ApplyEntryUpdateAsync(storageUpdateEvent, cancellationToken);
        EventStreamPosition = await AppendNewEntryAsync(storageUpdateEvent, cancellationToken);

        var folderData = Inner.Folders.First(x => x.StorableItemId == storageUpdateEvent.StorableItemId);
        var folder = await FolderDataToInstanceAsync(folderData, cancellationToken);
        
        return folder;
    }

    /// <inheritdoc/>
    public override async Task<IChildFile> CreateFileAsync(string name, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var existing = Inner.Files.FirstOrDefault(x => x.StorableItemName == name);
        if (!overwrite && existing is not null)
            return await FileDataToInstanceAsync(existing, cancellationToken);
            
        var storageUpdateEvent = new CreateFileInFolderEvent(Id, $"{Id}/{name}", name, overwrite);
        await ApplyEntryUpdateAsync(storageUpdateEvent, cancellationToken);
        EventStreamPosition = await AppendNewEntryAsync(storageUpdateEvent, cancellationToken);

        var fileData = Inner.Files.First(x => x.StorableItemId == storageUpdateEvent.StorableItemId);
        var file = await FileDataToInstanceAsync(fileData, cancellationToken);
        return file;
    }

    /// <inheritdoc/>
    public async Task<IChildFile> CreateCopyOfAsync(IFile fileToCopy, bool overwrite, CancellationToken cancellationToken, CreateCopyOfDelegate fallback)
    {
        var existing = Inner.Files.FirstOrDefault(x => x.StorableItemName == fileToCopy.Name);
        if (overwrite && existing is not null)
        {
            // Delete before creating.
            var storageUpdateEvent = new DeleteFromFolderEvent(Id, existing.StorableItemId, existing.StorableItemName);
            await ApplyEntryUpdateAsync(storageUpdateEvent, cancellationToken);
            EventStreamPosition = await AppendNewEntryAsync(storageUpdateEvent, cancellationToken);
        }
        
        var destinationFile = await CreateFileAsync(fileToCopy.Name, overwrite, cancellationToken);
        var destinationStream = await destinationFile.OpenWriteAsync(cancellationToken);
        var sourceStream = await fileToCopy.OpenReadAsync(cancellationToken);

        await sourceStream.CopyToAsync(destinationStream, 81920, cancellationToken);
        destinationStream.Dispose();
        sourceStream.Dispose();
        
        return destinationFile;
    }

    /// <inheritdoc/>
    public override async Task<EventStreamEntry<Cid>> AppendNewEntryAsync(FolderUpdateEvent updateEvent, CancellationToken cancellationToken = default)
    {
        // Use extension method for code deduplication (can't use inheritance).
        var localUpdateEventCid = await Client.Dag.PutAsync(updateEvent, pin: KuboOptions.ShouldPin, cancel: cancellationToken);
        var newEntry = await this.AppendAndPublishNewEntryToEventStreamAsync(localUpdateEventCid, updateEvent.EventId, targetId: Id, cancellationToken);
        EventStreamEntries.Add(newEntry);
        return newEntry;
    }

    /// <inheritdoc/>
    public Task ApplyEntryUpdateAsync(FolderUpdateEvent updateEventContent, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return this.ApplyFolderUpdateAsync(updateEventContent, cancellationToken);
    }

    /// <inheritdoc />
    public override Task AdvanceEventStreamAsync(EventStreamEntry<Cid> streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return this.TryAdvanceEventStreamAsync(streamEntry, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<IFolderWatcher> GetFolderWatcherAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderWatcher>(new TimerBasedNomadFolderWatcher(this, UpdateCheckInterval));

    /// <inheritdoc />
    protected override async Task<ReadOnlyNomadFile<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> FileDataToInstanceAsync(NomadFileData<Cid> fileData, CancellationToken cancellationToken)
    {
        var file = new KuboNomadFile(ListeningEventStreamHandlers)
        {
            Client = Client,
            KuboOptions = KuboOptions,
            Parent = this,
            Sources = Sources,
            Inner = fileData,
            LocalEventStreamKeyName = LocalEventStreamKeyName,
            RoamingKeyName = RoamingKeyName,
            EventStreamId = EventStreamId,
            EventStreamEntries = EventStreamEntries,
        };
        
        Guard.IsNotNull(EventStreamPosition?.TimestampUtc);

        // Modifiable data cannot read remote changes from the roaming snapshot.
        // Event stream must be advanced using known sources.
        // Resolved event stream entries are passed down the same as sources are.
        foreach (var entry in EventStreamEntries.ToArray().OrderBy(x => x.TimestampUtc).Where(x=> x.TargetId == file.Id))
            await file.AdvanceEventStreamAsync(entry, cancellationToken);

        return file;
    }

    /// <inheritdoc />
    protected override async Task<ReadOnlyNomadFolder<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> FolderDataToInstanceAsync(NomadFolderData<Cid> folderData, CancellationToken cancellationToken)
    {
        var folder = new KuboNomadFolder(ListeningEventStreamHandlers)
        {
            Client = Client,
            KuboOptions = KuboOptions,
            Parent = this,
            Sources = Sources,
            Inner = folderData,
            LocalEventStreamKeyName = LocalEventStreamKeyName,
            RoamingKeyName = RoamingKeyName,
            EventStreamEntries = EventStreamEntries,
            EventStreamId = EventStreamId,
        };
        
        Guard.IsNotNull(EventStreamPosition?.TimestampUtc);

        // Modifiable data cannot read remote changes from the roaming snapshot.
        // Event stream must be advanced using known sources.
        // Resolved event stream entries are passed down the same as sources are.
        foreach (var entry in EventStreamEntries.ToArray().OrderBy(x => x.TimestampUtc).Where(x=> x.TargetId == folder.Id))
            await folder.AdvanceEventStreamAsync(entry, cancellationToken);

        return folder;
    }

    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFolder"/> using the given parameters.
    /// </summary>
    /// <param name="roamingIpnsKey">The roaming ipns key to publish the final state to.</param>
    /// <param name="localEventStreamKeyName">The name of the local event stream to publish modifications to.</param>
    /// <param name="client">The client to use for communicating with ipfs.</param>
    /// <param name="kuboOptions">The options to use when publishing data to Kubo.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns>A task with the requested folder as the result.</returns>
    /// <exception cref="ArgumentException">The provided <paramref name="roamingIpnsKey"/> isn't imported on the local machine, so the folder can't be modified.</exception>
    public static async Task<KuboNomadFolder> CreateAsync(Cid roamingIpnsKey, string localEventStreamKeyName, ICoreApi client, IKuboOptions kuboOptions, CancellationToken cancellationToken)
    {
        var sharedEventStreamHandlers = new List<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>>();
        var keys = await client.Key.ListAsync(cancellationToken);

        // Roaming keys are published from multiple nodes.
        // If we're publishing this roaming key, we cannot read the latest published by another node, we must build from sources.
        var enumerable = keys as IKey[] ?? keys.ToArray();
        var importedRoamingKey = enumerable.FirstOrDefault(x => x.Id == roamingIpnsKey);
        if (importedRoamingKey is null)
            throw new ArgumentException($"Tried to create modifiable folder, but roaming key {roamingIpnsKey} hasn't been imported and can't be written to.");

        // Add published event stream sources, if any
        Logger.LogInformation($"Loading sources from roaming key {roamingIpnsKey}");
        var (publishedData, _) = await roamingIpnsKey.ResolveDagCidAsync<NomadFolderData<Cid>>(client, nocache: !kuboOptions.UseCache, cancellationToken);
        Guard.IsNotNull(publishedData);

        var folder = new KuboNomadFolder(sharedEventStreamHandlers)
        {
            EventStreamId = roamingIpnsKey,
            LocalEventStreamKeyName = localEventStreamKeyName,
            RoamingKeyName = importedRoamingKey.Name,
            Sources = publishedData.Sources,
            Inner = publishedData,
            Client = client,
            KuboOptions = kuboOptions,
            Parent = null,
        };

        // Add local event stream source, if exists and needed
        Logger.LogInformation($"Loading local event stream");
        var localKey = await client.GetOrCreateKeyAsync(localEventStreamKeyName, _ => new EventStream<Cid> { TargetId = roamingIpnsKey, Label = string.Empty, Entries = [] }, kuboOptions.IpnsLifetime, 4096, cancellationToken);
        var (localEventStream, _) = await client.ResolveDagCidAsync<EventStream<Cid>>(localKey.Id, nocache: !kuboOptions.UseCache, cancellationToken);
        Guard.IsNotNull(localEventStream);

        var roamingKeyMatchesLocalEventStream = localEventStream.TargetId == roamingIpnsKey;
        if (roamingKeyMatchesLocalEventStream)
        {
            var sourceNotAdded = folder.Sources.All(x => x != localKey.Id);
            if (sourceNotAdded)
            {
                Logger.LogInformation($"Adding local key {localKey.Id} to sources");
                folder.Sources.Add(localKey.Id);
            }
            else
            {
                Logger.LogInformation($"Local source {localKey.Id} already present in sources");
            }
        }
        
        Logger.LogInformation($"Using {folder.Sources.Count} event stream sources:");
        foreach (var source in folder.Sources)
            Logger.LogInformation(source);

        return folder;
    }

    /// <summary>
    /// Flushes changes to the <see cref="RoamingKeyName"/> and <see cref="LocalEventStreamKeyName"/>.
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public virtual async Task FlushAsync(CancellationToken cancellationToken)
    {
        // TODO: Optional. Create a read-only version and recursively copy to mfs, then publish the resulting unixfs cid.
        var root = (KuboNomadFolder?)await GetRootAsync(cancellationToken);
        if (root is null)
            root = this;
        
        await root.PublishRoamingAsync<KuboNomadFolder, FolderUpdateEvent, NomadFolderData<Cid>>(cancellationToken);
    }
}