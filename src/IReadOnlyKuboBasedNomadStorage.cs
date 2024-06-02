using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A shared interface for all read-only kubo based nomad storage.
/// </summary>
public interface IReadOnlyKuboBasedNomadStorage : IReadOnlyNomadKuboEventStreamHandler<StorageUpdateEvent>, IStorableChild
{
}