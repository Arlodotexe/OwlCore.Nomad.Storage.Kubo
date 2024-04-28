using OwlCore.Storage;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A modifiable kubo-based storage interface for files.
/// </summary>
public interface IModifiableKuboBasedNomadFile : IModifiableKuboBasedNomadStorage, IChildFile
{
}