using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyGame.Core.Common;

namespace MyGame.Core.World;

/// <summary>
/// How turns are scheduled in a multiplayer session. Port-side addition
/// (not in TS <c>ruleset.ts</c>) — the desktop rewrite's host server uses
/// this to decide when players may submit actions.
/// </summary>
public enum TurnModel
{
    /// <summary>
    /// Players enqueue actions as they arrive; the GM resolves them in order
    /// (one at a time, FIFO). Default for single-player and small groups.
    /// </summary>
    ActionQueue = 0,

    /// <summary>Strict turn-taking: only the active player may act.</summary>
    TurnBased = 1,

    /// <summary>
    /// Free-form actions outside combat, switch to TurnBased inside combat.
    /// </summary>
    Hybrid = 2,
}

/// <summary>
/// How LLM token cost is divided across players in a multiplayer session.
/// Port-side addition; the host computes each player's share using this.
/// </summary>
public enum TokenBillMode
{
    /// <summary>Host pays for everything (single-player mode default).</summary>
    Host = 0,

    /// <summary>Each player pays for their own turns' tokens.</summary>
    Random = 1,

    /// <summary>Tokens are pooled and split evenly across active players.</summary>
    Balanced = 2,
}

/// <summary>
/// What entities a player can see on the map. Drives the World's
/// visibility filter for the desktop UI.
/// </summary>
public enum Visibility
{
    /// <summary>Everything (debug / GM view).</summary>
    All = 0,

    /// <summary>Only the player's current location + discovered neighbors.</summary>
    Location = 1,

    /// <summary>
    /// Location + anything within a perception-radius (uses player's PER
    /// attribute to expand the visible set).
    /// </summary>
    Perception = 2,
}

/// <summary>
/// The dice/check mechanic the engine uses to resolve actions. Port of
/// <c>DiceMechanic</c> from <c>ruleset.ts</c>.
/// </summary>
public enum DiceMechanic
{
    D20 = 0,
    D100 = 1,
    D3d6 = 2,
    D6Pool = 3,
    TwoD6Pbta = 4,
    Coin = 5,
    D10Pool = 6,
    D6 = 7,
}

/// <summary>How a check result is interpreted against the difficulty.</summary>
public enum DifficultyModel
{
    Dc = 0,
    Tn = 1,
    SuccessCount = 2,
    Mixed = 3,
}

/// <summary>Visual category the UI uses to pick colors/icons for a resource.</summary>
public enum ResourceUI
{
    Health = 0,
    Mental = 1,
    Energy = 2,
    Armor = 3,
    Currency = 4,
}

/// <summary>What happens when a resource hits 0.</summary>
public enum OnZeroBehaviour
{
    Death = 0,
    Unconscious = 1,
    Madness = 2,
    Destruction = 3,
    Nothing = 4,
}

/// <summary>Progression model.</summary>
public enum ProgressionType
{
    LevelXp = 0,
    Milestone = 1,
    SkillBased = 2,
    None = 3,
}

/// <summary>A measurable character attribute (formerly the 6 D&D ability scores).</summary>
public sealed record AttributeDef
{
    /// <summary>Stable key referenced in formulas and character attributes, e.g. <c>str</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Display name, e.g. «Сила».</summary>
    public required string Name { get; init; }

    /// <summary>Short UI abbreviation, e.g. «СИЛ».</summary>
    public string? Abbr { get; init; }

    /// <summary>Inclusive [min, max] range for the value.</summary>
    public required (int Min, int Max) Range { get; init; }

    /// <summary>Value used when a character has no explicit value set.</summary>
    public required int Default { get; init; }

    /// <summary>
    /// Flat modifier formula applied on checks, e.g. <c>floor((v-10)/2)</c>.
    /// <c>v</c> is the attribute value. Omit = no modifier.
    /// </summary>
    public string? ModifierFormula { get; init; }
}

