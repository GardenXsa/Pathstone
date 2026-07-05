using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Desktop.ViewModels.Panels;

/// <summary>
/// View model for the character sheet panel. Shows the active player's
/// identity, attributes, resources (HP/MP/etc.), equipped gear, skills,
/// background. Read-only — no commands (character editing happens via the
/// GM tool flow, not direct UI manipulation).
/// </summary>
public partial class CharacterPanelViewModel : ObservableObject
{
    private Player? _player;

    /// <summary>
    /// Refresh the panel from the given world. Reads the active player
    /// (or the first player if no active is set) and rebuilds all
    /// observable collections.
    /// </summary>
    public void RefreshFromWorld(World world)
    {
        if (world is null) { Clear(); return; }
        _player = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (_player is null) { Clear(); return; }

        var p = _player;
        Name = p.Name;
        Race = p.Race ?? "—";
        Class = p.Class ?? "—";
        var lvl = p.Level ?? 1;
        Level = lvl.ToString();
        // XP / leveling. NextLevelXp uses the simple "level * 100" curve.
        // The award_xp tool handles the actual XP grant + level-up math;
        // the panel just reflects the current totals.
        Experience = p.Experience ?? 0;
        NextLevelXp = lvl * 100;
        Background = p.Background ?? "—";
        Speed = p.Speed?.ToString() ?? "30";

        Attributes.Clear();
        foreach (var kv in p.Attributes.OrderBy(kv => kv.Key))
            Attributes.Add(new AttributeRow(kv.Key, kv.Value));

        Resources.Clear();
        foreach (var kv in p.Resources.OrderBy(kv => kv.Key))
            Resources.Add(new ResourceRow(kv.Key, kv.Value));

        Equipped.Clear();
        foreach (var kv in p.Equipped)
            Equipped.Add(new EquippedRow(kv.Key, kv.Value.Name, kv.Value.Description ?? ""));

        // Active status effects. Populated from the character's Effects list.
        // Each effect is rendered as a row in the «Эффекты» section (below
        // Equipped, above Skills). Hidden entirely when no effects are active.
        Effects.Clear();
        if (p.Effects is not null)
        {
            foreach (var eff in p.Effects)
            {
                Effects.Add(new StatusEffectRow(
                    eff.Name,
                    eff.Description ?? "",
                    eff.Duration));
            }
        }

        ProficientSkills.Clear();
        if (p.ProficientSkills is not null)
            foreach (var s in p.ProficientSkills)
                ProficientSkills.Add(s);

        Currency = p.Inventory.Currency;
        InventoryCount = p.Inventory.Items.Count;
        OnPropertyChanged(nameof(HasAttributes));
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(HasEquipped));
        OnPropertyChanged(nameof(HasEffects));
        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(XpProgress));
    }

    private void Clear()
    {
        Name = Race = Class = Level = Background = Speed = "—";
        Experience = 0;
        NextLevelXp = 100;
        Attributes.Clear();
        Resources.Clear();
        Equipped.Clear();
        Effects.Clear();
        ProficientSkills.Clear();
        Currency = 0;
        InventoryCount = 0;
        OnPropertyChanged(nameof(HasAttributes));
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(HasEquipped));
        OnPropertyChanged(nameof(HasEffects));
        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(XpProgress));
    }

    // ─── Observable properties ───────────────────────────────────────

    [ObservableProperty] private string _name = "—";
    [ObservableProperty] private string _race = "—";
    [ObservableProperty] private string _class = "—";
    [ObservableProperty] private string _level = "1";
    [ObservableProperty] private string _background = "—";
    [ObservableProperty] private string _speed = "30";
    [ObservableProperty] private int _currency;
    [ObservableProperty] private int _inventoryCount;

    /// <summary>Total XP the player has accumulated so far.</summary>
    [ObservableProperty] private int _experience;

    /// <summary>XP threshold to reach the next level (level * 100).</summary>
    [ObservableProperty] private int _nextLevelXp = 100;

    /// <summary>Progress 0-100 toward the next level, for the XP bar.</summary>
    public double XpProgress => NextLevelXp > 0
        ? System.Math.Min(100.0, (Experience / (double)NextLevelXp) * 100.0)
        : 0;

    public ObservableCollection<AttributeRow> Attributes { get; } = new();
    public ObservableCollection<ResourceRow> Resources { get; } = new();
    public ObservableCollection<EquippedRow> Equipped { get; } = new();
    public ObservableCollection<StatusEffectRow> Effects { get; } = new();
    public ObservableCollection<string> ProficientSkills { get; } = new();

    public bool HasAttributes => Attributes.Count > 0;
    public bool HasResources => Resources.Count > 0;
    public bool HasEquipped => Equipped.Count > 0;
    public bool HasEffects => Effects.Count > 0;
    public bool HasSkills => ProficientSkills.Count > 0;
}

/// <summary>One row in the attributes grid (e.g. STR=16).</summary>
public sealed record AttributeRow(string Name, int Value);

/// <summary>One row in the resources list (e.g. HP=12/12, MP=5/5).</summary>
public sealed record ResourceRow(string Name, int Value);

/// <summary>One row in the equipped-gear list (slot → item name).</summary>
public sealed record EquippedRow(string Slot, string ItemName, string Description);

/// <summary>
/// One active status effect on the character. Duration &lt; 0 means
/// «until dispelled»; 0 means «expired» (will be reaped on the next tick);
/// positive values are rounds remaining.
/// </summary>
public sealed record StatusEffectRow(string Name, string Description, int Duration);
