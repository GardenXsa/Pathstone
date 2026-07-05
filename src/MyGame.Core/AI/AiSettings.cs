namespace MyGame.Core.AI;

/// <summary>
/// Connection + sampling settings for the OpenAI-compatible AI provider.
///
/// Port-side record (no direct TS counterpart — the TS source kept these
/// fields on <c>lib/settings.ts</c>'s <c>AISettings</c> interface, which
/// mixed UI/server concerns with pure connection data). This C# record is
/// the pure transport layer: it carries only what
/// <see cref="AiClient.ChatAsync"/> needs to format a request, and it
/// serialises plainly (System.Text.Json defaults) so a settings file on
/// disk round-trips losslessly.
///
/// <para>
/// There is NO reference to the z-ai-web-dev-sdk here. The desktop rewrite
/// talks to any OpenAI-compatible provider (OpenAI, LLMost, DeepSeek,
/// local LM Studio, etc.) over plain HTTP — see <see cref="AiClient"/>.
/// </para>
/// </summary>
public sealed record AiSettings
{
    /// <summary>
    /// Base URL of the OpenAI-compatible API, WITHOUT the trailing
    /// <c>/chat/completions</c> path. Defaults to OpenAI's public endpoint.
    /// <see cref="AiClient"/> appends <c>/chat/completions</c> itself, so a
    /// base URL of <c>https://api.openai.com/v1</c> resolves to
    /// <c>https://api.openai.com/v1/chat/completions</c>.
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    /// <summary>
    /// Bearer token sent in the <c>Authorization</c> header. Null/empty
    /// means anonymous (only valid for local providers that don't enforce
    /// auth). The <see cref="AiClient"/> skips the <c>Authorization</c>
    /// header when ApiKey is null so local servers don't reject the request,
    /// and throws a typed <see cref="AiException"/> if the provider returns
    /// 401/403.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Model id passed in the request body (<c>gpt-4o-mini</c>,
    /// <c>deepseek-chat</c>, <c>llama3.1:8b</c>, …). Defaults to OpenAI's
    /// cheapest current model — the desktop MVP targets low cost-per-turn.
    /// </summary>
    public string Model { get; init; } = "gpt-4o-mini";

    /// <summary>
    /// Sampling temperature, 0.0 = greedy, 2.0 = max chaos. Default 0.7 is
    /// the OpenAI-recommended starting point for creative narrative.
    /// </summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>
    /// Hard cap on completion tokens per request. Default 2000 is enough
    /// for a couple of paragraphs of narration + a tool-call round. The
    /// world-builder orchestrator uses a higher cap (configured at the
    /// call site, not here — this record is the shared default).
    /// </summary>
    public int MaxTokens { get; init; } = 2000;
}
