using Ipfs;
using OwlCore.ComponentModel;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A kubo-based storage interface for files.
/// </summary>
public interface IReadOnlyNomadKuboFile : IChildFile, IDelegable<NomadFileData<DagCid>>
{
}