using MyGame.Core.Common;

namespace MyGame.Core.Saves;

/// <summary>
/// A standalone, exportable character sheet — the BG3-style "your
/// character travels with you between hosts" feature. NOT tied to a
/// World: it's a self-contained snapshot of one player character that
/// can be exported from a save, sent to another install, and imported
/// into a new game there.
/// </summary>
/// <remarks>
/// <b>Design choice — record vs. class:</b> a CharacterSheet is an
/// immutable snapshot (frozen at export time). It doesn't track live
/// HP changes — those happen on the World's Player entity, not on the
/// sheet. The sheet is just the "starting build" of the character:
/// race, class, attributes, level, inventory, equipment. When the sheet
/// is imported into a new world, the world's <c>SpawnPlayer</c> flow
/// materializes a live Player from it.
/// </remarks>
public sealed record CharacterSheet
{
    /// <summary>
    /// Stable character id (Guid, formatted as <c>char_{Guid:N}</c> on
    /// disk — see <see cref="CharacterSheetStore"/>). Generated at
    /// export time; reused if the same player is re-exported from the
    /// same source save (so a re-export overwrites rather than
    /// duplicating).
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name (matches the source player's Name).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional flavor label (race / species / people).</summary>
    public string? Race { get; init; }

    /// <summary>Optional flavor label (class / archetype / role).</summary>
    public string? Class { get; init; }

    /// <summary>Player-authored background text.</summary>
    public string? Background { get; init; }

    /// <summary>
    /// Attribute values keyed by ruleset attribute key
    /// (<c>str</c>, <c>dex</c>, <c>con</c>, …). Frozen at export time —
    /// doesn't include status-effect modifiers.
    /// </summary>
    public Dictionary<string, int> Attributes { get; init; } = new();

    /// <summary>
    /// Resource pools (hp, mana, …) keyed by ruleset resource key. Frozen
    /// at the exported values — when imported into a new world, the
    /// engine recomputes maxima from the ruleset's formulas.
    /// </summary>
    public Dictionary<string, int> Resources { get; init; } = new();

    /// <summary>Character level (ruleset-driven progression).</summary>
    public int Level { get; init; } = 1;

    /// <summary>XP total (ruleset-driven progression).</summary>
    public int Xp { get; init; }

    /// <summary>
    /// Item template ids in the character's inventory at export time.
    /// Stored as template ids (not full item instances) so the import
    /// path can re-materialize fresh instances from the destination
    /// world's content registry.
    /// </summary>
    public IReadOnlyList<string> InventoryItemIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Equipment slots → item template ids (e.g.
    /// <c>{ "weapon": "wpn_shortsword", "armor": "arm_leather" }</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string> EquippedItemIds { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Proficient skill labels (free-form).</summary>
    public IReadOnlyList<string> ProficientSkills { get; init; } = Array.Empty<string>();

    /// <summary>Movement speed (ruleset-defined; D&amp;D default is 30 ft).</summary>
    public int? Speed { get; init; }

    /// <summary>
    /// Id of the save this sheet was exported from. Null if the sheet
    /// was created from scratch (e.g. the import-side "new character"
    /// flow that starts from a sheet template). Useful for the UI to
    /// show "Exported from: &lt;save name&gt;" and for the import path
    /// to detect "are we re-importing into the same save?".
    /// </summary>
    public string? SourceSaveId { get; init; }

    /// <summary>
    /// EntityId (as string) of the player entity this sheet was
    /// exported from. Stored as a string rather than
    /// <see cref="EntityId"/> so the sheet JSON is portable across
    /// installs that might not have the EntityId converter loaded (e.g.
    /// a future web-based viewer).
    /// </summary>
    public string? SourcePlayerId { get; init; }

    /// <summary>UTC timestamp when this sheet was exported.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp of the last re-export (same player, same source).</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
