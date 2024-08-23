using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A modifiable kubo-based storage interface for folders.
/// </summary>
public interface IModifiableKuboNomadFolder : IReadOnlyKuboBasedNomadFolder, INomadKuboEventStreamHandler<FolderUpdateEvent>, IModifiableFolder
{
}