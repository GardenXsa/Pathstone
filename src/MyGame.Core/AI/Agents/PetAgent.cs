using System.Text.Json;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;

namespace MyGame.Core.AI.Agents;

/// <summary>
/// Event raised by <see cref="PetAgent.RunAsync"/> to surface progress to
/// the UI. Mirrors the TS <c>PetAgentEvent</c> shape.
/// </summary>
public sealed record PetAgentEvent
{
    public required string AgentId { get; init; }
    public required string AgentName { get; init; }
    public required PetEventKind Kind { get; init; }
    public string? Text { get; init; }
    public string? ToolName { get; init; }
    public string? ArgsJson { get; init; }
    public string? Result { get; init; }
}

/// <summary>Kind of <see cref="PetAgentEvent"/>.</summary>
public enum PetEventKind
{
    Start,
    Text,
    Tool,
    Done,
    Error,
}

/// <summary>
/// Configuration for a pet agent. The TS source stored these in
/// <c>AISettings.petAgents</c>; the desktop MVP keeps a simpler model —
/// a pet agent is just (id, name, optional per-agent AI overrides).
/// If the overrides are null, the main <see cref="AiSettings"/> are used.
/// </summary>
public sealed record PetAgentConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public AiSettings? Settings { get; init; }
}

/// <summary>
/// Result of <see cref="PetAgent.RunAsync"/>.
/// </summary>
public sealed record PetAgentResult
{
    public required string Summary { get; init; }
    public int Iterations { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Pet agent — a delegated sub-agent that runs a focused task with its own
/// LLM conversation and tool-call loop. Port of
/// <c>ai/agents/petAgent.ts</c>.
///
/// <para>
/// The pet agent is used by the world-builder orchestrator (or any other
/// supervisor) to delegate a well-scoped sub-task ("spawn 5 bandits in the
/// forest", "design a custom item template for the artifact") to a
/// secondary LLM call. It has its own message history, its own iteration
/// cap, and reports back via a <c>pet_done</c> tool call (or a fallback
/// summary if it runs out of iterations).
/// </para>
///
/// <para>
/// Unlike the TS source, the desktop MVP does NOT support nested pet
/// agents (a pet agent cannot delegate to another pet agent). The
/// <c>pet_done</c> tool is the only pet-specific tool; all other tools
/// come from the shared <see cref="ToolRegistry"/>.
/// </para>
/// </summary>
public sealed class PetAgent
{
    /// <summary>Default per-run iteration cap. The TS source used 8.</summary>
    public const int DefaultMaxIterations = 6;

    private readonly AiClient _mainAi;
    private readonly AiSettings? _aiSettings;
    private readonly MyGame.Core.World.World _world;
    private readonly ToolRegistry _tools;
    private readonly PetAgentConfig _config;
    private readonly int _maxIterations;

    // Pet-specific tool: signals task completion. Registered on a private
    // registry snapshot so it doesn't pollute the main tool list.
    private static readonly ToolDefinition PetDoneTool = new()
    {
        Name = "pet_done",
        Description = "Завершить задачу pet-агента. Аргумент: summary — краткое описание результата.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "Краткое описание выполненной работы." }
          },
          "required": ["summary"]
        }
        """,
    };

    /// <summary>Create a pet agent bound to the given AI client, world, tool registry, and config.</summary>
    /// <param name="mainAi">Base AI client. When <paramref name="aiSettings"/>
    /// is provided AND the pet's config doesn't carry its own
    /// <see cref="PetAgentConfig.Settings"/>, a role-specific client is
    /// derived via <see cref="AiClient.WithModel"/> for the
    /// <see cref="AiRole.Pet"/> model override (issue #26).</param>
    /// <param name="aiSettings">Optional AI settings for the PetModel
    /// override. When null, the base <paramref name="mainAi"/> client is
    /// used as-is (unless the config has its own Settings).</param>
    public PetAgent(
        AiClient mainAi,
        MyGame.Core.World.World world,
        ToolRegistry tools,
        PetAgentConfig config,
        int? maxIterations = null,
        AiSettings? aiSettings = null)
    {
        _mainAi = mainAi ?? throw new ArgumentNullException(nameof(mainAi));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _maxIterations = Math.Max(1, Math.Min(20, maxIterations ?? DefaultMaxIterations));
        _aiSettings = aiSettings;
    }

    /// <summary>
    /// Run the pet agent on the given task. Returns when the agent calls
    /// <c>pet_done</c> or hits the iteration cap. Events are raised via
    /// <paramref name="onEvent"/> (if provided).
    /// </summary>
    public async Task<PetAgentResult> RunAsync(
        string task,
        Action<PetAgentEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task))
            return new PetAgentResult
            {
                Summary = "Пустая задача — pet-агент не запущен.",
                Iterations = 0,
                Success = false,
                Error = "empty task",
            };

        // Pick the AI client to use:
        //   1. If the pet config has its own Settings (per-pet override),
        //      use a fresh client from those settings (highest priority).
        //   2. Else if the orchestrator passed shared settings (with a
        //      possible PetModel override), derive a role-specific client
        //      from the main AI client (issue #26).
        //   3. Else fall back to the main AI client as-is.
        AiClient ai;
        if (_config.Settings is not null)
            ai = new AiClient(_config.Settings);
        else if (_aiSettings is not null)
            ai = _mainAi.WithModel(_aiSettings.GetModelForRole(AiRole.Pet));
        else
            ai = _mainAi;

        var systemPrompt =
            "Ты — делегированный pet-агент внутри конвейера построения мира. Работай на русском. " +
            "Выполни конкретную задачу от главного агента. Можешь использовать обычные инструменты мира, " +
            "но НЕ можешь делегировать другим pet-агентам. " +
            "По завершении вызови pet_done с кратким summary. " +
            "НЕ вызывай finish_stage, end_worldbuilding, end_turn, commit_world_plan, set_world_meta — это инструменты главного агента.";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(task),
        };

        onEvent?.Invoke(new PetAgentEvent
        {
            AgentId = _config.Id,
            AgentName = _config.Name,
            Kind = PetEventKind.Start,
            Text = task,
        });

        // Build the tool list: main registry + pet_done.
        var toolDefs = _tools.Definitions.ToList();
        toolDefs.Add(PetDoneTool);

        string fullText = string.Empty;
        string summary = string.Empty;

        try
        {
            for (int iteration = 0; iteration < _maxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                var response = await ai.ChatWithToolsAsync(messages, toolDefs, ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(response.Content))
                {
                    fullText += response.Content;
                    onEvent?.Invoke(new PetAgentEvent
                    {
                        AgentId = _config.Id,
                        AgentName = _config.Name,
                        Kind = PetEventKind.Text,
                        Text = response.Content,
                    });
                }

                if (response.ToolCalls is null || response.ToolCalls.Count == 0)
                {
                    // Nudge: ask the agent to either continue or call pet_done.
                    messages.Add(ChatMessage.Assistant(response.Content));
                    messages.Add(ChatMessage.User(
                        "Если задача завершена, вызови pet_done с кратким summary. " +
                        "Иначе продолжай инструментами."));
                    continue;
                }

                // Append assistant message with tool_calls.
                messages.Add(ChatMessage.AssistantWithTools(response.ToolCalls, response.Content));

                // Execute each tool.
                bool done = false;
                foreach (var tc in response.ToolCalls)
                {
                    var result = await _tools.ExecuteAsync(tc.Id, tc.Name, tc.Arguments ?? "{}", ct)
                        .ConfigureAwait(false);

                    // Special-case pet_done — extract the summary and short-circuit.
                    if (tc.Name == "pet_done")
                    {
                        summary = ExtractSummary(tc.Arguments) ?? result.Content;
                        onEvent?.Invoke(new PetAgentEvent
                        {
                            AgentId = _config.Id,
                            AgentName = _config.Name,
                            Kind = PetEventKind.Tool,
                            ToolName = tc.Name,
                            ArgsJson = tc.Arguments,
                            Result = result.Content,
                        });
                        onEvent?.Invoke(new PetAgentEvent
                        {
                            AgentId = _config.Id,
                            AgentName = _config.Name,
                            Kind = PetEventKind.Done,
                            Text = summary,
                        });
                        done = true;
                        break;
                    }

                    onEvent?.Invoke(new PetAgentEvent
                    {
                        AgentId = _config.Id,
                        AgentName = _config.Name,
                        Kind = PetEventKind.Tool,
                        ToolName = tc.Name,
                        ArgsJson = tc.Arguments,
                        Result = result.Content,
                    });

                    messages.Add(ChatMessage.ToolResult(tc.Id, result.Content));
                }

                if (done)
                {
                    return new PetAgentResult
                    {
                        Summary = summary,
                        Iterations = iteration + 1,
                        Success = true,
                    };
                }
            }

            // Hit the iteration cap — return a fallback summary.
            var fallback = !string.IsNullOrWhiteSpace(summary)
                ? summary
                : (!string.IsNullOrWhiteSpace(fullText)
                    ? fullText.Trim()
                    : "Pet-агент остановился без summary.");
            onEvent?.Invoke(new PetAgentEvent
            {
                AgentId = _config.Id,
                AgentName = _config.Name,
                Kind = PetEventKind.Done,
                Text = fallback,
            });
            return new PetAgentResult
            {
                Summary = fallback,
                Iterations = _maxIterations,
                Success = false,
                Error = "iteration cap reached",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiException ex)
        {
            onEvent?.Invoke(new PetAgentEvent
            {
                AgentId = _config.Id,
                AgentName = _config.Name,
                Kind = PetEventKind.Error,
                Text = ex.Message,
            });
            return new PetAgentResult
            {
                Summary = $"Pet-агент упал: {ex.Message}",
                Iterations = 0,
                Success = false,
                Error = ex.Message,
            };
        }
        catch (Exception ex)
        {
            onEvent?.Invoke(new PetAgentEvent
            {
                AgentId = _config.Id,
                AgentName = _config.Name,
                Kind = PetEventKind.Error,
                Text = ex.Message,
            });
            return new PetAgentResult
            {
                Summary = $"Pet-агент упал: {ex.Message}",
                Iterations = 0,
                Success = false,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Extract the <c>summary</c> field from the pet_done tool's arguments
    /// JSON. Defensive — returns null on any parse failure.
    /// </summary>
    private static string? ExtractSummary(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.TryGetProperty("summary", out var s) ? s.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
