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

        // ITEM-RARITY (issue #66): look up the template for each item so
        // the row can carry the template's rarity (color-coding in the
        // view). Items whose template can't be found (e.g. loaded from a
        // pre-migration save) fall back to "common".
        var items = world?.Registries?.Items;
        Items.Clear();
        foreach (var it in p.Inventory.Items)
        {
            var tmpl = it.TemplateId is null ? null : items?.Get(it.TemplateId);
            Items.Add(new InventoryItemRow(it, rarity: tmpl?.Rarity));
        }

        Equipped.Clear();
        foreach (var kv in p.Equipped)
        {
            var tmpl = kv.Value.TemplateId is null ? null : items?.Get(kv.Value.TemplateId);
            Equipped.Add(new InventoryItemRow(kv.Value, slot: kv.Key, rarity: tmpl?.Rarity));
        }

        // Compute total carried weight. Stackable items contribute
        // weight * quantity; non-stackables (quantity==1) just contribute
        // their weight. Math.Max(1, qty) guards against malformed saves
        // where quantity slipped to 0 or negative.
        var total = p.Inventory.Items.Sum(i => ItemWeight(i) * Math.Max(1, i.Quantity));
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
        // The Item entity carries its per-unit weight (denormalized from
        // ItemTemplate.Weight at instantiation time by EntityFactory). Items
        // loaded from pre-migration saves default to 0 here — they'll report
        // no weight until re-instantiated (see Item.Weight TODO).
        return it.Weight;
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
    public InventoryItemRow(Item item, string? slot = null, string? rarity = null)
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
        // ITEM-RARITY (issue #66): caller passes the template's rarity
        // (looked up via World.Registries.Items). Defaults to "common"
        // when the template wasn't found or the rarity is empty.
        Rarity = NormalizeRarity(rarity);
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

    // ─── Rarity-driven UI helpers (issue #66) ──────────────────────────
    //
    // RarityClass maps the rarity string to the CSS-like style class
    // applied to the row's name TextBlock + Border (App.axaml defines
    // matching TextBlock.Rarity* / Border.ItemRow.Rarity* selectors that
    // set the foreground / left-border color).
    //
    // RarityLabel is the localized (Russian) adjective shown in the
    // expanded item detail under the name.
    public string RarityClass => Rarity switch
    {
        "common"    => "RarityCommon",
        "uncommon"  => "RarityUncommon",
        "rare"      => "RarityRare",
        "veryRare"  => "RarityVeryRare",
        "legendary" => "RarityLegendary",
        "artifact"  => "RarityArtifact",
        _           => "RarityCommon",
    };

    public string RarityLabel => Rarity switch
    {
        "common"    => "Обычный",
        "uncommon"  => "Необычный",
        "rare"      => "Редкий",
        "veryRare"  => "Очень редкий",
        "legendary" => "Легендарный",
        "artifact"  => "Артефакт",
        _           => "Обычный",
    };

    private static string NormalizeRarity(string? r) =>
        string.IsNullOrEmpty(r) ? "common" : r;
}

/// <summary>Kind of item action the user requested.</summary>
public enum ItemActionKind { Use, Equip, Unequip, Drop }

/// <summary>An item action raised by the inventory panel.</summary>
public sealed record ItemAction(EntityId ItemId, ItemActionKind Kind);
