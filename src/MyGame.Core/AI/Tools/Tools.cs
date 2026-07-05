using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyGame.Core.Common;
using MyGame.Core.Rules;
using MyGame.Core.World;
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
        // Original 5 built-ins (initial MVP tool surface).
        Register(RollDiceTool.Definition, RollDiceTool.Handle(_world));
        Register(GetPlayerStateTool.Definition, GetPlayerStateTool.Handle(_world));
        Register(GetLocationTool.Definition, GetLocationTool.Handle(_world));
        Register(SpawnNpcTool.Definition, SpawnNpcTool.Handle(_world));
        Register(AdvanceTimeTool.Definition, AdvanceTimeTool.Handle(_world));

        // TOOL-SUITE expansion — 13 tools covering movement, inventory,
        // equipment, quests, world flags, character snapshots, combat, and
        // runtime content authoring. With these the GM can run a real game
        // end-to-end (move player, give loot, advance quests, run combat).
        Register(MovePlayerTool.Definition, MovePlayerTool.Handle(_world));
        Register(GiveItemTool.Definition, GiveItemTool.Handle(_world));
        Register(SpawnItemOnGroundTool.Definition, SpawnItemOnGroundTool.Handle(_world));
        Register(EquipPlayerTool.Definition, EquipPlayerTool.Handle(_world));
        Register(UpdateQuestTool.Definition, UpdateQuestTool.Handle(_world));
        Register(SetFlagTool.Definition, SetFlagTool.Handle(_world));
        Register(GetWorldStateTool.Definition, GetWorldStateTool.Handle(_world));
        Register(GetNpcStateTool.Definition, GetNpcStateTool.Handle(_world));
        Register(AwardXpTool.Definition, AwardXpTool.Handle(_world));
        Register(RollAttackTool.Definition, RollAttackTool.Handle(_world));
        Register(DealDamageTool.Definition, DealDamageTool.Handle(_world));
        Register(ApplyStatusTool.Definition, ApplyStatusTool.Handle(_world));
        Register(CreateItemTemplateTool.Definition, CreateItemTemplateTool.Handle(_world));
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

// ─── Movement / inventory / equipment tools ─────────────────────────────

/// <summary>
/// Relocate the active player to a destination location (by id or name).
/// Marks the destination <see cref="Location.Visited"/> and
/// <see cref="Location.Discovered"/> so the map UI reflects the new state.
/// </summary>
internal static class MovePlayerTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "move_player",
        Description = "Переместить активного игрока в локацию (по ID или имени). Помечает локацию посещённой и обнаруженной.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "locationId": { "type": "string", "description": "ID или имя локации назначения." }
          },
          "required": ["locationId"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        var idOrName = args.TryGetProperty("locationId", out var el) ? el.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(idOrName))
            return ToolResult.Error(string.Empty, "Параметр locationId обязателен.");

        var loc = GetLocationTool.ResolveLocation(world, idOrName);
        if (loc is null)
            return ToolResult.Error(string.Empty, $"Локация «{idOrName}» не найдена.");

        p.LocationId = loc.Id;
        loc.Visited = true;
        loc.Discovered = true;
        return ToolResult.Ok(string.Empty, $"Игрок переместился в «{loc.Name}».");
    };
}

/// <summary>
/// Grant an item (from a content-registry template) to the active player's
/// inventory.
/// </summary>
internal static class GiveItemTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "give_item",
        Description = "Выдать предмет в инвентарь активного игрока по ID шаблона.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "templateId": { "type": "string", "description": "ID шаблона предмета (напр. wpn_shortsword, cons_healing_potion)." },
            "quantity": { "type": "integer", "description": "Количество (>= 1, по умолчанию 1)." }
          },
          "required": ["templateId"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        var templateId = args.TryGetProperty("templateId", out var tEl) ? tEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(templateId))
            return ToolResult.Error(string.Empty, "Параметр templateId обязателен.");

        var qty = args.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out var q) && q > 0 ? q : 1;

        var tpl = world.Registries.Items.Get(templateId);
        if (tpl is null)
        {
            var known = world.Registries.Items.All().Select(t => t.Id).OrderBy(s => s).ToList();
            var sample = known.Count > 0 ? string.Join(", ", known.Take(20)) : "(реестр пуст)";
            return ToolResult.Error(string.Empty, $"Шаблон предмета «{templateId}» не найден. Доступные: {sample}.");
        }

        var item = EntityFactory.InstantiateItem(tpl, qty);
        p.Inventory.Items.Add(item);
        return ToolResult.Ok(string.Empty, $"Выдан «{item.Name}» ×{qty}.");
    };
}

