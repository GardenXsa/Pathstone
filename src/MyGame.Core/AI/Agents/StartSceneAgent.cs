using System.Text;
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
    /// in the story feed.
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
/// In the TS source this agent used a tool-call loop to move the player to
/// a starting location, grant starter gear, and spawn nearby NPCs before
/// handing off to the GM. The C# port is SIMPLER — it builds a prompt
/// from world context, asks the model for an atmospheric opening-scene
/// description (no tools), and returns the text. The actual scene setup
/// (player placement, starter gear) is expected to be done by the caller
/// (e.g. the Desktop UI's "New Game" flow) using the World API directly,
/// which is more reliable than driving it through an LLM tool loop.
///
/// Rationale: the TS source's tool-driven start-scene was a frequent
/// source of bugs (the model would forget to <c>move_player</c> with
/// <c>force:true</c>, or skip <c>equip_player</c> after <c>give_item</c>).
/// The desktop rewrite ships deterministic scene setup + AI-generated
/// narration instead.
/// </summary>
public sealed class StartSceneAgent
{
    /// <summary>Default per-run iteration cap. The TS source used 12.</summary>
    public const int DefaultMaxIterations = 4;

    private readonly AiClient _ai;
    private readonly MyGame.Core.World.World _world;
    private readonly PromptLoader _prompts;
    private readonly int _maxIterations;

    /// <summary>Create a start-scene agent bound to the given AI client, world, and prompt loader.</summary>
    public StartSceneAgent(AiClient ai, MyGame.Core.World.World world, PromptLoader prompts, int? maxIterations = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _maxIterations = Math.Max(1, Math.Min(20, maxIterations ?? DefaultMaxIterations));
    }

    /// <summary>
    /// Generate the opening scene description. Calls the AI once (no tool
    /// loop) with the world-start-scene prompt, world state, and a hint
    /// derived from the active player's identity. Returns the model's
    /// response as <see cref="StartSceneResult.SceneDescription"/>.
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
                    "Напиши 2–3 коротких абзаца вступительной наррации для стартовой сцены. " +
                    "Без звёздочек, без заголовков, без «Что будешь делать?». Просто художественный текст."),
            };

            var response = await _ai.ChatAsync(messages, ct).ConfigureAwait(false);
            var text = response.Content?.Trim() ?? string.Empty;
            return new StartSceneResult
            {
                SceneDescription = text,
                Iterations = 1,
                Success = !string.IsNullOrEmpty(text),
                Error = string.IsNullOrEmpty(text) ? "AI вернул пустую наррацию." : null,
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
        sb.AppendLine("Напиши атмосферное вступление для стартовой сцены. Без инструментов, без звёздочек. " +
                      "2–3 коротких абзаца на русском. Не задавай вопросов игроку.");
        return sb.ToString();
    }
}
