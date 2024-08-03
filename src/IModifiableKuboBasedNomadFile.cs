using Ipfs;
using OwlCore.ComponentModel;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A modifiable kubo-based storage interface for files.
/// </summary>
public interface IModifiableKuboBasedNomadFile : IModifiableKuboBasedNomadStorage, IChildFile, IDelegable<NomadFileData<Cid>>
{
}