/// <summary>A pool that depletes and refills (hp, mana, sanity, shield, stamina…).</summary>
public sealed record ResourceDef
{
    /// <summary>Stable key, e.g. <c>hp</c>, <c>mana</c>, <c>sanity</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Display name, e.g. «Здоровье».</summary>
    public required string Name { get; init; }

    /// <summary>Short abbreviation, e.g. «HP».</summary>
    public string? Abbr { get; init; }

    /// <summary>
    /// Formula for the maximum value, evaluated against
    /// <c>{ level, &lt;attrKeys&gt;, &lt;attrKeys&gt;Mod }</c>. Omit if the
    /// resource has a fixed default instead.
    /// </summary>
    public string? MaxFormula { get; init; }

    /// <summary>Fixed starting/maximum value when no MaxFormula is provided.</summary>
    public int? Default { get; init; }

    /// <summary>Amount regenerated at the end of every combat turn.</summary>
    public int? RegenPerTurn { get; init; }

    /// <summary>Amount regenerated when the player rests.</summary>
    public int? RegenOnRest { get; init; }

    /// <summary>What happens when the resource hits 0.</summary>
    public required OnZeroBehaviour OnZero { get; init; }

    /// <summary>UI styling hint.</summary>
    public required ResourceUI UI { get; init; }
}

/// <summary>Currency definition (one per world).</summary>
public sealed record CurrencyDef
{
    /// <summary>Stable key, e.g. <c>gold</c>, <c>credits</c>, <c>caps</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Display name, e.g. «Золото».</summary>
    public required string Name { get; init; }

    /// <summary>Symbol/prefix for the UI, e.g. <c>ζ</c>, <c>€$</c>.</summary>
    public string? Symbol { get; init; }

    /// <summary>Starting amount for a new player.</summary>
    public int Default { get; init; }
}

/// <summary>Progression model descriptor.</summary>
public sealed record ProgressionDef
{
    /// <summary>How characters grow.</summary>
    public required ProgressionType Type { get; init; }

    /// <summary>XP threshold formula for the next level, e.g. <c>level*300</c>.</summary>
    public string? XpFormula { get; init; }

    /// <summary>Maximum level cap. Null = uncapped.</summary>
    public int? MaxLevel { get; init; }
}

/// <summary>
/// The full rulebook for one world. Plain data — JSON-serializable.
///
/// Port of <c>Ruleset</c> from <c>ruleset.ts</c>. The engine INTERPRETS the
/// ruleset — it never hardcodes D&amp;D. The default world ships
/// <see cref="Rulesets.DefaultDnd"/>; a world built from a cyberpunk/horror/
/// sci-fi brief gets a bespoke ruleset authored by the AI planner.
///
/// Three port-side fields (<see cref="TurnModel"/>, <see cref="TokenBill"/>,
/// <see cref="Visibility"/>) are added for the multiplayer/desktop layer —
/// they configure host behavior and the UI's visibility filter. The TS
/// original lived entirely in the AI/engine layer and didn't need them.
/// </summary>
public sealed record Ruleset
{
    /// <summary>Stable identifier (e.g. <c>dnd5e-default</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Human label, e.g. «D&D 5e (Долина Туманов)».</summary>
    public required string Name { get; init; }

    /// <summary>Genre/setting hint for prompts and UI, e.g. <c>dark-fantasy</c>.</summary>
    public required string Genre { get; init; }

    /// <summary>Multiplayer turn model. Port-side addition.</summary>
    public TurnModel TurnModel { get; init; } = TurnModel.ActionQueue;

    /// <summary>Token billing model. Port-side addition.</summary>
    public TokenBillMode TokenBill { get; init; } = TokenBillMode.Host;

    /// <summary>Player visibility mode. Port-side addition.</summary>
    public Visibility Visibility { get; init; } = Visibility.Location;

    /// <summary>Attribute definitions (size &gt;= 1 for a valid ruleset).</summary>
    public required IReadOnlyList<AttributeDef> Attributes { get; init; }

