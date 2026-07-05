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
        Level = p.Level?.ToString() ?? "1";
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

        ProficientSkills.Clear();
        if (p.ProficientSkills is not null)
            foreach (var s in p.ProficientSkills)
                ProficientSkills.Add(s);

        Currency = p.Inventory.Currency;
        InventoryCount = p.Inventory.Items.Count;
        OnPropertyChanged(nameof(HasAttributes));
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(HasEquipped));
        OnPropertyChanged(nameof(HasSkills));
    }

    private void Clear()
    {
        Name = Race = Class = Level = Background = Speed = "—";
        Attributes.Clear();
        Resources.Clear();
        Equipped.Clear();
        ProficientSkills.Clear();
        Currency = 0;
        InventoryCount = 0;
        OnPropertyChanged(nameof(HasAttributes));
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(HasEquipped));
        OnPropertyChanged(nameof(HasSkills));
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

    public ObservableCollection<AttributeRow> Attributes { get; } = new();
    public ObservableCollection<ResourceRow> Resources { get; } = new();
    public ObservableCollection<EquippedRow> Equipped { get; } = new();
    public ObservableCollection<string> ProficientSkills { get; } = new();

    public bool HasAttributes => Attributes.Count > 0;
    public bool HasResources => Resources.Count > 0;
    public bool HasEquipped => Equipped.Count > 0;
    public bool HasSkills => ProficientSkills.Count > 0;
}

/// <summary>One row in the attributes grid (e.g. STR=16).</summary>
public sealed record AttributeRow(string Name, int Value);

/// <summary>One row in the resources list (e.g. HP=12/12, MP=5/5).</summary>
public sealed record ResourceRow(string Name, int Value);

/// <summary>One row in the equipped-gear list (slot → item name).</summary>
public sealed record EquippedRow(string Slot, string ItemName, string Description);
