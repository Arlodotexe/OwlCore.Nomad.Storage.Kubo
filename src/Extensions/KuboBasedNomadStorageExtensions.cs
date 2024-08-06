using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using Ipfs;
using OwlCore.ComponentModel;
using OwlCore.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Extensions;

/// <summary>
/// Extension methods for <see cref="IModifiableKuboBasedNomadStorage"/> and <see cref="IReadOnlyKuboBasedNomadStorage"/>.
/// </summary>
public static class KuboBasedNomadStorageExtensions
{
    /// <inheritdoc cref="IEventStreamHandler{TEventStreamEntry}"/>
    public static async Task TryAdvanceEventStreamAsync(this IReadOnlyKuboBasedNomadStorage nomadStorage, EventStreamEntry<Cid> eventEntry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (nomadStorage is IReadOnlyKuboBasedNomadFolder folder)
        {
            // Only process event entries for this object.
            if (eventEntry.TargetId != ((IHasId)nomadStorage).Id)
                return;

            var (updateEvent, _) = await nomadStorage.Client.ResolveDagCidAsync<StorageUpdateEvent>(eventEntry.Content, nocache: !nomadStorage.KuboOptions.UseCache, cancellationToken);

            // Prevent non-folder updates.
            if (updateEvent is not FolderUpdateEvent folderUpdateEvent)
                throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't a {nameof(FolderUpdateEvent)} and cannot be applied to this folder.");

            // Prevent updates intended for other folders.
            if (folderUpdateEvent.WorkingFolderId != ((IStorable)nomadStorage).Id)
                throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't designated for this folder and can't be applied.");

            await ApplyEntryUpdateAsync(folder, updateEvent, cancellationToken);

            folder.EventStreamPosition = eventEntry;
        }

        if (nomadStorage is IReadOnlyKuboBasedNomadFile file)
        {
            // Ignore events not targeted for this object.
            if (eventEntry.TargetId != ((IHasId)file).Id)
                return;

            var eventContent = await file.Client.ResolveDagCidAsync<StorageUpdateEvent>(eventEntry.Content, nocache: !file.KuboOptions.UseCache, cancellationToken);
            var updateEvent = eventContent.Result;

            if (updateEvent is not null)
            {
                await ApplyEntryUpdateAsync(file, updateEvent, cancellationToken);

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
    public static Task ApplyEntryUpdateAsync(this IReadOnlyKuboBasedNomadFile nomadFile, StorageUpdateEvent updateEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Prevent non-folder updates.
        if (updateEvent is not FileUpdateEvent<Cid> fileUpdateEvent)
            throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't a {nameof(FileUpdateEvent<Cid>)} and cannot be applied to this file.");

        // Prevent updates intended for other files.
        if (fileUpdateEvent.StorableItemId != ((IStorable)nomadFile).Id)
            throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't designated for this folder and can't be applied.");

        // Apply file updates
        nomadFile.Inner.ContentId = fileUpdateEvent.NewContentId;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies the provided <paramref name="updateEvent"/> in the provided <paramref name="nomadFolder"/>.
    /// </summary>
    /// <param name="nomadFolder">The folder to operate in.</param>
    /// <param name="updateEvent">The event content to apply without side effects.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing task.</param>
    public static async Task ApplyEntryUpdateAsync(this IReadOnlyKuboBasedNomadFolder nomadFolder, StorageUpdateEvent updateEvent, CancellationToken cancellationToken)
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
            var emptyContent = await nomadFolder.Client.FileSystem.AddAsync(new MemoryStream(), cancel: cancellationToken);

            if (createFileEvent.Overwrite)
                nomadFolder.Inner.Files.RemoveAll(x => x.StorableItemId == createFileEvent.StorableItemId || x.StorableItemName == createFileEvent.StorableItemName);

            nomadFolder.Inner.Files.Add(new NomadFileData<Cid>
            {
                ContentId = emptyContent.Id,
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
    }

    /// <summary>
    /// Appends a new event to the event stream and publishes it to ipns.
    /// </summary>
    /// <param name="storage">The storage interface to operate on.</param>
    /// <param name="updateEvent">The update event to publish in a new event.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns>A task containing the new event stream entry.</returns>
    public static async Task<EventStreamEntry<Cid>> AppendAndPublishNewEntryToEventStreamAsync(this IModifiableKuboBasedNomadStorage storage, StorageUpdateEvent updateEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = storage.Client;

        // Get local event stream.
        var keys = await client.Key.ListAsync(cancellationToken);

        var nomadLocalSourceKey = keys.First(x => x.Name == storage.LocalEventStreamKeyName);
        var localSource = await nomadLocalSourceKey.Id.ResolveDagCidAsync<EventStream<Cid>>(client, nocache: !storage.KuboOptions.UseCache, cancellationToken);
        var localEventStreamContent = localSource.Result;
        Guard.IsNotNull(localEventStreamContent);

        var localUpdateEventCid = await client.Dag.PutAsync(updateEvent, pin: storage.KuboOptions.ShouldPin, cancel: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Append the event to the local event stream.
        var newEventStreamEntry = new EventStreamEntry<Cid>
        {
            TargetId = ((IStorableChild)storage).Id,
            EventId = updateEvent.EventId,
            TimestampUtc = DateTime.UtcNow,
            Content = localUpdateEventCid,
        };

        // Get new cid for new local event stream entry.
        var newEventStreamEntryCid = await client.Dag.PutAsync(newEventStreamEntry, pin: storage.KuboOptions.ShouldPin, cancel: cancellationToken);

        // Add new entry cid to event stream content.
        localEventStreamContent.Entries.Add(newEventStreamEntryCid);

        // Get new cid for full local event stream.
        var localEventStreamCid = await client.Dag.PutAsync(localEventStreamContent, pin: storage.KuboOptions.ShouldPin, cancel: cancellationToken);

        // Update the local event stream in ipns.
        await client.Name.PublishAsync(localEventStreamCid, storage.LocalEventStreamKeyName, cancel: cancellationToken);

        return newEventStreamEntry;
    }
}