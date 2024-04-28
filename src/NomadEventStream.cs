using Ipfs;
using OwlCore.ComponentModel.Nomad;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// An event stream with event entry content stored on ipfs.
/// </summary>
public record NomadEventStream : EventStream<Cid>;