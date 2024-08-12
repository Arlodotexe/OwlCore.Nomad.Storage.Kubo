using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Kubo.Models;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A modifiable kubo-based storage interface for files.
/// </summary>
public interface IModifiableKuboBasedNomadFile : IReadOnlyKuboBasedNomadFile, IModifiableNomadKuboEventStreamHandler<FileUpdateEvent>
{
}