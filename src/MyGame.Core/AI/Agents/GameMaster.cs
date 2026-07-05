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

    private readonly AiClient _ai;
    private readonly MyGame.Core.World.World _world;
    private readonly PromptLoader _prompts;
    private readonly ToolRegistry _tools;
    private readonly int _maxIterations;
    private readonly List<ChatMessage> _history = new();

    /// <summary>
    /// Create a GameMaster bound to the given AI client, world, prompt
    /// loader, and tool registry. The same instance should be reused
    /// across turns so the in-memory conversation history accumulates
    /// (call <see cref="ResetHistory"/> to clear it, e.g. on save load).
    /// </summary>
    public GameMaster(
        AiClient ai,
        MyGame.Core.World.World world,
        PromptLoader prompts,
        ToolRegistry tools,
        int? maxIterations = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _maxIterations = Math.Max(1, Math.Min(50, maxIterations ?? DefaultMaxIterations));
    }

    /// <summary>
    /// In-memory conversation history (excluding the system prompt, which
    /// is rebuilt each turn). Trimmed to the last
    /// <see cref="MessagesConstants.MaxConversationMessages"/> messages.
    /// </summary>
    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>Clear the in-memory conversation history.</summary>
    public void ResetHistory() => _history.Clear();

    /// <summary>
    /// Process one player action: build the prompt, run the tool-call loop,
    /// and return the final narration + token usage + applied tool calls.
    /// </summary>
    public async Task<NarrativeResult> ProcessActionAsync(
        string playerAction,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerAction))
            throw new ArgumentException("playerAction is required.", nameof(playerAction));

        var systemPrompt = BuildSystemPrompt();
        var toolDefs = _tools.Definitions.ToList();
        var working = new List<ChatMessage>();
        working.Add(ChatMessage.System(systemPrompt));
        // Trim history to the most recent N messages so the prompt doesn't
        // blow past the provider's context window on long sessions.
        var trimmedHistory = _history.Count > MessagesConstants.MaxConversationMessages
            ? _history.Skip(_history.Count - MessagesConstants.MaxConversationMessages).ToList()
            : _history;
        working.AddRange(trimmedHistory);
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

                var response = await _ai.ChatWithToolsAsync(working, toolDefs, ct).ConfigureAwait(false);

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
            // turn). The first message in `working` is the system prompt;
            // the second-through-second-to-last are prior history (already
            // in _history); the last N are this turn's new messages.
            // Simpler: skip system (idx 0) and the trimmed history, take
            // the rest.
            var newMessages = working
                .Skip(1 + trimmedHistory.Count)
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
            // source did the same).
            var userActionIdx = working.FindIndex(m => m.Role == ChatRole.User && m.Content == playerAction);
            if (userActionIdx >= 0)
            {
                _history.AddRange(working.Skip(userActionIdx));
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

    /// <summary>
    /// Build the system prompt: load the <c>system.md</c> template and
    /// fill in the <c>{{WORLD_STATE}}</c>, <c>{{ITEM_TEMPLATES}}</c>,
    /// <c>{{NPC_TEMPLATES}}</c>, <c>{{BUILDING_TEMPLATES}}</c>
    /// placeholders with a minimal world snapshot.
    ///
    /// The TS source's <c>buildStableSystemPrompt</c> + <c>buildLiveStatePrompt</c>
    /// produced a rich ~10KB block with attributes, resources, equipment,
    /// inventory, log tail, all-locations overview, world lore, etc. We
    /// ship a much smaller snapshot here — enough for the GM to know who
    /// the player is, where they are, and what templates are available.
    /// A later task can port the full rendering.
    /// </summary>
    private string BuildSystemPrompt()
    {
        var worldState = BuildWorldStateBlock();
        var itemTpls = string.Join(", ", _world.Registries.Items.All().Select(t => t.Id).OrderBy(s => s).Take(30));
        var npcTpls = string.Join(", ", _world.Registries.Npcs.All().Select(t => t.Id).OrderBy(s => s).Take(30));
        var bldTpls = string.Join(", ", _world.Registries.Buildings.All().Select(t => t.Id).OrderBy(s => s).Take(30));

        var vars = new Dictionary<string, string>
        {
            ["WORLD_STATE"] = worldState,
            ["ITEM_TEMPLATES"] = itemTpls,
            ["NPC_TEMPLATES"] = npcTpls,
            ["BUILDING_TEMPLATES"] = bldTpls,
        };

        var system = _prompts.Render("system", vars);
        // The narrator style guide is appended to the system prompt so the
        // model gets both the rules block and the style block in one
        // stable prefix (good for provider-side prompt caching).
        var narrator = _prompts.Get("narrator");
        return system + "\n\n---\n\n" + narrator;
    }

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
}
