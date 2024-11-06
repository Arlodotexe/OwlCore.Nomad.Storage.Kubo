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
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;
using SharpCompress.IO;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual file constructed by advancing an <see cref="IEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry}.EventStreamPosition"/> using multiple <see cref="ISources{T}.Sources"/> in concert with other <see cref="ISharedEventStreamHandler{TContentPointer, TEventStreamSource, TEventStreamEntry, TListeningHandlers}.ListeningEventStreamHandlers"/>.
/// </summary>
public class KuboNomadFile : NomadFile<Cid, EventStream<Cid>, EventStreamEntry<Cid>>, IModifiableKuboNomadFile, IFlushable, IGetCid
{
    /// <summary>
    /// Creates a new instance of <see cref="KuboNomadFolder"/> from the specified handler configuration.
    /// </summary>
    /// <param name="handlerConfig">The handler configuration to use.</param>
    /// <param name="parent">The parent folder of this file.</param>
    /// <param name="tempCacheFile">A file to use for seeking reads and holding writes until flush. </param>
    /// <param name="kuboOptions">The options used to read and write data to and from Kubo.</param>
    /// <param name="client">The IPFS client used to interact with the network.</param>
    /// <returns>A new instance of <see cref="KuboNomadFolder"/>.</returns>
    public static KuboNomadFile FromHandlerConfig(NomadKuboEventStreamHandlerConfig<NomadFileData<Cid>> handlerConfig, IFolder parent, IFile tempCacheFile, IKuboOptions kuboOptions, ICoreApi client)
    {
        Guard.IsNotNull(handlerConfig.RoamingValue);
        Guard.IsNotNull(handlerConfig.RoamingKey);
        Guard.IsNotNull(handlerConfig.LocalValue);
        Guard.IsNotNull(handlerConfig.LocalKey);

        return new KuboNomadFile(handlerConfig.ListeningEventStreamHandlers)
        {
            Parent = parent,
            EventStreamHandlerId = handlerConfig.RoamingKey.Id,
            Inner = new()
            {
                StorableItemId = handlerConfig.RoamingValue.StorableItemId,
                StorableItemName = handlerConfig.RoamingValue.StorableItemName,
                ContentId = null,
            },
            LocalEventStream = handlerConfig.LocalValue,
            RoamingKey = handlerConfig.RoamingKey,
            LocalEventStreamKey = handlerConfig.LocalKey,
            AllEventStreamEntries = handlerConfig.AllEventStreamEntries,
            Sources = [],
            KuboOptions = kuboOptions,
            Client = client,
            TempCacheFile = tempCacheFile,
        };
    }
    
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

    /// <summary>
    /// A file to use for seeking reads and holding writes until flush. 
    /// </summary>
    public required IFile TempCacheFile { get; init; }

    /// <inheritdoc />
    public override async Task<Stream> OpenStreamAsync(FileAccess accessMode = FileAccess.Read, CancellationToken cancellationToken = default)
    {
        if (this is IModifiableKuboNomadFile { TempCacheFile: MfsFile mfsFile, Client: { } client })
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

    /// <summary>
    /// Flushes the contents of or changes to this file to the underlying datastore. 
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        Cid newCid = await TempCacheFile.GetCidAsync(Client, new AddFileOptions { Pin = KuboOptions.ShouldPin, OnlyHash = false }, cancellationToken);
        
        var fileUpdateEvent = new FileUpdateEvent(Id, newCid);
        await ApplyEntryUpdateAsync(fileUpdateEvent, cancellationToken);
        var appendedEvent = await AppendNewEntryAsync(fileUpdateEvent, cancellationToken);

        Guard.IsNotNull(Inner.ContentId);
        Guard.IsEqualTo(Inner.ContentId, fileUpdateEvent.NewContentId);
        EventStreamPosition = appendedEvent;
    }

    /// <inheritdoc />
    public Task<Cid> GetCidAsync(CancellationToken cancellationToken)
    {
        Guard.IsNotNull(Inner.ContentId);
        return Task.FromResult(Inner.ContentId);
    }
}