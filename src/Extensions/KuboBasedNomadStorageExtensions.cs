using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using Ipfs;
using OwlCore.Kubo;
using OwlCore.Nomad.Storage.Kubo.Models;
using OwlCore.Nomad.Storage.Models;

namespace OwlCore.Nomad.Storage.Kubo.Extensions;

/// <summary>
/// Extension methods for kubo-based nomad storage implementations.
/// </summary>
public static class KuboBasedNomadStorageExtensions
{
    /// <summary>
    /// Attempts to advance the event stream position for the <paramref name="file"/> using the data from the given <paramref name="eventEntry"/>. 
    /// </summary>
    /// <param name="file">The nomad-based file to apply the <paramref name="eventEntry"/> to.</param>
    /// <param name="eventEntry">The event entry to apply to the given <paramref name="file"/>.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <exception cref="InvalidOperationException">Raised when the <see cref="EventStreamEntry{TContentPointer}.TargetId"/> doesn't match the <see cref="OwlCore.Storage.IStorable.Id"/> of the provided <paramref name="file"/>.</exception>
    public static async Task TryAdvanceEventStreamAsync(this IModifiableKuboNomadFile file, EventStreamEntry<Cid> eventEntry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        // Only process event entries for this object.
        if (eventEntry.TargetId != file.Id)
            throw new InvalidOperationException($"The provided {nameof(eventEntry)} isn't designated for this file and can't be applied.");

        var (updateEvent, _) = await file.Client.ResolveDagCidAsync<FileUpdateEvent>(eventEntry.Content, nocache: !file.KuboOptions.UseCache, cancellationToken);
        Guard.IsNotNull(updateEvent); 

        await ApplyFileUpdateAsync(file, updateEvent, cancellationToken);
        file.EventStreamPosition = eventEntry;
        Diagnostics.Logger.LogInformation($"Advanced event stream for file {file.Id} with {eventEntry.EventId} content {eventEntry.Content}");
    }
    
    /// <summary>
    /// Attempts to advance the event stream position for the <paramref name="folder"/> using the data from the given <paramref name="eventEntry"/>. 
    /// </summary>
    /// <param name="folder">The nomad-based folder to apply the <paramref name="eventEntry"/> to.</param>
    /// <param name="eventEntry">The event entry to apply to the given <paramref name="folder"/>.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <exception cref="InvalidOperationException">Raised when the <see cref="EventStreamEntry{TContentPointer}.TargetId"/> doesn't match the <see cref="OwlCore.Storage.IStorable.Id"/> of the provided <paramref name="folder"/>.</exception>
    public static async Task TryAdvanceEventStreamAsync(this IModifiableKuboNomadFolder folder, EventStreamEntry<Cid> eventEntry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        // Only process event entries for this object.
        if (eventEntry.TargetId != folder.Id)
            throw new InvalidOperationException($"The provided {nameof(eventEntry)} isn't designated for this folder and can't be applied.");

        var (updateEvent, _) = await folder.Client.ResolveDagCidAsync<FolderUpdateEvent>(eventEntry.Content, nocache: !folder.KuboOptions.UseCache, cancellationToken);
        Guard.IsNotNull(updateEvent);

        // Prevent updates intended for other folders.
        if (updateEvent?.WorkingFolderId != folder.Id)
            throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't designated for this folder and can't be applied.");

        await folder.ApplyEntryUpdateAsync(updateEvent, cancellationToken);
        folder.EventStreamPosition = eventEntry;
        Diagnostics.Logger.LogInformation($"Advanced event stream for folder {folder.Id} with {eventEntry.EventId} content {eventEntry.Content}");
    }

    /// <summary>
    /// Applies the provided <paramref name="updateEvent"/> in the provided <paramref name="nomadFile"/>.
    /// </summary>
    /// <param name="nomadFile">The file to operate in.</param>
    /// <param name="updateEvent">The event content to apply without side effects.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing task.</param>
    public static Task ApplyFileUpdateAsync(this IReadOnlyKuboNomadFile nomadFile, FileUpdateEvent updateEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Prevent updates intended for other files.
        if (updateEvent.StorableItemId != nomadFile.Id)
            throw new InvalidOperationException($"The provided {nameof(updateEvent)} isn't designated for this folder and can't be applied.");

        // Apply file updates
        nomadFile.Inner.ContentId = updateEvent.NewContentId;
        Guard.IsNotEqualTo("QmbFMke1KXqnYyBBWxB74N4c5SBnJMVAiMNRcGu6x1AwQH", updateEvent.NewContentId.ToString());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies the provided <paramref name="updateEvent"/> in the provided <paramref name="nomadFolder"/>.
    /// </summary>
    /// <param name="nomadFolder">The folder to operate in.</param>
    /// <param name="updateEvent">The event content to apply without side effects.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing task.</param>
    public static Task ApplyFolderUpdateAsync(this IReadOnlyKuboBasedNomadFolder nomadFolder, DeleteFromFolderEvent updateEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        // If deleted, it should already exist in the folder.
        // Remove the item if it exists.
        // If it doesn't exist, it may have been removed in another timeline (by another peer).
        // Folders
        var targetFolder = nomadFolder.Inner.Folders.FirstOrDefault(x => x.StorableItemId == updateEvent.StorableItemId || x.StorableItemName == updateEvent.StorableItemName);
        if (targetFolder is not null)
            nomadFolder.Inner.Folders.Remove(targetFolder);
        
        // Files
        var targetFile = nomadFolder.Inner.Files.FirstOrDefault(x=> x.StorableItemId == updateEvent.StorableItemId || updateEvent.StorableItemName == x.StorableItemName);
        if (targetFile is not null)
            nomadFolder.Inner.Files.Remove(targetFile);

        return Task.CompletedTask;
    }
}
