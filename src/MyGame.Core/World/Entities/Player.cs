namespace MyGame.Core.World.Entities;

/// <summary>
/// A player-controlled character. Port of <c>Player</c> from
/// <c>engine/types/index.ts</c>.
/// </summary>
public sealed class Player : Character
{
    /// <summary>Create a new blank player.</summary>
    public Player() : base("player") { }

    /// <summary>XP total, if the ruleset uses level-xp progression.</summary>
    public int? Experience { get; set; }

    /// <summary>Player-authored background text.</summary>
    public string? Background { get; set; }

    /// <summary>Player-authored notes (private to the player).</summary>
    public string? Notes { get; set; }
}
