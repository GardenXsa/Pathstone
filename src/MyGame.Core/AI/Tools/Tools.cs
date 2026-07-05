using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyGame.Core.Common;
using MyGame.Core.World.Entities;

// Tool handlers are async lambdas (per the ToolHandler delegate signature
// Task<ToolResult>) even when they don't await anything — keeping them
// async lets future revisions add awaits without changing the signature.
#pragma warning disable CS1998 // Async method lacks 'await' operators

namespace MyGame.Core.AI.Tools;

// Note: ToolDefinition is defined in MyGame.Core.AI (Messages.cs). It is
// visible here without an explicit `using` because MyGame.Core.AI.Tools is
// a child namespace of MyGame.Core.AI, so parent-namespace types resolve
// automatically. The spec for task 3-c-1 places ToolDefinition in
// Messages.cs (alongside ChatMessage / ChatResponse / ToolCall) so the
// pure-message types live together in one file; the ToolRegistry below
// consumes that definition.

/// <summary>
/// Result of executing one tool call. Port of the inline shape the TS
/// <c>executeToolAsync</c> returned (a plain string); we wrap it in a
/// record so we can flag errors separately from successful results
/// without parsing the content.
/// </summary>
public sealed record ToolResult
{
    /// <summary>
    /// The tool-call id this result answers. Matches
    /// <see cref="ToolCall.Id"/> from the assistant message that issued
    /// the call.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Human-readable result text fed back to the model as the
    /// <c>role:tool</c> message content. Always set, even on error.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// True if the tool failed (bad args, missing entity, exception).
    /// The agent loop still feeds the result back to the model so the
    /// model can correct itself, but it can also branch on this flag for
    /// its own bookkeeping (e.g. counting failures).
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>Build a successful result.</summary>
    public static ToolResult Ok(string toolCallId, string content) => new()
    {
        ToolCallId = toolCallId,
        Content = content,
        IsError = false,
    };

    /// <summary>Build an error result.</summary>
    public static ToolResult Error(string toolCallId, string message) => new()
    {
        ToolCallId = toolCallId,
        Content = message,
        IsError = true,
    };
}

/// <summary>
/// Handler signature for a registered tool. Receives the parsed arguments
/// (as a <see cref="JsonElement"/>) and a cancellation token; returns a
/// <see cref="ToolResult"/> that the agent loop feeds back to the model.
/// </summary>
public delegate Task<ToolResult> ToolHandler(JsonElement args, CancellationToken ct);

/// <summary>
/// Registry of function-calling tools available to the AI agents. Port
/// (skeleton) of <c>ai/tools/index.ts</c>.
///
/// The TS source shipped ~50 tools (combat, inventory, world-building,
/// dialogue, quests, …). This C# port starts with a 5-tool set per the
/// task spec — <c>roll_dice</c>, <c>get_player_state</c>,
/// <c>get_location</c>, <c>spawn_npc</c>, <c>advance_time</c>. The full
/// suite can be layered on in a later task without changing the registry
/// API.
///
/// The registry holds a <see cref="World"/> reference (injected at
/// construction) so tool handlers can mutate live world state. The
/// registry itself is stateless beyond that reference — handlers are pure
/// functions of (world, args).
/// </summary>
public sealed class ToolRegistry
{
    private readonly MyGame.Core.World.World _world;
    private readonly Dictionary<string, ToolDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>
    /// Create a registry with the 5 built-in tools registered, operating
    /// on <paramref name="world"/>.
    /// </summary>
    public ToolRegistry(MyGame.Core.World.World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        RegisterBuiltins();
    }

    /// <summary>All registered tool definitions (for sending to the model).</summary>
    public IReadOnlyCollection<ToolDefinition> Definitions => _definitions.Values;

    /// <summary>Lookup a definition by name. Returns null if not registered.</summary>
    public ToolDefinition? GetDefinition(string name) =>
        _definitions.TryGetValue(name, out var d) ? d : null;

