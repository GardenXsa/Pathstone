using MyGame.Core.Common;

namespace MyGame.Core.World;

/// <summary>
/// Structured combat state — the round/turn/initiative tracker for an
/// in-progress encounter. Lives on <see cref="World.Combat"/> (nullable):
/// when null (or <see cref="Active"/> = false) the game is in freeform
/// mode and the GM is freeform-narrating; when active, the GM must
/// respect the turn order exposed in <see cref="TurnOrder"/>.
///
/// <para>
/// The state machine is intentionally minimal: it tracks whose turn it
/// is, but the GM (driven via tools) is still responsible for the actual
/// attack / damage / status calls. The <see cref="GameMaster"/> surfaces
/// a "## БОЙ" block in the system-prompt world-state so the model knows
/// whose turn it is and doesn't act out of order.
/// </para>
///
/// <para>
/// <b>Serialization:</b> plain POCO + System.Text.Json (via
/// <see cref="WorldJson.Options"/>). Round-trips cleanly through save/load
/// because <see cref="Combatant.EntityId"/> uses the
/// <see cref="EntityIdJsonConverter"/> (bare JSON string).
/// </para>
/// </summary>
public sealed class CombatState
{
    /// <summary>
    /// Whether combat is currently in progress. Theoretically redundant
    /// with <see cref="World.Combat"/> being non-null, but kept as an
    /// explicit flag so the GM context block can short-circuit on it
    /// without a null-check, and so a paused / ended state can be
    /// expressed without throwing away the turn order.
    /// </summary>
    public bool Active { get; set; }

    /// <summary>
    /// Current round number (1-based). Incremented when
    /// <see cref="CurrentActorIndex"/> wraps past the end of
    /// <see cref="TurnOrder"/>.
    /// </summary>
    public int Round { get; set; } = 1;

    /// <summary>
    /// Ordered list of combatants in initiative order (highest first).
    /// Dead combatants are removed from this list as they fall, so the
    /// index math stays simple.
    /// </summary>
    public List<Combatant> TurnOrder { get; set; } = new();

    /// <summary>
    /// Index into <see cref="TurnOrder"/> for the combatant whose turn it
    /// currently is. The <c>next_turn</c> tool advances this (wrapping
    /// around + incrementing <see cref="Round"/>).
    /// </summary>
    public int CurrentActorIndex { get; set; }

    /// <summary>
    /// World turn counter (<see cref="World.Turn"/>) snapshot at the
    /// moment combat started — informational, so the UI can show "бой
    /// длится N ходов" if desired.
    /// </summary>
    public long StartedAtTurn { get; set; }
}

/// <summary>
/// One entry in <see cref="CombatState.TurnOrder"/>. Records the entity
/// (player or NPC), their display name (snapshotted at combat start so
/// renames mid-combat don't desync the UI), their rolled initiative, and
/// whether they've already acted this round.
///
/// <para>
/// A <c>record</c> (immutable snapshot) — the engine replaces entries
/// rather than mutating them, so the <c>HasActedThisRound</c> flag
/// updates as a new Combatant value with the same EntityId.
/// </para>
/// </summary>
public sealed record Combatant(EntityId EntityId, string Name, int Initiative, bool HasActedThisRound);
