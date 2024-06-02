using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Nomad;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OwlCore.Nomad.Storage;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class KuboNomadFolder : NomadFolder<Cid, KuboNomadEventStream, KuboNomadEventStreamEntry>, IModifiableKuboBasedNomadFolder
{
    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFolder"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known nomad event streams participating in event seeking.</param>
    public KuboNomadFolder(ICollection<ISharedEventStreamHandler<Cid, KuboNomadEventStream, KuboNomadEventStreamEntry>> listeningEventStreamHandlers)
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
    /// The interval that IPNS should be checked for updates.
    /// </summary>
    public TimeSpan UpdateCheckInterval { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    public override Task DeleteAsync(IStorableChild item, CancellationToken cancellationToken = default)
    {
        var target = Items.FirstOrDefault(x => x.Id == item.Id);
        if (target is not null)
            Items.Remove(target);

        return AppendNewEntryAsync(new DeleteFromFolderEvent(Id, item.Id, item.Name), cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<IChildFolder> CreateFolderAsync(string name, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var newFolder = new KuboNomadFolder(ListeningEventStreamHandlers)
        {
            Client = Client,
            Id = $"{Id}/{name}",
            Name = name,
            Sources = Sources,
            LocalEventStreamKeyName = LocalEventStreamKeyName,
            Parent = this,
            KuboOptions = KuboOptions,
            RoamingKeyName = RoamingKeyName,
        };

        Items.Add(newFolder);

        await AppendNewEntryAsync(new CreateFolderInFolderEvent(Id, newFolder.Id, newFolder.Name, overwrite), cancellationToken);

        return newFolder;
    }

    /// <inheritdoc/>
    public override async Task<IChildFile> CreateFileAsync(string name, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var emptyContent = await Client.FileSystem.AddAsync(new MemoryStream(), cancel: cancellationToken);

        var file = new KuboNomadFile(ListeningEventStreamHandlers)
        {
            LocalEventStreamKeyName = LocalEventStreamKeyName,
            Client = Client,
            Id = $"{Id}/{name}",
            Name = name,
            Parent = this,
            CurrentContentId = emptyContent.Id,
            KuboOptions = KuboOptions,
            RoamingKeyName = RoamingKeyName,
            Sources = Sources,
        };

        Items.Add(file);

        await AppendNewEntryAsync(new CreateFileInFolderEvent(Id, file.Id, file.Name, overwrite), cancellationToken);

        return file;
    }

    /// <inheritdoc/>
    public override Task AppendNewEntryAsync(StorageUpdateEvent updateEvent, CancellationToken cancellationToken = default)
    {
        // Use extension method to deduplicate code between NomadFile (can't use inheritance).
        return this.AppendAndPublishNewEntryToEventStreamAsync(updateEvent, cancellationToken);
    }

    /// <inheritdoc/>
    public Task ApplyEntryUpdateAsync(StorageUpdateEvent updateEventContent, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.ApplyEntryUpdateAsync(this, updateEventContent, cancellationToken);
    }

    /// <inheritdoc />
    public override Task TryAdvanceEventStreamAsync(KuboNomadEventStreamEntry streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.TryAdvanceEventStreamAsync(this, streamEntry, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<IFolderWatcher> GetFolderWatcherAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderWatcher>(new TimerBasedNomadFolderWatcher(this, UpdateCheckInterval));
}