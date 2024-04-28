using Ipfs;
using OwlCore.Storage;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A kubo-based storage interface for files.
/// </summary>
public interface IReadOnlyKuboBasedNomadFile : IReadOnlyKuboBasedNomadStorage, IChildFile
{
    /// <summary>
    /// The <see cref="Cid"/> that represents the content in this file.
    /// </summary>
    public Cid? CurrentContentId { get; set; }
}