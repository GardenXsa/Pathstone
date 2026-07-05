using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;

namespace MyGame.Core.AI.Agents;

/// <summary>
/// Result of <see cref="StartSceneAgent.RunAsync"/>.
/// </summary>
public sealed record StartSceneResult
{
    /// <summary>
    /// Atmospheric opening narration to show the player as the first entry
    /// in the story feed. May be empty if the agent focused purely on tool
    /// calls (the GM's opening turn that follows will narrate).
    /// </summary>
    public required string SceneDescription { get; init; }

    /// <summary>Number of LLM iterations the agent ran.</summary>
    public int Iterations { get; init; }

    /// <summary>True if the agent completed normally; false if it hit an error or the iteration cap.</summary>
    public bool Success { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Start-Scene Agent. Port of <c>ai/agents/startSceneAgent.ts</c>.
///
/// <para>
/// Runs a tool-call loop BEFORE the GM's opening narration turn. The agent:
///   1. Reads the world state + the freshly-created character.
///   2. Decides where the character should start (based on role/class) and
///      moves them there (<c>move_player</c> with force).
///   3. Grants role-appropriate starting gear (<c>create_item_template</c>
///      + <c>give_item</c> + <c>equip_player</c>).
///   4. Spawns NPCs that should logically be nearby (guards, apprentices).
///   5. Produces a short atmospheric scene description as its final text
///      response (the GM writes the full opening narration in the turn
///      that follows — see GameViewModel.RunOpeningNarrationAsync).
/// </summary>
/// <remarks>
/// The agent owns item grants — the engine/DefaultWorld does NOT pre-equip
/// the player (per the TS original's design: <c>defaultWorld.ts</c> "Starting
/// gear is NO LONGER granted by the engine here. The GM agent owns item
/// grants — it issues them via the give_item tool"). This keeps the world
/// reusable (exportable) and the gear role-appropriate rather than baked in.
/// </remarks>
public sealed class StartSceneAgent
{
    /// <summary>Default per-run iteration cap. The TS source used 12.</summary>
    public const int DefaultMaxIterations = 12;

    private readonly AiClient _ai;
    private readonly MyGame.Core.World.World _world;
    private readonly PromptLoader _prompts;
    private readonly ToolRegistry _tools;
    private readonly int _maxIterations;

    /// <summary>
    /// Create a start-scene agent bound to the given AI client, world, prompt
    /// loader, and tool registry. The tool registry is built from the same
    /// world (so the agent's <c>give_item</c>/<c>equip_player</c>/etc. mutate
    /// the live world the GM will narrate from).
    /// </summary>
    public StartSceneAgent(
        AiClient ai,
        MyGame.Core.World.World world,
        PromptLoader prompts,
        ToolRegistry? tools = null,
        int? maxIterations = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _tools = tools ?? new ToolRegistry(world);
        _maxIterations = Math.Max(1, Math.Min(30, maxIterations ?? DefaultMaxIterations));
    }

    /// <summary>
    /// Run the start-scene tool-call loop. Calls the AI with the registered
    /// tools (move_player, give_item, equip_player, create_item_template,
    /// spawn_npc, …) and iterates until the model stops calling tools or the
    /// iteration cap is hit. The model's final text response becomes
    /// <see cref="StartSceneResult.SceneDescription"/> (a short atmospheric
    /// scene-setting paragraph; the GM writes the full opening narration in
    /// the turn that follows). Returns even on error (best-effort) so the
    /// caller can fall back to a default scene.
    /// </summary>
    public async Task<StartSceneResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            var systemPrompt = BuildSystemPrompt();
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(systemPrompt),
                ChatMessage.User(
                    "Подготовь стартовую сцену для этого персонажа. Действуй инструментами: " +
                    "перемести игрока в подходящую локацию (move_player с force:true), выдай " +
                    "стартовое снаряжение по роли (create_item_template + give_item + equip_player), " +
                    "спавни NPC если логично. После инструментов напиши 1–2 коротких абзаца " +
                    "атмосферного описания сцены. Без звёздочек, без заголовков, без «Что будешь делать?»."),
            };

            var toolDefs = new List<ToolDefinition>(_tools.Definitions);
            var narration = new StringBuilder();
            int iteration = 0;

            while (iteration < _maxIterations)
            {
                iteration++;
                var response = await _ai.ChatWithToolsAsync(messages, toolDefs, ct).ConfigureAwait(false);

                // Accumulate any text the model produces (the final
                // scene-description paragraphs come on the last iteration
                // when the model stops calling tools).
                if (!string.IsNullOrEmpty(response.Content))
                    narration.Append(response.Content);

                // No tool calls → the agent is done (it produced its final
                // text response). Append the assistant message for completeness
                // and break.
                if (response.ToolCalls is null || response.ToolCalls.Count == 0)
                {
                    messages.Add(ChatMessage.Assistant(response.Content));
                    break;
                }

                // Append the assistant message WITH tool_calls so the
                // provider can correlate the tool results with this batch.
                messages.Add(new ChatMessage
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
                    messages.Add(ChatMessage.ToolResult(tc.Id, result.Content));
                }
                // Loop continues — the model will see the tool results and
                // either call more tools or produce its final scene text.
            }

            var text = narration.ToString().Trim();
            return new StartSceneResult
            {
                SceneDescription = text,
                Iterations = iteration,
                Success = true,
                Error = null,
            };
        }
        catch (AiException ex)
        {
            return new StartSceneResult
            {
                SceneDescription = string.Empty,
                Iterations = 0,
                Success = false,
                Error = ex.Message,
            };
        }
    }

    private string BuildSystemPrompt()
    {
        var template = _prompts.Get("world-start-scene");
        var stateBlock = BuildWorldStateBlock();
        var vars = new Dictionary<string, string>
        {
            ["WORLD_STATE"] = stateBlock,
        };
        return PromptLoader.Substitute(template, vars);
    }

    private string BuildWorldStateBlock()
    {
        var sb = new StringBuilder();
        var p = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        if (p is not null)
        {
            sb.AppendLine("# ПЕРСОНАЖ ИГРОКА");
            sb.AppendLine($"Имя: {p.Name}");
            sb.AppendLine($"Раса: {p.Race ?? "—"}");
            sb.AppendLine($"Класс/роль: {p.Class ?? "—"}");
            sb.AppendLine($"Предыстория: {p.Background ?? "—"}");
            var loc = _world.GetLocation(p.LocationId);
            sb.AppendLine($"Текущая локация: {loc?.Name ?? "—"} ({p.LocationId})");
            sb.AppendLine();
        }

        sb.AppendLine("# ТЕКУЩЕЕ СОСТОЯНИЕ МИРА");
        sb.AppendLine("Локации (первые 30):");
        if (_world.Locations.Count == 0)
        {
            sb.AppendLine("  (пока нет)");
        }
        else
        {
            foreach (var l in _world.Locations.Take(30))
                sb.AppendLine($"  • {l.Name} ({l.Id}, terrain={l.Terrain})");
        }

        sb.AppendLine();
        sb.AppendLine("NPC (первые 15):");
        if (_world.Npcs.Count == 0)
        {
            sb.AppendLine("  (пока нет)");
        }
        else
        {
            foreach (var n in _world.Npcs.Take(15))
                sb.AppendLine($"  • {n.Name} ({n.Id}, loc={n.LocationId})");
        }

        sb.AppendLine();
        sb.AppendLine("# ЗАДАЧА");
        sb.AppendLine("Размести персонажа в подходящей локации, выдай снаряжение по роли, спавни NPC если логично. " +
                      "Действуй ИНСТРУМЕНТАМИ. После инструментов — 1–2 коротких абзаца атмосферного описания. " +
                      "Не задавай вопросов игроку.");
        return sb.ToString();
    }
}
