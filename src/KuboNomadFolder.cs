using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.ComponentModel.Nomad;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class KuboNomadFolder : NomadFolder<Cid, NomadEventStream, NomadEventStreamEntry>, IModifiableKuboBasedNomadFolder
{
    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFolder"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known nomad event streams participating in event seeking.</param>
    public KuboNomadFolder(ICollection<ISharedEventStreamHandler<Cid, NomadEventStream, NomadEventStreamEntry>> listeningEventStreamHandlers)
        : base(listeningEventStreamHandlers)
    {
    }

    /// <summary>
    /// The client to use for communicating with Ipfs.
    /// </summary>
    public required ICoreApi Client { get; set; }

    /// <summary>
    /// Whether to pin content added to Ipfs.
    /// </summary>
    public bool ShouldPin { get; set; }

    /// <summary>
    /// Whether to use the cache when resolving Ipns Cids.
    /// </summary>
    public bool UseCache { get; set; }

    /// <summary>
    /// The lifetime of the ipns key containing the local event stream. Your node will need to be online at least once every <see cref="IpnsLifetime"/> to keep the ipns key alive.
    /// </summary>
    public TimeSpan IpnsLifetime { get; set; }

    /// <summary>
    /// The name of an Ipns key containing a Nomad event stream that can be appended and republished to modify the current folder.
    /// </summary>
    public required string LocalEventStreamKeyName { get; init; }

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
            IpnsLifetime = IpnsLifetime,
            UseCache = UseCache,
            ShouldPin = ShouldPin,
            Parent = this,
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
            UseCache = UseCache,
            ShouldPin = ShouldPin,
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
    public override Task TryAdvanceEventStreamAsync(NomadEventStreamEntry streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.TryAdvanceEventStreamAsync(this, streamEntry, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<IFolderWatcher> GetFolderWatcherAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderWatcher>(new TimerBasedNomadFolderWatcher(this, UpdateCheckInterval));
}