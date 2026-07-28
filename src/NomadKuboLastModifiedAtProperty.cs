using System;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// Represents a property that can be get or set to indicate the last modified time of a storable instance.
/// </summary>
public sealed class NomadKuboLastModifiedAtOffsetProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleModifiableStorageProperty<DateTimeOffset?>(
        id: owner.Id + "/" + nameof(ILastModifiedAtOffset.LastModifiedAtOffset),
        name: nameof(ILastModifiedAtOffset.LastModifiedAtOffset),
        getter: () => nomadStorableData.LastModifiedAt,
        setter: value => nomadStorableData.LastModifiedAt = value
    ), ILastModifiedAtOffsetProperty
{
}

/// <summary>
/// Represents a property that can be get or set to indicate the last modified time of a storable instance.
/// </summary>
public sealed class NomadKuboLastModifiedAtProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleModifiableStorageProperty<DateTime?>(
        id: owner.Id + "/" + nameof(ILastModifiedAt.LastModifiedAt),
        name: nameof(ILastModifiedAt.LastModifiedAt),
        getter: () => nomadStorableData.LastModifiedAt?.LocalDateTime,
        setter: value => nomadStorableData.LastModifiedAt = value
    ), ILastModifiedAtProperty
{
}

/// <summary>
/// Represents a property that can be get to indicate the last modified time of a storable instance.
/// </summary>
public sealed class NomadKuboReadOnlyLastModifiedAtOffsetProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleStorageProperty<DateTimeOffset?>(
        id: owner.Id + "/" + nameof(ILastModifiedAtOffset.LastModifiedAtOffset),
        name: nameof(ILastModifiedAtOffset.LastModifiedAtOffset),
        getter: () => nomadStorableData.LastModifiedAt
    ), ILastModifiedAtOffsetProperty
{
}

/// <summary>
/// Represents a property that can be get to indicate the last modified time of a storable instance.
/// </summary>
public sealed class NomadKuboReadOnlyLastModifiedAtProperty(IStorable owner, NomadStorableData nomadStorableData)
    : SimpleStorageProperty<DateTime?>(
        id: owner.Id + "/" + nameof(ILastModifiedAt.LastModifiedAt),
        name: nameof(ILastModifiedAt.LastModifiedAt),
        getter: () => nomadStorableData.LastModifiedAt?.LocalDateTime
    ), ILastModifiedAtProperty
{
}
