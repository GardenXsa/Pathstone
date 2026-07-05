using System.Text.Json.Serialization;

namespace MyGame.Core.AI;

/// <summary>
/// Structured world plan produced by the world-planner agent and consumed
/// by the location/population/buildings/content/narrator sub-agents. Port
/// of <c>ai/worldPlan.ts</c>.
///
/// Fields are intentionally simple (strings/numbers/arrays) so the LLM can
/// produce a valid JSON tool-call. Template IDs reference the standard
/// content registry (<c>World.Content.data.json</c>) or custom templates
/// the planner itself creates in <see cref="WorldPlan.CustomItemTemplates"/>
/// / <see cref="WorldPlan.CustomNpcTemplates"/> /
/// <see cref="WorldPlan.CustomBuildingTemplates"/>.
/// </summary>
public sealed record WorldPlan
{
    /// <summary>World title (e.g. «Тёмные Шпили Велариса»).</summary>
    public required string Title { get; init; }

    /// <summary>Genre / theme tag: <c>dark fantasy</c>, <c>cyberpunk</c>, etc.</summary>
    public required string Theme { get; init; }

    /// <summary>1–2 sentence setting description.</summary>
    public required string Setting { get; init; }

    /// <summary>Atmospheric tone — feeds the narrator's voice.</summary>
    public required string Atmosphere { get; init; }

    /// <summary>
    /// Generation mode chosen by the player at world creation.
    /// <c>full</c> = planner plans ALL regions; initial build creates the
    /// whole world. <c>chunked</c> = planner plans ONLY the start region;
    /// others are generated on-demand via travel. Defaults to <c>full</c>
    /// for back-compat with plans that predate the mode field.
    /// </summary>
    [JsonPropertyName("generationMode")]
    public string? GenerationMode { get; init; }

    /// <summary>Planner-authored step-by-step build plan (Codex plan mode style).</summary>
    public List<PlanActionStep>? ActionPlan { get; init; }

    // ─── World lore (the world beyond the start bubble) ────────────────────

    /// <summary>Cosmology and nature of the world.</summary>
    public PlanCosmology? Cosmology { get; init; }

    /// <summary>History by eras (oldest → current).</summary>
    public List<PlanHistoryEra>? History { get; init; }

    /// <summary>Major regions; the start area sits inside one of these.</summary>
    public List<PlanRegion>? Regions { get; init; }

    /// <summary>Peoples and cultures in the world.</summary>
    public List<PlanCulture>? Cultures { get; init; }

    /// <summary>World economy: currencies, trade routes, key goods.</summary>
    public PlanEconomy? Economy { get; init; }

    /// <summary>Magic / technology system.</summary>
    public PlanMagicSystem? MagicSystem { get; init; }

    /// <summary>Current geopolitical events (1–2 paragraphs).</summary>
    public string? CurrentEvents { get; init; }

    // ─── Start-area structure (the bubble the player starts in) ────────────

    /// <summary>Factions present in the world.</summary>
    public List<PlanFaction> Factions { get; init; } = new();

    /// <summary>Locations of the start area (graph nodes).</summary>
    public List<PlanLocation> Locations { get; init; } = new();

    /// <summary>NPCs of the start area.</summary>
    public List<PlanNpc> Npcs { get; init; } = new();

    /// <summary>Buildings of the start area.</summary>
    public List<PlanBuilding> Buildings { get; init; } = new();

    /// <summary>Custom item templates the planner invented for non-fantasy settings.</summary>
    public List<PlanCustomItemTemplate> CustomItemTemplates { get; init; } = new();

    /// <summary>Custom NPC templates the planner invented.</summary>
    public List<PlanCustomNpcTemplate> CustomNpcTemplates { get; init; } = new();

    /// <summary>Custom building templates the planner invented.</summary>
    public List<PlanCustomBuildingTemplate> CustomBuildingTemplates { get; init; } = new();

    /// <summary>Item template IDs granted as the player's starter gear.</summary>
    public List<string> StarterGear { get; init; } = new();

    /// <summary>Starting currency (gold/credits/…).</summary>
    public int StarterCurrency { get; init; }

    /// <summary>Opening plot hook (1–2 sentences) for the narrator.</summary>
    public required string StartingHook { get; init; }
}

/// <summary>
/// One step in the planner-authored action plan. Port of
/// <c>PlanActionStep</c> from <c>worldPlan.ts</c>.
/// </summary>
public sealed record PlanActionStep
{
    /// <summary>Stage this step belongs to.</summary>
    public required string Stage { get; init; }

    /// <summary>Short title.</summary>
    public required string Title { get; init; }

    /// <summary>What to do.</summary>
    public required string Instructions { get; init; }

    /// <summary>Optional agent name (for multi-agent orchestrators).</summary>
    public string? Agent { get; init; }
}

/// <summary>Role a location plays in the world graph.</summary>
public enum LocationRole
{
    Start,
    Hub,
    Settlement,
    Wilderness,
    Dungeon,
    Dangerous,
    Landmark,
}

