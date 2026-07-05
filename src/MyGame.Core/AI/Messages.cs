using System.Text.Json.Serialization;

namespace MyGame.Core.AI;

/// <summary>
/// Role of a <see cref="ChatMessage"/> in the OpenAI chat-completions
/// protocol. Port of the string-union <c>'system' | 'user' | 'assistant' |
/// 'tool'</c> from <c>ai/messages.ts</c>.
/// </summary>
public enum ChatRole
{
    /// <summary>Director / system instructions — shapes model behavior.</summary>
    [JsonPropertyName("system")] System = 0,

    /// <summary>Player / human input.</summary>
    [JsonPropertyName("user")] User = 1,

    /// <summary>Model output (narration + optional tool_calls).</summary>
    [JsonPropertyName("assistant")] Assistant = 2,

    /// <summary>Result of a previous tool_call, fed back to the model.</summary>
    [JsonPropertyName("tool")] Tool = 3,
}

/// <summary>
/// One tool-call request issued by the model. Port of the
/// <c>tool_calls[].function</c> sub-shape from <c>ai/messages.ts</c>.
/// </summary>
public sealed record ToolCall
{
    /// <summary>
    /// Stable id the provider assigns so the next <c>role:tool</c> message
    /// can reference this call by <see cref="ChatMessage.ToolCallId"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Tool name (matches <see cref="ToolDefinition.Name"/>).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Raw JSON-string arguments the model produced. MUST be parsed
    /// defensively — the model can emit malformed JSON, in which case the
    /// tool registry wraps the parse error in a tool-result with
    /// <c>IsError</c>=true instead of throwing.
    /// </summary>
    public string Arguments { get; init; } = "{}";
}

/// <summary>
/// One chat message — structurally identical to the OpenAI
/// <c>/chat/completions</c> message shape. Port of <c>ChatMessage</c> from
/// <c>ai/messages.ts</c>.
///
/// <para>
/// Carries the four wire fields (<c>role</c>, <c>content</c>,
/// <c>tool_calls</c>, <c>tool_call_id</c>) plus the optional <c>name</c>
/// field OpenAI accepts on any message (used to differentiate multiple
/// authors of the same role, e.g. two players in a multiplayer session).
/// </para>
/// </summary>
public sealed record ChatMessage
{
    /// <summary>Sender role.</summary>
    [JsonPropertyName("role")]
    public required ChatRole Role { get; init; }

    /// <summary>
    /// Text content. Nullable because assistant messages that carry tool
    /// calls often have null content, and tool messages have their result
    /// in this field as a string.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>
    /// Tool calls issued by the model. Only set on assistant messages.
    /// Null on other roles.
    /// </summary>
    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Reference to the tool call this message answers. Only set on
    /// <see cref="ChatRole.Tool"/> messages.
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Optional author name (OpenAI accepts this on any role; used to
    /// differentiate multiple authors of the same role, e.g. multiple
    /// players in a multiplayer session).
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    // ── Factory helpers for the agent layer ───────────────────────────────

    /// <summary>Build a <see cref="ChatRole.System"/> message.</summary>
    public static ChatMessage System(string content) => new()
    {
        Role = ChatRole.System,
        Content = content,
    };

    /// <summary>Build a <see cref="ChatRole.User"/> message.</summary>
    public static ChatMessage User(string content) => new()
    {
        Role = ChatRole.User,
        Content = content,
    };

    /// <summary>Build a <see cref="ChatRole.User"/> message with a name.</summary>
    public static ChatMessage User(string content, string name) => new()
    {
        Role = ChatRole.User,
        Content = content,
        Name = name,
    };

    /// <summary>Build a <see cref="ChatRole.Assistant"/> message.</summary>
    public static ChatMessage Assistant(string? content) => new()
    {
        Role = ChatRole.Assistant,
        Content = content,
    };

    /// <summary>
    /// Build a <see cref="ChatRole.Assistant"/> message carrying a batch
    /// of tool calls (the model's request to invoke functions before it
    /// produces its final narration).
    /// </summary>
    public static ChatMessage AssistantWithTools(IReadOnlyList<ToolCall> toolCalls, string? content = null) => new()
    {
        Role = ChatRole.Assistant,
        Content = content,
        ToolCalls = toolCalls,
    };

    /// <summary>Build a <see cref="ChatRole.Tool"/> result message.</summary>
    public static ChatMessage ToolResult(string toolCallId, string content) => new()
    {
        Role = ChatRole.Tool,
        ToolCallId = toolCallId,
        Content = content,
    };
}

/// <summary>
/// OpenAI-compatible function-tool definition. Port of <c>ToolDef</c> from
/// <c>ai/tools/index.ts</c>.
///
/// <para>
/// The <see cref="ParametersJson"/> field is a JSON-schema STRING (not a
/// parsed object) so the registry can hold tools defined by hand-written
/// string literals without forcing each call site to construct a
/// <c>JsonElement</c>. The <see cref="AiClient"/> parses the string when
/// serializing the request body and embeds it as a raw JSON value.
/// </para>
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>Tool name (matches what the model emits in <c>tool_calls[].function.name</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description shown to the model.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// JSON schema for the tool's arguments, as a string. Empty/whitespace
    /// means <c>"{}"</c> (no parameters).
    /// </summary>
    public string ParametersJson { get; init; } = "{}";
}

/// <summary>
/// Normalised response from one chat-completion call. Port of the
/// <c>StreamResult</c> shape from <c>aiClient.ts</c> (renamed because the
/// C# client doesn't expose a separate streaming-result type — the streaming
/// API yields <see cref="string"/> deltas directly).
/// </summary>
public sealed record ChatResponse
{
    /// <summary>
    /// Concatenated assistant content. May be null/empty if the model only
    /// emitted tool calls this round.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Tool calls the model issued (parsed from
    /// <c>choices[0].message.tool_calls</c>). Null when the model didn't
    /// call any tools.
    /// </summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Provider's <c>finish_reason</c> for the choice
    /// (<c>stop</c>, <c>tool_calls</c>, <c>length</c>, <c>content_filter</c>).
    /// Null when not present (some providers omit it on errors).
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// Prompt (input) token count parsed from the provider's
    /// <c>usage.prompt_tokens</c> field. 0 when the provider didn't
    /// return a usage block.
    /// </summary>
    public int PromptTokens { get; init; }

    /// <summary>
    /// Completion (output) token count parsed from the provider's
    /// <c>usage.completion_tokens</c> field. 0 when the provider didn't
    /// return a usage block.
    /// </summary>
    public int CompletionTokens { get; init; }
}

/// <summary>
/// Default cap on how many turns of conversation we keep in the agent's
/// history. Port of <c>MAX_CONVERSATION_MESSAGES</c> from
/// <c>ai/messages.ts</c>. Each "turn" is one user action → GM response
/// (which may span multiple assistant/tool messages). 200 messages ≈ 30-50
/// turns, plenty for any provider's context window after the system prompt;
/// older history is trimmed from the front (FIFO) to keep memory bounded.
/// </summary>
public static class MessagesConstants
{
    /// <summary>Max messages kept in the GM's in-memory conversation history.</summary>
    public const int MaxConversationMessages = 200;
}
