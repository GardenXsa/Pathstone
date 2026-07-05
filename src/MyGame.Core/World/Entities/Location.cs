using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// One exit edge in the location graph: from the containing location to
/// <see cref="To"/>, labeled with <see cref="Direction"/> (e.g. «север», «к
/// таверне»), optionally locked. Port of the inline exit shape from
/// <c>engine/types/index.ts</c>.
/// </summary>
public sealed class LocationExit
{
    /// <summary>Destination location id.</summary>
    public EntityId To { get; set; }

    /// <summary>Direction label (display string).</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>If true, the exit is locked (player cannot traverse it).</summary>
    public bool? Locked { get; set; }
}

/// <summary>
/// A location node in the world graph. Port of <c>Location</c> from
/// <c>engine/types/index.ts</c>.
/// </summary>
public sealed class Location : Entity
{
    /// <summary>Create a new blank location.</summary>
    public Location() : base("location") { }

    /// <summary>
    /// Terrain type — free-form string defined by the ruleset
    /// (<c>plains</c>, <c>forest</c>, <c>mountain</c>, …).
    /// </summary>
    public string Terrain { get; set; } = "plains";

    /// <summary>World-grid X coordinate (optional).</summary>
    public int? X { get; set; }

    /// <summary>World-grid Y coordinate (optional).</summary>
    public int? Y { get; set; }

    /// <summary>Outgoing exits to other locations (graph edges).</summary>
    public List<LocationExit> Exits { get; set; } = new();

    /// <summary>NPC ids present at this location.</summary>
    public List<EntityId> Npcs { get; set; } = new();

    /// <summary>Loose item ids on the ground at this location.</summary>
    public List<EntityId> Items { get; set; } = new();

    /// <summary>Building ids at this location.</summary>
    public List<EntityId> Buildings { get; set; } = new();

    /// <summary>Danger level (0-10) — drives random encounter rolls.</summary>
    public int Danger { get; set; }

    /// <summary>Whether the player has visited this location.</summary>
    public bool Visited { get; set; }

    /// <summary>Whether this location is on the player's discovered map.</summary>
    public bool Discovered { get; set; }

    // NOTE: Location inherits Flags from Entity. The per-location flags
    // (visited-by-faction, locked-reason, etc.) go in Entity.Flags.
}
