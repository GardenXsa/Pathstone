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

    /// <summary>Quests that must be completed before this one can start.</summary>
    public List<EntityId>? PrerequisiteQuestIds { get; set; }
}
