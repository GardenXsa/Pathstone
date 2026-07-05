using MyGame.Core.Common;
using MyGame.Core.Rules;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

namespace MyGame.Core.World;

/// <summary>
/// Entity factories (ruleset-driven).
///
/// Port of <c>engine/entities/factory.ts</c>. Pure functions that construct
/// runtime entities (<see cref="Player"/>, <see cref="Npc"/>,
/// <see cref="Item"/>, <see cref="Building"/>) from either explicit args or
/// content templates. Resource pools and attribute defaults are derived from
/// the world's <see cref="Ruleset"/> — no D&amp;D-specific derivation lives
/// here. <c>world.Ruleset</c> is the single source of truth for "what a
/// fresh character looks like".
/// </summary>
public static class EntityFactory
{
    // ─── Attributes / resources ────────────────────────────────────────────

    /// <summary>
    /// Build an attribute map seeded from the ruleset's per-attribute
    /// <see cref="AttributeDef.Default"/> values, overridden by any explicit
    /// values in <paramref name="partial"/>. Missing keys fall back to the
    /// ruleset default. Extra keys (not in the ruleset) are passed through
    /// unchanged so world-defined-on-the-fly attributes stay alive.
    /// </summary>
    public static Dictionary<string, int> MakeAttributes(
        Ruleset ruleset,
        IReadOnlyDictionary<string, int>? partial = null)
    {
        if (ruleset is null) throw new ArgumentNullException(nameof(ruleset));
        var output = new Dictionary<string, int>();
        foreach (var def in ruleset.Attributes)
        {
            output[def.Key] = (partial is not null && partial.TryGetValue(def.Key, out var v))
                ? v
                : def.Default;
        }
        // Pass through any extra keys the caller passed that the ruleset
        // doesn't know about.
        if (partial is not null)
        {
            foreach (var (k, v) in partial)
            {
                if (!output.ContainsKey(k)) output[k] = v;
            }
        }
        return output;
    }

    /// <summary>
    /// Build the full resource pool map for a character: every resource in
    /// the ruleset starts at its derived maximum (or <see cref="ResourceDef.Default"/>
    /// if no MaxFormula). Explicit overrides in <paramref name="partial"/>
    /// win, e.g. a wounded NPC spawning at half hp.
    /// </summary>
    public static Dictionary<string, int> MakeResources(
        Ruleset ruleset,
        IReadOnlyDictionary<string, int> attributes,
        int level = 1,
        IReadOnlyDictionary<string, int>? partial = null)
    {
        var derived = Rulesets.DeriveResources(ruleset, attributes, level);
        if (partial is not null)
        {
            foreach (var (k, v) in partial) derived[k] = v;
        }
        return derived;
    }

    // ─── Player ────────────────────────────────────────────────────────────

    /// <summary>Arguments for <see cref="CreatePlayer"/>.</summary>
    public sealed record CreatePlayerArgs
    {
        public required string Name { get; init; }
        public string? Race { get; init; }
        public string? Class { get; init; }
        public int? Level { get; init; }
        public IReadOnlyDictionary<string, int>? Attributes { get; init; }
        public IReadOnlyDictionary<string, int>? Resources { get; init; }
        public required EntityId LocationId { get; init; }
        public IReadOnlyList<string>? ProficientSkills { get; init; }
        public int? StartingCurrency { get; init; }
        public string? Background { get; init; }
        public int? Speed { get; init; }
    }

    /// <summary>
    /// Construct a fresh player character. Resource pools and attribute
    /// defaults derive from <paramref name="ruleset"/> unless overridden.
    /// </summary>
    public static Player CreatePlayer(CreatePlayerArgs args, Ruleset? ruleset = null)
    {
        if (args is null) throw new ArgumentNullException(nameof(args));
        var rs = ruleset ?? Rulesets.DefaultDnd;

        int level = args.Level ?? 1;
        var attributes = MakeAttributes(rs, args.Attributes);
        var resources = MakeResources(rs, attributes, level, args.Resources);
        int currency = args.StartingCurrency ?? rs.Currency.Default;

        return new Player
        {
            Id = EntityId.NewId(),
            Name = args.Name,
            Race = args.Race ?? "Human",
            Class = args.Class ?? "Adventurer",
            Level = level,
            Attributes = attributes,
            Resources = resources,
            ProficientSkills = args.ProficientSkills?.ToList() ?? new List<string>(),
            Equipped = new(),
            Inventory = new()
            {
                Items = new(),
                Currency = currency,
                Capacity = 150,
            },
            Effects = new(),
            Speed = args.Speed ?? 30,
            LocationId = args.LocationId,
            IsAlive = true,
            Experience = 0,
            Background = args.Background,
        };
    }

