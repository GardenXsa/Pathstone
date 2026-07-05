using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// One objective in a quest's checklist. Port of <c>QuestObjective</c>
/// from <c>engine/types/index.ts</c>.
/// </summary>
public sealed class QuestObjective
{
    /// <summary>Stable id (used to mark this objective done via complete_objective).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Player-facing description of what to do.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether this objective is complete.</summary>
    public bool Done { get; set; }
}

/// <summary>
/// Reward granted on quest completion. Port of <c>Quest.reward</c> from
/// <c>engine/types/index.ts</c>.
/// </summary>
public sealed class QuestReward
{
    /// <summary>Currency amount (gold/credits/…).</summary>
    public int? Currency { get; set; }

    /// <summary>XP amount.</summary>
    public int? Experience { get; set; }

    /// <summary>Item template ids granted on completion.</summary>
    public List<string>? Items { get; set; }

    /// <summary>Legacy alias for <see cref="Currency"/> (older saves may carry gold).</summary>
    public int? Gold { get; set; }
}

/// <summary>
/// A quest in the player's log. Port of <c>Quest</c> from
/// <c>engine/types/index.ts</c>. The TS <c>title</c>/<c>description</c> map
/// to the inherited <see cref="Entity.Name"/>/<see cref="Entity.Description"/>.
/// </summary>
public sealed class Quest : Entity
{
    /// <summary>Create a new blank quest.</summary>
    public Quest() : base("quest") { }

    /// <summary>NPC who gave this quest (if any).</summary>
    public EntityId? GiverNpcId { get; set; }

    /// <summary>Quest status.</summary>
    public QuestStatus Status { get; set; } = QuestStatus.Inactive;

    /// <summary>Checklist of objectives.</summary>
    public List<QuestObjective> Objectives { get; set; } = new();

    /// <summary>Reward granted on completion.</summary>
    public QuestReward? Reward { get; set; }

    /// <summary>
    /// Reward waiting to be claimed by the player (issue #70). When the
    /// GM calls <c>update_quest</c> with action <c>complete</c>, the
    /// quest's <see cref="Reward"/> is MOVED here (rather than granted
    /// inline as before). The player must then click «Получить награду»
    /// in the Quest panel to actually receive the currency / XP / items,
    /// at which point this field is cleared (set to null).
    ///
    /// <para>
    /// Null for: quests that haven't been completed yet, quests that
    /// were completed before issue #70 (legacy auto-grant), and quests
    /// whose reward the player has already claimed. The Quest panel
    /// shows the «Получить награду» button only when this field is
    /// non-null.
    /// </para>
    ///
    /// <para>
    /// <b>Backward compatibility:</b> old saves don't have this field —
    /// they load with <c>null</c>, which is correct (either the quest
    /// is incomplete and the reward is in <see cref="Reward"/>, or it's
    /// complete and the reward was already auto-granted under the old
    /// behavior). No migration needed.
    /// </para>
    /// </summary>
    public QuestReward? UnclaimedRewards { get; set; }

    /// <summary>Quests that must be completed before this one can start.</summary>
    public List<EntityId>? PrerequisiteQuestIds { get; set; }
}
