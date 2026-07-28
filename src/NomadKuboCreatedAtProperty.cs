using System;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// Represents a property that can be get or set to indicate the date and time, and timezone offset that a storable instance was created at.
/// </summary>
public sealed class NomadKuboCreatedAtOffsetProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleModifiableStorageProperty<DateTimeOffset?>(
        id: owner.Id + "/" + nameof(ICreatedAt.CreatedAt),
        name: nameof(ICreatedAtOffset.CreatedAtOffset),
        getter: () => nomadStorableData.CreatedAt,
        setter: value => nomadStorableData.CreatedAt = value
    ), ICreatedAtOffsetProperty
{
}

/// <summary>
/// Represents a property that can be get or set to indicate the date and time that a storable instance was created at.
/// </summary>
public sealed class NomadKuboCreatedAtProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleModifiableStorageProperty<DateTime?>(
        id: owner.Id + "/" + nameof(ICreatedAt.CreatedAt),
        name: nameof(ICreatedAtOffset.CreatedAtOffset),
        getter: () => nomadStorableData.CreatedAt?.LocalDateTime,
        setter: value => nomadStorableData.CreatedAt = value
    ), ICreatedAtProperty
{
}

/// <summary>
/// Represents a property that can be get or set to indicate the date and time, and timezone offset that a storable instance was created at.
/// </summary>
public sealed class NomadKuboReadOnlyCreatedAtOffsetProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleStorageProperty<DateTimeOffset?>(
        id: owner.Id + "/" + nameof(ICreatedAt.CreatedAt),
        name: nameof(ICreatedAtOffset.CreatedAtOffset),
        getter: () => nomadStorableData.CreatedAt
    ), ICreatedAtOffsetProperty
{
}

/// <summary>
/// Represents a property that can be get or set to indicate the date and time that a storable instance was created at.
/// </summary>
public sealed class NomadKuboReadOnlyCreatedAtProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleStorageProperty<DateTime?>(
        id: owner.Id + "/" + nameof(ICreatedAt.CreatedAt),
        name: nameof(ICreatedAtOffset.CreatedAtOffset),
        getter: () => nomadStorableData.CreatedAt?.LocalDateTime
    ), ICreatedAtProperty
{
}