    /// <summary>Register a custom tool. Overwrites any prior registration under the same name.</summary>
    public void Register(ToolDefinition definition, ToolHandler handler)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        _definitions[definition.Name] = definition;
        _handlers[definition.Name] = handler;
    }

    /// <summary>
    /// Execute one tool call. Defensive: any exception thrown by the
    /// handler is caught and converted to a <see cref="ToolResult"/> with
    /// <see cref="ToolResult.IsError"/> = true (per the task spec's
    /// "wrap in try/catch, return a ToolResult with IsError=true"
    /// requirement). Malformed JSON args are likewise converted.
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(string toolCallId, string name, string argsJson, CancellationToken ct = default)
    {
        if (!_handlers.TryGetValue(name, out var handler))
        {
            return ToolResult.Error(toolCallId, $"Инструмент «{name}» не найден. Доступные: {string.Join(", ", _definitions.Keys.OrderBy(k => k))}.");
        }

        JsonElement args;
        try
        {
            // Per the TS source's robustJsonParse: the model can send
            // double-encoded JSON, trailing garbage, etc. We do a single
            // tolerant parse — fall back to an empty object on failure.
            args = ParseArgsLenient(argsJson);
        }
        catch (Exception ex)
        {
            return ToolResult.Error(toolCallId, $"Не удалось разобрать аргументы инструмента: {ex.Message}");
        }

        ToolResult result;
        try
        {
            result = await handler(args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Error(toolCallId, $"Инструмент «{name}» упал с ошибкой: {ex.Message}");
        }

        // Handlers don't know their own tool-call id (it's assigned by the
        // provider), so stamp it on the result here before returning.
        return result with { ToolCallId = toolCallId };
    }

    /// <summary>
    /// Lenient JSON parse: try the raw string first, then unescape
    /// <c>\n</c>/<c>\"</c>/<c>\\</c> and retry. Returns an empty object
    /// on total failure.
    /// </summary>
    private static JsonElement ParseArgsLenient(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return JsonDocument.Parse("{}").RootElement;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Try unescaping \n / \" / \\ — some providers send args as a
            // string-within-string with extra escaping.
            try
            {
                var unescaped = argsJson!.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
                using var doc2 = JsonDocument.Parse(unescaped);
                return doc2.RootElement.Clone();
            }
            catch (JsonException)
            {
                return JsonDocument.Parse("{}").RootElement;
            }
        }
    }

    // ─── Built-in tool registrations ───────────────────────────────────────

    private void RegisterBuiltins()
    {
        Register(RollDiceTool.Definition, RollDiceTool.Handle(_world));
        Register(GetPlayerStateTool.Definition, GetPlayerStateTool.Handle(_world));
        Register(GetLocationTool.Definition, GetLocationTool.Handle(_world));
        Register(SpawnNpcTool.Definition, SpawnNpcTool.Handle(_world));
        Register(AdvanceTimeTool.Definition, AdvanceTimeTool.Handle(_world));
    }
}

// ─── Built-in tools ──────────────────────────────────────────────────────
//
// Each tool is a static class exposing a `Definition` (ToolDefinition
// literal) and a `Handle(World)` factory returning the handler. This keeps
// the registration list above readable while letting each tool own its
// schema + logic in one place. The TS source had all 50 tools in one giant
// array; we split them per-tool for navigability.

/// <summary>
/// Roll dice using an <c>NdM±K</c> expression. Uses the world's seedable
/// RNG so rolls are reproducible from a save.
/// </summary>
internal static class RollDiceTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "roll_dice",
        Description = "Бросок кубиков по формуле «NdM±K» (напр. 2d6+3, 1d20, 3d8-1). Используй для случайных событий, не-D20 бросков (урон, сокровище).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "expression": { "type": "string", "description": "Запись вида «2d6+3», «1d20», «3d8-1»." },
            "purpose": { "type": "string", "description": "Зачем бросок (для лога)." }
          },
          "required": ["expression"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var expr = args.TryGetProperty("expression", out var eEl) ? eEl.GetString() ?? "1d20" : "1d20";
        var purpose = args.TryGetProperty("purpose", out var pEl) ? pEl.GetString() ?? "" : "";

        var (total, rolls, modifier) = DiceExpressionEvaluator.Eval(world.Rng, expr);
        var sign = modifier >= 0 ? "+" : "-";
        var rollList = string.Join(", ", rolls);
        var text = $"Брошено {expr}: кости [{rollList}] {sign}{Math.Abs(modifier)} = {total}.";
        return ToolResult.Ok(string.Empty, text);
    };
}