    /// <summary>Resource definitions (hp, mana, sanity…).</summary>
    public required IReadOnlyList<ResourceDef> Resources { get; init; }

    /// <summary>Primary dice/check mechanic.</summary>
    public DiceMechanic Dice { get; init; } = DiceMechanic.D20;

    /// <summary>How a check result is interpreted against the difficulty.</summary>
    public DifficultyModel Difficulty { get; init; } = DifficultyModel.Dc;

    /// <summary>
    /// Natural roll values that count as auto-crit (success) and auto-fumble
    /// (failure) on the primary die. For d20 defaults to {20, 1}. Null =
    /// crit-less system.
    /// </summary>
    public (int Success, int Failure)? CriticalRange { get; init; }

    /// <summary>Currency definition.</summary>
    public required CurrencyDef Currency { get; init; }

    /// <summary>Progression model.</summary>
    public required ProgressionDef Progression { get; init; }

    /// <summary>Damage type vocabulary for this world.</summary>
    public required IReadOnlyList<string> DamageTypes { get; init; }

    /// <summary>Interaction keywords the damage resolver understands.</summary>
    public IReadOnlyList<string>? DamageInteractions { get; init; }

    /// <summary>Item categories this world uses.</summary>
    public required IReadOnlyList<string> ItemCategories { get; init; }

    /// <summary>Building types this world uses.</summary>
    public required IReadOnlyList<string> BuildingTypes { get; init; }

    /// <summary>Terrain types this world uses.</summary>
    public required IReadOnlyList<string> TerrainTypes { get; init; }

    /// <summary>Equipment slot keys characters can fill.</summary>
    public required IReadOnlyList<string> EquipmentSlots { get; init; }

    /// <summary>Optional genre-specific subsystems to enable.</summary>
    public IReadOnlyList<string>? Modules { get; init; }
}

/// <summary>
/// Default ruleset instances and static helpers.
/// </summary>
public static class Rulesets
{
    /// <summary>
    /// The D&amp;D-5e-flavoured ruleset used by the default world and as a
    /// fallback when a custom build fails to commit a valid ruleset. Encoded
    /// purely as data — no behaviour beyond what the generic engine implements.
    ///
    /// HP formula: at L1 the fighter-style hit die (8) + CON mod; each
    /// additional level adds 5 + CON mod (average of a d8).
    /// </summary>
    public static Ruleset DefaultDnd { get; } = new Ruleset
    {
        Id = "dnd5e-default",
        Name = "D&D 5e (Долина Туманов)",
        Genre = "dark-fantasy",
        TurnModel = TurnModel.ActionQueue,
        TokenBill = TokenBillMode.Host,
        Visibility = Visibility.Location,
        Attributes = new List<AttributeDef>
        {
            new() { Key = "str", Name = "Сила", Abbr = "СИЛ", Range = (3, 20), Default = 10, ModifierFormula = "floor((v-10)/2)" },
            new() { Key = "dex", Name = "Ловкость", Abbr = "ЛОВ", Range = (3, 20), Default = 10, ModifierFormula = "floor((v-10)/2)" },
            new() { Key = "con", Name = "Телосложение", Abbr = "ТЕЛ", Range = (3, 20), Default = 10, ModifierFormula = "floor((v-10)/2)" },
            new() { Key = "int", Name = "Интеллект", Abbr = "ИНТ", Range = (3, 20), Default = 10, ModifierFormula = "floor((v-10)/2)" },
            new() { Key = "per", Name = "Восприятие", Abbr = "ВСП", Range = (3, 20), Default = 10, ModifierFormula = "floor((v-10)/2)" },
            new() { Key = "cha", Name = "Харизма", Abbr = "ХАР", Range = (3, 20), Default = 10, ModifierFormula = "floor((v-10)/2)" },
        },
        Resources = new List<ResourceDef>
        {
            new()
            {
                Key = "hp",
                Name = "Здоровье",
                Abbr = "HP",
                MaxFormula = "8 + conMod + (level-1) * (5 + conMod)",
                RegenOnRest = 0,
                OnZero = OnZeroBehaviour.Death,
                UI = ResourceUI.Health,
            },
        },
        Dice = DiceMechanic.D20,
        Difficulty = DifficultyModel.Dc,
        CriticalRange = (20, 1),
        Currency = new CurrencyDef { Key = "gold", Name = "Золото", Default = 10 },
        Progression = new ProgressionDef { Type = ProgressionType.LevelXp, XpFormula = "level*300", MaxLevel = 20 },
        DamageTypes = new List<string>
        {
            "slashing", "piercing", "bludgeoning",
            "fire", "cold", "lightning", "thunder", "acid", "poison",
            "psychic", "necrotic", "radiant", "force",
        },
        DamageInteractions = new List<string> { "resistant", "immune", "vulnerable" },
        ItemCategories = new List<string>
        {
            "weapon", "armor", "shield", "consumable",
            "tool", "treasure", "key", "misc", "quest",
        },
        BuildingTypes = new List<string>
        {
            "tavern", "shop", "temple", "house", "tower",
            "dungeon", "cave", "ruin", "fortress", "landmark", "custom",
        },
        TerrainTypes = new List<string>
        {
            "plains", "forest", "mountain", "desert", "swamp",
            "water", "urban", "underground", "snow", "coast", "void",
        },
        EquipmentSlots = new List<string> { "weapon", "armor", "shield" },
    };

