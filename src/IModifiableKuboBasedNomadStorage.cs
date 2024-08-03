using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A modifiable kubo-based storage interface.
/// </summary>
public interface IModifiableKuboBasedNomadStorage : IReadOnlyKuboBasedNomadStorage, IModifiableNomadKuboEventStreamHandler<StorageUpdateEvent>
{
}