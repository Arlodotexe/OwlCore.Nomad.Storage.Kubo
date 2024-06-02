using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Nomad;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage;
using OwlCore.Nomad.Storage.Models;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class KuboNomadFile : NomadFile<Cid, KuboNomadEventStream, KuboNomadEventStreamEntry>, IModifiableKuboBasedNomadFile
{
    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFile"/>.
    /// </summary>
    /// <param name="listeningEventStreamHandlers">The shared collection of known event stream targets participating in event seeking.</param>
    public KuboNomadFile(ICollection<ISharedEventStreamHandler<Cid, KuboNomadEventStream, KuboNomadEventStreamEntry>> listeningEventStreamHandlers)
        : base(listeningEventStreamHandlers)
    {
    }

    /// <inheritdoc/>
    public required IKuboOptions KuboOptions { get; set; }

    /// <inheritdoc/>
    public required ICoreApi Client { get; set; }

    /// <inheritdoc/>
    public required string RoamingKeyName { get; init; }

    /// <summary>
    /// The Cid of the content in this file.
    /// </summary>
    public required Cid? CurrentContentId { get; set; }

    /// <inheritdoc />
    public override async Task<Stream> OpenStreamAsync(FileAccess accessMode = FileAccess.Read, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(CurrentContentId);
        var backingFile = new IpfsFile(CurrentContentId, Client);
        var sourceStream = await backingFile.OpenStreamAsync(FileAccess.Read, cancellationToken);

        return new WritableNomadFileStream(this, sourceStream);
    }

    /// <inheritdoc />
    public override Task TryAdvanceEventStreamAsync(KuboNomadEventStreamEntry streamEntry, CancellationToken cancellationToken)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return KuboBasedNomadStorageExtensions.TryAdvanceEventStreamAsync(this, streamEntry, cancellationToken);
    }

    /// <summary>
    /// Appends a new <paramref name="updateEvent"/> to the local event stream and updates the current folder.
    /// </summary>
    /// <param name="updateEvent">The storage event to apply.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public override Task AppendNewEntryAsync(StorageUpdateEvent updateEvent, CancellationToken cancellationToken = default)
    {
        // Use extension method for code deduplication (can't use inheritance).
        return this.AppendAndPublishNewEntryToEventStreamAsync(updateEvent, cancellationToken);
    }
    
    /// <inheritdoc cref="NomadFile{TContentPointer,TEventStreamSource,TEventStreamEntry}.ApplyEntryUpdateAsync" />
    public override async Task ApplyEntryUpdateAsync(StorageUpdateEvent updateEvent, CancellationToken cancellationToken)
    {
        // Prevent non-folder updates.
        if (updateEvent is not FileUpdateEvent fileUpdateEvent)
            throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't a {nameof(FileUpdateEvent)} and cannot be applied to this file.");

        // Prevent updates intended for other files.
        if (fileUpdateEvent.StorableItemId != Id)
            throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't designated for this folder and can't be applied.");

        // Apply file updates
        CurrentContentId = fileUpdateEvent.NewContentId;
    }
}