using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Kubo.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A modifiable kubo-based storage interface for files.
/// </summary>
public interface IModifiableKuboNomadFile : INomadKuboEventStreamHandler<FileUpdateEvent>, IReadOnlyKuboNomadFile
{
}