using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// A transient status effect on a character (poisoned, blessed, frightened, …).
///
/// Port of <c>StatusEffect</c> from <c>engine/types/index.ts</c>. Records are
/// used here because an effect, once applied, is treated as an immutable
/// snapshot — the engine replaces it (rather than mutating it in place) when
/// the duration ticks down.
/// </summary>
public sealed record StatusEffect
{
    /// <summary>Unique per-effect instance id.</summary>
    public required EntityId Id { get; init; }

    /// <summary>Optional template id (if this effect came from a template).</summary>
    public string? TemplateId { get; init; }

    /// <summary>Display name, e.g. «Отравление».</summary>
    public required string Name { get; init; }

    /// <summary>What the effect does, flavor + mechanics.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Rounds remaining. -1 = until dispelled. 0 = expired (will be reaped
    /// on the next tick).
    /// </summary>
    public int Duration { get; init; }

    /// <summary>Optional stack count (e.g. 3 stacks of poison).</summary>
    public int? Stacks { get; init; }

    /// <summary>
    /// Flat modifiers applied to attributes while this effect is active,
    /// keyed by attribute key (e.g. <c>{ "str": -2 }</c>).
    /// </summary>
    public Dictionary<string, int>? Modifiers { get; init; }

    /// <summary>
    /// Per-turn damage applied at the start of the affected character's turn.
    /// </summary>
    public PerTurnDamage? DamagePerTurn { get; init; }

    /// <summary>Optional UI icon hint (sprite name / emoji / etc.).</summary>
    public string? Icon { get; init; }
}

/// <summary>
/// Per-turn damage dealt by a status effect (e.g. <c>1d6</c> poison).
/// </summary>
public sealed record PerTurnDamage(string Dice, string Type);
