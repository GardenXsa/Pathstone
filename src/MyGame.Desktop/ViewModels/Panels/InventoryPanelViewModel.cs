using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Desktop.ViewModels.Panels;

/// <summary>
/// View model for the inventory panel. Lists the active player's carried
/// items (with quantity, weight, value, rarity), the equipped gear, and
/// the carrying-capacity bar. Item actions (use/equip/drop) are surfaced
/// as commands that emit <see cref="ItemActionRequested"/> events the
/// GameViewModel can wire to the GM or to direct world mutation.
/// </summary>
public partial class InventoryPanelViewModel : ObservableObject
{
    private Player? _player;
    private World? _world;

    /// <summary>
    /// Raised when the user clicks use/equip/drop on an item. The
    /// GameViewModel subscribes and either mutates the world directly
    /// (single-player) or sends a tool-call request to the host
    /// (multiplayer). The <see cref="ItemAction"/> record carries the
    /// item id + action kind.
    /// </summary>
    public event Action<ItemAction>? ItemActionRequested;

    /// <summary>Refresh from the given world.</summary>
    public void RefreshFromWorld(World world)
    {
        _world = world;
        _player = world?.ActivePlayer ?? world?.Players.FirstOrDefault();
        if (_player is null) { Clear(); return; }

        var p = _player;
        Currency = p.Inventory.Currency;
        Capacity = p.Inventory.Capacity;

        Items.Clear();
        foreach (var it in p.Inventory.Items)
            Items.Add(new InventoryItemRow(it));

        Equipped.Clear();
        foreach (var kv in p.Equipped)
            Equipped.Add(new InventoryItemRow(kv.Value, slot: kv.Key));

        // Compute total carried weight.
        var total = p.Inventory.Items.Sum(i => (i.Quantity > 0 ? 1 : 1) * ItemWeight(i));
        CarriedWeight = Math.Round(total, 1);
        OnPropertyChanged(nameof(WeightPercent));
        OnPropertyChanged(nameof(IsOverweight));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasEquipped));
    }

    private void Clear()
    {
        Currency = 0;
        Capacity = 150;
        CarriedWeight = 0;
        Items.Clear();
        Equipped.Clear();
        OnPropertyChanged(nameof(WeightPercent));
        OnPropertyChanged(nameof(IsOverweight));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasEquipped));
    }

    private static double ItemWeight(Item it)
    {
        // The Item entity doesn't carry weight directly; the template does.
        // We'd need a registry lookup. For the panel display, fall back to
        // a per-item default if the template isn't available. This is a
        // known limitation — the save should ideally denormalize weight
        // onto the item instance (TBD). For now, return 0.5 per unit.
        return 0.5;
    }

    // ─── Observable properties ───────────────────────────────────────

    [ObservableProperty] private int _currency;
    [ObservableProperty] private int _capacity = 150;
    [ObservableProperty] private double _carriedWeight;

    public ObservableCollection<InventoryItemRow> Items { get; } = new();
    public ObservableCollection<InventoryItemRow> Equipped { get; } = new();

    public double WeightPercent => Capacity > 0 ? Math.Min(100.0, (CarriedWeight / Capacity) * 100.0) : 0;
    public bool IsOverweight => CarriedWeight >= Capacity;
    public bool HasItems => Items.Count > 0;
    public bool HasEquipped => Equipped.Count > 0;

    // ─── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private void Use(InventoryItemRow row) =>
        ItemActionRequested?.Invoke(new ItemAction(row.Id, ItemActionKind.Use));

    [RelayCommand]
    private void Equip(InventoryItemRow row) =>
        ItemActionRequested?.Invoke(new ItemAction(row.Id, ItemActionKind.Equip));

    [RelayCommand]
    private void Unequip(InventoryItemRow row) =>
        ItemActionRequested?.Invoke(new ItemAction(row.Id, ItemActionKind.Unequip));

    [RelayCommand]
    private void Drop(InventoryItemRow row) =>
        ItemActionRequested?.Invoke(new ItemAction(row.Id, ItemActionKind.Drop));
}

/// <summary>One row in the inventory list.</summary>
public sealed class InventoryItemRow
{
    public InventoryItemRow(Item item, string? slot = null)
    {
        Id = item.Id;
        Name = item.Name;
        Quantity = item.Quantity;
        TemplateId = item.TemplateId ?? "";
        Slot = slot;
        Description = item.Description ?? "";
        Equipped = item.Equipped || slot is not null;
        // Category / rarity / weapon / armor / consumable flags would
        // come from the template registry; the Item entity itself
        // doesn't carry them. We expose them as empty/default so the
        // view can bind without nulls; a later task that denormalizes
        // template info onto Item instances fills them in.
        Category = "";
        Rarity = "common";
        IsWeapon = false;
        IsArmor = false;
        IsConsumable = false;
    }

    public EntityId Id { get; }
    public string Name { get; }
    public int Quantity { get; }
    public string TemplateId { get; }
    public string? Slot { get; }
    public string Description { get; }
    public bool Equipped { get; }
    public string Category { get; }
    public string Rarity { get; }
    public bool IsWeapon { get; }
    public bool IsArmor { get; }
    public bool IsConsumable { get; }
    public bool CanUse => IsConsumable;
    public bool CanEquip => IsWeapon || IsArmor;
}

/// <summary>Kind of item action the user requested.</summary>
public enum ItemActionKind { Use, Equip, Unequip, Drop }

/// <summary>An item action raised by the inventory panel.</summary>
public sealed record ItemAction(EntityId ItemId, ItemActionKind Kind);
