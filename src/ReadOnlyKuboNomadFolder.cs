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
public class ReadOnlyKuboNomadFolder : ReadOnlyNomadFolder<Cid, EventStream<Cid>, EventStreamEntry<Cid>>, IReadOnlyKuboBasedNomadFolder
{
    /// <summary>
    /// Creates a new instance of <see cref="ReadOnlyKuboNomadFolder"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known nomad event streams participating in event seeking.</param>
    public ReadOnlyKuboNomadFolder(ICollection<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> listeningEventStreamHandlers)
        : base(listeningEventStreamHandlers)
    {
    }

    /// <inheritdoc/>
    public required IKuboOptions KuboOptions { get; set; }

    /// <inheritdoc/>
    public required ICoreApi Client { get; set; }

    /// <summary>
    /// The interval that IPNS should be checked for updates.
    /// </summary>
    public TimeSpan UpdateCheckInterval { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public override Task AdvanceEventStreamAsync(EventStreamEntry<Cid> streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.TryAdvanceEventStreamAsync(this, streamEntry, cancellationToken);
    }

    /// <summary>
    /// Applies the provided storage update event without external side effects.
    /// </summary>
    /// <param name="updateEventContent">The event to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public Task ApplyEntryUpdateAsync(FolderUpdateEvent updateEventContent, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.ApplyFolderUpdateAsync(this, updateEventContent, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<IFolderWatcher> GetFolderWatcherAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolderWatcher>(new TimerBasedNomadFolderWatcher(this, UpdateCheckInterval));

    /// <inheritdoc />
    protected override Task<ReadOnlyNomadFile<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> FileDataToInstanceAsync(NomadFileData<Cid> fileData, CancellationToken cancellationToken)
    {
        var file = new ReadOnlyKuboNomadFile(ListeningEventStreamHandlers)
        {
            Parent = this,
            Inner = fileData,
            Sources = Sources,
            Client = Client,
            KuboOptions = KuboOptions,
            EventStreamId = EventStreamId,
        };
        
        // Event stream doesn't need to be advanced for read only data. The Inner fileData should have the current state.
        return Task.FromResult<ReadOnlyNomadFile<Cid, EventStream<Cid>, EventStreamEntry<Cid>>>(file);
    }

    /// <inheritdoc />
    protected override Task<ReadOnlyNomadFolder<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> FolderDataToInstanceAsync(NomadFolderData<Cid> folderData, CancellationToken cancellationToken)
    {
        var folder = new ReadOnlyKuboNomadFolder(ListeningEventStreamHandlers)
        {
            Parent = this,
            Inner = folderData,
            Sources = Sources,
            Client = Client,
            KuboOptions = KuboOptions,
            EventStreamId = EventStreamId,
        };

        // Event stream doesn't need to be advanced for read only data. The Inner folderData should have the current state.
        return Task.FromResult<ReadOnlyNomadFolder<Cid, EventStream<Cid>, EventStreamEntry<Cid>>>(folder);
    }
    
    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFolder"/> using the given parameters.
    /// </summary>
    /// <param name="roamingIpnsKey">The roaming ipns key to publish the final state to.</param>
    /// <param name="client">The client to use for communicating with ipfs.</param>
    /// <param name="kuboOptions">The options to use when interacting with to Kubo.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns>A task with the requested folder as the result.</returns>
    /// <exception cref="ArgumentException">The provided <paramref name="roamingIpnsKey"/> isn't imported on the local machine, so the folder can't be modified.</exception>
    public static async Task<ReadOnlyKuboNomadFolder> CreateAsync(Cid roamingIpnsKey, ICoreApi client, IKuboOptions kuboOptions, CancellationToken cancellationToken)
    {
        var sharedEventStreamHandlers = new List<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>>();

        // Add published event stream sources, if any
        Logger.LogInformation($"Loading sources from roaming key {roamingIpnsKey}");
        var (publishedData, _) = await roamingIpnsKey.ResolveDagCidAsync<NomadFolderData<Cid>>(client, nocache: !kuboOptions.UseCache, cancellationToken);
        Guard.IsNotNull(publishedData);

        var folder = new ReadOnlyKuboNomadFolder(sharedEventStreamHandlers)
        {
            EventStreamId = roamingIpnsKey,
            Sources = publishedData.Sources,
            Parent = null,
            Inner = publishedData,
            Client = client,
            KuboOptions = kuboOptions,
        };

        // Event stream doesn't need to be advanced for read only data. The Inner folderData should have the current state.
        Logger.LogInformation($"Folder {folder.Id} loaded");
        return folder;
    }
}