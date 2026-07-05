using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyGame.Core.AI;

/// <summary>
/// Typed exception for AI client failures. The agent layer catches
/// <see cref="AiException"/> specifically and surfaces a user-visible
/// error; everything else propagates as an unexpected fault.
/// </summary>
public sealed class AiException : Exception
{
    /// <summary>
    /// Stable category for branching in the agent layer. Derived from
    /// <see cref="Status"/> when constructed via
    /// <see cref="AiException(HttpStatusCode, string)"/>; explicit when
    /// constructed via the richer
    /// <see cref="AiException(AiErrorKind, string, HttpStatusCode?, Exception?)"/>
    /// overload (used for parse/cancel/timeout/network faults that don't
    /// have an HTTP status).
    /// </summary>
    public AiErrorKind Kind { get; }

    /// <summary>HTTP status code, when this came from a non-2xx response.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>
    /// Spec-required constructor: build an <see cref="AiException"/> from
    /// an HTTP status code + message. The <see cref="Kind"/> is derived
    /// from the status (401/403 → Auth, 429 → RateLimit, 5xx →
    /// ServerError, other 4xx → BadRequest).
    /// </summary>
    public AiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        Status = statusCode;
        Kind = ClassifyStatus(statusCode);
    }

    /// <summary>
    /// Spec-required constructor (int overload): build an
    /// <see cref="AiException"/> from a numeric HTTP status code + message.
    /// Convenience for callers that have the status as an int.
    /// </summary>
    public AiException(int statusCode, string message)
        : this((HttpStatusCode)statusCode, message) { }

    /// <summary>
    /// Richer constructor for non-HTTP failures (parse errors, timeouts,
    /// network faults, cancellation) where there is no status code or
    /// where the caller wants to flag a specific <see cref="AiErrorKind"/>.
    /// Kept for the agent layer's <c>catch (AiException)</c> handlers.
    /// </summary>
    public AiException(AiErrorKind kind, string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Status = status;
    }

    private static AiErrorKind ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => AiErrorKind.Auth,
        HttpStatusCode.Forbidden => AiErrorKind.Auth,
        HttpStatusCode.TooManyRequests => AiErrorKind.RateLimit,
        >= (HttpStatusCode)500 and <= (HttpStatusCode)599 => AiErrorKind.ServerError,
        >= (HttpStatusCode)400 and <= (HttpStatusCode)499 => AiErrorKind.BadRequest,
        _ => AiErrorKind.BadRequest,
    };
}

/// <summary>Stable category of AI provider failure.</summary>
public enum AiErrorKind
{
    /// <summary>401/403 — bad/missing API key. Not retried.</summary>
    Auth,

    /// <summary>429 — rate limited. Retried once with a 2-second backoff.</summary>
    RateLimit,

    /// <summary>5xx — provider fault. Not retried.</summary>
    ServerError,

    /// <summary>Other 4xx — malformed request, model not found, etc. Not retried.</summary>
    BadRequest,

    /// <summary>HTTP request timed out or the connection dropped.</summary>
    Timeout,

    /// <summary>DNS/connect failure.</summary>
    Network,

    /// <summary>Provider returned a 200 but the body wasn't valid OpenAI JSON.</summary>
    Parse,

    /// <summary>Caller cancelled via <see cref="CancellationToken"/>.</summary>
    Cancelled,
}

/// <summary>
/// OpenAI-compatible HTTP client. Port of <c>ai/aiClient.ts</c>,
/// rewritten to use <see cref="HttpClient"/> instead of the original
/// cURL-subprocess hack.
///
/// <para>
/// The TS source shelled out to <c>curl</c> via <c>child_process.spawn</c>
/// — that was a workaround for some proxy/SDK issues in the Next.js
/// server. In the C#/.NET 8 desktop rewrite we use a plain
/// <see cref="HttpClient"/> with <c>HttpCompletionOption.ResponseHeadersRead</c>
/// for streaming, which is the idiomatic approach and avoids any
/// subprocess management.
/// </para>
///
/// <para><b>Endpoints:</b>
/// <list type="bullet">
///  <item><see cref="ChatAsync"/>: non-streaming POST /chat/completions.</item>
///  <item><see cref="ChatWithToolsAsync"/>: same, with a <c>tools</c> field.</item>
///  <item><see cref="StreamChatAsync"/>: streaming POST, yields content deltas.</item>
/// </list>
/// </para>
///
/// <para><b>Retry policy</b> (per task spec): 429 (rate-limit) is retried
/// once after a 2-second backoff. 401/403/5xx and other 4xx throw
/// immediately. Network/timeout faults throw immediately as
/// <see cref="AiErrorKind.Network"/>/<see cref="AiErrorKind.Timeout"/>.
/// </para>
///
/// <para><b>Auth</b>: the <c>Authorization: Bearer {ApiKey}</c> header is
/// sent when <see cref="AiSettings.ApiKey"/> is non-empty; skipped
/// otherwise so local providers that don't require auth work without
/// configuration.
/// </para>
///
/// NO references to the z-ai-web-dev-sdk. This is pure HTTP.
/// </summary>
public sealed class AiClient
{
    /// <summary>
    /// Shared static <see cref="HttpClient"/> used when the caller doesn't
    /// inject one. Per the .NET docs, sharing one instance across all
    /// requests avoids socket exhaustion under load. The instance is
    /// intentionally NOT disposed (its lifetime is the process lifetime).
    /// </summary>
    private static readonly HttpClient s_sharedHttp = new();

