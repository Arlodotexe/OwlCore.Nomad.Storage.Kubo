using Ipfs;
using OwlCore.Nomad.Storage.Models;

namespace OwlCore.Kubo.Nomad.Storage;

/// <summary>
/// Represents an update event for a file.
/// </summary>
/// <param name="StorableItemId">The Id of the file that was changed.</param>
/// <param name="NewContentId">A Cid that represents immutable content. The same ID should always point to the same content, and different content should point to different Ids.</param>
public record FileUpdateEvent(string StorableItemId, Cid NewContentId) : StorageUpdateEvent(StorableItemId, "file_update");