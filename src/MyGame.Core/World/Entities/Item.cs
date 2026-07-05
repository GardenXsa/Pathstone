using System.Text.Json.Serialization;
using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

// ─── Damage / weapon / armor / consumable sub-shapes ───────────────────────
//
// All ports of the corresponding sub-interfaces in engine/types/index.ts.
// Records for immutability — a weapon spec never mutates after creation.

/// <summary>
/// A damage expression: dice notation (<c>"1d8"</c>) + damage type
/// (<c>"slashing"</c>). The type is a free-form string whose vocabulary is
/// defined by the world's Ruleset.
/// </summary>
public sealed record Damage(string Dice, string Type);

/// <summary>
/// Weapon-specific properties on an item template. Only meaningful in worlds
/// whose ruleset has weapons. Port of <c>ItemTemplate.weapon</c>.
/// </summary>
public sealed record WeaponSpec
{
    /// <summary>Weapon type (<c>simple</c>, <c>martial</c>, <c>improvised</c>, or ruleset-defined).</summary>
    public required string Type { get; init; }

    /// <summary>Base damage dice + damage type.</summary>
    public required Damage Damage { get; init; }

    /// <summary>Range in feet (0 = melee, 20 = thrown, 80 = shortbow, etc.).</summary>
    public int? Range { get; init; }

    /// <summary>Whether the weapon can use STR or DEX (whichever is higher).</summary>
    public bool? Finesse { get; init; }

    /// <summary>Requires two hands to wield.</summary>
    public bool? TwoHanded { get; init; }

    /// <summary>Two-handed damage dice (e.g. <c>"1d10"</c> for a longsword used 2H).</summary>
    public string? Versatile { get; init; }

    /// <summary>Free-form property tags (<c>light</c>, <c>heavy</c>, <c>thrown</c>, <c>ammunition</c>, …).</summary>
    public List<string>? Properties { get; init; }
}

/// <summary>Armor-specific properties on an item template.</summary>
public sealed record ArmorSpec
{
    /// <summary>Base armor class (before dex bonus).</summary>
    public required int BaseAc { get; init; }

    /// <summary>Armor category (<c>light</c>, <c>medium</c>, <c>heavy</c>, or ruleset-defined).</summary>
    public required string Type { get; init; }

    /// <summary>
    /// Max dexterity bonus to AC (null = unlimited; light = null, medium = 2,
    /// heavy = 0).
    /// </summary>
    public int? DexBonusMax { get; init; }

    /// <summary>Strength score required to wear without penalty.</summary>
    public int? StrengthRequired { get; init; }

    /// <summary>Whether the armor imposes stealth disadvantage.</summary>
    public bool? StealthDisadvantage { get; init; }
}

/// <summary>An effect granted by a consumable on use (heal, cure, buff, …).</summary>
public sealed record ConsumableEffect
{
    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>What the effect does.</summary>
    public required string Description { get; init; }

    /// <summary>Duration in rounds (0 = instant).</summary>
    public int Duration { get; init; }
}

/// <summary>Consumable-specific properties on an item template.</summary>
public sealed record ConsumableSpec
{
    /// <summary>Resource key to restore (defaults to <c>hp</c> in D&amp;D-style worlds).</summary>
    public string? Resource { get; init; }

    /// <summary>Amount to restore, or a dice expression (<c>"2d4+2"</c>).</summary>
    public string? Healing { get; init; }

    /// <summary>Effects granted on consumption.</summary>
    public List<ConsumableEffect>? Effects { get; init; }
}

// ─── ItemTemplate (definition) ──────────────────────────────────────────────

/// <summary>
/// Static definition of an item type. Lives in the
/// <see cref="Content.ItemRegistry"/>, referenced by TemplateId. Port of
/// <c>ItemTemplate</c> from <c>engine/types/index.ts</c>.
/// </summary>
public sealed record ItemTemplate
{
    /// <summary>Stable template id, e.g. <c>wpn_shortsword</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Flavor text.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Item category — free-form string defined by the ruleset
    /// (<c>weapon</c>, <c>armor</c>, <c>consumable</c>, <c>treasure</c>, …).
    /// </summary>
    public required string Category { get; init; }

    /// <summary>Weight in lb.</summary>
    public double Weight { get; init; }

    /// <summary>Value in the world's currency units.</summary>
    public double Value { get; init; }

    /// <summary>
    /// Rarity bucket (<c>common</c>, <c>uncommon</c>, <c>rare</c>,
    /// <c>veryRare</c>, <c>legendary</c>, <c>artifact</c>, or ruleset-defined).
    /// </summary>
    public required string Rarity { get; init; }

    /// <summary>Whether multiple instances stack into a single inventory slot.</summary>
    public bool Stackable { get; init; }

    /// <summary>Weapon specifics (only if category is <c>weapon</c>).</summary>
    public WeaponSpec? Weapon { get; init; }

    /// <summary>Armor specifics (only if category is <c>armor</c>).</summary>
    public ArmorSpec? Armor { get; init; }

    /// <summary>Consumable specifics (only if category is <c>consumable</c>).</summary>
    public ConsumableSpec? Consumable { get; init; }

    /// <summary>Free-form tags for AI / scripting.</summary>
    public List<string>? Tags { get; init; }
}

// ─── Item (runtime instance) ────────────────────────────────────────────────

/// <summary>
/// A runtime item instance — a concrete object that exists in the world
/// (on the ground, in an inventory, or equipped). Port of
/// <c>ItemInstance</c> from <c>engine/types/index.ts</c>.
///
/// The TS interface used <c>uid</c> as the unique instance id; here the
/// inherited <see cref="Entity.Id"/> plays that role.
/// </summary>
public sealed class Item : Entity
{
    /// <summary>Create a new blank item instance.</summary>
    public Item() : base("item") { }

    /// <summary>The template this instance was spawned from.</summary>
    public string? TemplateId { get; set; }

    /// <summary>Stack size (1 for unstackable items).</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Optional runtime override of the template's damage profile.</summary>
    public Damage? CustomDamage { get; set; }

    /// <summary>Whether this item is currently equipped in a slot.</summary>
    public bool Equipped { get; set; }

    /// <summary>Enchantment ids applied to this instance.</summary>
    public List<string>? Enchantments { get; set; }
}

// ─── Inventory ──────────────────────────────────────────────────────────────

/// <summary>
/// A character's carried inventory. Port of <c>Inventory</c> from
/// <c>engine/types/index.ts</c>.
/// </summary>
public sealed class Inventory
{
    /// <summary>Items carried (does not include equipped items).</summary>
    public List<Item> Items { get; set; } = new();

    /// <summary>Amount of the world's primary currency.</summary>
    public int Currency { get; set; }

    /// <summary>Weight capacity in lb.</summary>
    public int Capacity { get; set; } = 150;
}
