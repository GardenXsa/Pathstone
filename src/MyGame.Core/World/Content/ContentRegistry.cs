using System.Reflection;
using System.Text.Json;
using MyGame.Core.World.Entities;

namespace MyGame.Core.World.Content;

/// <summary>
/// NPC template — a static definition the world-builder / GM uses to spawn
/// NPCs with consistent attributes, equipment, and starting inventory.
/// Port of <c>NPCTemplate</c> from <c>engine/content/registry.ts</c>.
/// </summary>
public sealed record NpcTemplate
{
    /// <summary>Stable template id (e.g. <c>npc_tavern_keeper</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional flavor label (race / species).</summary>
    public string? Race { get; init; }

    /// <summary>Optional flavor label (class / archetype).</summary>
    public string? Class { get; init; }

    /// <summary>Level override (default = 1).</summary>
    public int? Level { get; init; }

    /// <summary>Attribute values keyed by ruleset attribute key.</summary>
    public required IReadOnlyDictionary<string, int> Attributes { get; init; }

    /// <summary>
    /// Optional explicit resource pools keyed by ruleset resource key. Any
    /// resource omitted here is derived from the ruleset's MaxFormula at
    /// spawn time.
    /// </summary>
    public IReadOnlyDictionary<string, int>? Resources { get; init; }

    /// <summary>Movement speed override.</summary>
    public int? Speed { get; init; }

    /// <summary>Default disposition toward the player.</summary>
    public string? Disposition { get; init; }

    /// <summary>AI GM behavior hint (<c>aggressive</c>, <c>merchant</c>, …).</summary>
    public required string Behavior { get; init; }

    /// <summary>Equipment template ids keyed by ruleset slot (weapon/armor/shield/…).</summary>
    public IReadOnlyDictionary<string, string>? Equipment { get; init; }

    /// <summary>Merchant inventory template ids (if merchant).</summary>
    public IReadOnlyList<string>? ShopInventory { get; init; }

    /// <summary>Default spawn inventory (template ids + quantities).</summary>
    public IReadOnlyList<TemplateStack>? StartingInventory { get; init; }

    /// <summary>Flavor description.</summary>
    public string? Description { get; init; }

    /// <summary>Aggro radius (ft) for hostile NPCs.</summary>
    public int? AggroRange { get; init; }
}

/// <summary>
/// A "stack" entry in an NPC template's starting inventory: a template id
/// and a quantity. Port of the inline shape from
/// <c>engine/content/registry.ts</c>.
/// </summary>
public sealed record TemplateStack(string TemplateId, int Quantity);

/// <summary>
/// Building template — a static definition used to spawn buildings with
/// consistent type/name/occupants. Port of <c>BuildingTemplate</c> from
/// <c>engine/content/registry.ts</c>.
/// </summary>
public sealed record BuildingTemplate
{
    /// <summary>Stable template id (e.g. <c>bld_tavern</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Building type (ruleset-defined).</summary>
    public required string Type { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Flavor text.</summary>
    public required string Description { get; init; }

    /// <summary>Whether the player can enter the building.</summary>
    public bool Enterable { get; init; }

    /// <summary>Whether the building starts locked.</summary>
    public bool? Locked { get; init; }

    /// <summary>NPC template ids that should occupy this building.</summary>
    public IReadOnlyList<string>? OccupantTemplateIds { get; init; }

    /// <summary>Shopkeeper NPC template id (if this building is a shop).</summary>
    public string? ShopkeeperTemplateId { get; init; }
}

/// <summary>
/// Aggregate content pack — the JSON shape loaded from the embedded
/// <c>data.json</c> resource. Port of <c>ContentPack</c> from
/// <c>engine/content/registry.ts</c>.
/// </summary>
public sealed class ContentPack
{
    /// <summary>Item templates.</summary>
    public List<ItemTemplate> Items { get; set; } = new();

    /// <summary>NPC templates.</summary>
    public List<NpcTemplate> Npcs { get; set; } = new();

    /// <summary>Building templates.</summary>
    public List<BuildingTemplate> Buildings { get; set; } = new();
}

// ─── Registries ────────────────────────────────────────────────────────────

/// <summary>
/// Registry of <see cref="ItemTemplate"/>s, keyed by template id. Port of
/// <c>ItemRegistry</c> from <c>engine/content/registry.ts</c>.
/// </summary>
public sealed class ItemRegistry
{
    private readonly Dictionary<string, ItemTemplate> _items = new();

    /// <summary>Register a template (overwrites existing).</summary>
    public void Register(ItemTemplate template) => _items[template.Id] = template;

    /// <summary>Register many templates.</summary>
    public void RegisterAll(IEnumerable<ItemTemplate> templates)
    {
        foreach (var t in templates) Register(t);
    }

