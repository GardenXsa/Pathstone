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
///
/// <para>
/// STACK-SPLIT (issue #64): a separate SplitCommand opens a small dialog
/// overlay (managed by the view's code-behind). The VM exposes
/// <see cref="SplitTarget"/> + <see cref="SplitQuantity"/> +
/// <see cref="IsSplitDialogOpen"/> so the view can bind the dialog's
/// fields two-way. <see cref="ConfirmSplitCommand"/> raises an
/// <see cref="ItemAction"/> with the new <see cref="ItemActionKind.Split"/>
/// kind + the split quantity in <see cref="ItemAction"/>'s Quantity
/// field; the GameViewModel handles the actual world mutation (creating a
/// new Item, decrementing the original, refreshing the panel).
/// </para>
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
    /// item id + action kind (+ quantity for Split).
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
            Items.Add(new InventoryItemRow(it, rarity: tmpl?.Rarity, stackable: tmpl?.Stackable ?? false));
        }

        Equipped.Clear();
        foreach (var kv in p.Equipped)
        {
            var tmpl = kv.Value.TemplateId is null ? null : items?.Get(kv.Value.TemplateId);
            Equipped.Add(new InventoryItemRow(kv.Value, slot: kv.Key, rarity: tmpl?.Rarity, stackable: tmpl?.Stackable ?? false));
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

    // ─── Stack-split dialog state (issue #64) ──────────────────────────
    //
    // The split dialog is a Border overlay in InventoryPanelView (like
    // the death / reconnect overlays in GameView). When the user clicks
    // «Разделить» on a stackable row, OpenSplit sets SplitTarget + a
    // sensible default SplitQuantity (half the stack, clamped to 1..qty-1)
    // and flips IsSplitDialogOpen=true. The view's overlay binds its
    // NumericUpDown to SplitQuantity and its title to SplitTarget.Name.
    // ConfirmSplit raises the ItemAction event with the Split kind; the
    // GameViewModel mutates the world. CancelSplit just closes the dialog.

    /// <summary>
    /// True when the split-stack dialog overlay is visible. Bound to the
    /// overlay Border's IsVisible so the view doesn't need code-behind
    /// for show/hide.
    /// </summary>
    [ObservableProperty] private bool _isSplitDialogOpen;

    /// <summary>
    /// The row the user is splitting. Null when the dialog is closed.
    /// The view binds the dialog's title to <c>SplitTarget.Name</c> +
    /// <c>SplitTarget.Quantity</c>.
    /// </summary>
    [ObservableProperty] private InventoryItemRow? _splitTarget;

    /// <summary>
    /// The quantity the user wants to split off into a new stack. Bound
    /// two-way to the dialog's NumericUpDown (1..SplitTarget.Quantity-1).
    /// </summary>
    [ObservableProperty] private int _splitQuantity = 1;

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

    /// <summary>
    /// Open the split-stack dialog for the given row. Defaults the split
    /// quantity to half the stack (clamped to 1..qty-1). No-op when the
    /// row isn't stackable or has fewer than 2 items.
    /// </summary>
    [RelayCommand]
    private void OpenSplit(InventoryItemRow row)
    {
        if (row is null || !row.Stackable || row.Quantity < 2) return;
        SplitTarget = row;
        // Default to half the stack, rounded down, clamped to 1..qty-1.
        var half = row.Quantity / 2;
        if (half < 1) half = 1;
        if (half > row.Quantity - 1) half = row.Quantity - 1;
        SplitQuantity = half;
        IsSplitDialogOpen = true;
    }

    /// <summary>
    /// Confirm the split: raise an ItemAction with the Split kind +
    /// the chosen quantity. The GameViewModel does the actual world
    /// mutation. Closes the dialog afterwards.
    /// </summary>
    [RelayCommand]
    private void ConfirmSplit()
    {
        if (SplitTarget is null) return;
        var qty = SplitQuantity;
        var max = SplitTarget.Quantity - 1;
        if (qty < 1) qty = 1;
        if (qty > max) qty = max;
        ItemActionRequested?.Invoke(new ItemAction(
            SplitTarget.Id,
            ItemActionKind.Split,
            Quantity: qty));
        IsSplitDialogOpen = false;
        SplitTarget = null;
    }

    /// <summary>Close the split dialog without applying.</summary>
    [RelayCommand]
    private void CancelSplit()
    {
        IsSplitDialogOpen = false;
        SplitTarget = null;
    }
}

/// <summary>One row in the inventory list.</summary>
public sealed class InventoryItemRow
{
    public InventoryItemRow(Item item, string? slot = null, string? rarity = null, bool stackable = false)
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
        // STACK-SPLIT (issue #64): true when the source template is
        // stackable (consumables, ammunition, currency). The split
        // button is shown only for Stackable rows with Quantity > 1.
        Stackable = stackable;
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
    /// <summary>
    /// True when the source template is stackable (consumables,
    /// ammunition, currency). Drives the Split button's visibility.
    /// </summary>
    public bool Stackable { get; }
    public bool CanUse => IsConsumable;
    public bool CanEquip => IsWeapon || IsArmor;
    /// <summary>
    /// True when the Split button should be shown: stackable item with
    /// more than one unit in the stack.
    /// </summary>
    public bool CanSplit => Stackable && Quantity > 1;

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
        "uncommon"  => "Необычная",
        "rare"      => "Редкая",
        "veryRare"  => "Очень редкая",
        "legendary" => "Легендарная",
        "artifact"  => "Артефакт",
        _           => "Обычная",
    };

    private static string NormalizeRarity(string? r) =>
        string.IsNullOrEmpty(r) ? "common" : r;
}

/// <summary>Kind of item action the user requested.</summary>
public enum ItemActionKind { Use, Equip, Unequip, Drop, Split }

/// <summary>
/// An item action raised by the inventory panel. For
/// <see cref="ItemActionKind.Split"/>, <see cref="Quantity"/> carries the
/// number of units to split off into a new stack.
/// </summary>
public sealed record ItemAction(EntityId ItemId, ItemActionKind Kind, int Quantity = 0);
