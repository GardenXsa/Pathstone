namespace MyGame.Core.World;

/// <summary>
/// One entry in the world's lore database (issue #43): a topic (e.g.
/// <c>"history"</c>, <c>"magic"</c>) + its canonical content. The GM
/// queries entries via the <c>get_lore</c> tool to avoid hallucinating
/// world facts.
/// </summary>
public sealed class LoreEntry
{
    /// <summary>
    /// Topic key. Canonical values populated by the world-builder:
    /// <c>"deities"</c> (cosmology), <c>"history"</c>,
    /// <c>"magic"</c> (magic system), <c>"cultures"</c>,
    /// <c>"economy"</c>, <c>"current events"</c>. Free-form beyond
    /// that — the planner can introduce custom topics.
    /// </summary>
    public string Topic { get; set; } = "";

    /// <summary>
    /// Canonical content for this topic. Multi-line plain text (RU).
    /// The world-builder compiles this from the plan's structured lore
    /// fields (cosmology / history / magicSystem / cultures / economy /
    /// currentEvents).
    /// </summary>
    public string Content { get; set; } = "";
}

/// <summary>
/// Lore database for a world (issue #43): a list of <see cref="LoreEntry"/>
/// the GM can query via the <c>get_lore</c> tool. Null on
/// <see cref="World.Lore"/> means the world doesn't have a lore DB
/// (legacy saves / worlds built before this task); an empty database
/// means the builder ran but found no lore fields in the plan.
///
/// <para>
/// <b>Serialization:</b> plain POCO + System.Text.Json via
/// <see cref="WorldJson.Options"/>. Round-trips through save/load.
/// </para>
/// </summary>
public sealed class LoreDatabase
{
    /// <summary>All lore entries, in planner order.</summary>
    public List<LoreEntry> Entries { get; set; } = new();

    /// <summary>
    /// Case-insensitive topic lookup. Returns null when the topic isn't
    /// in the database (the GM tool surfaces this as an error to the
    /// model so it can re-prompt with the available topics).
    /// </summary>
    public LoreEntry? Get(string topic) =>
        Entries.FirstOrDefault(e =>
            e.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// All available topic keys, in planner order. Used by the
    /// <c>get_lore</c> tool when no topic is supplied (lists what's
    /// queryable) and by the GM context block as a hint.
    /// </summary>
    public IReadOnlyList<string> Topics => Entries.Select(e => e.Topic).ToList();
}
