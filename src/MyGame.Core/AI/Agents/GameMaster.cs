using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.World;
using MyGame.Core.World.Entities;


namespace MyGame.Core.AI.Agents;

/// <summary>
/// Result of one GameMaster turn. Port-side record (the TS source returned
/// a bare <c>string</c> narration; we wrap it to also surface token usage
/// and applied tool calls for the UI).
/// </summary>
public sealed record NarrativeResult
{
    /// <summary>
    /// Concatenated assistant narration across all iterations of the
    /// tool-call loop. May be empty if the model only emitted tool calls.
    /// </summary>
    public required string NarrativeText { get; init; }

    /// <summary>Total prompt tokens billed across all iterations.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Total completion tokens billed across all iterations.</summary>
    public int CompletionTokens { get; init; }

    /// <summary>Total tokens billed across all iterations.</summary>
    public int TotalTokens { get; init; }

    /// <summary>How many iterations the tool-call loop ran.</summary>
    public int Iterations { get; init; }

    /// <summary>Final <c>finish_reason</c> from the provider.</summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// Tool calls the GM made this turn (name, raw args JSON, result text,
    /// whether it errored). Useful for the UI's tool-call feed.
    /// </summary>
    public IReadOnlyList<AppliedToolCall> ToolCalls { get; init; } = Array.Empty<AppliedToolCall>();

    /// <summary>True if the loop terminated via an AI exception (auth, network, etc.).</summary>
    public bool Failed { get; init; }

    /// <summary>Error message when <see cref="Failed"/> is true.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// One tool call the GM executed during a turn. Port-side record for the
/// UI's tool-call feed.
/// </summary>
public sealed record AppliedToolCall
{
    public required string Name { get; init; }
    public required string ArgsJson { get; init; }
    public required string Result { get; init; }
    public bool IsError { get; init; }
}

/// <summary>
/// AI Game Master agent. Port of <c>ai/agents/gameMaster.ts</c> (core loop
/// only — features tied to the Next.js server are skipped, see worklog).
///
/// Flow:
///  <list type="number">
///  <item>Build messages: [system prompt, recent history, player action].</item>
///  <item>Call <see cref="AiClient.ChatWithToolsAsync"/> with the registered tools.</item>
///  <item>If the response carries tool calls, execute each via
///    <see cref="ToolRegistry.ExecuteAsync"/>, append the assistant message
///    + tool result messages to the working history, and loop back to (2).</item>
///  <item>If no tool calls, the loop terminates; return the concatenated
///    narration + token usage + applied tool calls as a
///    <see cref="NarrativeResult"/>.</item>
///  </list>
///
/// <b>Skips vs. TS source</b> (documented in the worklog):
///  <list type="bullet">
///  <item>The <c>{{time:+Xm}}</c> hidden-block stream filter — not needed
///    here because we don't stream and the model can call
///    <c>advance_time</c> explicitly.</item>
///  <item>The <c>end_turn</c> tool — the minimal tool set doesn't include
///    it; the loop simply terminates when the model stops calling tools.</item>
///  <item>Length-pacing nudges and loop-stall nudges — the agent will be
///    wired into a future "soft nudge" mechanism; for now we cap at
///    <see cref="MaxIterations"/> and return what we have.</item>
///  <item>Conversation persistence (<c>world.appendConversation</c>) — the
///    spec says to skip persistence (SaveManager is a different layer).
///    History is kept in-memory on this <see cref="GameMaster"/> instance.</item>
///  <item>Streaming — the loop uses the non-streaming
///    <see cref="AiClient.ChatWithToolsAsync"/> for simplicity. A future
///    task can swap to streaming for live UI feedback.</item>
///  </list>
/// </summary>
public sealed class GameMaster
{
    /// <summary>
    /// Default per-turn iteration cap. Matches the TS source's default
    /// (<c>maxIterations || 8</c>). Each iteration = one
    /// <see cref="AiClient.ChatWithToolsAsync"/> call; a turn with N tool
    /// calls typically takes N+1 iterations (one per tool batch + a final
    /// narration-only round).
    /// </summary>
    public const int DefaultMaxIterations = 8;

    /// <summary>
    /// When the in-memory conversation history exceeds this many messages,
    /// the oldest half is sent to the summarizer and replaced with a
    /// short text summary (issue #25). 30 messages ≈ 5–10 turns of
    /// back-and-forth (each turn = one user action + one assistant
    /// narration, occasionally split by tool calls). The threshold is
    /// deliberately conservative: we'd rather summarise a bit early than
    /// blow the context window mid-turn.
    /// </summary>
    public const int SummarizeAfterMessages = 30;

    /// <summary>
    /// Hard cap on completion tokens for the summarization call itself.
    /// The summary is meant to be a short recap (~300 words ≈ 400 tokens),
    /// so 500 leaves headroom for the model's preamble / formatting
    /// without burning the full <see cref="AiSettings.MaxTokens"/> budget.
    /// </summary>
    public const int SummaryMaxTokens = 500;

    /// <summary>
    /// Default cap on the total estimated prompt size (system + summary +
    /// history + world state), in tokens. When the estimate exceeds 80%
    /// of this value, summarization is triggered EARLY — before the
    /// <see cref="SummarizeAfterMessages"/> threshold is reached. 12000 is
    /// conservative for the typical 16k-context model class (gpt-4o-mini,
    /// deepseek-chat, llama3.1:8b); larger-context models can override
    /// this via <see cref="Profile.Settings.MaxContextTokens"/>.
    /// </summary>
    public const int DefaultMaxContextTokens = 12000;

