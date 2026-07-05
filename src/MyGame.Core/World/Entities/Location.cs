using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// One exit edge in the location graph: from the containing location to
/// <see cref="To"/>, labeled with <see cref="Direction"/> (e.g. «север», «к
/// таверне»), optionally locked. Port of the inline exit shape from
/// <c>engine/types/index.ts</c>.
/// </summary>
/// <remarks>
/// <b>Phantom exits (issue #20 — chunked generation):</b> when
/// <see cref="To"/> is <see cref="EntityId.Empty"/> AND
/// <see cref="ToName"/> is non-empty, the exit is a "phantom" exit
/// pointing to a not-yet-generated cold-region location (or a cold
/// region's name). The world panel renders the destination as
/// <see cref="ToName"/>; the GameViewModel's travel handler detects the
/// phantom exit and triggers region generation via
/// <c>WorldBuilderOrchestrator.GenerateRegionAsync</c>.
/// </remarks>
public sealed class LocationExit
{
    /// <summary>
    /// Destination location id. <see cref="EntityId.Empty"/> for a
    /// phantom exit (cold-region boundary) — see <see cref="ToName"/>.
    /// </summary>
    public EntityId To { get; set; }

    /// <summary>
    /// Destination location name. Set only for phantom exits (issue #20)
    /// — when <see cref="To"/> is empty. For normal exits, this is null
    /// and the destination is resolved via <see cref="To"/> +
    /// <c>World.GetLocation(To).Name</c>.
    /// </summary>
    public string? ToName { get; set; }

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
