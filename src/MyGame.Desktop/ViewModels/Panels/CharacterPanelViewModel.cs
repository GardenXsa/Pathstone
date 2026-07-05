using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Desktop.ViewModels.Panels;

/// <summary>
/// View model for the character sheet panel. Shows the active player's
/// identity, attributes, resources (HP/MP/etc.), equipped gear, skills,
/// background. Read-only — no commands (character editing happens via the
/// GM tool flow, not direct UI manipulation) — EXCEPT for the
/// <see cref="ExportCommand"/> / <see cref="ExportRequested"/> event,
/// which lets the user export this character as a portable
/// <c>CharacterSheet</c> (issue #62). The actual export call (which needs
/// the active save id + player id) is handled by the
/// <see cref="GameViewModel"/>, which subscribes to
/// <see cref="ExportRequested"/> and resolves the
/// <c>CharacterSheetStore</c> from DI.
/// </summary>
public partial class CharacterPanelViewModel : ObservableObject
{
    private Player? _player;

    /// <summary>
    /// Raised when the user clicks the «Экспорт» button. The
    /// <see cref="GameViewModel"/> subscribes and handles the actual
    /// <c>CharacterSheetStore.Export</c> call (it has the save id + active
    /// player). The handler should set <see cref="ExportStatus"/> to a
    /// success/error message so the panel shows the result inline.
    /// </summary>
    public event Action? ExportRequested;

    /// <summary>
    /// Fire <see cref="ExportRequested"/>. Bound to the «Экспорт» button
    /// in the identity section. No-op if no handler is attached (the
    /// panel might be shown outside a game session, e.g. in a future
    /// read-only character viewer).
    /// </summary>
    [RelayCommand]
    private void Export()
    {
        if (_player is null)
        {
            ExportStatus = "Нет активного персонажа для экспорта.";
            return;
        }
        ExportRequested?.Invoke();
    }

    /// <summary>
    /// Last export result message (success path summary or error). Bound
    /// to a small status TextBlock under the «Экспорт» button. Cleared
    /// on the next <see cref="RefreshFromWorld"/> call (so stale messages
    /// don't linger after a turn). Null hides the block.
    /// </summary>
    [ObservableProperty] private string? _exportStatus;

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

        // Clear the export-status toast on every refresh — the next
        // export will set a fresh message.
        ExportStatus = null;
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
