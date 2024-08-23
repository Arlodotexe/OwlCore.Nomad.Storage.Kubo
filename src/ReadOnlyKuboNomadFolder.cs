using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual folder constructed by reading the roaming <see cref="NomadFolderData{TContentPointer}"/> published by another node.
/// </summary>
public class ReadOnlyKuboNomadFolder : IChildFolder, IDelegable<NomadFolderData<Cid>>, IGetRoot
{
    /// <summary>
    /// The client to use for communicating with ipfs/kubo.
    /// </summary>
    public required ICoreApi Client { get; set; }

    /// <inheritdoc />
    public string Id => Inner.StorableItemId;

    /// <inheritdoc />
    public string Name => Inner.StorableItemName;

    /// <inheritdoc />
    public required NomadFolderData<Cid> Inner { get; init; }

    /// <summary>
    /// The parent for this folder, if any.
    /// </summary>
    public required ReadOnlyKuboNomadFolder? Parent { get; init; }
    
    /// <inheritdoc />
    public async IAsyncEnumerable<IStorableChild> GetItemsAsync(StorableType type = StorableType.All, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();

        if (type.HasFlag(StorableType.File))
        {
            foreach (var file in Inner.Files)
            {
                yield return new ReadOnlyKuboNomadFile
                {
                    Client = Client,
                    Inner = file,
                    Parent = this,
                };
            }
        }

        if (type.HasFlag(StorableType.Folder))
        {
            foreach (var folder in Inner.Folders)
            {
                yield return new ReadOnlyKuboNomadFolder
                {
                    Client = Client,
                    Inner = folder,
                    Parent = this,
                };
            } 
        }
    }

    /// <inheritdoc />
    public Task<IFolder?> GetParentAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolder?>(Parent);

    /// <inheritdoc />
    public Task<IFolder?> GetRootAsync(CancellationToken cancellationToken = default)
    {
        // No parent = no root
        if (Parent is null)
            return Task.FromResult<IFolder?>(null);
        
        // At least one parent is required for a root to exist
        // Crawl up and return where parent is null
        var current = this;
        while (current.Parent is { } parent)
        {
            current = parent;
        }

        return Task.FromResult<IFolder?>(current);
    }
}