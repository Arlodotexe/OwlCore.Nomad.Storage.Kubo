using OwlCore.Storage;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A modifiable kubo-based storage interface for folders.
/// </summary>
public interface IModifiableKuboBasedNomadFolder : IModifiableKuboBasedNomadStorage, IReadOnlyKuboBasedNomadFolder, IChildFolder
{
}