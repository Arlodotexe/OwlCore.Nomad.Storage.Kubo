using System.Text;
using OwlCore.Diagnostics;
using OwlCore.Extensions;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Tests.Extensions;

/// <summary>
/// Extension methods and helpers for storage.
/// </summary>
public static partial class StorageExtensions
{
    /// <summary>
    /// Recursively sync the items in a source folder to a destination folder, taking last update time into account, avoiding overwrite when the destination is newer and <paramref name="getLastUpdateTime"/> is defined.
    /// </summary>
    /// <param name="sourceFolder">The source folder to recursively crawl and copy.</param>
    /// <param name="destinationFolder">The folder to copy contents to.</param>
    /// <param name="getLastUpdateTime">A callback to provide the last update time for a given <see cref="IStorable"/>.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <exception cref="ArgumentException">Source folder yielded a child item that isn't a file or folder.</exception>
    public static async Task CopyToAsync(this IFolder sourceFolder, IModifiableFolder destinationFolder, Func<IStorable, DateTime>? getLastUpdateTime = null, CancellationToken cancellationToken = default, IChildFile? gitignoreFile = null)
    {
        // Create a dictionary of destination items.
        // Items are gradually removed as source items are copied to destination.
        // Whatever is left in this dictionary is a list of items not present in the source folder.
        var destinationItemsByName = await destinationFolder.GetItemsAsync(StorableType.All, cancellationToken).ToDictionaryAsync(item => item.Name, item => item, cancellationToken);

        var gitignore = new Ignore.Ignore();
        IFolder? gitignoreFolder = null;

        try
        {
            // Inherit gitignore from parent, or open in current folder if exists
            gitignoreFile ??= (IChildFile)await sourceFolder.GetFirstByNameAsync(".gitignore", cancellationToken);

            await using var stream = await gitignoreFile.OpenStreamAsync(FileAccess.Read, cancellationToken);
            var bytes = await stream.ToBytesAsync(cancellationToken: cancellationToken);

            gitignore.Add(Encoding.UTF8.GetString(bytes).Split(Environment.NewLine.ToCharArray()));

            // Get containing folder
            gitignoreFolder = await gitignoreFile.GetParentAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
        }

        // Sync from source to destination.
        await foreach (var sourceItem in sourceFolder.GetItemsAsync(StorableType.All, cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            // File exists in source and needs copied to destination, unmark for deletion on destination
            destinationItemsByName.Remove(sourceItem.Name);

            // If gitignore is present
            if (gitignoreFolder is not null)
            {
                // Get relative path from git root to item being copied
                var relativePathFromGitignore = await gitignoreFolder.GetRelativePathToAsync(sourceItem, cancellationToken: cancellationToken);

                var isExcludedByGitignore = gitignore.IsIgnored(relativePathFromGitignore);
                if (isExcludedByGitignore)
                {
                    Logger.LogInformation($"Skipped via gitignore: {relativePathFromGitignore}");
                    continue;
                }
            }

            // Process as folder
            if (sourceItem is IFolder sourceSubFolder)
            {
                // Create or get existing subfolder in the destination and recursively synchronize.
                var destinationSubFolder = (IModifiableFolder)await destinationFolder.CreateFolderAsync(sourceSubFolder.Name, overwrite: false, cancellationToken);

                await sourceSubFolder.CopyToAsync(destinationSubFolder, getLastUpdateTime, cancellationToken, gitignoreFile);
                continue;
            }

            // Process as file (for the rest of this method)
            if (sourceItem is not IFile sourceFile)
                continue;

            IFile? existingDestinationFile = null;
            bool sourceIsNewerOrSame = false;
            try
            {
                // Get destination storable for lastModified comparison
                existingDestinationFile = (IFile)await destinationFolder.GetFirstByNameAsync(sourceFile.Name, cancellationToken);

                // Compare the last modified dates
                var destinationLastModified = getLastUpdateTime?.Invoke(existingDestinationFile);
                var sourceLastModified = getLastUpdateTime?.Invoke(sourceFile);

                sourceIsNewerOrSame = sourceLastModified >= destinationLastModified;
            }
            catch (FileNotFoundException)
            {
                // ignored, create file instead.
                // overwrite value no longer matters (file does not exist).
            }

            // If destination file already exists
            if (existingDestinationFile is not null)
            {
                // and the source is older than the destination
                if (!sourceIsNewerOrSame)
                {
                    // we should not copy it
                    Logger.LogInformation($"Up to date {existingDestinationFile.Id}");
                    continue;
                }
            }

            Logger.LogInformation($"Copying {sourceFile.Name} to {destinationFolder.Id}");
            await destinationFolder.CreateCopyOfAsync(sourceFile, overwrite: true, cancellationToken);
        }

        // Any remaining items are present in the destination folder but not in the source folder and should be deleted.
        foreach (var destinationItem in destinationItemsByName.Values)
        {
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            await destinationFolder.DeleteAsync(destinationItem, cancellationToken);
            Logger.LogInformation($"Cleanup deleted file {destinationItem.Id}");
        }
    }

    /// <summary>
    /// From the provided <see cref="IStorable"/>, traverses the provided relative path and returns the item at that path.
    /// </summary>
    /// <param name="from">The item to start with when traversing.</param>
    /// <param name="relativePath">The path of the storable item to return, relative to the provided item.</param>
    /// <param name="cancellationToken">A token to cancel the ongoing operation.</param>
    /// <returns>The <see cref="IStorable"/> item found at the relative path.</returns>
    /// <exception cref="ArgumentException">
    /// A parent directory was specified, but the provided <see cref="IStorable"/> is not addressable.
    /// Or, the provided relative path named a folder, but the item was a file.
    /// Or, an empty path part was found.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A parent folder was requested, but the storable item did not return a parent.</exception>
    /// <exception cref="FileNotFoundException">A named item was specified in a folder, but the item wasn't found.</exception>
    public static async Task<IStorable> GetOrCreateRelativePathAsync(this IStorable from, string relativePath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var inputPathChars = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.PathSeparator, Path.VolumeSeparatorChar };
        var ourPathSeparator = @"/";

        // Traverse only one level at a time
        // But recursively, until the target has been reached.
        var pathParts = relativePath.Split(inputPathChars).Where(x => !string.IsNullOrWhiteSpace(x) && x != ".").ToArray();

        // Current directory was specified.
        if (pathParts.Length == 0)
            return from;

        var nextPathPart = pathParts[0];
        if (string.IsNullOrWhiteSpace(nextPathPart))
            throw new ArgumentException("Empty path part found. Cannot navigate to an item without a name.", nameof(nextPathPart));

        // Get parent directory.
        if (nextPathPart == "..")
        {
            if (from is not IStorableChild child)
                throw new ArgumentException($"A parent folder was requested, but the storable item named {from.Name} is not the child of a directory.", nameof(relativePath));

            var parent = await child.GetParentAsync(cancellationToken);

            // If this item was the last one needed.
            if (parent is not null && pathParts.Length == 1)
                return parent;

            if (parent is null)
                throw new ArgumentOutOfRangeException(nameof(relativePath), "A parent folder was requested, but the storable item did not return a parent.");

            var newRelativePath = string.Join(ourPathSeparator, pathParts.Skip(1));
            return await GetOrCreateRelativePathAsync(parent, newRelativePath);
        }

        // Create/Open item by name.
        if (from is not IModifiableFolder folder)
            throw new ArgumentException($"An item named {nextPathPart} was requested from the folder named {from.Name}, but {from.Name} is not a folder.");

        var item = await folder.CreateFolderAsync(nextPathPart, overwrite, cancellationToken);
        if (item is null)
            throw new FileNotFoundException($"An item named {nextPathPart} was requested from the folder named {from.Name}, but {nextPathPart} wasn't found in the folder.");

        return await GetOrCreateRelativePathAsync(item, string.Join(ourPathSeparator, pathParts.Skip(1)));
    }
}