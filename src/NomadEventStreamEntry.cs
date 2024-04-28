using Ipfs;
using OwlCore.ComponentModel.Nomad;

namespace OwlCore.Kubo.Nomad;

/// <summary>
/// Represents a single entry in an event stream and the data needed to reconstruct it.
/// </summary>
public record NomadEventStreamEntry : LinkedEventStreamEntry<Cid>
{
    /// <summary>
    /// The ID of the peer that locally synced this entry.
    /// </summary>
    public required MultiHash PeerId { get; init; }
}