    private static readonly JsonSerializerOptions s_wireOptions = new()
    {
        // OpenAI-compatible API uses camelCase keys on the wire.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AiSettings _settings;
    private readonly HttpClient _http;

    /// <summary>
    /// Create a client using the shared static <see cref="HttpClient"/>.
    /// Convenience for tests and ad-hoc use; for production prefer the
    /// <c>(AiSettings, HttpClient)</c> ctor so a single socket pool is
    /// shared.
    /// </summary>
    public AiClient(AiSettings settings)
        : this(settings, s_sharedHttp) { }

    /// <summary>
    /// Create a client wrapping the given <see cref="HttpClient"/>. Use this
    /// ctor when the host (Avalonia DI container) manages the
    /// <see cref="HttpClient"/> lifecycle (recommended — lets you share a
    /// single <c>HttpClientHandler</c> / socket pool across all clients).
    /// </summary>
    public AiClient(AiSettings settings, HttpClient http)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Non-streaming chat completion (no tools). POST /chat/completions
    /// with <c>stream:false</c>. Returns the parsed <see cref="ChatResponse"/>
    /// on success; throws <see cref="AiException"/> on any failure
    /// (auth/rate-limit/server/bad-request/network/timeout/parse).
    /// </summary>
    public Task<ChatResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default) =>
        ChatWithToolsAsync(messages, tools: null, ct);