/// <summary>
/// One location in the plan graph. Port of <c>PlanLocation</c>.
/// The planner produces these; the locations sub-agent instantiates them.
/// </summary>
public sealed record PlanLocation
{
    /// <summary>Short codename (used by NPC/building plans for references).</summary>
    public required string Name { get; init; }

    /// <summary>Terrain type: plains / forest / mountain / … (ruleset vocabulary).</summary>
    public required string Terrain { get; init; }

    /// <summary>Danger level (0–10) — drives random-encounter rolls.</summary>
    public int Danger { get; init; }

    /// <summary>Role in the world graph.</summary>
    [JsonPropertyName("role")]
    public LocationRole Role { get; init; } = LocationRole.Settlement;

    /// <summary>Atmospheric description.</summary>
    public required string Description { get; init; }

    /// <summary>Names of locations this one connects to.</summary>
    public List<string> Connections { get; init; } = new();

    /// <summary>Direction label from the hub/start (e.g. «север», «к таверне»).</summary>
    public string? DirectionFromHub { get; init; }

    /// <summary>
    /// Region name (from <see cref="WorldPlan.Regions"/>) this location
    /// belongs to. In <c>chunked</c> mode only the start region has
    /// locations in the plan; others are generated on demand.
    /// </summary>
    public string? Region { get; init; }
}

/// <summary>One NPC in the plan.</summary>
public sealed record PlanNpc
{
    /// <summary>Unique NPC name (goes into <c>nameOverride</c> at spawn time).</summary>
    public required string Name { get; init; }

    /// <summary>Standard template id OR a <see cref="PlanCustomNpcTemplate.Id"/>.</summary>
    public required string Template { get; init; }

    /// <summary>Location name (from <see cref="WorldPlan.Locations"/>).</summary>
    public required string Location { get; init; }

    /// <summary>Role in the world.</summary>
    public string? Role { get; init; }

    /// <summary>Disposition toward the player.</summary>
    public string? Disposition { get; init; }

    /// <summary>AI GM behavior hint.</summary>
    public string? Behavior { get; init; }

    /// <summary>Character level.</summary>
    public int Level { get; init; }

    /// <summary>Notes for the narrator / GM (personality, motivation).</summary>
    public required string Notes { get; init; }
}

/// <summary>One building placement in the plan.</summary>
public sealed record PlanBuilding
{
    /// <summary>Standard template id OR a <see cref="PlanCustomBuildingTemplate.Id"/>.</summary>
    public required string Template { get; init; }

    /// <summary>Location name where the building sits.</summary>
    public required string Location { get; init; }

    /// <summary>Optional display-name override.</summary>
    public string? NameOverride { get; init; }
}

/// <summary>A faction in the world (state, guild, cult, band, corp, clan, order).</summary>
public sealed record PlanFaction
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Alignment: good / neutral / evil / chaotic / lawful (free-form).</summary>
    public required string Alignment { get; init; }

    /// <summary>Faction type (state / guild / cult / band / corp / clan / order).</summary>
    public string? Type { get; init; }

    /// <summary>Where the faction is based (region, capital, territory).</summary>
    public string? Territory { get; init; }

    /// <summary>Goals and motivation (1–2 sentences).</summary>
    public string? Goals { get; init; }

    /// <summary>Key resources / source of power.</summary>
    public string? Resources { get; init; }

    /// <summary>Size: small / medium / large / empire.</summary>
    public string? Size { get; init; }

    /// <summary>Relations with other factions.</summary>
    public List<PlanFactionRelation>? Relations { get; init; }
}

/// <summary>One relation entry on a faction.</summary>
public sealed record PlanFactionRelation
{
    public required string Faction { get; init; }
    public required string Relation { get; init; }
    public string? Note { get; init; }
}

/// <summary>Cosmology + fundamental nature of the world.</summary>
public sealed record PlanCosmology
{
    public required string Origin { get; init; }
    public required string Nature { get; init; }
    public string? HigherPowers { get; init; }
    public string? CosmicThreats { get; init; }
}

/// <summary>One historical era in the world's timeline.</summary>
public sealed record PlanHistoryEra
{
    public required string Name { get; init; }
    public string? YearsAgo { get; init; }
    public List<string> Events { get; init; } = new();
    public required string Legacy { get; init; }
}

/// <summary>A major geographical region.</summary>
public sealed record PlanRegion
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Climate { get; init; }
    public required string Population { get; init; }
    public required string Economy { get; init; }
    public string? Capital { get; init; }
    public required string Politics { get; init; }
    public required string Culture { get; init; }

    /// <summary>True if the start area sits inside this region.</summary>
    public bool? ContainsStart { get; init; }

    /// <summary>
    /// Generation status (chunked mode only). The start region is
    /// <c>ready</c> after the initial build; others start <c>cold</c> and
    /// flip to <c>generating</c> then <c>ready</c> when the player travels
    /// there.
    /// </summary>
    [JsonPropertyName("genStatus")]
    public string? GenStatus { get; init; }
}

