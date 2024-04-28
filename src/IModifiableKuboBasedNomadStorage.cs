using Ipfs;
using OwlCore.ComponentModel.Nomad;
using OwlCore.Nomad.Storage.Models;
using System;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A modifiable kubo-based storage interface.
/// </summary>
public interface IModifiableKuboBasedNomadStorage : IReadOnlyKuboBasedNomadStorage, IModifiableSharedEventStreamHandler<StorageUpdateEvent, Cid, NomadEventStream, NomadEventStreamEntry>
{
    /// <summary>
    /// Whether to pin content added to Ipfs.
    /// </summary>
    public bool ShouldPin { get; set; }

    /// <summary>
    /// The lifetime of the ipns key containing the local event stream. Your node will need to be online at least once every <see cref="IpnsLifetime"/> to keep the ipns key alive.
    /// </summary>
    public TimeSpan IpnsLifetime { get; set; }

    /// <summary>
    /// The name of an Ipns key containing a Nomad event stream that can be appended and republished to modify the current folder.
    /// </summary>
    public string LocalEventStreamKeyName { get; init; }
}