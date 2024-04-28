using OwlCore.Storage;
using System.Collections.Generic;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// A kubo-based storage interface for folders.
/// </summary>
/// <remarks>
/// Primarily use to create extension method helpers between file/folder implementations of the generic base classes.
/// </remarks>
public interface IReadOnlyKuboBasedNomadFolder : IReadOnlyKuboBasedNomadStorage, IChildFolder, IMutableFolder
{
    /// <summary>
    /// The items current in this folder.
    /// </summary>
    public List<IStorableChild> Items { get; } 
}