using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A modifiable kubo-based storage interface for folders.
/// </summary>
public interface IModifiableKuboBasedNomadFolder : IModifiableKuboBasedNomadStorage, IReadOnlyKuboBasedNomadFolder, IChildFolder
{
}