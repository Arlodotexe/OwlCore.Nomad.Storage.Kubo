using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual folder constructed by reading the roaming <see cref="NomadFolderData{TImmutablePointer, TMutablePointer}"/> published by another node.
/// </summary>
public class ReadOnlyNomadKuboFolder : IChildFolder, IDelegable<NomadFolderData<DagCid, Cid>>, IGetRoot
{
    /// <summary>
    /// Creates a new instance of <see cref="ReadOnlyNomadKuboFolder"/> from the given handler configuration.
    /// </summary>
    /// <param name="handlerConfig">The handler configuration to use.</param>
    /// <param name="client">A client that can be used for accessing ipfs.</param>
    /// <returns>A new instance of <see cref="ReadOnlyNomadKuboFolder"/>.</returns>
    public static ReadOnlyNomadKuboFolder FromHandlerConfig(NomadKuboEventStreamHandlerConfig<NomadFolderData<DagCid, Cid>> handlerConfig, ICoreApi client)
    {
        Guard.IsNotNull(handlerConfig.RoamingValue);
        Guard.IsNotNull(handlerConfig.RoamingId);

        return new ReadOnlyNomadKuboFolder
        {
            // Only a root-level event stream handler (or equivalent in the case of read-only) can be created from a config.
            Parent = null,
            Client = client,
            Inner = handlerConfig.RoamingValue,
        };
    }
    
    /// <summary>
    /// The client to use for communicating with ipfs/kubo.
    /// </summary>
    public required ICoreApi Client { get; set; }

    /// <inheritdoc />
    public string Id => Inner.StorableItemId;

    /// <inheritdoc />
    public string Name => Inner.StorableItemName;

    /// <inheritdoc />
    public required NomadFolderData<DagCid, Cid> Inner { get; init; }

    /// <summary>
    /// The parent for this folder, if any.
    /// </summary>
    public required IFolder? Parent { get; init; }
    
    /// <inheritdoc />
    public async IAsyncEnumerable<IStorableChild> GetItemsAsync(StorableType type = StorableType.All, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();

        if (type.HasFlag(StorableType.File))
        {
            foreach (var file in Inner.Files)
            {
                yield return new ReadOnlyNomadKuboFile
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
                yield return new ReadOnlyNomadKuboFolder
                {
                    Client = Client,
                    Inner = folder,
                    Parent = this,
                };
            } 
        }
    }

    /// <inheritdoc />
    public Task<IFolder?> GetParentAsync(CancellationToken cancellationToken = default) => Task.FromResult(Parent);

    /// <inheritdoc />
    public Task<IFolder?> GetRootAsync(CancellationToken cancellationToken = default)
    {
        // No parent = no root
        if (Parent is null)
            return Task.FromResult<IFolder?>(null);
        
        // At least one parent is required for a root to exist
        // Crawl up and return where parent is null
        var current = this;
        while (current.Parent is ReadOnlyNomadKuboFolder parent)
        {
            current = parent;
        }

        if (current.Parent is IStorableChild storableChild)
            return storableChild.GetRootAsync(cancellationToken);
            
        return Task.FromResult<IFolder?>(current);
    }
}