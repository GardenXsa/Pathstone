namespace MyGame.Core.AI;

/// <summary>
/// Abstraction over an OpenAI-compatible chat-completions client.
/// Implemented by <see cref="AiClient"/> (production) and by test stubs
/// (e.g. <c>StubAiClient</c> in MyGame.Tests) that return canned
/// <see cref="ChatResponse"/> objects without making any HTTP call.
///
/// <para>
/// Introduced for issue #57 (multiplayer integration tests) so the
/// <see cref="Agents.GameMaster"/> can be constructed in tests with a
/// stub client that returns deterministic narration, letting the
/// end-to-end host-server + game-client protocol be exercised without
/// contacting a real LLM provider.
/// </para>
///
/// <para>
/// <b>Members:</b>
/// <list type="bullet">
///   <item><see cref="Settings"/> — the <see cref="AiSettings"/> the
///     client was constructed with. Agents read this to derive
///     role-specific clients + per-call token caps (e.g. the GM's
///     summarization call derives a one-off client with a lower
///     MaxTokens).</item>
///   <item><see cref="WithModel"/> — return a client that shares this
///     client's HTTP/socket pool (in the production implementation) but
///     uses the given model id. Used by the agents to derive a
///     role-specific client for a single run. Test stubs return
///     <c>this</c> (model overrides are irrelevant when the response is
///     canned).</item>
///   <item><see cref="ChatAsync"/> — non-streaming chat completion, no
///     tools.</item>
///   <item><see cref="ChatWithToolsAsync"/> — non-streaming chat
///     completion with optional function-calling tools.</item>
///   <item><see cref="StreamChatAsync"/> — streaming chat completion
///     (content deltas only).</item>
///   <item><see cref="StreamChatWithToolsAsync"/> — streaming chat
///     completion with tools, accumulating tool-call deltas into the
///     supplied <see cref="StreamChatResult"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Implementation notes for test stubs:</b>
/// <list type="bullet">
///   <item><see cref="WithModel"/> may return <c>this</c> — the stub's
///     <see cref="Settings"/> + canned-response queue don't depend on
///     the model id.</item>
///   <item><see cref="StreamChatWithToolsAsync"/> should yield the
///     canned content as a single delta and populate the
///     <see cref="StreamChatResult.Response"/> with the full
///     <see cref="ChatResponse"/> (content + token counts) so the
///     agent's loop terminates immediately on iteration 1 (no tool
///     calls → loop exits).</item>
///   <item>Token counts in the canned <see cref="ChatResponse"/> are
///     informational — tests can set them to any value for assertion
///     purposes.</item>
/// </list>
/// </para>
/// </summary>
public interface IAiClient
{
    /// <summary>
    /// The settings this client was constructed with. Exposed so callers
    /// that hold an <see cref="IAiClient"/> can read back the resolved
    /// <see cref="AiSettings.Model"/> etc. without keeping a separate
    /// settings reference.
    /// </summary>
    AiSettings Settings { get; }

    /// <summary>
    /// Return a client that uses the given model id instead of
    /// <see cref="AiSettings.Model"/>. Implementations should share the
    /// underlying HTTP/socket pool when one exists (production
    /// <see cref="AiClient"/>); test stubs may simply return
    /// <c>this</c>.
    /// <para>
    /// When <paramref name="model"/> is null/empty/whitespace OR equals
    /// the current model, implementations should return the same
    /// instance (no allocation). This makes the pattern
    /// <c>ai.WithModel(roleOverride ?? ai.Settings.Model)</c> a no-op
    /// when no override is set.
    /// </para>
    /// </summary>
    IAiClient WithModel(string? model);

    /// <summary>
    /// Non-streaming chat completion (no tools). Equivalent to calling
    /// <see cref="ChatWithToolsAsync"/> with <c>tools: null</c>.
    /// </summary>
    Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default);

    /// <summary>
    /// Non-streaming chat completion with optional function-calling
    /// tools. Returns the parsed <see cref="ChatResponse"/> on success;
    /// throws <see cref="AiException"/> on any failure.
    /// </summary>
    Task<ChatResponse> ChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming chat completion. Yields each <c>delta.content</c>
    /// chunk as it arrives. Tool-call deltas are accumulated internally
    /// and discarded; for tool-call support use
    /// <see cref="StreamChatWithToolsAsync"/>.
    /// </summary>
    IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming chat completion WITH optional function-calling tools.
    /// Yields each <c>delta.content</c> chunk as it arrives while
    /// accumulating tool-call deltas (keyed by their <c>index</c>) into
    /// the supplied <paramref name="result"/> holder. The final
    /// <see cref="ChatResponse"/> is written to
    /// <see cref="StreamChatResult.Response"/> when the stream
    /// completes.
    /// </summary>
    IAsyncEnumerable<string> StreamChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        StreamChatResult result,
        CancellationToken ct = default);
}