    /// <summary>
    /// Minimal starter ruleset handed to a fresh custom world BEFORE the
    /// planner commits its bespoke ruleset. The planner's commit_ruleset
    /// call overwrites <c>world.Ruleset</c> entirely.
    /// </summary>
    public static Ruleset Starter { get; } = DefaultDnd with
    {
        Id = "starter-pending",
        Name = "Система мира (в разработке)",
    };

    // ─── Lookup helpers ────────────────────────────────────────────────────

    /// <summary>Find an attribute def by key. Null if not present.</summary>
    public static AttributeDef? GetAttribute(Ruleset rs, string key) =>
        rs.Attributes.FirstOrDefault(a => a.Key == key);

    /// <summary>Find a resource def by key. Null if not present.</summary>
    public static ResourceDef? GetResource(Ruleset rs, string key) =>
        rs.Resources.FirstOrDefault(r => r.Key == key);

    // ─── Formula evaluation ────────────────────────────────────────────────

    /// <summary>
    /// Apply an attribute's <see cref="AttributeDef.ModifierFormula"/> to a
    /// raw value. If the attribute has no formula (or isn't found), returns 0.
    /// </summary>
    public static double AttributeModifier(Ruleset rs, string key, int value)
    {
        var def = GetAttribute(rs, key);
        if (def?.ModifierFormula is null) return 0;
        return FormulaEval.Eval(def.ModifierFormula, new Dictionary<string, double> { ["v"] = value });
    }

    /// <summary>
    /// Build the full variable scope used by MaxFormula/XpFormula:
    /// <c>{ level, &lt;attrKey&gt;, &lt;attrKey&gt;Mod }</c> for every attribute
    /// in the ruleset.
    /// </summary>
    public static Dictionary<string, double> BuildFormulaScope(
        Ruleset rs,
        IReadOnlyDictionary<string, int> attributes,
        int level = 1)
    {
        var scope = new Dictionary<string, double> { ["level"] = level };
        foreach (var def in rs.Attributes)
        {
            int v = attributes.TryGetValue(def.Key, out var av) ? av : def.Default;
            scope[def.Key] = v;
            scope[def.Key + "Mod"] = AttributeModifier(rs, def.Key, v);
        }
        return scope;
    }

