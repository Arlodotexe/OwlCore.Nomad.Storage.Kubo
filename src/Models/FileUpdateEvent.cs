using Ipfs;

namespace OwlCore.Nomad.Storage.Kubo.Models;

/// <summary>
/// Represents an update event for a file.
/// </summary>
/// <param name="StorableItemId">The id of the file that was changed.</param>
/// <param name="NewContentId">A Cid that represents immutable content. The same ID should always point to the same content, and different content should point to different Ids.</param>
/// <param name="EventId">A unique identifier for this type of event.</param>
public record FileUpdateEvent(string StorableItemId, Cid NewContentId, string EventId = nameof(FileUpdateEvent));