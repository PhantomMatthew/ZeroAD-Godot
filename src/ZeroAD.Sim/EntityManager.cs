using System;
using System.Collections.Generic;

namespace ZeroAD.Sim;

/// <summary>
/// Entity ID. First valid entity ID is 1; 0 is invalid.
/// IDs &gt;= <see cref="FirstLocalEntity"/> are local (not serialized/networked).
/// Matches <c>entity_id_t</c> in <c>source/simulation2/Simulation2.h</c>.
/// </summary>
public readonly struct EntityId : IEquatable<EntityId>
{
    public const uint Invalid = 0;
    public const uint FirstLocalEntity = (1u << 24) + 1;

    public readonly uint Value;

    public EntityId(uint value)
    {
        if (value == Invalid)
            throw new ArgumentException("Entity ID 0 is reserved for 'invalid'", nameof(value));
        Value = value;
    }

    public bool IsValid => Value != Invalid;
    public bool IsLocal => Value >= FirstLocalEntity;

    public bool Equals(EntityId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is EntityId e && Equals(e);
    public override int GetHashCode() => (int)Value;
    public override string ToString() => $"#{Value}";

    public static bool operator ==(EntityId a, EntityId b) => a.Value == b.Value;
    public static bool operator !=(EntityId a, EntityId b) => a.Value != b.Value;
}

public sealed class EntityManager
{
    private uint _nextEntityId = 1;
    private uint _nextLocalEntityId = EntityId.FirstLocalEntity;

    public EntityId AllocateEntity()
    {
        return new EntityId(_nextEntityId++);
    }

    public EntityId AllocateLocalEntity()
    {
        return new EntityId(_nextLocalEntityId++);
    }

    public uint NextEntityId => _nextEntityId;

    public void Reset()
    {
        _nextEntityId = 1;
        _nextLocalEntityId = EntityId.FirstLocalEntity;
    }

    public void RestoreNextEntityId(uint id)
    {
        _nextEntityId = id;
    }
}
