using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Desktop.ViewModels.Panels;

/// <summary>
/// View model for the quest panel. Lists active, completed, and failed
/// quests with their objectives (with done/pending markers) and rewards.
/// Read-only — quest state changes happen via the GM tool flow (the GM
/// marks objectives done via <c>update_quest</c>).
///
/// <para>
/// <b>Polish (issue #69):</b> the panel supports:
/// <list type="bullet">
///   <item>Search box — filters all three sections by name/description
///     substring (case-insensitive). Bound to <see cref="SearchText"/>;
///     re-filtering happens automatically on change.</item>
///   <item>Sort dropdown — orders the active-quests section by creation
///     order (<see cref="QuestSortMode.Newness"/>), name
///     (<see cref="QuestSortMode.Alphabetical"/>), or giver's location
///     name (<see cref="QuestSortMode.Location"/>). Completed/Failed
///     sections stay in creation order (they're already collapsed
///     summaries; sorting adds no value there).</item>
///   <item>Collapse/expand all — two small buttons that fold/unfold every
///     active quest row. Each row has its own <see cref="QuestRow.IsExpanded"/>
///     flag so the user can also toggle individually by clicking the row
///     header.</item>
/// </list>
/// </para>
/// </summary>
public partial class QuestPanelViewModel : ObservableObject
{
    // Source-of-truth quest rows in creation order. ApplyFilter copies
    // (with optional search filter + sort) into the observable collections
    // the View binds to. We keep the source separate so re-filtering on
    // SearchText / SortMode change doesn't require a full RefreshFromWorld
    // (which would blow away IsExpanded state on every keystroke).
    private readonly List<QuestRow> _allActive = new();
    private readonly List<QuestRow> _allCompleted = new();
    private readonly List<QuestRow> _allFailed = new();

    /// <summary>Refresh from the given world.</summary>
    public void RefreshFromWorld(World world)
    {
        _allActive.Clear();
        _allCompleted.Clear();
        _allFailed.Clear();

        if (world is not null)
        {
            foreach (var q in world.Quests)
            {
                var locationName = ResolveGiverLocationName(world, q);
                var row = new QuestRow(q, locationName);
                switch (q.Status)
                {
                    case QuestStatus.Active:
                        _allActive.Add(row);
                        break;
                    case QuestStatus.Completed:
                        _allCompleted.Add(row);
                        break;
                    case QuestStatus.Failed:
                        _allFailed.Add(row);
                        break;
                }
            }
        }

        ApplyFilter();
    }

    /// <summary>
    /// Rebuild the observable collections from the source lists using the
    /// current <see cref="SearchText"/> + <see cref="SortMode"/>. Called
    /// on every SearchText / SortMode change (via the partial OnChanged
    /// hooks) and at the end of <see cref="RefreshFromWorld"/>.
    /// </summary>
    private void ApplyFilter()
    {
        ApplyFilter(_allActive, ActiveQuests, sort: true);
        ApplyFilter(_allCompleted, CompletedQuests, sort: false);
        ApplyFilter(_allFailed, FailedQuests, sort: false);
        UpdateHasFlags();
    }

