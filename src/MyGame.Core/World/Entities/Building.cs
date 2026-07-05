using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// A building or landmark within a location. Port of <c>Building</c> from
/// <c>engine/types/index.ts</c>.
/// </summary>
public sealed class Building : Entity
{
    /// <summary>Create a new blank building.</summary>
    public Building() : base("building") { }

    /// <summary>Template this building was spawned from (if any).</summary>
    public string? TemplateId { get; set; }

    /// <summary>
    /// Building type — free-form string defined by the ruleset
    /// (<c>tavern</c>, <c>shop</c>, <c>temple</c>, <c>dungeon</c>, …).
    /// </summary>
    public string Type { get; set; } = "house";

    /// <summary>Location this building sits in.</summary>
    public EntityId LocationId { get; set; }

    /// <summary>NPC ids occupying this building.</summary>
    public List<EntityId> Occupants { get; set; } = new();

    /// <summary>Interior location id (if the building is enterable).</summary>
    public EntityId? InteriorLocationId { get; set; }

    /// <summary>Whether the player can enter the building.</summary>
    public bool Enterable { get; set; }

    /// <summary>Whether the building is currently locked.</summary>
    public bool? Locked { get; set; }

    /// <summary>If this building is a shop, the shopkeeper NPC id.</summary>
    public EntityId? ShopkeeperNpcId { get; set; }
}