    // ─── NPC ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Construct an NPC from a content template. Resource pools are derived
    /// from the ruleset formulas (with template overrides winning); equipment
    /// and starting inventory are instantiated from the item registry.
    /// </summary>
    public static Npc CreateNpcFromTemplate(
        NpcTemplate template,
        EntityId locationId,
        ContentRegistry? registries = null,
        Ruleset? ruleset = null)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        var rs = ruleset ?? Rulesets.DefaultDnd;

        var attributes = MakeAttributes(rs, template.Attributes);
        int level = template.Level ?? 1;
        var resources = MakeResources(rs, attributes, level, template.Resources);

        var npc = new Npc
        {
            Id = EntityId.NewId(),
            Name = template.Name,
            TemplateId = template.Id,
            Race = template.Race,
            Class = template.Class,
            Level = level,
            Attributes = attributes,
            Resources = resources,
            ProficientSkills = new(),
            Equipped = new(),
            Inventory = new()
            {
                Items = new(),
                Currency = 0,
                Capacity = 150,
            },
            Effects = new(),
            Disposition = template.Disposition ?? "neutral",
            Behavior = template.Behavior,
            AggroRange = template.AggroRange,
            ShopInventory = template.ShopInventory?.ToList(),
            Speed = template.Speed ?? 30,
            LocationId = locationId,
            IsAlive = true,
        };

        // Equip from templates — slot keys come from the template's equipment map.
        if (template.Equipment is not null && registries is not null)
        {
            foreach (var (slot, tplId) in template.Equipment)
            {
                if (string.IsNullOrEmpty(tplId)) continue;
                var t = registries.Items.Get(tplId);
                if (t is null) continue;
                var inst = InstantiateItem(t);
                npc.Equipped[slot] = inst;
            }
            RecomputeAcResource(npc, rs);
        }

        // Starting inventory.
        if (template.StartingInventory is not null && registries is not null)
        {
            foreach (var entry in template.StartingInventory)
            {
                var t = registries.Items.Get(entry.TemplateId);
                if (t is null) continue;
                npc.Inventory.Items.Add(InstantiateItem(t, entry.Quantity));
            }
        }

