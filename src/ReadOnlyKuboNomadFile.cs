using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.ComponentModel;
using OwlCore.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A virtual file constructed by reading the roaming <see cref="NomadFileData{TContentPointer}"/> published by another node.
/// </summary>
public class ReadOnlyKuboNomadFile : IChildFile, IDelegable<NomadFileData<Cid>>
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
    public required NomadFileData<Cid> Inner { get; init; }

    /// <summary>
    /// The parent for this folder, if any.
    /// </summary>
    public required ReadOnlyKuboNomadFolder? Parent { get; init; }
    
    /// <inheritdoc />
    public async Task<Stream> OpenStreamAsync(FileAccess accessMode = FileAccess.Read, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (accessMode.HasFlag(FileAccess.Write))
            throw new ArgumentException($"{nameof(ReadOnlyKuboNomadFile)} doesn't support writing. Use {nameof(KuboNomadFile)} instead.");

        var contentId = Inner.ContentId;
        if (contentId is null)
            return new MemoryStream();

        Guard.IsNotNull(contentId);

        var backingFile = new IpfsFile(contentId, Client);
        var sourceStream = await backingFile.OpenStreamAsync(accessMode, cancellationToken);

        return sourceStream;
    }
    
    /// <inheritdoc />
    public Task<IFolder?> GetParentAsync(CancellationToken cancellationToken = default) => Task.FromResult<IFolder?>(Parent);
}