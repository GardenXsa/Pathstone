using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Desktop.ViewModels.Panels;

/// <summary>
/// View model for the quest panel. Lists active and completed quests,
/// their objectives (with done/pending markers), and rewards. Read-only —
/// quest state changes happen via the GM tool flow (the GM marks
/// objectives done via <c>update_quest</c>).
/// </summary>
public partial class QuestPanelViewModel : ObservableObject
{
    /// <summary>Refresh from the given world.</summary>
    public void RefreshFromWorld(World world)
    {
        ActiveQuests.Clear();
        CompletedQuests.Clear();
        FailedQuests.Clear();

        if (world is null)
        {
            UpdateHasFlags();
            return;
        }

        foreach (var q in world.Quests)
        {
            var row = new QuestRow(q);
            switch (q.Status)
            {
                case QuestStatus.Active:
                    ActiveQuests.Add(row);
                    break;
                case QuestStatus.Completed:
                    CompletedQuests.Add(row);
                    break;
                case QuestStatus.Failed:
                    FailedQuests.Add(row);
                    break;
            }
        }

        UpdateHasFlags();
    }

    private void UpdateHasFlags()
    {
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasAny));
    }

    // ─── Observable collections ──────────────────────────────────────

    public ObservableCollection<QuestRow> ActiveQuests { get; } = new();
    public ObservableCollection<QuestRow> CompletedQuests { get; } = new();
    public ObservableCollection<QuestRow> FailedQuests { get; } = new();

    public bool HasActive => ActiveQuests.Count > 0;
    public bool HasCompleted => CompletedQuests.Count > 0;
    public bool HasFailed => FailedQuests.Count > 0;
    public bool HasAny => HasActive || HasCompleted || HasFailed;
}

/// <summary>One row in the quest list. Wraps a <see cref="Quest"/>.</summary>
public sealed class QuestRow
{
    public QuestRow(Quest q)
    {
        Name = q.Name;
        Description = q.Description ?? "";
        Status = q.Status.ToString();
        Objectives = (q.Objectives ?? new()).Select(o => new QuestObjectiveRow(o)).ToList();
        RewardCurrency = q.Reward?.Currency ?? 0;
        RewardExperience = q.Reward?.Experience ?? 0;
        RewardItemIds = q.Reward?.Items ?? new();
        CompletedObjectives = Objectives.Count(o => o.Done);
        TotalObjectives = Objectives.Count;
    }

    public string Name { get; }
    public string Description { get; }
    public string Status { get; }
    public IReadOnlyList<QuestObjectiveRow> Objectives { get; }
    public int RewardCurrency { get; }
    public int RewardExperience { get; }
    public IReadOnlyList<string> RewardItemIds { get; }
    public int CompletedObjectives { get; }
    public int TotalObjectives { get; }
    public bool HasRewards => RewardCurrency > 0 || RewardExperience > 0 || RewardItemIds.Count > 0;
}

/// <summary>One objective in a quest.</summary>
public sealed class QuestObjectiveRow
{
    public QuestObjectiveRow(QuestObjective o)
    {
        Id = o.Id;
        Description = o.Description;
        Done = o.Done;
    }
    public string Id { get; }
    public string Description { get; }
    public bool Done { get; }
}