    private readonly AiClient _ai;
    private readonly AiSettings? _aiSettings;
    private readonly MyGame.Core.World.World _world;
    private readonly PromptLoader _prompts;
    private readonly ToolRegistry _tools;
    private readonly int _maxIterations;
    private readonly int _maxContextTokens;
    private readonly List<ChatMessage> _history = new();

    /// <summary>
    /// Running text summary of the older (already-summarised) portion of
    /// the conversation. Null until the first summarization pass completes
    /// (issue #25). Persisted on <see cref="Saves.SaveMeta.HistorySummary"/>
    /// so the GM doesn't lose context across save/load — see
    /// <see cref="HistorySummary"/>.
    /// </summary>
    private string? _summary;

    /// <summary>
    /// Per-turn tool-call loop detector. Reset at the start of each
    /// <see cref="ProcessActionAsync"/> call; consulted after each tool
    /// call to inject a Russian-language nudge into the working history
    /// when the GM is repeating itself.
    /// </summary>
    private readonly LoopDetector _loopDetector = new();

    /// <summary>
    /// Create a GameMaster bound to the given AI client, world, prompt
    /// loader, and tool registry. The same instance should be reused
    /// across turns so the in-memory conversation history accumulates
    /// (call <see cref="ResetHistory"/> to clear it, e.g. on save load).
    /// </summary>
    /// <param name="ai">Base AI client used for all GM calls. When
    /// <paramref name="aiSettings"/> is provided, a role-specific client
    /// is derived via <see cref="AiClient.WithModel"/> for the
    /// <see cref="AiRole.GM"/> model override (issue #26).</param>
    /// <param name="aiSettings">Optional AI settings — used to derive a
    /// role-specific model (issue #26) and read per-role overrides. When
    /// null, the base <paramref name="ai"/> client is used as-is.</param>
    /// <param name="maxContextTokens">Optional override for the
    /// context-window threshold used by the early-summarization trigger
    /// (issue #25). Defaults to <see cref="DefaultMaxContextTokens"/>.</param>
    public GameMaster(
        AiClient ai,
        MyGame.Core.World.World world,
        PromptLoader prompts,
        ToolRegistry tools,
        int? maxIterations = null,
        AiSettings? aiSettings = null,
        int? maxContextTokens = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _maxIterations = Math.Max(1, Math.Min(50, maxIterations ?? DefaultMaxIterations));
        _aiSettings = aiSettings;
        _maxContextTokens = Math.Max(1024, maxContextTokens ?? DefaultMaxContextTokens);
    }