/// <summary>
/// Place a loose item on the ground at a location (default: the player's
/// current location).
/// </summary>
internal static class SpawnItemOnGroundTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "spawn_item_on_ground",
        Description = "Положить предмет на землю в локации. По умолчанию — в текущей локации игрока.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "templateId": { "type": "string", "description": "ID шаблона предмета." },
            "locationId": { "type": "string", "description": "Локация (ID или имя); пусто = текущая локация игрока." },
            "quantity": { "type": "integer", "description": "Количество (>= 1, по умолчанию 1)." }
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

        var qty = args.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out var q) && q > 0 ? q : 1;

        var tpl = world.Registries.Items.Get(templateId);
        if (tpl is null)
        {
            var known = world.Registries.Items.All().Select(t => t.Id).OrderBy(s => s).ToList();
            var sample = known.Count > 0 ? string.Join(", ", known.Take(20)) : "(реестр пуст)";
            return ToolResult.Error(string.Empty, $"Шаблон предмета «{templateId}» не найден. Доступные: {sample}.");
        }

        var item = EntityFactory.InstantiateItem(tpl, qty);
        world.SpawnItemOnGround(item, loc.Id);
        return ToolResult.Ok(string.Empty, $"«{item.Name}» ×{qty} появился в «{loc.Name}».");
    };
}

/// <summary>
/// Equip an item from the player's inventory into a slot, swapping any
/// currently-equipped item back into the inventory. Slot auto-detected from
/// the item template (weapon/armor/misc) when not supplied.
/// </summary>
internal static class EquipPlayerTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "equip_player",
        Description = "Экипировать предмет из инвентаря игрока в слот. Старый предмет возвращается в инвентарь. Слот автоопределяется по шаблону (weapon/armor/misc).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "itemId": { "type": "string", "description": "ID экземпляра предмета в инвентаре." },
            "slot": { "type": "string", "description": "Слот (weapon/armor/shield/...). Если пусто — автоопределение по шаблону." }
          },
          "required": ["itemId"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        var itemId = args.TryGetProperty("itemId", out var iEl) ? iEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(itemId))
            return ToolResult.Error(string.Empty, "Параметр itemId обязателен.");

        var idx = p.Inventory.Items.FindIndex(it =>
            string.Equals(it.Id.ToString(), itemId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return ToolResult.Error(string.Empty, $"Предмет «{itemId}» не найден в инвентаре игрока.");

        var item = p.Inventory.Items[idx];
        p.Inventory.Items.RemoveAt(idx);

        // Auto-detect slot from the template if the caller didn't specify one.
        var slot = args.TryGetProperty("slot", out var sEl) ? sEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(slot))
        {
            var tpl = !string.IsNullOrEmpty(item.TemplateId) ? world.Registries.Items.Get(item.TemplateId) : null;
            slot = tpl?.Weapon is not null ? "weapon"
                : tpl?.Armor is not null ? "armor"
                : "misc";
        }

        // Swap the previously-equipped item (if any) back to the inventory.
        if (p.Equipped.TryGetValue(slot, out var oldItem) && oldItem is not null)
        {
            oldItem.Equipped = false;
            p.Inventory.Items.Add(oldItem);
        }
        item.Equipped = true;
        p.Equipped[slot] = item;

        // Recompute AC for D&D-style worlds (no-op when the ruleset has no
        // 'ac' resource).
        EntityFactory.RecomputeAcResource(p, world.Ruleset);

        return ToolResult.Ok(string.Empty, $"Экипирован «{item.Name}» (слот: {slot}).");
    };
}

// ─── Quest / flag / state tools ──────────────────────────────────────────