    /// <summary>
    /// Derive every resource maximum from the ruleset given a character's
    /// attributes and level. Resources with no MaxFormula use their
    /// <see cref="ResourceDef.Default"/> (or 0). Returns a map keyed by
    /// resource key.
    /// </summary>
    public static Dictionary<string, int> DeriveResources(
        Ruleset rs,
        IReadOnlyDictionary<string, int> attributes,
        int level = 1)
    {
        var scope = BuildFormulaScope(rs, attributes, level);
        var output = new Dictionary<string, int>();
        foreach (var def in rs.Resources)
        {
            if (def.MaxFormula is { } formula)
            {
                output[def.Key] = Math.Max(0, (int)Math.Floor(FormulaEval.Eval(formula, scope)));
            }
            else
            {
                output[def.Key] = def.Default ?? 0;
            }
        }
        return output;
    }

    /// <summary>
    /// Find the world's "vital" resource — the one whose OnZero is Death or
    /// Destruction (hp in D&amp;D, hull in a starship, structure for a mech).
    /// Null if the world has none (pure social / puzzle world).
    /// </summary>
    public static string? VitalResourceKey(Ruleset rs) =>
        rs.Resources.FirstOrDefault(r => r.OnZero is OnZeroBehaviour.Death or OnZeroBehaviour.Destruction)?.Key;

    /// <summary>
    /// Temporary-HP resource key (a resource named <c>temphp</c> or
    /// <c>tempHp</c>), if any.
    /// </summary>
    public static string? TempResourceKey(Ruleset rs) =>
        rs.Resources.FirstOrDefault(r => r.Key is "temphp" or "tempHp")?.Key;

    /// <summary>
    /// Compute a resource's current max for a given attribute map and level —
    /// either from its MaxFormula (evaluated against the scope) or its flat
    /// <see cref="ResourceDef.Default"/>. Used by damage/heal clamps and the UI.
    /// Returns <see cref="double.PositiveInfinity"/> if the resource isn't
    /// found OR has neither a formula nor a default (treated as unbounded).
    /// </summary>
    public static double DerivedResourceMax(
        Ruleset rs,
        IReadOnlyDictionary<string, int> attributes,
        int level,
        string resourceKey)
    {
        var def = GetResource(rs, resourceKey);
        if (def is null) return double.PositiveInfinity;
        if (def.MaxFormula is null) return def.Default ?? double.PositiveInfinity;
        var scope = BuildFormulaScope(rs, attributes, level);
        return Math.Max(0, Math.Floor(FormulaEval.Eval(def.MaxFormula, scope)));
    }

    /// <summary>True if this ruleset uses level/xp or milestone progression.</summary>
    public static bool HasLevels(Ruleset rs) =>
        rs.Progression.Type is ProgressionType.LevelXp or ProgressionType.Milestone;

    /// <summary>XP needed to advance from the given level to the next.</summary>
    public static int XpForLevel(Ruleset rs, int level)
    {
        if (rs.Progression.XpFormula is null) return int.MaxValue;
        return Math.Max(1, (int)Math.Floor(
            FormulaEval.Eval(rs.Progression.XpFormula, new Dictionary<string, double> { ["level"] = level })));
    }