    /// <summary>
    /// In-memory conversation history (excluding the system prompt, which
    /// is rebuilt each turn). Trimmed to the last
    /// <see cref="MessagesConstants.MaxConversationMessages"/> messages.
    /// Older history may also be summarized into <see cref="HistorySummary"/>
    /// when it exceeds <see cref="SummarizeAfterMessages"/> or when the
    /// estimated prompt size approaches the context-window threshold
    /// (issue #25).
    /// </summary>
    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>
    /// Text summary of the older (already-summarised) portion of the
    /// conversation, or null when no summarization has happened yet (issue #25).
    ///
    /// <para>
    /// Persisted on <see cref="Saves.SaveMeta.HistorySummary"/> so the GM
    /// doesn't lose context across save/load: on save, the host copies
    /// this property into <c>meta.HistorySummary</c>; on load, the host
    /// restores it by assigning back here. The summary is then prepended
    /// to the working message list as a user message
    /// (<c>## Предыдущие события (изложение)</c>) so the model sees the
    /// compressed history before the recent turns.
    /// </para>
    /// </summary>
    public string? HistorySummary
    {
        get => _summary;
        set => _summary = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Clear the in-memory conversation history.</summary>
    public void ResetHistory() => _history.Clear();

    /// <summary>
    /// Process one player action: build the prompt, run the tool-call loop,
    /// and return the final narration + token usage + applied tool calls.
    /// </summary>
    public Task<NarrativeResult> ProcessActionAsync(
        string playerAction,
        CancellationToken ct = default)
        => ProcessActionBatchAsync(new[] { playerAction }, narrativeDelta: null, ct);

    /// <summary>
    /// Process one player action with optional streaming narrative deltas.
    /// </summary>
    public Task<NarrativeResult> ProcessActionAsync(
        string playerAction,
        IProgress<string>? narrativeDelta,
        CancellationToken ct = default)
        => ProcessActionBatchAsync(new[] { playerAction }, narrativeDelta, ct);

    /// <summary>
    /// Process a batch of player actions in a single GM turn. This is the
    /// "action_queue" turn model: all players act, the GM resolves the
    /// actions together in one narration + tool-call loop.
    ///
    /// <list type="bullet">
    ///   <item><c>actions.Count == 0</c> → returns immediately with an
    ///     empty result (no-op).</item>
    ///   <item><c>actions.Count == 1</c> → identical to the legacy
    ///     single-action <see cref="ProcessActionAsync(string, CancellationToken)"/>
    ///     call (the player action becomes the lone user message).</item>
    ///   <item><c>actions.Count &gt; 1</c> → builds a single combined user
    ///     message listing the numbered actions and runs the GM once. The
    ///     GM narrates the resolution of ALL actions in one scene.</item>
    /// </list>
    ///
    /// <para><b>Prompt structure (issue #89):</b> the system prompt is
    /// built from <c>system.md</c> (rules) + <c>narrator.md</c> (style
    /// guide) + <c>tools-guide.md</c> (tool reference) + a static
    /// world-lore block (title, theme, setting, atmosphere, starting
    /// hook). All of these change ONLY on world rebuild, so the system
    /// prompt is byte-identical across turns within a session — which
    /// lets the AI provider cache it server-side. The dynamic per-turn
    /// world state (player, location, NPCs, time, last action) is sent
    /// as a SEPARATE user message right before the player action,
    /// labeled <c>## ТЕКУЩЕЕ СОСТОЯНИЕ МИРА</c>, so it doesn't
    /// invalidate the cached system prefix.</para>
    /// </summary>
    /// <param name="actions">Player action texts. Order is preserved in
    /// the combined prompt (1-indexed list).</param>
    /// <param name="narrativeDelta">Optional streaming callback. See
    /// <see cref="ProcessActionAsync(string, IProgress{string}, CancellationToken)"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NarrativeResult> ProcessActionBatchAsync(
        IReadOnlyList<string> actions,
        IProgress<string>? narrativeDelta = null,
        CancellationToken ct = default)
    {
        if (actions is null || actions.Count == 0)
        {
            return new NarrativeResult { NarrativeText = string.Empty };
        }

        var playerAction = actions.Count == 1
            ? actions[0]
            : BuildBatchActionMessage(actions);

        if (string.IsNullOrWhiteSpace(playerAction))
            throw new ArgumentException("Action text is required.", nameof(actions));

        // Reset the loop detector for this turn — signatures don't carry
        // across turns.
        _loopDetector.Reset();

        // Derive a role-specific client for the GM role (issue #26). When
        // _aiSettings is null OR no GMModel override is set, WithModel
        // returns the same client (no allocation). Otherwise it produces a
        // thin wrapper that shares the base client's HttpClient.
        var gmAi = _aiSettings is null ? _ai : _ai.WithModel(_aiSettings.GetModelForRole(AiRole.GM));

        // Build the STATIC system prompt (system.md + narrator.md +
        // tools-guide.md + STATIC_WORLD_LORE). Identical across turns
        // within a session → eligible for provider-side prompt caching.
        var systemPrompt = BuildSystemPrompt();

        // Verification hook (issue #89): when MYGAME_VERIFY_STATIC_PROMPT=1,
        // assert that the system prompt is byte-identical to the previous
        // turn's. If it differs, the cache is being defeated — log a
        // warning so the dev sees it. (No-op in production unless the
        // env var is set.)
        VerifyStaticSystemPrompt(systemPrompt);

        // Dynamic per-turn world state (player, location, NPCs, time,
        // last action). Goes in a separate user message labeled clearly
        // as context, NOT as a player command. This keeps the cached
        // system prefix untouched.
        var worldStateBlock = "## ТЕКУЩЕЕ СОСТОЯНИЕ МИРА\n" + BuildWorldStateBlock();

        // Context-window management (issue #25): if the in-memory history
        // exceeds the summarization threshold, OR the estimated total
        // prompt size approaches the context-window limit, fold the oldest
        // half of the history into a short text summary. The summary is
        // stored in _summary and prepended to the working message list as
        // a user message. Failures are non-fatal — the turn continues
        // with the unsummarized history (we'll try again next turn).
        await MaybeSummarizeAsync(gmAi, systemPrompt, worldStateBlock, ct).ConfigureAwait(false);

        var toolDefs = _tools.Definitions.ToList();
        var working = new List<ChatMessage>();
        working.Add(ChatMessage.System(systemPrompt));
        // If a summary from a prior summarization pass exists, surface it
        // as a user message right after the system prompt — the model
        // sees the compressed history before the recent turns. Marked as
        // context (not a player command) via the heading.
        if (!string.IsNullOrWhiteSpace(_summary))
        {
            working.Add(ChatMessage.User(
                "## Предыдущие события (изложение)\n" + _summary));
        }
        // Trim history to the most recent N messages so the prompt doesn't
        // blow past the provider's context window on long sessions.
        var trimmedHistory = _history.Count > MessagesConstants.MaxConversationMessages
            ? _history.Skip(_history.Count - MessagesConstants.MaxConversationMessages).ToList()
            : _history;
        working.AddRange(trimmedHistory);
        working.Add(ChatMessage.User(worldStateBlock));
        // firstNewIdx marks where the per-turn messages start. Everything
        // from here onward is new this turn and gets persisted to
        // _history (the world-state context-refresh message is NOT
        // persisted — it's per-turn and would otherwise leave stale
        // snapshots in history).
        var firstNewIdx = working.Count;
        working.Add(ChatMessage.User(playerAction));

        var narration = new StringBuilder();
        var appliedCalls = new List<AppliedToolCall>();
        int promptTokens = 0, completionTokens = 0, totalTokens = 0;
        string? finishReason = null;
        int iteration = 0;

        try
        {
            while (iteration < _maxIterations)
            {
                iteration++;
                ct.ThrowIfCancellationRequested();

                // Streaming iteration: forward content deltas to the
                // callback as they arrive; accumulate the full
                // ChatResponse (content + tool_calls + finish_reason +
                // token usage) in the holder for processing below.
                var holder = new StreamChatResult();
                await foreach (var delta in gmAi.StreamChatWithToolsAsync(
                    working, toolDefs, holder, ct).ConfigureAwait(false))
                {
                    narrativeDelta?.Report(delta);
                }
                var response = holder.Response ?? new ChatResponse();

                // ChatResponse now carries token counts directly (per task
                // 3-c-1 spec) — no separate Usage sub-record.
                promptTokens += response.PromptTokens;
                completionTokens += response.CompletionTokens;
                totalTokens += response.PromptTokens + response.CompletionTokens;
                finishReason = response.FinishReason;

                if (!string.IsNullOrEmpty(response.Content))
                    narration.Append(response.Content);

                // No tool calls — the turn is done.
                if (response.ToolCalls is null || response.ToolCalls.Count == 0)
                {
                    // Append the assistant's content-only message to the
                    // working history so the next turn sees it.
                    working.Add(ChatMessage.Assistant(response.Content));
                    break;
                }

                // Append the assistant message WITH tool_calls so the
                // provider can correlate the tool results with this batch.
                working.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = response.Content,
                    ToolCalls = response.ToolCalls.ToList(),
                });

                // Anti-loop: record each tool call's signature and inject
                // a nudge if a repetition pattern is detected. At most one
                // nudge per iteration (so we don't spam the model with
                // multiple nudges for one batch of tool calls).
                string? nudgeThisIteration = null;
                foreach (var tc in response.ToolCalls)
                {
                    var nudge = _loopDetector.Record(tc.Name, tc.Arguments);
                    if (nudge is not null && nudgeThisIteration is null)
                        nudgeThisIteration = nudge.Text;
                }
                if (nudgeThisIteration is not null)
                    working.Add(ChatMessage.System(nudgeThisIteration));

                // Execute each tool call in order. The provider expects a
                // tool-result message per call, with tool_call_id matching.
                foreach (var tc in response.ToolCalls)
                {
                    var result = await _tools.ExecuteAsync(tc.Id, tc.Name, tc.Arguments, ct).ConfigureAwait(false);
                    appliedCalls.Add(new AppliedToolCall
                    {
                        Name = tc.Name,
                        ArgsJson = tc.Arguments,
                        Result = result.Content,
                        IsError = result.IsError,
                    });
                    working.Add(ChatMessage.ToolResult(tc.Id, result.Content));
                }

                // Loop continues — the model will see the tool results and
                // either call more tools or produce a final narration.
            }

