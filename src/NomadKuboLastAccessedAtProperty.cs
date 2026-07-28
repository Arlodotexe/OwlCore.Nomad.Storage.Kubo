using System;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// Represents a property that can be get or set to indicate the date and time, and timezone offset that a storable instance was last accessed at.
/// </summary>
public sealed class NomadKuboLastAccessedAtOffsetProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleModifiableStorageProperty<DateTimeOffset?>(
        id: owner.Id + "/" + nameof(ILastAccessedAtOffset.LastAccessedAtOffset),
        name: nameof(ILastAccessedAtOffset.LastAccessedAtOffset),
        getter: () => nomadStorableData.LastAccessedAt,
        setter: value => nomadStorableData.LastAccessedAt = value
    ), ILastAccessedAtOffsetProperty
{
}

/// <summary>
/// Represents a property that can be get or set to indicate the date that a storable instance was last accessed at.
/// </summary>
public sealed class NomadKuboLastAccessedAtProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleModifiableStorageProperty<DateTime?>(
        id: owner.Id + "/" + nameof(ILastAccessedAt.LastAccessedAt),
        name: nameof(ILastAccessedAt.LastAccessedAt),
        getter: () => nomadStorableData.LastAccessedAt?.LocalDateTime,
        setter: value => nomadStorableData.LastAccessedAt = value
    ), ILastAccessedAtProperty
{
}

/// <summary>
/// Represents a property that can be get to indicate the date and time, and timezone offset that a storable instance was last accessed at.
/// </summary>
public sealed class NomadKuboReadOnlyLastAccessedAtOffsetProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleStorageProperty<DateTimeOffset?>(
        id: owner.Id + "/" + nameof(ILastAccessedAtOffset.LastAccessedAtOffset),
        name: nameof(ILastAccessedAtOffset.LastAccessedAtOffset),
        getter: () => nomadStorableData.LastAccessedAt
    ), ILastAccessedAtOffsetProperty
{
}

/// <summary>
/// Represents a property that can be get to indicate the date that a storable instance was last accessed at.
/// </summary>
public sealed class NomadKuboReadOnlyLastAccessedAtProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleStorageProperty<DateTime?>(
        id: owner.Id + "/" + nameof(ILastAccessedAt.LastAccessedAt),
        name: nameof(ILastAccessedAt.LastAccessedAt),
        getter: () => nomadStorableData.LastAccessedAt?.LocalDateTime
    ), ILastAccessedAtProperty
{
}