/// <summary>
/// Change quest state: activate / complete / fail / objective_done /
/// objective_undone. Completion grants rewards (currency, XP, items) inline.
/// </summary>
internal static class UpdateQuestTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "update_quest",
        Description = "Изменить состояние квеста: activate / complete / fail / objective_done / objective_undone. При complete выдаёт награды (валюта, опыт, предметы).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "questId": { "type": "string", "description": "ID или имя квеста." },
            "action": { "type": "string", "description": "Действие: activate | complete | fail | objective_done | objective_undone." },
            "objectiveId": { "type": "string", "description": "ID цели (для objective_done / objective_undone)." }
          },
          "required": ["questId", "action"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var questId = args.TryGetProperty("questId", out var qEl) ? qEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(questId))
            return ToolResult.Error(string.Empty, "Параметр questId обязателен.");

        var action = args.TryGetProperty("action", out var aEl) ? aEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(action))
            return ToolResult.Error(string.Empty, "Параметр action обязателен.");

        var quest = ResolveQuest(world, questId);
        if (quest is null)
            return ToolResult.Error(string.Empty, $"Квест «{questId}» не найден.");

        var objectiveId = args.TryGetProperty("objectiveId", out var oEl) ? oEl.GetString() ?? "" : "";

        switch (action.ToLowerInvariant())
        {
            case "activate":
                quest.Status = QuestStatus.Active;
                return ToolResult.Ok(string.Empty, $"Квест «{quest.Name}» активирован.");

            case "fail":
                quest.Status = QuestStatus.Failed;
                return ToolResult.Ok(string.Empty, $"Квест «{quest.Name}» провален.");

            case "complete":
            {
                quest.Status = QuestStatus.Completed;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Квест «{quest.Name}» выполнен.");
                var reward = quest.Reward;
                var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
                if (reward is not null && p is not null)
                {
                    int currency = reward.Currency ?? reward.Gold ?? 0;
                    if (currency > 0)
                    {
                        p.Inventory.Currency += currency;
                        sb.AppendLine($"Награда: {currency} валюты.");
                    }
                    int xp = reward.Experience ?? 0;
                    if (xp > 0)
                    {
                        var (leveled, newLevel) = AwardXpTool.GrantXp(p, xp);
                        sb.Append($"Награда: {xp} опыта.");
                        if (leveled) sb.Append($" Новый уровень: {newLevel}!");
                        sb.AppendLine();
                    }
                    if (reward.Items is not null)
                    {
                        foreach (var tplId in reward.Items)
                        {
                            var tpl = world.Registries.Items.Get(tplId);
                            if (tpl is null) continue;
                            var inst = EntityFactory.InstantiateItem(tpl, 1);
                            p.Inventory.Items.Add(inst);
                            sb.AppendLine($"Награда: «{inst.Name}».");
                        }
                    }
                }
                return ToolResult.Ok(string.Empty, sb.ToString().TrimEnd());
            }

            case "objective_done":
            case "objective_undone":
            {
                if (string.IsNullOrEmpty(objectiveId))
                    return ToolResult.Error(string.Empty,
                        "Для objective_done/objective_undone требуется objectiveId.");
                var obj = quest.Objectives.FirstOrDefault(o =>
                    string.Equals(o.Id, objectiveId, StringComparison.OrdinalIgnoreCase));
                if (obj is null)
                    return ToolResult.Error(string.Empty,
                        $"Цель «{objectiveId}» не найдена в квесте «{quest.Name}».");
                var done = action == "objective_done";
                obj.Done = done;
                return ToolResult.Ok(string.Empty,
                    $"Цель «{obj.Description}» квеста «{quest.Name}» {(done ? "выполнена" : "отменена")}.");
            }

            default:
                return ToolResult.Error(string.Empty,
                    $"Неизвестное действие «{action}». Допустимо: activate, complete, fail, objective_done, objective_undone.");
        }
    };

    /// <summary>
    /// Resolve a quest by id or name. Empty input returns null.
    /// </summary>
    internal static Quest? ResolveQuest(MyGame.Core.World.World world, string idOrName)
    {
        if (string.IsNullOrEmpty(idOrName)) return null;
        if (Common.EntityId.TryParse(idOrName, out var eid))
        {
            var direct = world.GetQuest(eid);
            if (direct is not null) return direct;
        }
        return world.Quests.FirstOrDefault(q =>
            string.Equals(q.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Set a flag (key=value) on an entity (looked up via FindEntity) or on the
/// world itself when <c>target</c> is "world" or empty.
/// </summary>
internal static class SetFlagTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "set_flag",
        Description = "Установить флаг (key=value) на сущности или на мире. target = ID сущности или \"world\" (по умолчанию).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "target": { "type": "string", "description": "ID сущности или \"world\" (по умолчанию \"world\")." },
            "key": { "type": "string", "description": "Ключ флага." },
            "value": { "type": "string", "description": "Значение флага." }
          },
          "required": ["key", "value"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var target = args.TryGetProperty("target", out var tEl) ? tEl.GetString() ?? "world" : "world";
        var key = args.TryGetProperty("key", out var kEl) ? kEl.GetString() ?? "" : "";
        var value = args.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(key))
            return ToolResult.Error(string.Empty, "Параметр key обязателен.");

        Dictionary<string, object> flags;
        if (string.IsNullOrEmpty(target) ||
            string.Equals(target, "world", StringComparison.OrdinalIgnoreCase))
        {
            flags = world.Flags ??= new Dictionary<string, object>();
        }
        else
        {
            if (!Common.EntityId.TryParse(target, out var eid))
                return ToolResult.Error(string.Empty,
                    $"target «{target}» не является корректным ID сущности.");
            var entity = world.FindEntity(eid);
            if (entity is null)
                return ToolResult.Error(string.Empty, $"Сущность «{target}» не найдена.");
            flags = entity.Flags ??= new Dictionary<string, object>();
        }

        flags[key] = value;
        return ToolResult.Ok(string.Empty, $"Флаг {key}={value} установлен на {target}.");
    };
}