            // Persist the new messages from this turn into the in-memory
            // history (excluding the system prompt, which is rebuilt each
            // turn, AND excluding the per-turn world-state user message,
            // which would leave stale state snapshots in history). The
            // first new message is the player action at firstNewIdx.
            var newMessages = working
                .Skip(firstNewIdx)
                .ToList();
            _history.AddRange(newMessages);
            TrimHistory();

            return new NarrativeResult
            {
                NarrativeText = narration.ToString(),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                Iterations = iteration,
                FinishReason = finishReason,
                ToolCalls = appliedCalls,
            };
        }
        catch (AiException ex)
        {
            // Even on failure, persist whatever messages we accumulated so
            // the next turn can continue from where we left off (the TS
            // source did the same). Persist from the player action
            // (firstNewIdx) onwards — the per-turn world-state user
            // message is excluded to avoid stale snapshots in history.
            if (firstNewIdx < working.Count)
            {
                _history.AddRange(working.Skip(firstNewIdx));
                TrimHistory();
            }

            return new NarrativeResult
            {
                NarrativeText = narration.ToString(),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                Iterations = iteration,
                FinishReason = finishReason,
                ToolCalls = appliedCalls,
                Failed = true,
                Error = ex.Message,
            };
        }
    }

    private void TrimHistory()
    {
        if (_history.Count > MessagesConstants.MaxConversationMessages)
            _history.RemoveRange(0, _history.Count - MessagesConstants.MaxConversationMessages);
    }

    // ── Context-window management (issue #25) ──────────────────────────

    /// <summary>
    /// Decide whether to summarize the oldest portion of the conversation
    /// history, and if so, do it. Two triggers (either fires):
    ///
    /// <list type="bullet">
    ///   <item><b>Message-count trigger:</b> <c>_history.Count &gt;</c>
    ///     <see cref="SummarizeAfterMessages"/>. 30 messages ≈ 5–10 turns;
    ///     past that, the older turns are increasingly stale and worth
    ///     compressing.</item>
    ///   <item><b>Token-estimate trigger:</b> the estimated total prompt
    ///     size (system + summary + history + world state) exceeds 80% of
    ///     <see cref="_maxContextTokens"/>. This catches long narrations
    ///     (single turns can be 1000+ tokens) before they blow the
    ///     provider's context window.</item>
    /// </list>
    ///
    /// <para>
    /// When triggered, the OLDEST HALF of <c>_history</c> is sent to
    /// <see cref="SummarizeHistoryAsync"/>. If a summary already exists
    /// (from a prior pass), it's prepended to the messages being
    /// summarized so the new summary is cumulative ("previous summary +
    /// new events") rather than a fresh recap each time. The summarized
    /// messages are then removed from <c>_history</c> and the new summary
    /// is stored in <see cref="_summary"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Failure handling:</b> if the summarization AI call throws an
    /// <see cref="AiException"/> (network, auth, etc.), the turn proceeds
    /// with the unsummarized history — the GM continues, the user sees
    /// their action resolved, and we'll try again next turn. This is
    /// preferable to bricking the game because the summarizer hit a rate
    /// limit. The exception is logged to <c>Trace</c>.
    /// </para>
    /// </summary>
    private async Task MaybeSummarizeAsync(
        AiClient gmAi,
        string systemPrompt,
        string worldStateBlock,
        CancellationToken ct)
    {
        if (_history.Count == 0) return;

        // Token-estimate trigger: total prompt size approaching 80% of the
        // context-window budget. The estimate is rough (4 chars ≈ 1 token,
        // see EstimateTokens) but adequate for triggering — we'd rather
        // summarize a touch early than blow the context window.
        var estimatedTokens = EstimateTokens(systemPrompt)
            + EstimateTokens(_summary)
            + EstimateTokens(worldStateBlock);
        foreach (var m in _history)
            estimatedTokens += EstimateTokens(m.Content);
        var tokenTrigger = estimatedTokens > (_maxContextTokens * 0.8);

        var countTrigger = _history.Count > SummarizeAfterMessages;
        if (!countTrigger && !tokenTrigger)
            return;

        // Summarize the oldest half (at least 1 message). The newer half
        // stays in _history verbatim so the most recent turns retain their
        // full detail (the model needs precise recall of the last few
        // actions to narrate coherently).
        var summarizeCount = Math.Max(1, _history.Count / 2);
        var toSummarize = _history.Take(summarizeCount).ToList();
        if (toSummarize.Count == 0) return;

        try
        {
            var newSummary = await SummarizeHistoryAsync(gmAi, toSummarize, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(newSummary))
            {
                // Remove the summarized messages from _history. The newer
                // half (which we kept) is now at the front.
                _history.RemoveRange(0, summarizeCount);
                _summary = newSummary;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (AiException ex)
        {
            // Non-fatal — log and continue with the unsummarized history.
            // We'll try again next turn (the trigger will re-fire).
            Trace.WriteLine(
                $"[GameMaster] summarization failed (non-fatal): {ex.Kind}: {ex.Message}. " +
                $"History will be sent unsummarized this turn.");
        }
    }

    /// <summary>
    /// Call the AI to summarize a slice of the conversation history into a
    /// short text recap (~300 words, ~400 tokens). Russian-language system
    /// prompt per the spec (issue #25). When <paramref name="oldMessages"/>
    /// is empty, returns an empty string (no AI call). When a summary
    /// already exists (see <see cref="_summary"/>), it's prepended to the
    /// user content so the new summary is cumulative — the model sees
    /// "previous summary + new events" and produces a unified recap.
    ///
    /// <para>
    /// The summarization call uses the GM role's AI client (issue #26:
    /// multi-model support) — it's the same agent that produced the
    /// history, so it has the right context-window budget and temperature.
    /// No tools are passed (plain chat completion). The
    /// <see cref="SummaryMaxTokens"/> cap is enforced via a derived
    /// <see cref="AiSettings"/> so the model doesn't burn the full GM
    /// completion budget on the recap.
    /// </para>
    /// </summary>
    private async Task<string> SummarizeHistoryAsync(
        AiClient gmAi,
        IReadOnlyList<ChatMessage> oldMessages,
        CancellationToken ct)
    {
        if (oldMessages is null || oldMessages.Count == 0)
            return string.Empty;

        const string systemContent =
            "Ты — ассистент, который кратко излагает историю игровой сессии. " +
            "Сожми следующие сообщения в краткое изложение (~300 слов), сохранив " +
            "ключевые события, решения, имена NPC, локации, и текущие цели. На русском.";

        // Concatenate the old messages as a readable transcript. Each
        // message is prefixed with its role so the summarizer can tell
        // player actions (user) from GM narration (assistant) from tool
        // results (tool).
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_summary))
        {
            // Cumulative summary: prepend the existing summary so the model
            // produces a unified recap ("previous summary + new events").
            sb.AppendLine("Предыдущее изложение:");
            sb.AppendLine(_summary);
            sb.AppendLine();
            sb.AppendLine("Новые события:");
        }
        foreach (var m in oldMessages)
        {
            var role = m.Role switch
            {
                ChatRole.System => "система",
                ChatRole.User => "игрок",
                ChatRole.Assistant => "GM",
                ChatRole.Tool => "инструмент",
                _ => m.Role.ToString(),
            };
            var content = m.Content ?? string.Empty;
            if (m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 })
            {
                // Surface tool-call requests compactly so the summarizer
                // sees the model's intent even when content is null.
                var calls = string.Join(", ", m.ToolCalls.Select(tc => $"{tc.Name}({tc.Arguments})"));
                content = string.IsNullOrEmpty(content)
                    ? $"[инструменты: {calls}]"
                    : $"{content} [инструменты: {calls}]";
            }
            sb.Append($"[{role}] ");
            sb.AppendLine(content);
        }

        // Derive a one-off client with the summarization token cap so we
        // don't burn the full GM MaxTokens budget on the recap. WithModel
        // returns the same instance when the model is unchanged; the
        // fresh AiSettings below lowers MaxTokens for this call only.
        // We construct via the public (AiSettings) ctor which uses the
        // shared static HttpClient — the summarization call doesn't need
        // a custom handler / socket pool.
        var summarySettings = gmAi.Settings with { MaxTokens = SummaryMaxTokens };
        var summaryClient = new AiClient(summarySettings);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemContent),
            ChatMessage.User(sb.ToString()),
        };
        var response = await summaryClient.ChatAsync(messages, ct).ConfigureAwait(false);
        return (response.Content ?? string.Empty).Trim();
    }

    /// <summary>
    /// Rough token-count estimate: 4 characters ≈ 1 token. Adequate for
    /// triggering summarization — we only need a reasonable upper bound,
    /// not an exact count (the actual tokenizer is provider-specific and
    /// not worth the dependency for this purpose). Returns 0 for null/
    /// empty input. Exposed as <c>public static</c> so tests can verify
    /// the math without constructing a GameMaster, and because future
    /// agents (PetAgent, StartSceneAgent) may want to reuse the estimate.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // (length + 3) / 4 rounds up so a 1-char string is 1 token (not 0).
        return (text!.Length + 3) / 4;
    }

    /// <summary>
    /// Build the combined user-message text for a batch of player actions.
    /// Renders as a numbered Russian list so the GM knows multiple players
    /// are acting in one turn and resolves them in one narration. Example:
    /// <code>
    /// ## Действия игроков в этом ходу:
    ///
    /// 1. атакую гоблина мечом
    /// 2. кастую огненный шар
    /// </code>
    /// </summary>
    private static string BuildBatchActionMessage(IReadOnlyList<string> actions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Действия игроков в этом ходу:");
        sb.AppendLine();
        for (int i = 0; i < actions.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {actions[i]}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build the STATIC system prompt: <c>system.md</c> (rules) +
    /// <c>narrator.md</c> (style guide) + <c>tools-guide.md</c> (tool
    /// reference) + a static world-lore block. All components change only
    /// on world rebuild, NOT per-turn — so the system prompt is
    /// byte-identical across turns within a session, which lets the AI
    /// provider cache it server-side (issue #89).
    ///
    /// <para>
    /// The <c>system.md</c> template's <c>{{WORLD_STATE}}</c> placeholder
    /// was removed in favor of a separate per-turn user message (see
    /// <see cref="ProcessActionBatchAsync"/>). The
    /// <c>{{STATIC_WORLD_LORE}}</c> placeholder is filled here with world
    /// title + theme + setting + atmosphere + starting hook (read from
    /// <see cref="World.World.Flags"/>). The
    /// <c>{{ITEM_TEMPLATES}}</c> / <c>{{NPC_TEMPLATES}}</c> /
    /// <c>{{BUILDING_TEMPLATES}}</c> placeholders are filled with the
    /// current registry contents (which only change on world rebuild).
    /// </para>
    /// </summary>
    private string BuildSystemPrompt()
    {
        var staticLore = BuildStaticWorldLoreBlock();
        var itemTpls = string.Join(", ", _world.Registries.Items.All().Select(t => t.Id).OrderBy(s => s).Take(30));
        var npcTpls = string.Join(", ", _world.Registries.Npcs.All().Select(t => t.Id).OrderBy(s => s).Take(30));
        var bldTpls = string.Join(", ", _world.Registries.Buildings.All().Select(t => t.Id).OrderBy(s => s).Take(30));

        var vars = new Dictionary<string, string>
        {
            ["STATIC_WORLD_LORE"] = staticLore,
            ["ITEM_TEMPLATES"] = itemTpls,
            ["NPC_TEMPLATES"] = npcTpls,
            ["BUILDING_TEMPLATES"] = bldTpls,
        };

        var system = _prompts.Render("system", vars);
        var narrator = _prompts.Get("narrator");
        var toolsGuide = _prompts.Exists("tools-guide") ? _prompts.Get("tools-guide") : string.Empty;
        return system + "\n\n---\n\n" + narrator + "\n\n---\n\n" + toolsGuide;
    }

    /// <summary>
    /// Render the static world-lore block from <see cref="World.World.Flags"/>.
    /// Pulls worldTitle / worldTheme / worldSetting / worldAtmosphere /
    /// startingHook — these are set by the world-builder and change only
    /// on world rebuild, so the rendered block is byte-identical across
    /// turns within a session (cached by the provider along with the rest
    /// of the system prompt).
    /// </summary>
    private string BuildStaticWorldLoreBlock()
    {
        var sb = new StringBuilder();
        var title = TryGetFlagString(_world.Flags, "worldTitle");
        var theme = TryGetFlagString(_world.Flags, "worldTheme");
        var setting = TryGetFlagString(_world.Flags, "worldSetting");
        var atmosphere = TryGetFlagString(_world.Flags, "worldAtmosphere");
        var hook = TryGetFlagString(_world.Flags, "startingHook");

        if (!string.IsNullOrWhiteSpace(title))
            sb.AppendLine($"- Название мира: {title}");
        if (!string.IsNullOrWhiteSpace(theme))
            sb.AppendLine($"- Тема: {theme}");
        if (!string.IsNullOrWhiteSpace(setting))
            sb.AppendLine($"- Сеттинг: {setting}");
        if (!string.IsNullOrWhiteSpace(atmosphere))
            sb.AppendLine($"- Атмосфера: {atmosphere}");
        if (!string.IsNullOrWhiteSpace(hook))
            sb.AppendLine($"- Стартовый крючок: {hook}");

        if (sb.Length == 0)
            sb.AppendLine("(лор мира ещё не задан — используй дефолтные предположения тёмного фэнтези).");

        return sb.ToString();
    }

    /// <summary>
    /// Verification hook for issue #89. When the environment variable
    /// <c>MYGAME_VERIFY_STATIC_PROMPT</c> is set to <c>"1"</c>, asserts
    /// that the system prompt is byte-identical to the previous turn's.
    /// If it differs, logs a warning (the prompt-cache is being defeated).
    /// No-op in production unless the env var is set.
    /// </summary>
    private void VerifyStaticSystemPrompt(string systemPrompt)
    {
        if (!s_verifyStaticPrompt) return;

        if (_lastSystemPrompt is not null && !string.Equals(_lastSystemPrompt, systemPrompt, StringComparison.Ordinal))
        {
            Trace.WriteLine("[GameMaster] WARNING: system prompt differs from the previous turn — " +
                            "provider-side prompt caching is defeated. " +
                            "Static prefix should not change between turns of the same world.");
        }
        _lastSystemPrompt = systemPrompt;
    }

    private string? _lastSystemPrompt;
    private static readonly bool s_verifyStaticPrompt =
        Environment.GetEnvironmentVariable("MYGAME_VERIFY_STATIC_PROMPT") == "1";

    /// <summary>
    /// Render a minimal world-state block. The full TS version
    /// (<c>buildWorldStateBlock</c> in <c>prompts/index.ts</c>) produced a
    /// rich block with attributes, resources, equipment, inventory, log
    /// tail, all-locations overview, world lore, etc. We ship a smaller
    /// version here that covers the essentials: player identity, current
    /// location, NPCs/buildings present, all-locations overview. A later
    /// task can port the full rendering.
    /// </summary>
    private string BuildWorldStateBlock()
    {
        var sb = new StringBuilder();
        var p = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        if (p is null)
        {
            sb.AppendLine("## Персонаж игрока\n(игрок ещё не создан)");
            return sb.ToString();
        }

        // World title (set by the world-builder via Flags["worldTitle"]).
        var worldTitle = TryGetFlagString(_world.Flags, "worldTitle");
        if (!string.IsNullOrWhiteSpace(worldTitle))
        {
            sb.AppendLine($"## Мир: {worldTitle}");
            sb.AppendLine();
        }

        var loc = _world.GetLocation(p.LocationId);

        sb.AppendLine("## Персонаж игрока");
        sb.AppendLine($"- Имя: {p.Name} | Раса: {p.Race ?? "—"} | Класс: {p.Class ?? "—"} | Уровень: {p.Level ?? 1}");
        if (p.Attributes.Count > 0)
            sb.AppendLine($"- Характеристики: {string.Join(", ", p.Attributes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");
        if (p.Resources.Count > 0)
            sb.AppendLine($"- Ресурсы: {string.Join(", ", p.Resources.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");
        if (p.Equipped.Count > 0)
            sb.AppendLine($"- Экипировка: {string.Join(", ", p.Equipped.Select(kv => $"{kv.Key}: {kv.Value.Name}"))}");
        if (p.Inventory.Items.Count > 0)
            sb.AppendLine($"- Инвентарь: {string.Join(", ", p.Inventory.Items.Select(i => $"{i.Name} ×{i.Quantity}"))}");
        sb.AppendLine($"- Валюта: {p.Inventory.Currency}");

        // Active status effects on the player (name + duration).
        if (p.Effects is { Count: > 0 })
        {
            var effs = string.Join(", ", p.Effects.Select(e =>
            {
                var dur = e.Duration < 0
                    ? "постоянно"
                    : $"{e.Duration} ход.";
                return $"{e.Name} ({dur})";
            }));
            sb.AppendLine($"- Эффекты: {effs}");
        }

        // Active quests (up to 5): name + completed/total + next incomplete.
        var activeQuests = _world.Quests
            .Where(q => q.Status == QuestStatus.Active)
            .Take(5)
            .ToList();
        if (activeQuests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Активные квесты");
            foreach (var q in activeQuests)
            {
                var done = q.Objectives.Count(o => o.Done);
                var total = q.Objectives.Count;
                var next = q.Objectives.FirstOrDefault(o => !o.Done);
                var nextLabel = next is not null && !string.IsNullOrWhiteSpace(next.Description)
                    ? $" → далее: {next.Description}"
                    : "";
                sb.AppendLine($"- {q.Name} [{done}/{total}]{nextLabel}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Время в мире");
        sb.AppendLine($"- {_world.Clock}");

        // COMBAT-DEATH: surface the structured combat state to the GM so
        // it knows whose turn it is and doesn't act out of order. When
        // combat is inactive (the default), this block is omitted — the
        // GM is freeform-narrating. When active, we list the round, the
        // current actor (whose turn it is), and the full initiative
        // order with the active combatant marked.
        if (_world.Combat is { Active: true } combat && combat.TurnOrder.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## БОЙ");
            sb.AppendLine($"- Раунд: {combat.Round}");
            var currentIdx = Math.Clamp(combat.CurrentActorIndex, 0, combat.TurnOrder.Count - 1);
            var currentName = combat.TurnOrder[currentIdx].Name;
            sb.AppendLine($"- Сейчас ход: {currentName}");
            // Initiative order with markers: "►" for the current actor,
            // "✓" for those who already acted this round, "·" otherwise.
            var order = string.Join(", ", combat.TurnOrder.Select((c, i) =>
            {
                var marker = i == currentIdx ? "►" : c.HasActedThisRound ? "✓" : "·";
                return $"{marker}{c.Name} ({c.Initiative})";
            }));
            sb.AppendLine($"- Инициатива: {order}");
            sb.AppendLine("- Соблюдай очерёдность. Действуй только за текущего бойца; для передачи хода вызывай next_turn.");
        }

        // COMBAT-DEATH: when the player is at 0 HP, surface the death-
        // save tally so the GM knows to call death_save (and eventually
        // narrate the death / stabilisation). The "deathSaves" flag is
        // "S,F" (0-3 each); we read it leniently.
        if (p.Resources.TryGetValue("hp", out var playerHp) && playerHp <= 0 && p.IsAlive)
        {
            var (suc, fail) = ReadDeathSaves(_world.Flags);
            sb.AppendLine();
            sb.AppendLine("## Спасброски от смерти");
            sb.AppendLine($"- Игрок на 0 HP! Успехи: {suc}/3, Провалы: {fail}/3.");
            sb.AppendLine("- Каждый ход вызывай death_save, пока игрок не стабилизируется (3 успеха) или не умрёт (3 провала).");
        }
        else if (!p.IsAlive)
        {
            sb.AppendLine();
            sb.AppendLine("## ВНИМАНИЕ");
            sb.AppendLine("- Игрок мёртв. Не вызывай инструменты, меняющие мир; просто наррируй финал и заверши сцену.");
        }

        sb.AppendLine();
        sb.AppendLine($"## Текущая локация: {loc?.Name ?? "—"}");
        if (loc is not null)
        {
            sb.AppendLine($"- Описание: {loc.Description ?? "—"}");
            sb.AppendLine($"- Местность: {loc.Terrain} | Опасность: {loc.Danger}/10");
            if (loc.Exits.Count > 0)
            {
                var exits = string.Join(", ", loc.Exits.Select(e =>
                {
                    var to = _world.GetLocation(e.To)?.Name ?? e.To.ToString();
                    return $"{e.Direction} → {to}";
                }));
                sb.AppendLine($"- Выходы: {exits}");
            }
            if (loc.Npcs.Count > 0)
            {
                // Enriched: name + race/class + level + disposition.
                var npcs = string.Join(", ", loc.Npcs
                    .Select(id => _world.GetNpc(id))
                    .Where(n => n is not null)
                    .Select(n =>
                    {
                        var npc = n!;
                        var disp = TryGetFlagString(npc.Flags, "disposition");
                        if (string.IsNullOrWhiteSpace(disp))
                            disp = npc.Disposition ?? "neutral";
                        var rc = string.IsNullOrEmpty(npc.Race) && string.IsNullOrEmpty(npc.Class)
                            ? ""
                            : $" {npc.Race ?? "—"}/{npc.Class ?? "—"}";
                        var lvl = npc.Level is int l ? $" ур.{l}" : "";
                        return $"{npc.Name}{rc}{lvl} ({disp})";
                    }));
                sb.AppendLine($"- Обитатели: {npcs}");
            }
            if (loc.Buildings.Count > 0)
            {
                var blds = string.Join(", ", loc.Buildings
                    .Select(id => _world.GetBuilding(id))
                    .Where(b => b is not null)
                    .Select(b => b!.Name));
                sb.AppendLine($"- Здания: {blds}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Все локации мира");
        if (_world.Locations.Count == 0)
        {
            sb.AppendLine("- (локаций пока нет)");
        }
        else
        {
            foreach (var l in _world.Locations.Take(30))
            {
                sb.AppendLine($"- {l.Name} (ID: {l.Id}, terrain: {l.Terrain}, danger: {l.Danger})");
            }
        }

        // Last player action — continuity hint pulled from the in-memory
        // conversation history (the World itself doesn't store a log).
        var lastUser = _history.LastOrDefault(m => m.Role == ChatRole.User);
        if (lastUser?.Content is string la && !string.IsNullOrWhiteSpace(la))
        {
            const int MaxLen = 240;
            var trimmed = la.Length > MaxLen ? la.Substring(0, MaxLen) + "…" : la;
            sb.AppendLine();
            sb.AppendLine("## Последнее действие игрока");
            sb.AppendLine($"- {trimmed}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Best-effort string extraction from a <see cref="Entity.Flags"/> /
    /// <see cref="World.Flags"/> entry. Values may be either plain strings
    /// (set in-memory) or <see cref="System.Text.Json.JsonElement"/> values
    /// (after a save round-trip). Returns null when missing or non-string.
    /// </summary>
    private static string? TryGetFlagString(System.Collections.Generic.Dictionary<string, object>? flags, string key)
    {
        if (flags is null) return null;
        if (!flags.TryGetValue(key, out var v) || v is null) return null;
        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// Read the <c>"deathSaves"</c> world flag (<c>"S,F"</c>) into a
    /// tuple of (successes, failures), each clamped to [0, 3]. Missing
    /// / malformed → (0, 0). Used by the system-prompt world-state block
    /// to surface the death-save tally to the GM.
    /// </summary>
    private static (int Successes, int Failures) ReadDeathSaves(
        System.Collections.Generic.Dictionary<string, object>? flags)
    {
        var s = TryGetFlagString(flags, "deathSaves");
        if (string.IsNullOrWhiteSpace(s)) return (0, 0);
        var parts = s.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return (0, 0);
        if (!int.TryParse(parts[0], out var suc)) suc = 0;
        if (!int.TryParse(parts[1], out var fail)) fail = 0;
        return (Math.Clamp(suc, 0, 3), Math.Clamp(fail, 0, 3));
    }
}