        return npc;
    }

    // ─── Items ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Instantiate a runtime <see cref="Item"/> from an
    /// <see cref="ItemTemplate"/>. Quantity is clamped to &gt;= 1.
    /// </summary>
    public static Item InstantiateItem(ItemTemplate template, int quantity = 1)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        return new Item
        {
            Id = EntityId.NewId(),
            TemplateId = template.Id,
            Name = template.Name,
            Quantity = Math.Max(1, quantity),
            Equipped = false,
        };
    }

    // ─── Buildings ─────────────────────────────────────────────────────────

    /// <summary>
    /// Construct a runtime <see cref="Building"/> from a content template.
    /// </summary>
    public static Building CreateBuilding(BuildingTemplate template, EntityId locationId)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        return new Building
        {
            Id = EntityId.NewId(),
            TemplateId = template.Id,
            Type = template.Type,
            Name = template.Name,
            Description = template.Description,
            LocationId = locationId,
            Occupants = new(),
            Enterable = template.Enterable,
            Locked = template.Locked,
        };
    }

    // ─── Status effects ────────────────────────────────────────────────────

    /// <summary>Create a fresh status effect with a new id.</summary>
    public static StatusEffect CreateEffect(string name, string description, int duration = 3) =>
        new()
        {
            Id = EntityId.NewId(),
            Name = name,
            Description = description,
            Duration = duration,
        };

    // ─── Combat hints (legacy D&D armor/shield model) ──────────────────────
    //
    // The combat/inventory systems historically read private hint fields
    // attached to equipped Items at equip time, avoiding a registry lookup
    // per attack. The C# port keeps this contract for D&D-style worlds; for
    // worlds without armor/weapon blocks the hints simply aren't attached
    // and combat falls back to flat rolls.

    /// <summary>
    /// Recompute the <c>ac</c> resource (if present in the ruleset) from
    /// equipped armor/shield using the legacy D&amp;D model. Worlds without
    /// an <c>ac</c> resource or without armor items are unaffected.
    /// </summary>
    public static void RecomputeAcResource(Character c, Ruleset ruleset)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (ruleset is null) throw new ArgumentNullException(nameof(ruleset));
        bool hasAc = ruleset.Resources.Any(r => r.Key == "ac");
        if (!hasAc) return;

        int dexVal = c.Attributes.TryGetValue("dex", out var dv) ? dv : 10;
        int dexMod = (int)Rulesets.AttributeModifier(ruleset, "dex", dexVal);

        int ac;
        if (c.Equipped.TryGetValue("armor", out var armor) && armor.TemplateId is not null)
        {
            // We don't have the template here without a registry hop; the
            // legacy code cached baseAc/dexBonusMax on the instance. For the
            // C# port, we instead fall back to 10 + dexMod when the armor
            // template can't be looked up. A future combat layer can fetch
            // the template via the content registry and recompute precisely.
            ac = 10 + dexMod;
        }
        else
        {
            ac = 10 + dexMod;
        }
        if (c.Equipped.ContainsKey("shield")) ac += 2;
        c.Resources["ac"] = ac;
    }

    /// <summary>
    /// Get the effective attribute values including effect modifiers.
    /// Defensive: ensures all ruleset attributes are present (fills in
    /// defaults for missing keys).
    /// </summary>
    public static Dictionary<string, int> EffectiveAttributes(
        Character c, Ruleset ruleset)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (ruleset is null) throw new ArgumentNullException(nameof(ruleset));
        var output = new Dictionary<string, int>(c.Attributes);
        foreach (var eff in c.Effects)
        {
            if (eff.Modifiers is null) continue;
            foreach (var (k, v) in eff.Modifiers)
            {
                output.TryGetValue(k, out var cur);
                output[k] = cur + v;
            }
        }
        // Ensure all ruleset attributes are present (defensive).
        foreach (var def in ruleset.Attributes)
        {
            if (!output.ContainsKey(def.Key)) output[def.Key] = def.Default;
        }
        return output;
    }

    /// <summary>
    /// 4d6-drop-lowest for each of the ruleset's attributes. Useful for
    /// quick randomized character generation in D&amp;D-style worlds; in
    /// other worlds it's still mathematically fine but flavour-inappropriate.
    /// </summary>
    public static Dictionary<string, int> RollStartingAttributes(Rng rng, Ruleset? ruleset = null)
    {
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        var rs = ruleset ?? Rulesets.DefaultDnd;
        var output = new Dictionary<string, int>();
        foreach (var def in rs.Attributes)
        {
            int score = Roll4d6DropLowest(rng);
            // Clamp to the ruleset range.
            int clamped = Math.Max(def.Range.Min, Math.Min(def.Range.Max, score));
            output[def.Key] = clamped;
        }
        return output;
    }

    private static int Roll4d6DropLowest(Rng rng)
    {
        Span<int> rolls = stackalloc int[4];
        for (int i = 0; i < 4; i++) rolls[i] = rng.NextInt(1, 7);
        // Sort ascending, sum top 3.
        for (int i = 1; i < 4; i++)
        {
            int v = rolls[i];
            int j = i - 1;
            while (j >= 0 && rolls[j] > v) { rolls[j + 1] = rolls[j]; j--; }
            rolls[j + 1] = v;
        }
        return rolls[1] + rolls[2] + rolls[3];
    }
}