    private void ApplyFilter(List<QuestRow> source, ObservableCollection<QuestRow> target, bool sort)
    {
        IEnumerable<QuestRow> rows = source;

        // Search filter — case-insensitive substring on name + description.
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            rows = rows.Where(r =>
                (r.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Sort — only applied to the active section (completed/failed are
        // short summary rows in creation order; sorting adds noise).
        if (sort)
        {
            rows = SortMode switch
            {
                QuestSortMode.Alphabetical =>
                    rows.OrderBy(r => r.Name ?? "", StringComparer.OrdinalIgnoreCase),
                QuestSortMode.Location =>
                    // Quests without a known giver location sort LAST (the
                    // "~~" sentinel sorts after any letter). Stable within
                    // the same location by name as a tiebreaker.
                    rows.OrderBy(r => string.IsNullOrEmpty(r.LocationName) ? "~~" : r.LocationName,
                                 StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Name ?? "", StringComparer.OrdinalIgnoreCase),
                _ => rows, // Newness = preserve creation order
            };
        }

        target.Clear();
        foreach (var r in rows) target.Add(r);
    }

    /// <summary>
    /// Resolve the location name of the NPC that gave this quest. Used by
    /// the «По локации» sort mode. Returns null for quests with no giver
    /// or whose giver can't be located in the world (shouldn't happen in
    /// normal play, but defensive — saves can drift).
    /// </summary>
    private static string? ResolveGiverLocationName(World world, Quest q)
    {
        if (q.GiverNpcId is null) return null;
        var npc = world.GetNpc(q.GiverNpcId.Value);
        if (npc is null) return null;
        var loc = world.GetLocation(npc.LocationId);
        return loc?.Name;
    }

    private void UpdateHasFlags()
    {
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasAny));
    }

    // ─── Search + sort state ─────────────────────────────────────────

    /// <summary>
    /// Search text — filters all three sections by name/description
    /// substring (case-insensitive). Empty string shows all quests.
    /// Re-filtering is automatic via <see cref="OnSearchTextChanged"/>.
    /// </summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>
    /// Sort mode for the active-quests section. Bound to the ComboBox
    /// via <see cref="SelectedSortOption"/>.
    /// </summary>
    [ObservableProperty] private QuestSortMode _sortMode = QuestSortMode.Newness;

    /// <summary>
    /// Re-filter on search text change. Fires whenever the user types in
    /// the search box (the binding is two-way, so this runs on every
    /// keystroke after a small delay baked into the TextBox binding).
    /// </summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Re-sort when the dropdown selection changes.</summary>
    partial void OnSortModeChanged(QuestSortMode value) => ApplyFilter();

    /// <summary>
    /// Sort options shown in the dropdown. Each entry pairs a Russian
    /// label with a <see cref="QuestSortMode"/>. The ComboBox binds
    /// SelectedItem to <see cref="SelectedSortOption"/>.
    /// </summary>
    public IReadOnlyList<QuestSortOption> SortOptions { get; } = new[]
    {
        new QuestSortOption("По новизне", QuestSortMode.Newness),
        new QuestSortOption("По алфавиту", QuestSortMode.Alphabetical),
        new QuestSortOption("По локации", QuestSortMode.Location),
    };

    /// <summary>
    /// Currently-selected sort option. Getter finds the option matching
    /// <see cref="SortMode"/>; setter updates <see cref="SortMode"/> (which
    /// fires <see cref="OnSortModeChanged"/> and triggers re-sort).
    /// </summary>
    public QuestSortOption? SelectedSortOption
    {
        get => SortOptions.FirstOrDefault(o => o.Mode == SortMode);
        set
        {
            if (value is not null && value.Mode != SortMode)
            {
                SortMode = value.Mode;
                OnPropertyChanged(nameof(SelectedSortOption));
            }
        }
    }

    // ─── Collapse/expand all ─────────────────────────────────────────

    /// <summary>
    /// Collapse every active quest row (sets <see cref="QuestRow.IsExpanded"/>
    /// = false). No-op on the completed/failed sections — they're already
    /// single-line summaries without an expand state.
    /// </summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var r in _allActive) r.IsExpanded = false;
    }

    /// <summary>Expand every active quest row.</summary>
    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var r in _allActive) r.IsExpanded = true;
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

/// <summary>
/// Sort mode for the active-quests section of <see cref="QuestPanelViewModel"/>.
/// </summary>
public enum QuestSortMode
{
    /// <summary>Creation order (the order quests appear in <c>world.Quests</c>).</summary>
    Newness,
    /// <summary>Alphabetical by quest name (case-insensitive).</summary>
    Alphabetical,
    /// <summary>By the giver NPC's location name (case-insensitive).</summary>
    Location,
}

/// <summary>
/// One entry in the sort dropdown. Pairs a Russian display label with a
/// <see cref="QuestSortMode"/> value.
/// </summary>
public sealed record QuestSortOption(string Label, QuestSortMode Mode);

/// <summary>
/// One row in the quest list. Wraps a <see cref="Quest"/>. Observable so
/// <see cref="IsExpanded"/> can be toggled by the row header click and
/// the collapse/expand-all commands.
/// </summary>
public sealed partial class QuestRow : ObservableObject
{
    public QuestRow(Quest q, string? locationName = null)
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
        LocationName = locationName;
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
    public string? LocationName { get; }
    public bool HasRewards => RewardCurrency > 0 || RewardExperience > 0 || RewardItemIds.Count > 0;
    public bool HasLocation => !string.IsNullOrEmpty(LocationName);

    private bool _isExpanded = true;
    /// <summary>
    /// Whether this row's full description + objectives are visible. True
    /// by default (so a fresh refresh shows everything); toggled by
    /// clicking the row header chevron or by the collapse/expand-all
    /// commands on <see cref="QuestPanelViewModel"/>.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Toggle <see cref="IsExpanded"/>. Bound to the row header Button's
    /// Command (generated as <c>ToggleExpandedCommand</c>) so a click
    /// anywhere on the header folds/unfolds the row.
    /// </summary>
    [RelayCommand]
    public void ToggleExpanded() => IsExpanded = !IsExpanded;
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
