using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// Abstract base for every game entity that lives in a <see cref="World"/>.
///
/// Port of the entity-shape from <c>engine/types/index.ts</c>. The TS module
/// didn't have a single <c>Entity</c> base interface — every concrete entity
/// (Character, Building, Location, Quest, ItemInstance) repeated the
/// <c>id</c>/<c>name</c>/<c>description</c> trio inline. The C# port
/// introduces this abstract base so the World can offer a single
/// <c>FindEntity(EntityId)</c> entry point that returns <c>Entity?</c>.
///
/// Entities are MUTABLE classes (not records): HP changes, locations shift,
/// inventories gain/lose items — the engine mutates the live instance. The
/// worklog's "records for immutable data" rule applies to snapshots
/// (<see cref="ItemTemplate"/>, <see cref="StatusEffect"/>,
/// <see cref="QuestObjective"/>, etc.); live world state is class-based.
/// </summary>
public abstract class Entity
{
    /// <summary>Unique identifier for this entity instance.</summary>
    public EntityId Id { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional long-form flavor description.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Type discriminator — useful for debugging and for any future
    /// polymorphic JSON serialization. Set by each concrete subclass ctor.
    /// </summary>
    public string Kind { get; protected init; }

    /// <summary>
    /// Free-form per-entity metadata bag. Used by the world-builder to stash
    /// plan-derived hints (NPC disposition/behavior/role/notes, location
    /// visit state, etc.) and by tools / GM for arbitrary state. Lazy-init
    /// to null to keep entity JSON clean when unused.
    /// </summary>
    public Dictionary<string, object>? Flags { get; set; }

    /// <summary>Protected ctor — only concrete subclasses call this.</summary>
    protected Entity(string kind)
    {
        Kind = kind;
        Id = EntityId.NewId();
    }
}

/// <summary>
/// Status of a quest in the player's log.
/// Port of <c>QuestStatus</c> from <c>engine/types/index.ts</c>.
/// </summary>
public enum QuestStatus
{
    Inactive = 0,
    Active = 1,
    Completed = 2,
    Failed = 3,
}
