using System.Collections.Generic;
using Ipfs;
using OwlCore.ComponentModel;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A kubo-based storage interface for folders.
/// </summary>
/// <remarks>
/// Primarily use to create extension method helpers between file/folder implementations of the generic base classes.
/// </remarks>
public interface IReadOnlyKuboBasedNomadFolder : IReadOnlyNomadKuboEventStreamHandler<FolderUpdateEvent>, IChildFolder, IMutableFolder, IDelegable<NomadFolderData<Cid>>
{
}