/// <summary>
/// Return a snapshot of the active player's state for the model to read.
/// Read-only — doesn't mutate the world.
/// </summary>
internal static class GetPlayerStateTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "get_player_state",
        Description = "Возвращает снимок состояния активного игрока: имя, раса, класс, уровень, характеристики, ресурсы, экипировка, инвентарь, локация.",
        ParametersJson = """
        { "type": "object", "properties": {} }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        var loc = world.GetLocation(p.LocationId);

        var attrs = p.Attributes.Count > 0
            ? string.Join(", ", p.Attributes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
            : "(нет)";
        var resources = p.Resources.Count > 0
            ? string.Join(", ", p.Resources.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
            : "(нет)";
        var equipped = p.Equipped.Count > 0
            ? string.Join(", ", p.Equipped.Select(kv => $"{kv.Key}: {kv.Value.Name}"))
            : "нет";
        var inv = p.Inventory.Items.Count > 0
            ? string.Join(", ", p.Inventory.Items.Select(i => $"{i.Name} ×{i.Quantity}"))
            : "пусто";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Имя: {p.Name}");
        sb.AppendLine($"Раса: {p.Race ?? "—"} | Класс: {p.Class ?? "—"} | Уровень: {p.Level ?? 1} | Скорость: {p.Speed ?? 30}");
        sb.AppendLine($"Характеристики: {attrs}");
        sb.AppendLine($"Ресурсы: {resources}");
        sb.AppendLine($"Экипировка: {equipped}");
        sb.AppendLine($"Инвентарь: {inv}");
        sb.AppendLine($"Валюта: {p.Inventory.Currency}");
        sb.AppendLine($"Локация: {loc?.Name ?? "—"} ({p.LocationId})");
        sb.AppendLine($"Жив: {(p.IsAlive ? "да" : "нет")}");
        return ToolResult.Ok(string.Empty, sb.ToString().TrimEnd());
    };
}

/// <summary>
/// Return a description of a location by ID or name. Defaults to the
/// player's current location. Read-only.
/// </summary>
internal static class GetLocationTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "get_location",
        Description = "Возвращает описание локации по ID или имени: terrain, danger, выходы, обитатели (NPC), здания, предметы на земле. Без аргументов = текущая локация игрока.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "locationId": { "type": "string", "description": "ID или имя локации. Пусто = текущая локация игрока." }
          }
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var idOrName = args.TryGetProperty("locationId", out var el) ? el.GetString() ?? "" : "";
        var loc = ResolveLocation(world, idOrName);
        if (loc is null)
            return ToolResult.Error(string.Empty, $"Локация «{idOrName}» не найдена.");

        var exits = loc.Exits.Count > 0
            ? string.Join(", ", loc.Exits.Select(e =>
            {
                var toName = world.GetLocation(e.To)?.Name ?? e.To.ToString();
                return $"{e.Direction} → {toName}{(e.Locked == true ? " (заперто)" : "")}";
            }))
            : "нет выходов";

        var npcs = loc.Npcs.Count > 0
            ? string.Join(", ", loc.Npcs.Select(id => world.GetNpc(id)).Where(n => n is not null).Select(n => $"{n!.Name} ({n!.Id})"))
            : "нет";
        var buildings = loc.Buildings.Count > 0
            ? string.Join(", ", loc.Buildings.Select(id => world.GetBuilding(id)).Where(b => b is not null).Select(b => $"{b!.Name}"))
            : "нет";
        var items = loc.Items.Count > 0
            ? string.Join(", ", loc.Items.Select(id => world.GetItem(id)).Where(i => i is not null).Select(i => $"{i!.Name} ×{i!.Quantity}"))
            : "нет";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Локация: {loc.Name} ({loc.Id})");
        sb.AppendLine($"Описание: {loc.Description ?? "—"}");
        sb.AppendLine($"Местность: {loc.Terrain} | Опасность: {loc.Danger}/10");
        sb.AppendLine($"Выходы: {exits}");
        sb.AppendLine($"Здания: {buildings}");
        sb.AppendLine($"Обитатели: {npcs}");
        sb.AppendLine($"Предметы на земле: {items}");
        return ToolResult.Ok(string.Empty, sb.ToString().TrimEnd());
    };

    /// <summary>
    /// Resolve a location by ID or name (case-insensitive on names). The
    /// model knows names (from the world-state prompt) but rarely knows
    /// generated IDs, so accept either. Shared with other tools — kept
    /// internal so the registry can offer it to future tools.
    /// </summary>
    internal static Location? ResolveLocation(MyGame.Core.World.World world, string idOrName)
    {
        if (string.IsNullOrEmpty(idOrName))
        {
            // Default to the active player's current location.
            var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
            return p is null ? null : world.GetLocation(p.LocationId);
        }
        // Try as EntityId first.
        if (Common.EntityId.TryParse(idOrName, out var eid))
        {
            var direct = world.GetLocation(eid);
            if (direct is not null) return direct;
        }
        var lower = idOrName.ToLowerInvariant();
        return world.Locations.FirstOrDefault(l =>
            string.Equals(l.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Spawn an NPC from a content-registry template at a location. The NPC
/// becomes a real inhabitant of the world.
/// </summary>
internal static class SpawnNpcTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "spawn_npc",
        Description = "Заспавнить NPC из реестра шаблонов в локации. NPC становится реальным обитателем мира.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "templateId": { "type": "string", "description": "ID шаблона NPC, напр. npc_goblin, npc_tavern_keeper." },
            "locationId": { "type": "string", "description": "Локация спавна (ID или имя); пусто = текущая локация игрока." },
            "nameOverride": { "type": "string", "description": "Сменить имя NPC." }
          },
          "required": ["templateId"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var templateId = args.TryGetProperty("templateId", out var tEl) ? tEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(templateId))
            return ToolResult.Error(string.Empty, "Параметр templateId обязателен.");

        var idOrName = args.TryGetProperty("locationId", out var lEl) ? lEl.GetString() ?? "" : "";
        var loc = GetLocationTool.ResolveLocation(world, idOrName);
        if (loc is null)
            return ToolResult.Error(string.Empty, $"Локация «{idOrName}» не найдена.");

        var npc = world.SpawnNpcFromTemplate(templateId, loc.Id);
        if (npc is null)
        {
            var known = world.Registries.Npcs.All().Select(t => t.Id).OrderBy(s => s).ToList();
            var sample = known.Count > 0 ? string.Join(", ", known.Take(15)) : "(реестр пуст)";
            return ToolResult.Error(string.Empty, $"Шаблон NPC «{templateId}» не найден. Доступные: {sample}.");
        }

        var nameOverride = args.TryGetProperty("nameOverride", out var nEl) ? nEl.GetString() : null;
        if (!string.IsNullOrWhiteSpace(nameOverride))
            npc.Name = nameOverride!;

        return ToolResult.Ok(string.Empty,
            $"NPC «{npc.Name}» ({npc.Race ?? "?"}, ур. {npc.Level?.ToString() ?? "?"}) появился в локации «{loc.Name}».");
    };
}

/// <summary>
/// Advance the in-world clock by N minutes. The clock is the primary
/// time-of-day surface for the desktop UI.
/// </summary>
internal static class AdvanceTimeTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "advance_time",
        Description = "Продвинуть внутриигровое время на N минут. Используй, когда ход игрока явно занял время (путешествие, сон, длительный разговор).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "minutes": { "type": "integer", "description": "Сколько минут прошло (>= 0)." }
          },
          "required": ["minutes"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        if (!args.TryGetProperty("minutes", out var mEl) || !mEl.TryGetInt32(out var minutes))
            return ToolResult.Error(string.Empty, "Параметр minutes обязателен и должен быть целым числом.");
        if (minutes < 0)
            return ToolResult.Error(string.Empty, "minutes должно быть >= 0.");
        if (minutes == 0)
            return ToolResult.Ok(string.Empty, "Время не изменилось (0 минут).");

        var before = world.Clock;
        world.Clock = world.Clock.Advance(minutes);
        return ToolResult.Ok(string.Empty,
            $"Время продвинуто на {minutes} мин: было «{before}», стало «{world.Clock}».");
    };
}

// ─── Dice expression evaluator ───────────────────────────────────────────

/// <summary>
/// Tiny <c>NdM±K</c> dice-expression evaluator used by the
/// <see cref="RollDiceTool"/>. The TS source had a full
/// <c>rollExpression</c> in <c>engine/rules/d20.ts</c>; this C# port is a
/// minimal regex-based version that handles the common cases
/// (<c>1d20</c>, <c>2d6+3</c>, <c>3d8-1</c>, <c>d100</c>, bare constants
/// like <c>5</c>). It returns (total, rolls[], modifier) so the tool can
/// render a faithful «кости [...] +K = total» line.
/// </summary>
internal static class DiceExpressionEvaluator
{
    private static readonly Regex s_pattern = new(
        @"^(?<count>\d*)d(?<sides>\d+)(?<mod>[+-]\d+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (int Total, IReadOnlyList<int> Rolls, int Modifier) Eval(Rng rng, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return (0, Array.Empty<int>(), 0);

        // Bare integer ("5") — just return it.
        if (int.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare))
            return (bare, Array.Empty<int>(), 0);

        var m = s_pattern.Match(expression.Trim());
        if (!m.Success)
            return (0, Array.Empty<int>(), 0);

        var count = int.TryParse(m.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 1;
        var sides = int.TryParse(m.Groups["sides"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;
        var mod = 0;
        if (m.Groups["mod"].Success && int.TryParse(m.Groups["mod"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm))
            mod = mm;

        // Cap count to avoid pathological inputs (the model occasionally
        // emits "1000d6" — we treat anything over 100 dice as 100).
        if (count > 100) count = 100;
        // Cap sides too — a d1000000 would overflow on a long run.
        if (sides > 1000) sides = 1000;

        var rolls = new int[count];
        int sum = 0;
        for (int i = 0; i < count; i++)
        {
            rolls[i] = rng.NextInt(1, sides + 1);
            sum += rolls[i];
        }
        return (sum + mod, rolls, mod);
    }
}
