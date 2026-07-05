using System.Text.Json;
using System.Text.Json.Serialization;
using MyGame.Core.Common;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

namespace MyGame.Core.World;

/// <summary>
/// The aggregate root: the authoritative simulation container holding every
/// entity collection, the in-world clock, the ruleset, and the deterministic
/// RNG. Port of <c>engine/world/world.ts</c>.
///
/// Scope of this port: the spec asks for the aggregate root with
/// <see cref="SpawnPlayer"/>, <see cref="SpawnNpc"/>, <see cref="AddLocation"/>,
/// <see cref="FindEntity"/>, and <see cref="ToJson"/>/<see cref="FromJson"/>.
/// The TS World has many more methods (movePlayer, damage, heal, giveItem,
/// logEntry, advanceTime, tick, conversation history, buildState, procedural
/// gen, token usage). Those are SKIPPED here — they belong to subsystems
/// (combat, narrative, AI) that don't exist in the C# port yet. They can be
/// layered onto <see cref="World"/> in later tasks without changing the
/// save schema. Skips are documented in the worklog.
///
/// Serialization: <see cref="World"/> is plain POCO + System.Text.Json.
/// Round-trip guarantees:
///  - All entity collections (players / npcs / items / buildings / locations /
///    quests) round-trip as JSON arrays.
///  - <see cref="RngState"/> (the PCG32 64-bit state) round-trips as a long;
///    the live <see cref="Rng"/> is reconstructed lazily from it on first
///    access after deserialize, so a reloaded save resumes deterministically
///    from the exact stream position the save was taken at.
///  - <see cref="Clock"/> (the simple <see cref="GameTime"/>) round-trips as
///    a struct.
///  - <see cref="CalendarSpec"/> (the rich calendar) round-trips as a record,
///    if the world-builder has installed one.
///  - <see cref="Ruleset"/> round-trips as a record (all enums + nested defs).
/// </summary>
public sealed class World
{
    // ─── Non-serialized runtime services ───────────────────────────────────

    /// <summary>
    /// In-process event bus. Subscribers wire up via
    /// <see cref="EventBus.Subscribe{T}"/>. Marked <c>[JsonIgnore]</c> — the
    /// bus is a runtime concern, not part of save state.
    /// </summary>
    [JsonIgnore]
    public EventBus Events { get; } = new();

    private ContentRegistry? _registries;

    /// <summary>
    /// Content registry (item / NPC / building templates). Injected by the
    /// caller — not serialized (templates live in the embedded
    /// <c>Content/data.json</c>, not in save files).
    /// </summary>
    [JsonIgnore]
    public ContentRegistry Registries
    {
        get => _registries ??= new ContentRegistry();
        set => _registries = value;
    }

    // ─── RNG (state round-trips; Rng object reconstructed lazily) ───────────

    /// <summary>
    /// The PCG32 internal state, snapshotted for save/load. The live
    /// <see cref="Rng"/> object is reconstructed from this on first access
    /// after deserialize. Mutating this property invalidates the cached
    /// <see cref="Rng"/> (the next getter call rebuilds it).
    /// </summary>
    public long RngState { get; set; }

    private Rng? _rng;

    /// <summary>
    /// The deterministic RNG. Lazily built from <see cref="RngState"/> when
    /// first accessed. Setting this property updates <see cref="RngState"/>
    /// to match (so a fresh New Game save immediately captures the seed).
    /// </summary>
    [JsonIgnore]
    public Rng Rng
    {
        get => _rng ??= Rng.FromState(RngState);
        set
        {
            _rng = value;
            RngState = value.State;
        }
    }

    /// <summary>
    /// Original seed used to start this save. Informational only (the engine
    /// resumes from <see cref="RngState"/>, not from this). Persisted so the
    /// save UI can show «Seed: 12345».
    /// </summary>
    public long Seed { get; set; }

    // ─── Clock + ruleset ───────────────────────────────────────────────────

