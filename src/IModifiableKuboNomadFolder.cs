using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A modifiable kubo-based storage interface for folders.
/// </summary>
public interface IModifiableKuboNomadFolder : IReadOnlyKuboBasedNomadFolder, INomadKuboEventStreamHandler<FolderUpdateEvent>, IModifiableFolder
{
    /// <summary>
    /// A temp folder for caching during read and persisting writes during flush.  
    /// </summary>
    public IModifiableFolder TempCacheFolder { get; init; }
}