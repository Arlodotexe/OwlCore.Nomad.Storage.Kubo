using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A modifiable kubo-based storage interface for folders.
/// </summary>
public interface IModifiableKuboBasedNomadFolder : IReadOnlyKuboBasedNomadFolder, IModifiableNomadKuboEventStreamHandler<FolderUpdateEvent>
{
}