    /// <summary>
    /// Simple in-world clock — primary time surface for the desktop UI.
    /// Defaults to <see cref="GameTime.Start"/> (day 1, 08:00).
    /// </summary>
    public GameTime Clock { get; set; } = GameTime.Start;

    /// <summary>
    /// Optional rich calendar (year/era/months/seasons/weekdays). When null,
    /// the World uses <see cref="Calendar.DefaultFantasyCalendar"/> for any
    /// rich formatting. Persisted so a world-builder-installed calendar
    /// survives save/reload.
    /// </summary>
    public CalendarSpec? CalendarSpec { get; set; }

    /// <summary>
    /// The ruleset that defines how this world plays. Every World has one —
    /// set at construction and mutated only by commit_ruleset /
    /// set_attribute / set_resource tools. Defaults to
    /// <see cref="Rulesets.DefaultDnd"/>.
    /// </summary>
    public Ruleset Ruleset { get; set; } = Rulesets.DefaultDnd;

    /// <summary>Turn counter — incremented by the simulation tick (TBD).</summary>
    public int Turn { get; set; }

    /// <summary>
    /// Structured combat state. Null when no combat is in progress
    /// (freeform mode — the default). When non-null and
    /// <see cref="CombatState.Active"/> = true, the GM must respect the
    /// turn order in <see cref="CombatState.TurnOrder"/> (the system-prompt
    /// world-state surfaces a "## БОЙ" block to enforce this). Set by the
    /// <c>start_combat</c> tool, cleared by <c>end_combat</c> (and
    /// auto-cleared when only the player remains in the turn order).
    /// </summary>
    public CombatState? Combat { get; set; }

    /// <summary>
    /// Active player id. In single-player, always the only player. In multi,
    /// rotates as the action queue resolves. Null until a player is spawned.
    /// </summary>
    public EntityId? ActivePlayerId { get; set; }

    /// <summary>Unix-ms timestamp when this World was first created.</summary>
    public long StartedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Cumulative playtime in ms across all sessions.</summary>
    public long PlaytimeMs { get; set; }

    // ─── Entity collections ────────────────────────────────────────────────
    //
    // Serialized as JSON arrays (not maps) — the entity's own Id is the key
    // on reload. This keeps the JSON compact and avoids the EntityId-as-key
    // serialization quirk (EntityId serializes as a string, which would make
    // Dictionary<EntityId, T> serialize with string keys — works but ugly).

    /// <summary>All players (one for single-player; many for multiplayer).</summary>
    public List<Player> Players { get; set; } = new();

    /// <summary>All NPCs.</summary>
    public List<Npc> Npcs { get; set; } = new();

    /// <summary>All loose world items (on the ground).</summary>
    public List<Item> Items { get; set; } = new();

    /// <summary>All buildings.</summary>
    public List<Building> Buildings { get; set; } = new();

    /// <summary>All locations (the world graph).</summary>
    public List<Location> Locations { get; set; } = new();

    /// <summary>All quests.</summary>
    public List<Quest> Quests { get; set; } = new();

    /// <summary>
    /// Free-form world-level metadata bag. Used by the world-builder to
    /// stash plan-derived atmosphere/title/setting/hook fields, and by the
    /// GM for arbitrary cross-entity state. Lazy-init to null to keep
    /// World JSON clean when unused.
    /// </summary>
    public Dictionary<string, object>? Flags { get; set; }

    // ─── Lookup index (rebuilt on demand, never serialized) ────────────────

    private Dictionary<EntityId, Entity>? _index;

