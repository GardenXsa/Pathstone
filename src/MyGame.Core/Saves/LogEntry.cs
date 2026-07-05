namespace MyGame.Core.Saves;

/// <summary>
/// One entry in a save's narrative/event log. The save's <c>log.json</c>
/// file is an array of these. Kinds mirror the TS app's log categories
/// (narrative / action / system / tool) so existing UI styling carries
/// over.
/// </summary>
/// <param name="Id">Unique per-entry id (so the UI can dedupe / key by id).</param>
/// <param name="Timestamp">UTC moment the entry was created.</param>
/// <param name="Kind">
/// One of <c>"narrative"</c>, <c>"action"</c>, <c>"system"</c>,
/// <c>"tool"</c>. Free-form string (kept as a string rather than an enum
/// so future kinds don't require a schema migration).
/// </param>
/// <param name="AuthorId">
/// Id of the player who triggered this entry. Null for system entries
/// (autosave, world-build stage, etc.).
/// </param>
/// <param name="Text">Human-readable entry text (may be markdown).</param>
/// <param name="Metadata">
/// Optional structured payload — e.g. for a tool entry, the tool name +
/// arguments + result JSON; for an action entry, the dice-check
/// breakdown. Values are <see cref="object"/> (System.Text.Json will
/// round-trip them as <see cref="System.Text.Json.JsonElement"/>).
/// </param>
public sealed record LogEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Kind,
    string? AuthorId,
    string Text,
    IReadOnlyDictionary<string, object>? Metadata)
{
    /// <summary>
    /// Convenience factory for a fresh narrative entry (auto-generates
    /// Id + Timestamp). The most common entry kind.
    /// </summary>
    public static LogEntry Narrative(string text, string? authorId = null,
        IReadOnlyDictionary<string, object>? metadata = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "narrative",
            authorId, text, metadata);

    /// <summary>Factory for a fresh action entry.</summary>
    public static LogEntry Action(string text, string? authorId = null,
        IReadOnlyDictionary<string, object>? metadata = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "action",
            authorId, text, metadata);

    /// <summary>Factory for a fresh system entry (autosave, build stage, …).</summary>
    public static LogEntry System(string text,
        IReadOnlyDictionary<string, object>? metadata = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "system",
            null, text, metadata);

    /// <summary>Factory for a fresh tool-call entry (GM tool invocation).</summary>
    public static LogEntry Tool(string text, string? authorId = null,
        IReadOnlyDictionary<string, object>? metadata = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "tool",
            authorId, text, metadata);
}