/// <summary>A people / culture / nation the player may encounter.</summary>
public sealed record PlanCulture
{
    public required string Name { get; init; }
    public required string Race { get; init; }
    public string? Religion { get; init; }
    public string? Language { get; init; }
    public required string Customs { get; init; }
    public required string AttitudeToOutsiders { get; init; }
    public string? Appearance { get; init; }
}

/// <summary>The world's economic system.</summary>
public sealed record PlanEconomy
{
    public required string Currencies { get; init; }
    public required string TradeRoutes { get; init; }
    public required string KeyGoods { get; init; }
    public string? MajorGuilds { get; init; }
    public required string Prosperity { get; init; }
}

/// <summary>Magic / technology system of the world.</summary>
public sealed record PlanMagicSystem
{
    public required string Type { get; init; }
    public required string Source { get; init; }
    public required string Wielders { get; init; }
    public required string Limits { get; init; }
    public required string PublicAttitude { get; init; }
}

/// <summary>Custom item template invented by the planner.</summary>
public sealed record PlanCustomItemTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public double? Weight { get; init; }
    public double? Value { get; init; }
    public string? Rarity { get; init; }

    /// <summary>Optional weapon profile.</summary>
    public PlanCustomWeapon? Weapon { get; init; }

    /// <summary>Optional armor profile.</summary>
    public PlanCustomArmor? Armor { get; init; }

    /// <summary>Optional consumable profile.</summary>
    public PlanCustomConsumable? Consumable { get; init; }
}

/// <summary>Weapon sub-profile for a custom item template.</summary>
public sealed record PlanCustomWeapon
{
    public required string Type { get; init; }
    public required PlanDamage Damage { get; init; }
    public bool? Finesse { get; init; }
    public bool? TwoHanded { get; init; }
    public int? Range { get; init; }
}

/// <summary>Damage expression for a custom weapon.</summary>
public sealed record PlanDamage
{
    public required string Dice { get; init; }
    public required string Type { get; init; }
}

/// <summary>Armor sub-profile for a custom item template.</summary>
public sealed record PlanCustomArmor
{
    public int BaseAc { get; init; }
    public required string Type { get; init; }
    public int? DexBonusMax { get; init; }
    public bool? StealthDisadvantage { get; init; }
}

/// <summary>Consumable sub-profile for a custom item template.</summary>
public sealed record PlanCustomConsumable
{
    public string? Healing { get; init; }
    public string? Effect { get; init; }
}

/// <summary>Custom NPC template invented by the planner.</summary>
public sealed record PlanCustomNpcTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Race { get; init; }
    [JsonPropertyName("class")]
    public required string Class { get; init; }
    public int Level { get; init; }
    public Dictionary<string, int> Attributes { get; init; } = new();
    public Dictionary<string, int>? Resources { get; init; }
    public required string Disposition { get; init; }
    public required string Behavior { get; init; }
    public Dictionary<string, string>? Equipment { get; init; }
    public required string Description { get; init; }
}

/// <summary>Custom building template invented by the planner.</summary>
public sealed record PlanCustomBuildingTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public bool Enterable { get; init; }

    /// <summary>If true, a shopkeeper NPC spawns inside.</summary>
    public bool? Shop { get; init; }
}

/// <summary>
/// Request to start a world-build. Port-side record — the TS source passed
/// a bare <c>brief</c> string; we wrap it (plus optional caller hints) so
/// the orchestrator can carry extra fields like the desired generation
/// mode, an optional seed, and a target ruleset name without changing the
/// orchestrator's signature.
/// </summary>
public sealed record WorldPlanRequest
{
    /// <summary>Free-form player brief (the world they want).</summary>
    public required string Brief { get; init; }

    /// <summary>Optional generation mode override; null = planner chooses.</summary>
    public string? GenerationMode { get; init; }

    /// <summary>Optional RNG seed for deterministic build (debugging / replays).</summary>
    public long? Seed { get; init; }

    /// <summary>Optional ruleset name hint (e.g. <c>cyberpunk-lite</c>).</summary>
    public string? RulesetHint { get; init; }
}

/// <summary>
/// Progress report emitted by the world-builder orchestrator. The UI binds
/// a progress bar / step list to this.
/// </summary>
public sealed record WorldBuildProgress
{
    /// <summary>Stage id (planning / ruleset / locations / population / buildings / content / narration / done / error).</summary>
    public required string Stage { get; init; }

    /// <summary>Status of this stage.</summary>
    public ProgressStatus Status { get; init; } = ProgressStatus.Active;

    /// <summary>Human-readable label (RU).</summary>
    public required string Label { get; init; }

    /// <summary>Optional detail / sub-step text.</summary>
    public string? Detail { get; init; }

    /// <summary>0–100 percent complete for the whole build.</summary>
    public int Percent { get; init; }
}

/// <summary>Status of one build stage.</summary>
public enum ProgressStatus
{
    Active,
    Done,
    Error,
    Skipped,
}