    /// <summary>Lookup by id. Null if not present.</summary>
    public ItemTemplate? Get(string id) => _items.TryGetValue(id, out var v) ? v : null;

    /// <summary>All registered templates.</summary>
    public IReadOnlyList<ItemTemplate> All() => _items.Values.ToList();

    /// <summary>All templates of a given category.</summary>
    public IReadOnlyList<ItemTemplate> ByCategory(string category) =>
        _items.Values.Where(t => t.Category == category).ToList();
}

/// <summary>
/// Registry of <see cref="NpcTemplate"/>s, keyed by template id.
/// </summary>
public sealed class NpcRegistry
{
    private readonly Dictionary<string, NpcTemplate> _templates = new();

    /// <summary>Register a template (overwrites existing).</summary>
    public void Register(NpcTemplate t) => _templates[t.Id] = t;

    /// <summary>Register many templates.</summary>
    public void RegisterAll(IEnumerable<NpcTemplate> list)
    {
        foreach (var t in list) Register(t);
    }

    /// <summary>Lookup by id. Null if not present.</summary>
    public NpcTemplate? Get(string id) => _templates.TryGetValue(id, out var v) ? v : null;

    /// <summary>All registered templates.</summary>
    public IReadOnlyList<NpcTemplate> All() => _templates.Values.ToList();
}

/// <summary>
/// Registry of <see cref="BuildingTemplate"/>s, keyed by template id.
/// </summary>
public sealed class BuildingRegistry
{
    private readonly Dictionary<string, BuildingTemplate> _templates = new();

    /// <summary>Register a template (overwrites existing).</summary>
    public void Register(BuildingTemplate t) => _templates[t.Id] = t;

    /// <summary>Register many templates.</summary>
    public void RegisterAll(IEnumerable<BuildingTemplate> list)
    {
        foreach (var t in list) Register(t);
    }

    /// <summary>Lookup by id. Null if not present.</summary>
    public BuildingTemplate? Get(string id) => _templates.TryGetValue(id, out var v) ? v : null;

    /// <summary>All registered templates.</summary>
    public IReadOnlyList<BuildingTemplate> All() => _templates.Values.ToList();
}

/// <summary>
/// The aggregate content registry — owns an <see cref="ItemRegistry"/>,
/// <see cref="NpcRegistry"/>, and <see cref="BuildingRegistry"/>. Loads
/// them from an embedded <c>Content/data.json</c> resource on first access
/// (or on explicit <see cref="Load"/>).
///
/// Port of <c>createRegistries</c> / <c>Registries</c> from
/// <c>engine/content/registry.ts</c>.
/// </summary>
public sealed class ContentRegistry
{
    private static readonly Assembly Assembly = typeof(ContentRegistry).Assembly;
    private const string EmbeddedResourceName = "MyGame.Core.World.Content.data.json";

    /// <summary>Item templates.</summary>
    public ItemRegistry Items { get; } = new();

    /// <summary>NPC templates.</summary>
    public NpcRegistry Npcs { get; } = new();

    /// <summary>Building templates.</summary>
    public BuildingRegistry Buildings { get; } = new();

    /// <summary>
    /// Load a <see cref="ContentPack"/> into this registry, replacing any
    /// prior content with the same template id.
    /// </summary>
    public void Load(ContentPack? pack)
    {
        if (pack is null) return;
        Items.RegisterAll(pack.Items);
        Npcs.RegisterAll(pack.Npcs);
        Buildings.RegisterAll(pack.Buildings);
    }

    /// <summary>
    /// Load content from a JSON string. The JSON shape must match
    /// <see cref="ContentPack"/>. Returns this registry for chaining.
    /// </summary>
    public ContentRegistry LoadFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return this;
        var pack = JsonSerializer.Deserialize<ContentPack>(json, WorldJson.Options)
                   ?? new ContentPack();
        Load(pack);
        return this;
    }

    /// <summary>
    /// Load content from the embedded <c>Content/data.json</c> resource.
    /// Returns this registry for chaining. Throws if the resource is missing
    /// or malformed — that's a build-time error, not a runtime one.
    /// </summary>
    public ContentRegistry LoadEmbedded()
    {
        using var stream = Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded content resource not found: {EmbeddedResourceName}. " +
                "Ensure Content/data.json is configured as an EmbeddedResource in the csproj.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return LoadFromJson(json);
    }

    /// <summary>
    /// Convenience factory: build a fresh registry loaded with the embedded
    /// default content pack. Used by <c>DefaultWorldFactory</c>.
    /// </summary>
    public static ContentRegistry LoadDefault()
    {
        var reg = new ContentRegistry();
        reg.LoadEmbedded();
        return reg;
    }
}
