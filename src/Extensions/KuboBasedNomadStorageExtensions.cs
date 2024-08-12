using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using Ipfs;
using OwlCore.Kubo;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Kubo.Models;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Extensions;

/// <summary>
/// Extension methods for kubo-based nomad storage implementations.
/// </summary>
public static class KuboBasedNomadStorageExtensions
{
    /// <inheritdoc cref="IEventStreamHandler{TEventStreamEntry}"/>
    public static async Task TryAdvanceEventStreamAsync<T>(this IReadOnlyNomadKuboEventStreamHandler<T> nomadStorage, EventStreamEntry<Cid> eventEntry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (nomadStorage is IReadOnlyKuboBasedNomadFolder folder)
        {
            // Only process event entries for this object.
            if (eventEntry.TargetId != folder.Id)
                throw new InvalidOperationException($"The provided {nameof(eventEntry)} isn't designated for this folder and can't be applied.");

            var (updateEvent, _) = await nomadStorage.Client.ResolveDagCidAsync<FolderUpdateEvent>(eventEntry.Content, nocache: !nomadStorage.KuboOptions.UseCache, cancellationToken);

            // Prevent updates intended for other folders.
            if (updateEvent?.WorkingFolderId != ((IStorable)nomadStorage).Id)
                throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't designated for this folder and can't be applied.");

            await ApplyFolderUpdateAsync(folder, updateEvent, cancellationToken);

            folder.EventStreamPosition = eventEntry;
        }

        if (nomadStorage is IReadOnlyKuboBasedNomadFile file)
        {
            // Ignore events not targeted for this object.
            if (eventEntry.TargetId != file.Id)
                return;

            var eventContent = await file.Client.ResolveDagCidAsync<FileUpdateEvent>(eventEntry.Content, nocache: !file.KuboOptions.UseCache, cancellationToken);
            var updateEvent = eventContent.Result;

            if (updateEvent is not null)
            {
                await ApplyFileUpdateAsync(file, updateEvent, cancellationToken);

                // Update the event stream position.
                file.EventStreamPosition = eventEntry;
            }
        }
    }

    /// <summary>
    /// Applies the provided <paramref name="updateEvent"/> in the provided <paramref name="nomadFile"/>.
    /// </summary>
    /// <param name="nomadFile">The file to operate in.</param>
    /// <param name="updateEvent">The event content to apply without side effects.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing task.</param>
    public static Task ApplyFileUpdateAsync(this IReadOnlyKuboBasedNomadFile nomadFile, FileUpdateEvent updateEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Prevent updates intended for other files.
        if (updateEvent.StorableItemId != nomadFile.Id)
            throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't designated for this folder and can't be applied.");

        // Apply file updates
        nomadFile.Inner.ContentId = updateEvent.NewContentId;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies the provided <paramref name="updateEvent"/> in the provided <paramref name="nomadFolder"/>.
    /// </summary>
    /// <param name="nomadFolder">The folder to operate in.</param>
    /// <param name="updateEvent">The event content to apply without side effects.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing task.</param>
    public static Task ApplyFolderUpdateAsync(this IReadOnlyKuboBasedNomadFolder nomadFolder, FolderUpdateEvent updateEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Apply folder updates
        if (updateEvent is CreateFolderInFolderEvent createFolderEvent)
        {
            nomadFolder.Inner.Folders.Add(new NomadFolderData<Cid>
            {
                StorableItemName = createFolderEvent.StorableItemName,
                StorableItemId = createFolderEvent.StorableItemId,
                Sources = nomadFolder.Sources,
                Files = [],
                Folders = [],
            });
        }

        if (updateEvent is CreateFileInFolderEvent createFileEvent)
        {
            if (createFileEvent.Overwrite)
                nomadFolder.Inner.Files.RemoveAll(x => x.StorableItemId == createFileEvent.StorableItemId || x.StorableItemName == createFileEvent.StorableItemName);

            nomadFolder.Inner.Files.Add(new NomadFileData<Cid>
            {
                ContentId = null,
                StorableItemId = createFileEvent.StorableItemId,
                StorableItemName = createFileEvent.StorableItemName,
            });
        }

        if (updateEvent is DeleteFromFolderEvent deleteEvent)
        {
            // If deleted, it should already exist in the folder.
            // Remove the item if it exists.
            // If it doesn't exist, it may have been removed in another timeline (by another peer).
            // Folders
            var targetFolder = nomadFolder.Inner.Folders.FirstOrDefault(x => x.StorableItemId == deleteEvent.StorableItemId || x.StorableItemName == deleteEvent.StorableItemName);
            if (targetFolder is not null)
                nomadFolder.Inner.Folders.Remove(targetFolder);
            
            // Files
            var targetFile = nomadFolder.Inner.Files.FirstOrDefault(x=> x.StorableItemId == deleteEvent.StorableItemId || deleteEvent.StorableItemName == x.StorableItemName);
            if (targetFile is not null)
                nomadFolder.Inner.Files.Remove(targetFile);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Appends a new event to the event stream and publishes it to ipns.
    /// </summary>
    /// <param name="handler">The storage interface to operate on.</param>
    /// <param name="updateEventContentCid">The CID to use for the content of this update event.</param>
    /// <param name="eventId">A unique identifier for this event type.</param>
    /// <param name="targetId">A unique identifier for the provided <paramref name="handler"/> that can be used to reapply the event later.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns>A task containing the new event stream entry.</returns>
    public static async Task<EventStreamEntry<Cid>> AppendAndPublishNewEntryToEventStreamAsync<T>(this IModifiableNomadKuboEventStreamHandler<T> handler, Cid updateEventContentCid, string eventId, string targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = handler.Client;

        // Get local event stream.
        var keys = await client.Key.ListAsync(cancellationToken);

        var nomadLocalSourceKey = keys.First(x => x.Name == handler.LocalEventStreamKeyName);
        var (localEventStreamContent, _) = await nomadLocalSourceKey.Id.ResolveDagCidAsync<EventStream<Cid>>(client, nocache: !handler.KuboOptions.UseCache, cancellationToken);
        Guard.IsNotNull(localEventStreamContent);
        
        cancellationToken.ThrowIfCancellationRequested();

        // Append the event to the local event stream.
        var newEventStreamEntry = new EventStreamEntry<Cid>
        {
            TargetId = targetId,
            EventId = eventId,
            TimestampUtc = DateTime.UtcNow,
            Content = updateEventContentCid,
        };

        // Get new cid for new local event stream entry.
        var newEventStreamEntryCid = await client.Dag.PutAsync(newEventStreamEntry, pin: handler.KuboOptions.ShouldPin, cancel: cancellationToken);

        // Add new entry cid to event stream content.
        localEventStreamContent.Entries.Add(newEventStreamEntryCid);

        // Get new cid for full local event stream.
        var localEventStreamCid = await client.Dag.PutAsync(localEventStreamContent, pin: handler.KuboOptions.ShouldPin, cancel: cancellationToken);

        // Update the local event stream in ipns.
        await client.Name.PublishAsync(localEventStreamCid, handler.LocalEventStreamKeyName, cancel: cancellationToken);

        return newEventStreamEntry;
    }
}