/// <summary>
/// Return a compact overview of the whole world (counts, title, clock, turn).
/// Read-only.
/// </summary>
internal static class GetWorldStateTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "get_world_state",
        Description = "Снимок состояния мира: количество локаций, NPC, зданий, квестов, игроков, заголовок мира, текущее время, ход.",
        ParametersJson = """
        { "type": "object", "properties": {} }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var title = (world.Flags is not null &&
                     world.Flags.TryGetValue("title", out var t) &&
                     t is not null)
            ? t.ToString() ?? "(без названия)"
            : "(без названия)";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Мир: {title}");
        sb.AppendLine($"Локаций: {world.Locations.Count}");
        sb.AppendLine($"NPC: {world.Npcs.Count}");
        sb.AppendLine($"Зданий: {world.Buildings.Count}");
        sb.AppendLine($"Квестов: {world.Quests.Count}");
        sb.AppendLine($"Игроков: {world.Players.Count}");
        sb.AppendLine($"Время: {world.Clock}");
        sb.AppendLine($"Ход: {world.Turn}");
        return ToolResult.Ok(string.Empty, sb.ToString().TrimEnd());
    };
}

/// <summary>
/// Return a snapshot of an NPC by id or name. Defaults to the first NPC at
/// the active player's current location. Read-only.
/// </summary>
internal static class GetNpcStateTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "get_npc_state",
        Description = "Снимок состояния NPC по ID или имени. По умолчанию — первый NPC в локации игрока.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "npcId": { "type": "string", "description": "ID или имя NPC. Пусто = первый NPC в текущей локации игрока." }
          }
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var idOrName = args.TryGetProperty("npcId", out var el) ? el.GetString() ?? "" : "";
        var npc = ResolveNpc(world, idOrName);
        if (npc is null)
            return ToolResult.Error(string.Empty,
                string.IsNullOrEmpty(idOrName)
                    ? "В текущей локации игрока нет NPC."
                    : $"NPC «{idOrName}» не найден.");

        var loc = world.GetLocation(npc.LocationId);
        var attrs = npc.Attributes.Count > 0
            ? string.Join(", ", npc.Attributes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
            : "(нет)";
        var resources = npc.Resources.Count > 0
            ? string.Join(", ", npc.Resources.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
            : "(нет)";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Имя: {npc.Name} ({npc.Id})");
        sb.AppendLine($"Раса: {npc.Race ?? "—"} | Класс: {npc.Class ?? "—"} | Уровень: {npc.Level?.ToString() ?? "—"}");
        sb.AppendLine($"Характеристики: {attrs}");
        sb.AppendLine($"Ресурсы: {resources}");
        sb.AppendLine($"Локация: {loc?.Name ?? "—"} ({npc.LocationId})");
        sb.AppendLine($"Диспозиция: {npc.Disposition ?? "—"} | Поведение: {npc.Behavior ?? "—"}");
        sb.AppendLine($"Жив: {(npc.IsAlive ? "да" : "нет")}");
        return ToolResult.Ok(string.Empty, sb.ToString().TrimEnd());
    };

    /// <summary>
    /// Resolve an NPC by id or name (case-insensitive on names). Empty/null
    /// defaults to the first NPC at the active player's current location.
    /// Internal so other tools (roll_attack, deal_damage, apply_status) reuse
    /// it without duplicating the lookup logic.
    /// </summary>
    internal static Npc? ResolveNpc(MyGame.Core.World.World world, string idOrName)
    {
        if (string.IsNullOrEmpty(idOrName))
        {
            var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
            if (p is null) return null;
            var loc = world.GetLocation(p.LocationId);
            if (loc is null || loc.Npcs.Count == 0) return null;
            return world.GetNpc(loc.Npcs[0]);
        }
        if (Common.EntityId.TryParse(idOrName, out var eid))
        {
            var direct = world.GetNpc(eid);
            if (direct is not null) return direct;
        }
        return world.Npcs.FirstOrDefault(n =>
            string.Equals(n.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolve a character (player or NPC) by target string. "player" returns
    /// the active player; any other value (including empty) goes through NPC
    /// resolution, which defaults to the first NPC at the player's location.
    /// </summary>
    internal static Character? ResolveCharacter(MyGame.Core.World.World world, string target)
    {
        if (string.Equals(target, "player", StringComparison.OrdinalIgnoreCase))
            return world.ActivePlayer ?? world.Players.FirstOrDefault();
        return ResolveNpc(world, target);
    }
}

// ─── Combat / XP tools ───────────────────────────────────────────────────

/// <summary>
/// Grant XP to the active player with a simple level-up rule: each level
/// threshold = current_level × 100 XP. Multi-level-ups resolved in a loop.
/// </summary>
internal static class AwardXpTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "award_xp",
        Description = "Начислить опыт активному игроку. Автоматически повышает уровень при достижении порога (level × 100 XP).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "amount": { "type": "integer", "description": "Сколько XP начислить (>= 0)." }
          },
          "required": ["amount"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        if (!args.TryGetProperty("amount", out var aEl) || !aEl.TryGetInt32(out var amount))
            return ToolResult.Error(string.Empty, "Параметр amount обязателен и должен быть целым числом.");
        if (amount < 0)
            return ToolResult.Error(string.Empty, "amount должно быть >= 0.");

        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        var (leveled, newLevel) = GrantXp(p, amount);
        var text = $"Получено {amount} опыта.";
        if (leveled) text += $" Новый уровень: {newLevel}!";
        return ToolResult.Ok(string.Empty, text);
    };

    /// <summary>
    /// Inline XP-grant + level-up logic shared with <see cref="UpdateQuestTool"/>
    /// (which grants quest-reward XP without a recursive tool call). Threshold
    /// per level = level × 100; multi-level-ups resolved in a loop. Mutates
    /// the player's <see cref="Player.Experience"/> and <see cref="Character.Level"/>.
    /// Returns (leveledUp, finalLevel).
    /// </summary>
    internal static (bool LeveledUp, int FinalLevel) GrantXp(Player p, int amount)
    {
        int xp = p.Experience ?? 0;
        int level = p.Level ?? 1;
        xp += amount;
        bool leveled = false;
        while (xp >= level * 100)
        {
            xp -= level * 100;
            level++;
            leveled = true;
        }
        p.Experience = xp;
        p.Level = level;
        return (leveled, level);
    }
}

/// <summary>
/// Roll a d20 attack against a target NPC's AC. Natural 20 = crit, natural 1
/// = crit-fail, otherwise hit iff total &gt;= AC. Damage is a separate
/// <c>deal_damage</c> call so the GM can narrate between.
/// </summary>
internal static class RollAttackTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "roll_attack",
        Description = "Бросок атаки (d20) по NPC. Natural 20 = крит, natural 1 = крит-провал, иначе попадание если total >= AC цели. Урон — отдельным инструментом deal_damage.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "targetNpcId": { "type": "string", "description": "ID или имя NPC-цели. Пусто = первый враждебный NPC в локации игрока." },
            "modifier": { "type": "integer", "description": "Бонус атаки (по умолчанию 0)." },
            "advantage": { "type": "boolean", "description": "Преимущество (бросок двух d20, берётся больший)." }
          }
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        var targetId = args.TryGetProperty("targetNpcId", out var tEl) ? tEl.GetString() ?? "" : "";
        Npc? npc;
        if (string.IsNullOrEmpty(targetId))
        {
            // Default to the first alive hostile NPC at the player's location.
            var loc = world.GetLocation(p.LocationId);
            npc = loc?.Npcs
                .Select(id => world.GetNpc(id))
                .FirstOrDefault(n => n is not null && n.IsAlive &&
                    string.Equals(n.Disposition, "hostile", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            npc = GetNpcStateTool.ResolveNpc(world, targetId);
        }

        if (npc is null || !npc.IsAlive)
            return ToolResult.Error(string.Empty,
                string.IsNullOrEmpty(targetId)
                    ? "В текущей локации нет враждебных NPC."
                    : $"NPC «{targetId}» не найден или мёртв.");

        var modifier = args.TryGetProperty("modifier", out var mEl) && mEl.TryGetInt32(out var m) ? m : 0;
        // JsonElement has no TryGetBoolean; check ValueKind directly. The
        // model usually emits a proper JSON bool, so we don't bother parsing
        // string-typed "true"/"false" fallbacks.
        var advantage = args.TryGetProperty("advantage", out var advEl) && advEl.ValueKind == JsonValueKind.True;

        int roll = advantage ? D20.Advantage(world.Rng, 20) : D20.Roll(world.Rng, 20);
        int total = roll + modifier;
        int ac = npc.Resources.TryGetValue("ac", out var acVal) ? acVal : 10;

        string outcome;
        if (roll == 20) outcome = "крит";
        else if (roll == 1) outcome = "крит-провал";
        else outcome = total >= ac ? "попадание" : "промах";

        var modStr = modifier >= 0 ? $"+{modifier}" : modifier.ToString();
        return ToolResult.Ok(string.Empty,
            $"Атака по «{npc.Name}» (AC {ac}): бросок {roll}{modStr}={total} — {outcome}.");
    };
}

/// <summary>
/// Deal damage to a target NPC or the player. Decrement <c>hp</c> resource;
/// if hp &lt;= 0, mark the target dead (<see cref="Character.IsAlive"/> = false).
/// </summary>
internal static class DealDamageTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "deal_damage",
        Description = "Нанести урон цели (NPC или игроку). Уменьшает HP; если HP <= 0, цель считается поверженной (IsAlive=false).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "target": { "type": "string", "description": "Цель: \"player\" или ID/имя NPC. Пусто = первый NPC в локации игрока." },
            "amount": { "type": "integer", "description": "Количество урона (>= 0)." },
            "damageType": { "type": "string", "description": "Тип урона (slashing/fire/...), для лога." }
          },
          "required": ["amount"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        if (!args.TryGetProperty("amount", out var aEl) || !aEl.TryGetInt32(out var amount))
            return ToolResult.Error(string.Empty, "Параметр amount обязателен и должен быть целым числом.");
        if (amount < 0)
            return ToolResult.Error(string.Empty, "amount должно быть >= 0.");

        var targetStr = args.TryGetProperty("target", out var tEl) ? tEl.GetString() ?? "" : "";
        var target = GetNpcStateTool.ResolveCharacter(world, targetStr);
        if (target is null)
            return ToolResult.Error(string.Empty,
                string.IsNullOrEmpty(targetStr)
                    ? "В текущей локации нет подходящей цели."
                    : $"Цель «{targetStr}» не найдена.");
        if (!target.IsAlive)
            return ToolResult.Error(string.Empty, $"Цель «{target.Name}» уже мертва.");

        if (!target.Resources.TryGetValue("hp", out var hp)) hp = 1;
        int hpAfter = hp - amount;
        if (hpAfter < 0) hpAfter = 0;
        target.Resources["hp"] = hpAfter;

        var text = $"«{target.Name}» получает {amount} урона. HP: {hpAfter}.";
        if (hpAfter <= 0)
        {
            target.IsAlive = false;
            text += $" {target.Name} повержен!";
        }
        return ToolResult.Ok(string.Empty, text);
    };
}

/// <summary>
/// Apply a status effect to a character (player or NPC) for N turns.
/// </summary>
internal static class ApplyStatusTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "apply_status",
        Description = "Наложить статус-эффект на персонажа (player или NPC).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "target": { "type": "string", "description": "Цель: \"player\" или ID/имя NPC. Пусто = первый NPC в локации игрока." },
            "name": { "type": "string", "description": "Название статуса (напр. «Отравление»)." },
            "description": { "type": "string", "description": "Описание эффекта." },
            "duration": { "type": "integer", "description": "Длительность в ходах (по умолчанию 3)." }
          },
          "required": ["name", "description"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var name = args.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
        var description = args.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name))
            return ToolResult.Error(string.Empty, "Параметр name обязателен.");
        if (string.IsNullOrEmpty(description))
            return ToolResult.Error(string.Empty, "Параметр description обязателен.");

        var duration = args.TryGetProperty("duration", out var duEl) && duEl.TryGetInt32(out var du) && du >= 0
            ? du : 3;

        var targetStr = args.TryGetProperty("target", out var tEl) ? tEl.GetString() ?? "" : "";
        var target = GetNpcStateTool.ResolveCharacter(world, targetStr);
        if (target is null)
            return ToolResult.Error(string.Empty,
                string.IsNullOrEmpty(targetStr)
                    ? "В текущей локации нет подходящей цели."
                    : $"Цель «{targetStr}» не найдена.");

        var effect = new StatusEffect
        {
            Id = Common.EntityId.NewId(),
            Name = name,
            Description = description,
            Duration = duration,
        };
        target.Effects.Add(effect);
        return ToolResult.Ok(string.Empty,
            $"«{target.Name}» получил статус «{name}» на {duration} ходов.");
    };
}

// ─── Runtime content authoring ───────────────────────────────────────────

/// <summary>
/// Register a custom item template at runtime (for AI-invented items not in
/// the embedded content pack). If <c>damageDice</c> is supplied, the template
/// becomes a weapon.
/// </summary>
internal static class CreateItemTemplateTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "create_item_template",
        Description = "Зарегистрировать кастомный шаблон предмета во время игры (для AI-выдуманных предметов). При наличии damageDice становится оружием.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "description": "ID шаблона (уникальный)." },
            "name": { "type": "string", "description": "Название предмета." },
            "description": { "type": "string", "description": "Описание." },
            "category": { "type": "string", "description": "Категория (по умолчанию \"misc\")." },
            "weight": { "type": "number", "description": "Вес (фунты, по умолчанию 0.5)." },
            "value": { "type": "number", "description": "Стоимость (по умолчанию 0)." },
            "rarity": { "type": "string", "description": "Редкость (по умолчанию \"common\")." },
            "damageDice": { "type": "string", "description": "Кости урона (напр. 1d6) — превращает предмет в оружие." },
            "damageType": { "type": "string", "description": "Тип урона (slashing/fire/...)." }
          },
          "required": ["id", "name", "description"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var name = args.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
        var description = args.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id))
            return ToolResult.Error(string.Empty, "Параметр id обязателен.");
        if (string.IsNullOrEmpty(name))
            return ToolResult.Error(string.Empty, "Параметр name обязателен.");
        if (string.IsNullOrEmpty(description))
            return ToolResult.Error(string.Empty, "Параметр description обязателен.");

        var category = args.TryGetProperty("category", out var cEl) ? cEl.GetString() ?? "misc" : "misc";
        if (string.IsNullOrWhiteSpace(category)) category = "misc";

        var weight = args.TryGetProperty("weight", out var wEl) && wEl.TryGetDouble(out var w) ? w : 0.5;
        var value = args.TryGetProperty("value", out var vEl) && vEl.TryGetDouble(out var v) ? v : 0;
        var rarity = args.TryGetProperty("rarity", out var rEl) ? rEl.GetString() ?? "common" : "common";
        if (string.IsNullOrWhiteSpace(rarity)) rarity = "common";
        var damageDice = args.TryGetProperty("damageDice", out var ddEl) ? ddEl.GetString() : null;
        var damageType = args.TryGetProperty("damageType", out var dtEl) ? dtEl.GetString() : null;

        WeaponSpec? weapon = null;
        if (!string.IsNullOrWhiteSpace(damageDice))
        {
            weapon = new WeaponSpec
            {
                Type = "simple",
                Damage = new Damage(
                    damageDice!,
                    string.IsNullOrWhiteSpace(damageType) ? "slashing" : damageType!),
            };
            if (string.Equals(category, "misc", StringComparison.OrdinalIgnoreCase))
                category = "weapon";
        }

        var tpl = new ItemTemplate
        {
            Id = id,
            Name = name,
            Description = description,
            Category = category,
            Weight = weight,
            Value = value,
            Rarity = rarity,
            Stackable = false,
            Weapon = weapon,
        };
        world.Registries.Items.Register(tpl);
        return ToolResult.Ok(string.Empty, $"Создан шаблон предмета «{name}» (id: {id}).");
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
