using System.Collections;
using System.Text.Json;
using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// A typed lookup container that the <see cref="World"/> uses for each
/// entity kind. Wraps a <see cref="Dictionary{TKey, TValue}"/> keyed by
/// <see cref="EntityId"/> with a strongly-typed <typeparamref name="T"/>
/// value, and exposes only read-only surface plus add/remove/lookup
/// mutators.
///
/// The TS World used plain <c>Record&lt;EntityId, T&gt;</c> maps; this class
/// is the C#-idiomatic equivalent — typed, mutable internally, read-only on
/// the public surface.
/// </summary>
/// <typeparam name="T">Concrete entity type held in this collection.</typeparam>
public sealed class EntityCollection<T> : IEnumerable<T> where T : Entity
{
    private readonly Dictionary<EntityId, T> _byId = new();

    /// <summary>Create an empty collection.</summary>
    public EntityCollection() { }

    /// <summary>Number of entities in the collection.</summary>
    public int Count => _byId.Count;

    /// <summary>True if an entity with the given id is in the collection.</summary>
    public bool Contains(EntityId id) => _byId.ContainsKey(id);

    /// <summary>Lookup by id. Null if not present.</summary>
    public T? Get(EntityId id) => _byId.TryGetValue(id, out var v) ? v : null;

    /// <summary>
    /// Add an entity. If an entity with the same id is already present, it
    /// is overwritten. Returns the added entity for chaining.
    /// </summary>
    public T Add(T entity)
    {
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        _byId[entity.Id] = entity;
        return entity;
    }

    /// <summary>Remove an entity by id. Returns true if it was present.</summary>
    public bool Remove(EntityId id) => _byId.Remove(id);

    /// <summary>Remove all entities.</summary>
    public void Clear() => _byId.Clear();

    /// <summary>Snapshot of all entities as a list.</summary>
    public IReadOnlyList<T> All() => _byId.Values.ToList();

    /// <summary>Read-only snapshot of all entity ids.</summary>
    public IReadOnlyList<EntityId> Ids() => _byId.Keys.ToList();

    /// <summary>Enumerate entities.</summary>
    public IEnumerator<T> GetEnumerator() => _byId.Values.GetEnumerator();

    /// <summary>Non-generic enumerator (for IEnumerable).</summary>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A snapshot of every entity collection in a <see cref="World"/> — used as
/// the JSON-serializable shape for save/load. Each collection is a list
/// rather than a map (the entity's own <c>Id</c> is the key on reload).
/// </summary>
public sealed class EntitySnapshot
{
    /// <summary>All players (one for single-player, many for multiplayer).</summary>
    public List<Player> Players { get; set; } = new();

    /// <summary>All NPCs.</summary>
    public List<Npc> Npcs { get; set; } = new();

    /// <summary>All loose world items (on the ground).</summary>
    public List<Item> Items { get; set; } = new();

    /// <summary>All buildings.</summary>
    public List<Building> Buildings { get; set; } = new();

    /// <summary>All locations.</summary>
    public List<Location> Locations { get; set; } = new();

    /// <summary>All quests.</summary>
    public List<Quest> Quests { get; set; } = new();
}