    /// <summary>
    /// Validate a candidate ruleset. Returns a list of human-readable
    /// problems; empty list = valid. Used by the commit_ruleset tool to
    /// reject malformed AI-authored schemas before they touch world state.
    /// </summary>
    public static List<string> Validate(Ruleset? rs)
    {
        var problems = new List<string>();
        if (rs is null) { problems.Add("ruleset is null"); return problems; }
        if (string.IsNullOrEmpty(rs.Id)) problems.Add("id must be a non-empty string");
        if (string.IsNullOrEmpty(rs.Name)) problems.Add("name must be a non-empty string");
        if (rs.Attributes is null || rs.Attributes.Count == 0)
        {
            problems.Add("attributes must be a non-empty array");
        }
        else
        {
            var seen = new HashSet<string>();
            foreach (var a in rs.Attributes)
            {
                if (string.IsNullOrEmpty(a.Key)) problems.Add($"attribute missing key");
                else if (!seen.Add(a.Key)) problems.Add($"duplicate attribute key: {a.Key}");
                if (a.Range.Min > a.Range.Max) problems.Add($"attribute {a.Key}: range.Min > range.Max");
                if (!string.IsNullOrEmpty(a.ModifierFormula))
                {
                    if (!FormulaEval.TryCompile(a.ModifierFormula, out var ferr))
                        problems.Add($"attribute {a.Key}: invalid modifierFormula \"{a.ModifierFormula}\" ({ferr})");
                }
            }
        }
        if (rs.Resources is null) problems.Add("resources must be an array");
        else
        {
            var seen = new HashSet<string>();
            foreach (var r in rs.Resources)
            {
                if (string.IsNullOrEmpty(r.Key)) problems.Add("resource missing key");
                else if (!seen.Add(r.Key)) problems.Add($"duplicate resource key: {r.Key}");
                if (!string.IsNullOrEmpty(r.MaxFormula))
                {
                    if (!FormulaEval.TryCompile(r.MaxFormula, out var ferr))
                        problems.Add($"resource {r.Key}: invalid maxFormula \"{r.MaxFormula}\" ({ferr})");
                }
            }
        }
        if (!Enum.IsDefined(rs.Dice)) problems.Add($"dice invalid: {rs.Dice}");
        if (!Enum.IsDefined(rs.Difficulty)) problems.Add($"difficulty invalid: {rs.Difficulty}");
        if (rs.Currency is null || string.IsNullOrEmpty(rs.Currency.Key))
            problems.Add("currency.key must be a non-empty string");
        if (rs.Progression is null || !Enum.IsDefined(rs.Progression.Type))
            problems.Add("progression.type invalid");
        if (!string.IsNullOrEmpty(rs.Progression?.XpFormula))
        {
            if (!FormulaEval.TryCompile(rs.Progression.XpFormula, out var ferr))
                problems.Add($"invalid progression.xpFormula \"{rs.Progression.XpFormula}\" ({ferr})");
        }
        if (rs.DamageTypes is null) problems.Add("damageTypes must be an array");
        if (rs.ItemCategories is null) problems.Add("itemCategories must be an array");
        if (rs.BuildingTypes is null) problems.Add("buildingTypes must be an array");
        if (rs.TerrainTypes is null) problems.Add("terrainTypes must be an array");
        if (rs.EquipmentSlots is null) problems.Add("equipmentSlots must be an array");
        return problems;
    }
}