    /// <summary>
    /// Find an entity by id across ALL collections (players, npcs, items,
    /// buildings, locations, quests). Returns null if not found.
    ///
    /// The index is rebuilt lazily and invalidated whenever a spawn / remove
    /// mutates a collection.
    /// </summary>
    public Entity? FindEntity(EntityId id)
    {
        _index ??= BuildIndex();
        return _index.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>Find a player by id. Null if not present.</summary>
    public Player? GetPlayer(EntityId id) => Players.FirstOrDefault(p => p.Id == id);

    /// <summary>Find an NPC by id. Null if not present.</summary>
    public Npc? GetNpc(EntityId id) => Npcs.FirstOrDefault(n => n.Id == id);

    /// <summary>Find a location by id. Null if not present.</summary>
    public Location? GetLocation(EntityId id) => Locations.FirstOrDefault(l => l.Id == id);

    /// <summary>Find a building by id. Null if not present.</summary>
    public Building? GetBuilding(EntityId id) => Buildings.FirstOrDefault(b => b.Id == id);

    /// <summary>Find an item (loose world item) by id. Null if not present.</summary>
    public Item? GetItem(EntityId id) => Items.FirstOrDefault(i => i.Id == id);

    /// <summary>Find a quest by id. Null if not present.</summary>
    public Quest? GetQuest(EntityId id) => Quests.FirstOrDefault(q => q.Id == id);

    /// <summary>
    /// Convenience accessor for the primary (first) player. Throws if the
    /// world has no players yet (single-player bootstrap phase).
    /// </summary>
    [JsonIgnore]
    public Player Player =>
        Players.Count > 0
            ? Players[0]
            : throw new InvalidOperationException("World has no players.");

    /// <summary>
    /// The active player (whose turn / action is being processed). Null
    /// before any player is spawned or if <see cref="ActivePlayerId"/> isn't
    /// set.
    /// </summary>
    [JsonIgnore]
    public Player? ActivePlayer
    {
        get
        {
            if (ActivePlayerId is null) return null;
            return Players.FirstOrDefault(p => p.Id == ActivePlayerId);
        }
    }

    // ─── Spawn / add methods ───────────────────────────────────────────────

    /// <summary>
    /// Add a player to the world. The first player added becomes the active
    /// player. Returns the added player (same reference) for chaining.
    /// </summary>
    public Player SpawnPlayer(Player player)
    {
        if (player is null) throw new ArgumentNullException(nameof(player));
        Players.Add(player);
        if (ActivePlayerId is null) ActivePlayerId = player.Id;
        InvalidateIndex();
        Events.Publish(new EntitySpawned(player.Id, "player", player.Name));
        return player;
    }

    /// <summary>
    /// Spawn an NPC in the world. The NPC is added to the global NPC list AND
    /// registered in its <see cref="Character.LocationId"/>'s
    /// <see cref="Location.Npcs"/> list (so location-scoped lookups find it).
    /// Returns the spawned NPC for chaining.
    /// </summary>
    public Npc SpawnNpc(Npc npc)
    {
        if (npc is null) throw new ArgumentNullException(nameof(npc));
        Npcs.Add(npc);
        var loc = GetLocation(npc.LocationId);
        if (loc is not null && !loc.Npcs.Contains(npc.Id))
        {
            loc.Npcs.Add(npc.Id);
        }
        InvalidateIndex();
        Events.Publish(new EntitySpawned(npc.Id, "npc", npc.Name));
        return npc;
    }

    /// <summary>
    /// Spawn an NPC by looking up the template id in the content registry
    /// and instantiating it at the given location. Returns null if the
    /// template isn't registered.
    ///
    /// Port of <c>world.spawnNpcFromTemplate</c>. The actual template-to-NPC
    /// materialization (attribute defaults, equipment, starting inventory)
    /// is delegated to <see cref="EntityFactory.CreateNpcFromTemplate"/>.
    /// </summary>
    public Npc? SpawnNpcFromTemplate(string templateId, EntityId locationId)
    {
        var tpl = Registries.Npcs.Get(templateId);
        if (tpl is null) return null;
        var npc = EntityFactory.CreateNpcFromTemplate(tpl, locationId, Registries, Ruleset);
        return SpawnNpc(npc);
    }

    /// <summary>
    /// Add a location to the world graph. Returns the added location for
    /// chaining. Does NOT connect it to anything — use
    /// <c>world.Locations[i].Exits.Add(...)</c> or a future Connect() helper.
    /// </summary>
    public Location AddLocation(Location location)
    {
        if (location is null) throw new ArgumentNullException(nameof(location));
        Locations.Add(location);
        InvalidateIndex();
        Events.Publish(new EntitySpawned(location.Id, "location", location.Name));
        return location;
    }

    /// <summary>
    /// Spawn a building at a location. Adds the building to the world AND
    /// registers it in the location's <see cref="Location.Buildings"/> list.
    /// </summary>
    public Building SpawnBuilding(Building building)
    {
        if (building is null) throw new ArgumentNullException(nameof(building));
        Buildings.Add(building);
        var loc = GetLocation(building.LocationId);
        if (loc is not null && !loc.Buildings.Contains(building.Id))
        {
            loc.Buildings.Add(building.Id);
        }
        InvalidateIndex();
        Events.Publish(new EntitySpawned(building.Id, "building", building.Name));
        return building;
    }

    /// <summary>
    /// Spawn a loose world item (on the ground) at a location. Adds the item
    /// to the world AND registers it in the location's
    /// <see cref="Location.Items"/> list.
    /// </summary>
    public Item SpawnItemOnGround(Item item, EntityId locationId)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        Items.Add(item);
        var loc = GetLocation(locationId);
        if (loc is not null && !loc.Items.Contains(item.Id))
        {
            loc.Items.Add(item.Id);
        }
        InvalidateIndex();
        Events.Publish(new EntitySpawned(item.Id, "item", item.Name));
        return item;
    }