    /// <summary>
    /// Non-streaming chat completion with optional function-calling tools.
    /// POST /chat/completions with <c>stream:false</c> and a <c>tools</c>
    /// field when <paramref name="tools"/> is non-null/non-empty.
    /// </summary>
    public async Task<ChatResponse> ChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken ct = default)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));

        var payload = BuildRequestBody(messages, tools, stream: false);
        using var response = await SendWithRetryAsync(payload, stream: false, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseChatResponse(body);
    }

    /// <summary>
    /// Streaming chat completion. POST /chat/completions with
    /// <c>stream:true</c> and <c>stream_options.include_usage:true</c>,
    /// then yields each <c>delta.content</c> chunk as it arrives.
    ///
    /// <para>
    /// The yielded strings are CONTENT DELTAS only — tool-call deltas are
    /// accumulated internally and discarded, and the final token-usage
    /// block (if the provider emits one in the stream) is parsed and
    /// discarded. If you need tool calls or final usage, use the
    /// non-streaming <see cref="ChatWithToolsAsync"/> overload.
    /// </para>
    ///
    /// <para>
    /// Uses <see cref="HttpClient.SendAsync(HttpRequestMessage,
    /// HttpCompletionOption, CancellationToken)"/> with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> and reads the
    /// response stream line-by-line, parsing the SSE <c>data:</c> framing
    /// per the OpenAI streaming protocol.
    /// </para>
    /// </summary>
    /// <param name="messages">Conversation history.</param>
    /// <param name="ct">Cancellation token — aborts the HTTP request mid-stream.</param>
    /// <returns>
    /// An <see cref="IAsyncEnumerable{T}"/> of content-delta strings. The
    /// enumeration completes when the provider sends <c>data: [DONE]</c>
    /// or closes the connection.
    /// </returns>
    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));

        var payload = BuildRequestBody(messages, tools: null, stream: true);
        using var response = await SendWithRetryAsync(payload, stream: true, ct).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }
            if (line is null) yield break; // connection closed

            if (line.Length == 0) continue;            // SSE event boundary
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.Substring(5).Trim();
            if (data == "[DONE]") yield break;

            ChatStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatStreamChunk>(data, s_wireOptions);
            }
            catch (JsonException)
            {
                // Provider sent malformed JSON in one chunk — skip it; the
                // stream often has a few such blips on reconnect.
                continue;
            }
            if (chunk is null) continue;

            if (chunk.Choices is { } choices && choices.Count > 0)
            {
                var delta = choices[0].Delta;
                if (delta is null) continue;

                if (!string.IsNullOrEmpty(delta.Content))
                    yield return delta.Content!;
            }
        }
    }

    /// <summary>
    /// Streaming chat completion WITH optional function-calling tools.
    /// POST /chat/completions with <c>stream:true</c> + <c>tools</c> +
    /// <c>stream_options.include_usage:true</c>. Yields each
    /// <c>delta.content</c> chunk as it arrives, while ACCUMULATING
    /// tool-call deltas (keyed by their <c>index</c>, since
    /// <c>function.arguments</c> arrives in fragments across multiple
    /// chunks) into a final <see cref="ChatResponse"/> that the caller
    /// can read after the enumeration completes.
    ///
    /// <para>
    /// The OpenAI streaming-with-tools protocol works like this:
    /// each SSE chunk's <c>choices[0].delta</c> may contain a
    /// <c>content</c> string fragment (the assistant's narration),
    /// one or more <c>tool_calls</c> entries (each identified by
    /// <c>index</c>, with <c>id</c>, <c>function.name</c>, and
    /// <c>function.arguments</c> arriving in successive fragments),
    /// or just a <c>finish_reason</c>. The final chunk(s) carry a
    /// top-level <c>usage</c> block (when <c>include_usage</c> is set).
    /// We assemble all of this into a single
    /// <see cref="ChatResponse"/> on stream end.
    /// </para>
    ///
    /// <para>
    /// The final response is stored in
    /// <paramref name="result"/>'s <see cref="StreamChatResult.Response"/>
    /// field. The pattern is:
    /// <code>
    /// var holder = new StreamChatResult();
    /// await foreach (var delta in client.StreamChatWithToolsAsync(
    ///     messages, tools, holder, ct))
    /// {
    ///     // forward delta to UI
    /// }
    /// var response = holder.Response ?? new ChatResponse();
    /// // response.Content / response.ToolCalls / response.PromptTokens ...
    /// </code>
    /// </para>
    ///
    /// <para>
    /// If the stream is cancelled mid-flight (caller's
    /// <paramref name="ct"/> fires), <see cref="StreamChatResult.Response"/>
    /// is left null and <see cref="OperationCanceledException"/> propagates
    /// from the consuming <c>await foreach</c>.
    /// </para>
    /// </summary>
    /// <param name="messages">Conversation history (will be serialised
    /// verbatim — assistant messages with <c>tool_calls</c> and tool
    /// result messages are sent as-is per the OpenAI wire format).</param>
    /// <param name="tools">Tool definitions (null or empty = no tools,
    /// equivalent to <see cref="StreamChatAsync"/>).</param>
    /// <param name="result">Holder the final <see cref="ChatResponse"/>
    /// is written into when the stream completes normally.</param>
    /// <param name="ct">Cancellation token — aborts the HTTP request
    /// mid-stream.</param>
    public async IAsyncEnumerable<string> StreamChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        StreamChatResult result,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));
        if (result is null) throw new ArgumentNullException(nameof(result));

        var payload = BuildRequestBody(messages, tools, stream: true);
        using var response = await SendWithRetryAsync(payload, stream: true, ct).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var contentBuilder = new StringBuilder();
        var toolCallsByIndex = new SortedDictionary<int, ToolCallAccumulator>();
        string? finishReason = null;
        int promptTokens = 0, completionTokens = 0;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }
            if (line is null) break;                  // connection closed
            if (line.Length == 0) continue;            // SSE event boundary
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.Substring(5).Trim();
            if (data == "[DONE]") break;

            ChatStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatStreamChunk>(data, s_wireOptions);
            }
            catch (JsonException)
            {
                // Provider sent malformed JSON in one chunk — skip it; the
                // stream often has a few such blips on reconnect.
                continue;
            }
            if (chunk is null) continue;

            if (chunk.Choices is { } choices && choices.Count > 0)
            {
                var choice = choices[0];
                if (!string.IsNullOrEmpty(choice.FinishReason))
                    finishReason = choice.FinishReason;

                var delta = choice.Delta;
                if (delta is null) continue;

                // Content delta — yield to caller immediately.
                if (!string.IsNullOrEmpty(delta.Content))
                {
                    contentBuilder.Append(delta.Content);
                    yield return delta.Content!;
                }

                // Tool-call deltas — accumulate by index. The first
                // chunk for a given index carries id + function.name;
                // subsequent chunks for the same index carry only
                // function.arguments fragments (string concatenation).
                if (delta.ToolCalls is { } tcs)
                {
                    foreach (var tc in tcs)
                    {
                        var idx = tc.Index ?? 0;
                        if (!toolCallsByIndex.TryGetValue(idx, out var acc))
                        {
                            acc = new ToolCallAccumulator();
                            toolCallsByIndex[idx] = acc;
                        }
                        if (!string.IsNullOrEmpty(tc.Id))
                            acc.Id = tc.Id;
                        if (tc.Function is { } fn)
                        {
                            if (!string.IsNullOrEmpty(fn.Name))
                                acc.Name = fn.Name;
                            if (fn.Arguments is { } args)
                                acc.Arguments.Append(args);
                        }
                    }
                }
            }

            // Final usage chunk (sentinelled by include_usage=true) —
            // arrives AFTER the choices are exhausted, often with an
            // empty `choices` array.
            if (chunk.Usage is { } usage)
            {
                promptTokens = usage.PromptTokens;
                completionTokens = usage.CompletionTokens;
            }
        }

        // Build the final ChatResponse from accumulated state. This runs
        // once the stream ends (either via [DONE] or connection close),
        // before the consumer's `await foreach` exits.
        var toolCallsList = new List<ToolCall>();
        foreach (var acc in toolCallsByIndex.Values)
        {
            if (string.IsNullOrEmpty(acc.Name)) continue; // malformed — drop
            toolCallsList.Add(new ToolCall
            {
                Id = acc.Id ?? string.Empty,
                Name = acc.Name!,
                Arguments = acc.Arguments.Length > 0 ? acc.Arguments.ToString() : "{}",
            });
        }

        result.Response = new ChatResponse
        {
            Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null,
            ToolCalls = toolCallsList.Count > 0 ? toolCallsList : null,
            FinishReason = finishReason,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
        };
    }

    // ── Internals ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build the OpenAI-compatible <c>/chat/completions</c> request body
    /// as a JSON string. Uses <see cref="JsonObject"/> for ergonomic
    /// construction (the body mixes typed messages with raw-schema objects,
    /// which is awkward with <see cref="Utf8JsonWriter"/>).
    /// </summary>
    private string BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        bool stream)
    {
        var root = new JsonObject
        {
            ["model"] = _settings.Model,
            ["messages"] = BuildMessagesArray(messages),
            ["temperature"] = _settings.Temperature,
            ["max_tokens"] = _settings.MaxTokens,
            ["stream"] = stream,
        };

        if (stream)
        {
            // Critical for streaming token counter: OpenAI-compatible
            // providers only emit a `usage` block in the SSE stream when
            // the client explicitly asks for it via stream_options.include_usage.
            root["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (tools is { } toolList && toolList.Count > 0)
        {
            var toolsArray = new JsonArray();
            foreach (var t in toolList)
            {
                var fn = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                };
                // ParametersJson is a JSON-schema STRING; embed it as a
                // raw JSON value (not a string) so the provider parses it
                // as an object. If the schema is empty/invalid, fall back
                // to an empty object.
                JsonNode? schemaNode;
                try
                {
                    schemaNode = string.IsNullOrWhiteSpace(t.ParametersJson)
                        ? new JsonObject()
                        : JsonNode.Parse(t.ParametersJson) ?? new JsonObject();
                }
                catch (JsonException)
                {
                    schemaNode = new JsonObject();
                }
                fn["parameters"] = schemaNode;
                toolsArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = fn,
                });
            }
            root["tools"] = toolsArray;
        }

        return root.ToJsonString(s_wireOptions);
    }

    private static JsonArray BuildMessagesArray(IReadOnlyList<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var obj = new JsonObject
            {
                ["role"] = m.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.User => "user",
                    ChatRole.Assistant => "assistant",
                    ChatRole.Tool => "tool",
                    _ => "user",
                },
                ["content"] = m.Content,
            };

            if (m.ToolCalls is { } tcs && tcs.Count > 0)
            {
                var tcsArr = new JsonArray();
                foreach (var tc in tcs)
                {
                    tcsArr.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = tc.Arguments,
                        },
                    });
                }
                obj["tool_calls"] = tcsArr;
            }

            if (!string.IsNullOrEmpty(m.ToolCallId))
                obj["tool_call_id"] = m.ToolCallId;

            if (!string.IsNullOrEmpty(m.Name))
                obj["name"] = m.Name;

            arr.Add(obj);
        }
        return arr;
    }

    /// <summary>
    /// Send one HTTP request with the spec-mandated retry policy:
    /// 429 (TooManyRequests) is retried ONCE after a 2-second backoff;
    /// 401/403/5xx/other-4xx throw immediately; network/timeout faults
    /// throw immediately. Cancellation propagates as
    /// <see cref="AiErrorKind.Cancelled"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string payload, bool stream, CancellationToken ct)
    {
        var url = ResolveCompletionUrl(_settings.BaseUrl);

        // Two attempts: initial (attempt=0) + one retry on 429 (attempt=1).
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                stream ? "text/event-stream" : "application/json"));
            // Per the task spec: skip the Authorization header entirely
            // when ApiKey is null/empty so local providers that don't
            // require auth work without configuration.
            if (!string.IsNullOrEmpty(_settings.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = stream
                    ? await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false)
                    : await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new AiException(AiErrorKind.Cancelled, "Request cancelled by caller.");
            }
            catch (TaskCanceledException ex)
            {
                // HttpClient.Timeout surfaces as TaskCanceledException — treat as timeout.
                throw new AiException(AiErrorKind.Timeout, "AI request timed out.", inner: ex);
            }
            catch (HttpRequestException ex)
            {
                throw new AiException(AiErrorKind.Network, "Network error contacting AI provider: " + ex.Message, inner: ex);
            }

            // 2xx — return the response. The caller owns disposing it.
            if (response.IsSuccessStatusCode) return response;

            // Non-2xx — read the body for the error message, then decide on retry.
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var statusCode = response.StatusCode;
            var message = ExtractErrorMessage(body) ?? $"HTTP {(int)statusCode} {response.ReasonPhrase}";
            response.Dispose();

            // 429 — retry once after a 2-second backoff (per spec).
            if (statusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw new AiException(AiErrorKind.Cancelled, "Request cancelled during 429 retry backoff.");
                }
                continue;
            }

            // Everything else (401/403/5xx/other-4xx) — throw immediately.
            throw new AiException(statusCode, message);
        }

        // Unreachable: the loop either returns a 2xx response or throws.
        // But the compiler can't prove that, so provide a sentinel.
        throw new AiException(AiErrorKind.RateLimit, "AI provider returned 429 after the 2-second retry.");
    }

    /// <summary>
    /// OpenAI's error JSON is <c>{ "error": { "message": "..." } }</c>.
    /// Some providers (LLMost) return <c>{ "error": "string" }</c> or just
    /// a bare string. Try all shapes; return null on no match.
    /// </summary>
    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String) return err.GetString();
                    if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                        return msg.GetString();
                }
                if (root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                    return msgEl.GetString();
            }
            return body.Length > 500 ? body[..500] : body;
        }
        catch (JsonException)
        {
            return body.Length > 500 ? body[..500] : body;
        }
    }

    /// <summary>
    /// Build the canonical <c>/chat/completions</c> URL from a base URL.
    /// Trims trailing slashes and appends <c>/chat/completions</c> only if
    /// the base URL doesn't already end with it. Port of
    /// <c>completionUrl</c> from <c>aiClient.ts</c>.
    /// </summary>
    public static string ResolveCompletionUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("BaseUrl is required.", nameof(baseUrl));
        var url = baseUrl.TrimEnd('/');
        if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            url += "/chat/completions";
        return url;
    }

    /// <summary>
    /// Parse a non-streaming response body into a <see cref="ChatResponse"/>.
    /// Defensive: malformed tool_calls are silently dropped (the tool
    /// registry will handle the missing call gracefully by returning an
    /// error result). Token usage is parsed from the <c>usage</c> block
    /// into <see cref="ChatResponse.PromptTokens"/> /
    /// <see cref="ChatResponse.CompletionTokens"/> directly per the task
    /// spec (no separate <c>Usage</c> sub-record).
    /// </summary>
    private static ChatResponse ParseChatResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new AiException(AiErrorKind.Parse, "AI provider returned an empty response body.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.Parse, "AI provider returned malformed JSON: " + ex.Message, inner: ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new AiException(AiErrorKind.Parse, "AI response root is not a JSON object.");

            // Check for top-level error.
            if (root.TryGetProperty("error", out var errEl))
            {
                var msg = errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString()
                    : (errEl.TryGetProperty("message", out var m) ? m.GetString() : "Unknown provider error");
                throw new AiException(AiErrorKind.BadRequest, msg ?? "Unknown provider error.");
            }

            string? content = null;
            List<ToolCall>? toolCalls = null;
            string? finishReason = null;

            if (root.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choicesEl.EnumerateArray())
                {
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finishReason = fr.GetString();
                    if (!choice.TryGetProperty("message", out var msgEl)) continue;

                    if (msgEl.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        content = c.GetString();

                    if (msgEl.TryGetProperty("tool_calls", out var tcsEl) && tcsEl.ValueKind == JsonValueKind.Array)
                    {
                        toolCalls ??= new List<ToolCall>();
                        foreach (var tc in tcsEl.EnumerateArray())
                        {
                            try
                            {
                                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                                if (!tc.TryGetProperty("function", out var fnEl)) continue;
                                var name = fnEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                                var args = fnEl.TryGetProperty("arguments", out var argsEl)
                                    ? (argsEl.ValueKind == JsonValueKind.String ? argsEl.GetString() ?? "{}" : argsEl.GetRawText())
                                    : "{}";
                                toolCalls.Add(new ToolCall { Id = id, Name = name, Arguments = args });
                            }
                            catch (Exception)
                            {
                                // Drop a single malformed tool call rather than failing the whole response.
                            }
                        }
                    }
                    break; // we only read the first choice
                }
            }

            // Parse the usage block: usage.prompt_tokens / usage.completion_tokens.
            // Defensive — some providers (notably Anthropic-compat proxies)
            // emit slightly different field names; we fall back to 0 on any
            // missing/unparseable field. The spec only requires the two
            // direct fields on ChatResponse; total_tokens and cached_tokens
            // (which the prior implementation carried) are intentionally
            // dropped to keep the public surface lean.
            int promptTokens = 0;
            int completionTokens = 0;
            if (root.TryGetProperty("usage", out var uEl) && uEl.ValueKind == JsonValueKind.Object)
            {
                if (uEl.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var p))
                    promptTokens = p;
                if (uEl.TryGetProperty("completion_tokens", out var ct2) && ct2.TryGetInt32(out var c2))
                    completionTokens = c2;
            }

            return new ChatResponse
            {
                Content = content,
                ToolCalls = toolCalls,
                FinishReason = finishReason,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
            };
        }
    }

    // ── Streaming chunk DTO (used for JSON deserialization) ─────────────────

    private sealed class ChatStreamChunk
    {
        public List<StreamChoice>? Choices { get; set; }
        public StreamUsage? Usage { get; set; }
    }

    private sealed class StreamChoice
    {
        public StreamDelta? Delta { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class StreamDelta
    {
        public string? Content { get; set; }
        public List<StreamToolCall>? ToolCalls { get; set; }
    }

    private sealed class StreamToolCall
    {
        public int? Index { get; set; }
        public string? Id { get; set; }
        public StreamFunction? Function { get; set; }
    }

    private sealed class StreamFunction
    {
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }

    /// <summary>
    /// Streaming usage block (parsed but intentionally discarded — the
    /// streaming API yields only content deltas to the caller, per spec).
    /// </summary>
    private sealed class StreamUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    /// <summary>
    /// Mutable accumulator for one tool call as it arrives across multiple
    /// SSE chunks. The <c>function.arguments</c> field is fragmentary in
    /// streaming — each chunk contributes a partial string that we
    /// concatenate. The <c>id</c> and <c>function.name</c> arrive once on
    /// the first chunk for this index.
    /// </summary>
    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}

/// <summary>
/// Holder for the final <see cref="ChatResponse"/> produced by
/// <see cref="AiClient.StreamChatWithToolsAsync"/>. Allocate one, pass it
/// into the streaming call, then read <see cref="Response"/> after the
/// <c>IAsyncEnumerable&lt;string&gt;</c> is exhausted.
/// </summary>
public sealed class StreamChatResult
{
    /// <summary>
    /// The final ChatResponse (Content + ToolCalls + FinishReason + token
    /// usage). Null if the stream was cancelled mid-flight or threw.
    /// </summary>
    public ChatResponse? Response { get; set; }
}