/// <summary>
/// Tiny sandboxed arithmetic expression evaluator used by
/// <see cref="Rulesets"/> to evaluate attribute modifier formulas, resource
/// max formulas, and XP formulas.
///
/// Replaces the TS dependency on the <c>expr-eval</c> npm package. Supports:
///  - integer and decimal literals (e.g. <c>8</c>, <c>0.5</c>),
///  - variable references (e.g. <c>v</c>, <c>level</c>, <c>conMod</c>),
///  - binary <c>+ - * /</c>,
///  - unary <c>- +</c>,
///  - parentheses,
///  - function calls: <c>floor</c>, <c>ceil</c>, <c>round</c>, <c>abs</c>,
///    <c>min</c>, <c>max</c>, <c>sqrt</c>, <c>pow</c>.
///
/// No member access, no assignments, no string ops — strictly numeric. A
/// malformed expression throws <see cref="FormatException"/>; callers should
/// use <see cref="Eval"/> which never throws (returns 0 on failure, matching
/// the TS <c>evalFormula</c> semantics).
/// </summary>
public static class FormulaEval
{
    /// <summary>
    /// Evaluate an expression against the given variable scope. Never throws:
    /// returns <paramref name="fallback"/> (default 0) on parse/eval failure
    /// so a malformed AI-authored formula can never crash the engine.
    /// </summary>
    public static double Eval(string expr, IReadOnlyDictionary<string, double> vars, double fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(expr)) return fallback;
        try
        {
            var tokens = Tokenize(expr);
            var parser = new Parser(tokens, vars);
            double result = parser.ParseExpression();
            parser.ExpectEnd();
            return double.IsFinite(result) ? result : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Check whether an expression parses cleanly. Does NOT evaluate it (so
    /// missing variable bindings don't fail — formulas reference runtime
    /// variables like <c>v</c>, <c>level</c>, <c>conMod</c> that aren't bound
    /// until a character is in scope). Mirrors the TS <c>parser.parse()</c>
    /// syntax-only check. Returns the parse error message via
    /// <paramref name="error"/> when it fails.
    /// </summary>
    public static bool TryCompile(string expr, out string? error)
    {
        if (string.IsNullOrWhiteSpace(expr)) { error = "empty expression"; return false; }
        try
        {
            var tokens = Tokenize(expr);
            // Use a permissive variable scope that returns 0 for any key, so
            // the parse-and-evaluate walk exercises the full grammar without
            // tripping on unbound runtime variables.
            var parser = new Parser(tokens, PermissiveVars.Instance);
            parser.ParseExpression();
            parser.ExpectEnd();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Variable scope that returns 0 for any key — used by
    /// <see cref="TryCompile"/> so the syntax check doesn't reject formulas
    /// that reference runtime variables (like <c>v</c> or <c>level</c>).
    /// </summary>
    private sealed class PermissiveVars : IReadOnlyDictionary<string, double>
    {
        public static readonly PermissiveVars Instance = new();
        public double this[string key] => 0;
        public IEnumerable<string> Keys => Array.Empty<string>();
        public IEnumerable<double> Values => Array.Empty<double>();
        public int Count => 0;
        public bool ContainsKey(string key) => true;
        public IEnumerator<KeyValuePair<string, double>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, double>>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool TryGetValue(string key, out double value) { value = 0; return true; }
    }

    // ── Tokenizer ──────────────────────────────────────────────────────────
    private enum TokenType { Number, Ident, Plus, Minus, Star, Slash, Comma, LParen, RParen, End }

    private readonly struct Token(TokenType type, string text, double value)
    {
        public readonly TokenType Type = type;
        public readonly string Text = text;
        public readonly double Value = value;
    }

    private static List<Token> Tokenize(string expr)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < expr.Length)
        {
            char c = expr[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '+': tokens.Add(new Token(TokenType.Plus, "+", 0)); i++; break;
                case '-': tokens.Add(new Token(TokenType.Minus, "-", 0)); i++; break;
                case '*': tokens.Add(new Token(TokenType.Star, "*", 0)); i++; break;
                case '/': tokens.Add(new Token(TokenType.Slash, "/", 0)); i++; break;
                case ',': tokens.Add(new Token(TokenType.Comma, ",", 0)); i++; break;
                case '(': tokens.Add(new Token(TokenType.LParen, "(", 0)); i++; break;
                case ')': tokens.Add(new Token(TokenType.RParen, ")", 0)); i++; break;
                default:
                    if (char.IsDigit(c) || c == '.')
                    {
                        int start = i;
                        while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.')) i++;
                        if (!double.TryParse(expr.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                            throw new FormatException($"invalid number at offset {start}");
                        tokens.Add(new Token(TokenType.Number, expr.Substring(start, i - start), d));
                    }
                    else if (char.IsLetter(c) || c == '_')
                    {
                        int start = i;
                        while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
                        tokens.Add(new Token(TokenType.Ident, expr.Substring(start, i - start), 0));
                    }
                    else
                    {
                        throw new FormatException($"unexpected char '{c}' at offset {i}");
                    }
                    break;
            }
        }
        tokens.Add(new Token(TokenType.End, "", 0));
        return tokens;
    }

    // ── Recursive-descent parser ───────────────────────────────────────────
    //
    // Grammar:
    //   expression := term (('+' | '-') term)*
    //   term       := factor (('*' | '/') factor)*
    //   factor     := number | ident | func-call | '(' expression ')' | ('-' | '+') factor
    //   func-call  := ident '(' expression (',' expression)* ')'
    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;
        private readonly IReadOnlyDictionary<string, double> _vars;

        public Parser(List<Token> tokens, IReadOnlyDictionary<string, double> vars)
        {
            _tokens = tokens;
            _vars = vars;
        }

        public double ParseExpression()
        {
            double v = ParseTerm();
            while (Peek().Type is TokenType.Plus or TokenType.Minus)
            {
                var t = Next();
                double rhs = ParseTerm();
                v = t.Type == TokenType.Plus ? v + rhs : v - rhs;
            }
            return v;
        }

        private double ParseTerm()
        {
            double v = ParseFactor();
            while (Peek().Type is TokenType.Star or TokenType.Slash)
            {
                var t = Next();
                double rhs = ParseFactor();
                if (t.Type == TokenType.Star) v *= rhs;
                else
                {
                    if (rhs == 0) throw new DivideByZeroException("division by zero in formula");
                    v /= rhs;
                }
            }
            return v;
        }

        private double ParseFactor()
        {
            var t = Peek();
            // Unary minus / plus.
            if (t.Type == TokenType.Minus) { Next(); return -ParseFactor(); }
            if (t.Type == TokenType.Plus) { Next(); return ParseFactor(); }

            if (t.Type == TokenType.Number)
            {
                Next();
                return t.Value;
            }
            if (t.Type == TokenType.Ident)
            {
                Next();
                // Function call?
                if (Peek().Type == TokenType.LParen)
                {
                    Next(); // consume (
                    var args = new List<double>();
                    if (Peek().Type != TokenType.RParen)
                    {
                        args.Add(ParseExpression());
                        while (Peek().Type == TokenType.Comma)
                        {
                            Next();
                            args.Add(ParseExpression());
                        }
                    }
                    Expect(TokenType.RParen);
                    return ApplyFunction(t.Text, args);
                }
                // Variable reference.
                if (_vars.TryGetValue(t.Text, out var v)) return v;
                throw new FormatException($"unknown variable '{t.Text}'");
            }
            if (t.Type == TokenType.LParen)
            {
                Next();
                double v = ParseExpression();
                Expect(TokenType.RParen);
                return v;
            }
            throw new FormatException($"unexpected token '{t.Text}' (type {t.Type})");
        }

        private static double ApplyFunction(string name, List<double> args)
        {
            return name switch
            {
                "floor" => args.Count == 1 ? Math.Floor(args[0]) : throw BadArity(name, 1, args.Count),
                "ceil" => args.Count == 1 ? Math.Ceiling(args[0]) : throw BadArity(name, 1, args.Count),
                "round" => args.Count == 1 ? Math.Round(args[0], MidpointRounding.AwayFromZero) : throw BadArity(name, 1, args.Count),
                "abs" => args.Count == 1 ? Math.Abs(args[0]) : throw BadArity(name, 1, args.Count),
                "sqrt" => args.Count == 1 ? Math.Sqrt(args[0]) : throw BadArity(name, 1, args.Count),
                "min" => args.Count >= 1 ? args.Min() : throw BadArity(name, 1, args.Count),
                "max" => args.Count >= 1 ? args.Max() : throw BadArity(name, 1, args.Count),
                "pow" => args.Count == 2 ? Math.Pow(args[0], args[1]) : throw BadArity(name, 2, args.Count),
                _ => throw new FormatException($"unknown function '{name}'"),
            };
        }

        private static FormatException BadArity(string fn, int expected, int actual) =>
            new($"{fn}() expects {expected} arg(s), got {actual}");

        private Token Peek() => _tokens[_pos];

        private Token Next() => _tokens[_pos++];

        private void Expect(TokenType type)
        {
            if (Peek().Type != type)
                throw new FormatException($"expected {type}, got {Peek().Type} ('{Peek().Text}')");
            _pos++;
        }

        public void ExpectEnd()
        {
            if (Peek().Type != TokenType.End)
                throw new FormatException($"unexpected trailing token '{Peek().Text}'");
        }
    }
}