    // ─── Serialization ─────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot this World to a JSON string. Captures every entity collection,
    /// the clock, the calendar, the ruleset, and the RNG state — enough for a
    /// save/load round-trip that resumes the simulation deterministically.
    /// </summary>
    public string ToJson() =>
        JsonSerializer.Serialize(this, WorldJson.Options);

    /// <summary>
    /// Rehydrate a World from a JSON string produced by <see cref="ToJson"/>.
    /// The caller must supply the live <see cref="ContentRegistry"/>
    /// (templates aren't persisted in saves).
    /// </summary>
    public static World FromJson(string json, ContentRegistry registries)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON string is empty.", nameof(json));
        if (registries is null) throw new ArgumentNullException(nameof(registries));

        var world = JsonSerializer.Deserialize<World>(json, WorldJson.Options)
            ?? throw new InvalidOperationException("Failed to deserialize World from JSON.");
        world.Registries = registries;
        // Force the lazy Rng to be built from the deserialized RngState (so
        // the next caller who touches Rng gets a fresh stream continuing
        // exactly where the save was taken).
        _ = world.Rng;
        return world;
    }

    // ─── Index management ──────────────────────────────────────────────────

    private void InvalidateIndex() => _index = null;

    private Dictionary<EntityId, Entity> BuildIndex()
    {
        var idx = new Dictionary<EntityId, Entity>();
        foreach (var p in Players) idx[p.Id] = p;
        foreach (var n in Npcs) idx[n.Id] = n;
        foreach (var i in Items) idx[i.Id] = i;
        foreach (var b in Buildings) idx[b.Id] = b;
        foreach (var l in Locations) idx[l.Id] = l;
        foreach (var q in Quests) idx[q.Id] = q;
        return idx;
    }
}

/// <summary>
/// Event published by <see cref="World.SpawnPlayer"/> /
/// <see cref="World.SpawnNpc"/> / <see cref="World.AddLocation"/> (and the
/// other spawn helpers) whenever a new entity enters the world. Subscribers
/// wire up via <c>world.Events.Subscribe&lt;EntitySpawned&gt;(...)</c>.
/// </summary>
public sealed record EntitySpawned(EntityId Id, string Kind, string Name);
