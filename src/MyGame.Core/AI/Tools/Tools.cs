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
    private static readonly JsonElement EmptyObjectElement = JsonDocument.Parse("{}").RootElement.Clone();

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
            return EmptyObjectElement;
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
                return EmptyObjectElement;
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

        // COMBAT-DEATH expansion — 4 tools covering the structured combat
        // state machine (start/end/next-turn) and the player death-save
        // roll. The GM drives combat via these + the existing
        // roll_attack / deal_damage / apply_status trio; the state machine
        // just tracks whose turn it is so the GM can be told (via the
        // "## БОЙ" block in the system prompt) not to act out of order.
        Register(StartCombatTool.Definition, StartCombatTool.Handle(_world));
        Register(EndCombatTool.Definition, EndCombatTool.Handle(_world));
        Register(NextTurnTool.Definition, NextTurnTool.Handle(_world));
        Register(DeathSaveTool.Definition, DeathSaveTool.Handle(_world));

        // Runtime content authoring — lets the GM invent new entity types
        // mid-game (create_item_template already registered above). These
        // are essential for AI-generated worlds where the planner may not
        // have anticipated every entity the GM needs.
        Register(CreateNpcTemplateTool.Definition, CreateNpcTemplateTool.Handle(_world));
        Register(CreateBuildingTemplateTool.Definition, CreateBuildingTemplateTool.Handle(_world));

        // ENGINE-DEPTH (issues #34/#36/#43): weather, faction reputation,
        // and lore query tools. All optional — they no-op on worlds that
        // haven't activated the corresponding subsystem (null weather /
        // empty Factions / null Lore). The GM context (GameMaster) tells
        // the model when these subsystems are active and what their
        // mechanical effects are.
        Register(SetWeatherTool.Definition, SetWeatherTool.Handle(_world));
        Register(GetWeatherTool.Definition, GetWeatherTool.Handle(_world));
        Register(AdjustReputationTool.Definition, AdjustReputationTool.Handle(_world));
        Register(GetFactionsTool.Definition, GetFactionsTool.Handle(_world));
        Register(GetLoreTool.Definition, GetLoreTool.Handle(_world));

        // Economy + crafting + containers (issues #37, #65, #67)
        Register(CraftItemTool.Definition, CraftItemTool.Handle(_world));
        Register(SearchLocationTool.Definition, SearchLocationTool.Handle(_world));
        Register(SetMarketPriceTool.Definition, SetMarketPriceTool.Handle(_world));
        Register(GetMarketPriceTool.Definition, GetMarketPriceTool.Handle(_world));

        // Procedural dungeon generation (issue #38)
        Register(GenerateDungeonTool.Definition, GenerateDungeonTool.Handle(_world));
        // Start-scene / world-setup tools — used by StartSceneAgent to create
        // role-appropriate locations + grant starting currency. Also available
        // to the GM mid-game for world expansion.
        Register(CreateLocationTool.Definition, CreateLocationTool.Handle(_world));
        Register(GiveCurrencyTool.Definition, GiveCurrencyTool.Handle(_world));
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
            ? string.Join(", ", loc.Npcs
                .Select(id => world.GetNpc(id))
                .Where(n => n is not null)
                .Select(n => n!.IsAlive ? $"{n!.Name} ({n!.Id})" : $"{n!.Name} (мёртв)"))
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

        // STACK-MERGE (issue #64): if the template is stackable AND the
        // player already carries an item with the same TemplateId, merge
        // into that stack instead of creating a new entry. This keeps the
        // inventory tidy — five separate «Зелье лечения ×1» rows collapse
        // into one «Зелье лечения ×5». Non-stackable templates always
        // instantiate a fresh Item (each instance is a distinct object).
        if (tpl.Stackable)
        {
            var existing = p.Inventory.Items.FirstOrDefault(i =>
                i.TemplateId == templateId && !i.Equipped);
            if (existing is not null)
            {
                existing.Quantity += qty;
                return ToolResult.Ok(string.Empty,
                    $"Выдан «{existing.Name}» ×{qty} (всего ×{existing.Quantity}).");
            }
        }

        var item = EntityFactory.InstantiateItem(tpl, qty);
        p.Inventory.Items.Add(item);
        return ToolResult.Ok(string.Empty,
            $"Выдан «{item.Name}» ×{qty}. itemId={item.Id} (используй это для equip_player, или просто передай templateId=«{templateId}» в equip_player).");
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
        Description = "Экипировать предмет из инвентаря игрока в слот. Старый предмет возвращается в инвентарь. Слот автоопределяется по шаблону (weapon/armor/misc). Можно указать templateId вместо itemId — движок сам найдёт экземпляр в инвентаре.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "itemId": { "type": "string", "description": "ID экземпляра предмета в инвентаре (необязательно если указан templateId)." },
            "templateId": { "type": "string", "description": "ID шаблона предмета (альтернатива itemId — движок найдёт первый экземпляр этого шаблона в инвентаре)." },
            "slot": { "type": "string", "description": "Слот (weapon/armor/shield/...). Если пусто — автоопределение по шаблону." }
          }
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        var itemId = args.TryGetProperty("itemId", out var iEl) ? iEl.GetString() ?? "" : "";
        var templateId = args.TryGetProperty("templateId", out var tEl) ? tEl.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(itemId) && string.IsNullOrEmpty(templateId))
            return ToolResult.Error(string.Empty, "Нужен itemId или templateId.");

        int idx;
        if (!string.IsNullOrEmpty(itemId))
        {
            // Try instance ID first.
            idx = p.Inventory.Items.FindIndex(it =>
                string.Equals(it.Id.ToString(), itemId, StringComparison.OrdinalIgnoreCase));
            // If not found by instance ID, try as templateId fallback.
            if (idx < 0)
                idx = p.Inventory.Items.FindIndex(it =>
                    string.Equals(it.TemplateId, itemId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // templateId mode — find first matching instance.
            idx = p.Inventory.Items.FindIndex(it =>
                string.Equals(it.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));
        }

        if (idx < 0)
            return ToolResult.Error(string.Empty,
                $"Предмет не найден в инвентаре игрока (искали itemId=«{itemId}», templateId=«{templateId}»).");

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
/// objective_undone. Completion STAGES the reward (currency, XP, items)
/// in <see cref="Quest.UnclaimedRewards"/> rather than granting it
/// inline — the player must claim it via the Quest panel's
/// «Получить награду» button (issue #70).
/// </summary>
internal static class UpdateQuestTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "update_quest",
        Description = "Изменить состояние квеста: activate / complete / fail / objective_done / objective_undone. При complete награда ожидает получения игроком во вкладке Квесты.",
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
                // Issue #70: stage the reward in UnclaimedRewards rather
                // than granting it inline. The player must click
                // «Получить награду» in the Quest panel to actually
                // receive the currency / XP / items. The host
                // (GameViewModel) detects this transition via the tool-
                // call result text + emits a system log entry telling
                // the player to go claim.
                quest.Status = QuestStatus.Completed;
                quest.UnclaimedRewards = quest.Reward;
                return ToolResult.Ok(string.Empty,
                    $"Квест «{quest.Name}» выполнен. Награда ожидает получения во вкладке Квесты.");
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
            // COMBAT-DEATH: branch on player vs NPC.
            //
            // NPC: mark dead (existing behaviour). The GM can narrate
            // looting — the corpse stays in the world.
            //
            // Player: do NOT auto-kill. Death saves apply first: the
            // player is unconscious at 0 HP, and the GM must call
            // death_save on each subsequent turn until the player
            // stabilises (3 successes) or dies (3 failures). We seed
            // the world.Flags["deathSaves"] = "0,0" so the death_save
            // tool has a starting point.
            if (target is Player)
            {
                world.Flags ??= new Dictionary<string, object>();
                if (!world.Flags.ContainsKey("deathSaves"))
                    world.Flags["deathSaves"] = "0,0";
                text += $" {target.Name} падает без сознания! Требуются спасброски от смерти.";
            }
            else
            {
                target.IsAlive = false;
                text += $" {target.Name} повержен!";
                // If this NPC is in the combat turn order, drop them so
                // the index math stays simple. If the turn order ends up
                // empty OR only the player remains, auto-end combat.
                if (world.Combat is { Active: true } combat)
                {
                    var removed = combat.TurnOrder
                        .RemoveAll(c => c.EntityId == target.Id);
                    if (removed > 0)
                    {
                        // Clamp the current-actor index in case we just
                        // removed the actor whose turn it was (or one
                        // before them).
                        if (combat.CurrentActorIndex >= combat.TurnOrder.Count)
                            combat.CurrentActorIndex = Math.Max(0, combat.TurnOrder.Count - 1);

                        // Auto-end combat when only the player (or no
                        // one) is left in the turn order.
                        var remainingNonPlayer = combat.TurnOrder
                            .Count(c => !world.Players.Any(p => p.Id == c.EntityId));
                        if (combat.TurnOrder.Count == 0 || remainingNonPlayer == 0)
                        {
                            combat.Active = false;
                            world.Combat = null;
                            text += " Бой окончен.";
                        }
                    }
                }
            }
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

// ─── Combat state machine + death-save tools (issue #88, #63) ────────────

/// <summary>
/// Start structured combat: collect the player + every alive hostile NPC at
/// the player's current location, roll initiative for each (d20 + DEX
/// modifier), sort descending, and install the result on
/// <see cref="World.Combat"/>. The GM is then expected to call
/// <c>next_turn</c> after each combatant's action.
/// </summary>
internal static class StartCombatTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "start_combat",
        Description = "Начать структурный бой: игрок + все живые враждебные NPC в текущей локации. Бросает инициативу (d20 + модификатор ЛОВ), сортирует по убыванию. Вызывай в начале боя и затем next_turn после каждого действия.",
        ParametersJson = """
        { "type": "object", "properties": {} }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        // Combatants: the player + every alive NPC at the player's
        // location whose Disposition is "hostile" (case-insensitive). If
        // no hostiles are present, we still start combat (the GM may
        // want initiative pre-rolled for an about-to-ambush), but warn
        // in the result text.
        var loc = world.GetLocation(p.LocationId);
        var hostiles = loc?.Npcs
            .Select(id => world.GetNpc(id))
            .Where(n => n is not null && n.IsAlive &&
                string.Equals(n.Disposition, "hostile", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new();

        var combatants = new List<Combatant>();

        // Player's initiative: d20 + DEX modifier (from Attributes["dex"];
        // default 10 → modifier 0 when missing). The modifier is computed
        // via the ruleset's AttributeModifier formula (so non-D&D
        // rulesets can plug in their own DEX-equivalent).
        int playerInit = D20.Roll(world.Rng, 20) + GetDexModifier(world, p);
        combatants.Add(new Combatant(p.Id, p.Name, playerInit, HasActedThisRound: false));

        foreach (var n in hostiles!)
        {
            int init = D20.Roll(world.Rng, 20) + GetDexModifier(world, n!);
            combatants.Add(new Combatant(n!.Id, n.Name, init, HasActedThisRound: false));
        }

        // Sort descending by initiative. Stable order preserves player-
        // first tie-break (the player was added first), so on a tie the
        // player goes before NPCs — the friendly default for a single-
        // player CRPG.
        combatants = combatants
            .Select((c, idx) => (c, idx))
            .OrderByDescending(x => x.c.Initiative)
            .ThenBy(x => x.idx)
            .Select(x => x.c)
            .ToList();

        world.Combat = new CombatState
        {
            Active = true,
            Round = 1,
            TurnOrder = combatants,
            CurrentActorIndex = 0,
            StartedAtTurn = world.Turn,
        };

        // Mark the first actor as having acted this round so the GM knows
        // the current actor is "live" — the next_turn call will advance
        // to the next combatant. (The flag is informational; the GM is
        // expected to call next_turn after each action regardless.)
        if (combatants.Count > 0)
            combatants[0] = combatants[0] with { HasActedThisRound = true };

        var order = string.Join(", ", combatants.Select(c => $"{c.Name} ({c.Initiative})"));
        var text = $"Бой начался! Инициатива: {order}.";
        if (hostiles.Count == 0)
            text += " (Враждебных NPC в локации не найдено — бой объявлен, но возможно ожидается засада.)";
        return ToolResult.Ok(string.Empty, text);
    };

    /// <summary>
    /// Get the DEX modifier for a character: looks up Attributes["dex"]
    /// (default 10), then applies the ruleset's modifier formula
    /// (<c>floor((v-10)/2)</c> for the default D&amp;D ruleset). Returns
    /// 0 when the attribute is missing or the ruleset doesn't define a
    /// modifier formula. Internal so the other combat tools can reuse it.
    /// </summary>
    internal static int GetDexModifier(MyGame.Core.World.World world, Character c)
    {
        int dexVal = c.Attributes.TryGetValue("dex", out var dv) ? dv : 10;
        return (int)Rulesets.AttributeModifier(world.Ruleset, "dex", dexVal);
    }
}

/// <summary>
/// End combat immediately: clear <see cref="World.Combat"/>. The GM calls
/// this when the fight is over (everyone surrendered, fled, or died). The
/// deal_damage tool also auto-calls this when only the player remains in
/// the turn order.
/// </summary>
internal static class EndCombatTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "end_combat",
        Description = "Завершить структурный бой (сбрасывает состояние боя). Вызывай, когда бой окончен: все противники побеждены, разбежались или сдались.",
        ParametersJson = """
        { "type": "object", "properties": {} }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        bool wasActive = world.Combat is { Active: true };
        world.Combat = null;
        return ToolResult.Ok(string.Empty, wasActive ? "Бой окончен." : "Бой не был активен.");
    };
}

/// <summary>
/// Advance to the next combatant's turn. Wraps around at the end of the
/// turn order (incrementing <see cref="CombatState.Round"/> and resetting
/// all <see cref="Combatant.HasActedThisRound"/> flags). The new current
/// actor is marked as having acted this round.
/// </summary>
internal static class NextTurnTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "next_turn",
        Description = "Передать ход следующему бойцу в инициативе. В конце раунда начинает новый раунд. Вызывай после каждого действия в бою.",
        ParametersJson = """
        { "type": "object", "properties": {} }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        if (world.Combat is not { Active: true } combat)
            return ToolResult.Error(string.Empty, "Бой не активен. Сначала вызови start_combat.");
        if (combat.TurnOrder.Count == 0)
            return ToolResult.Error(string.Empty, "Очередь боя пуста.");

        combat.CurrentActorIndex++;
        if (combat.CurrentActorIndex >= combat.TurnOrder.Count)
        {
            combat.CurrentActorIndex = 0;
            combat.Round++;
            // Reset the HasActedThisRound flag for every combatant at
            // the start of a new round. Combatant is a record, so we
            // rebuild the list with new values.
            combat.TurnOrder = combat.TurnOrder
                .Select(c => c with { HasActedThisRound = false })
                .ToList();
        }

        var current = combat.TurnOrder[combat.CurrentActorIndex];
        combat.TurnOrder[combat.CurrentActorIndex] = current with { HasActedThisRound = true };

        return ToolResult.Ok(string.Empty,
            $"Раунд {combat.Round}. Ход: {current.Name}.");
    };
}

/// <summary>
/// Roll a death save for the active player. Tracks successes / failures
/// in <c>World.Flags["deathSaves"]</c> as <c>"S,F"</c> (0-3 each).
///
/// <list type="bullet">
///   <item>Natural 20: regain 1 HP, conscious, saves reset.</item>
///   <item>Natural 1: 2 failures.</item>
///   <item>10-19: 1 success.</item>
///   <item>2-9: 1 failure.</item>
///   <item>3 successes: stable (HP stays 0, no more saves needed).</item>
///   <item>3 failures: <see cref="Character.IsAlive"/> = false — the player
///     is dead.</item>
/// </list>
/// </summary>
internal static class DeathSaveTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "death_save",
        Description = "Спасбросок от смерти для игрока (d20). 10+ = успех, &lt;10 = провал, natural 20 = +1 HP и сознание, natural 1 = 2 провала. 3 успеха = стабилен, 3 провала = смерть. Вызывай каждый ход, пока игрок на 0 HP.",
        ParametersJson = """
        { "type": "object", "properties": {} }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        // The player must be at 0 HP for a death save to make sense.
        // (We tolerate missing 'hp' — treat as 0 — so a freshly-reduced
        // player can be saved without a deal_damage call first.)
        int hp = p.Resources.TryGetValue("hp", out var hpVal) ? hpVal : 0;
        if (hp > 0)
            return ToolResult.Error(string.Empty,
                $"Игрок «{p.Name}» не на 0 HP (текущий HP: {hp}) — спасбросок не нужен.");

        world.Flags ??= new Dictionary<string, object>();
        var (successes, failures) = ReadDeathSaves(world.Flags);

        int roll = D20.Roll(world.Rng, 20);

        int successDelta = 0;
        int failureDelta = 0;
        string specialNote = "";

        if (roll == 20)
        {
            // Natural 20: regain 1 HP, become conscious, reset saves.
            p.Resources["hp"] = 1;
            successes = 0;
            failures = 0;
            specialNote = "Natural 20! Игрок приходит в сознание с 1 HP.";
        }
        else if (roll == 1)
        {
            // Natural 1: 2 failures.
            failureDelta = 2;
            specialNote = "Natural 1! Два провала.";
        }
        else if (roll >= 10)
        {
            successDelta = 1;
        }
        else
        {
            failureDelta = 1;
        }

        successes = Math.Min(3, successes + successDelta);
        failures = Math.Min(3, failures + failureDelta);

        // 3 failures → dead. 3 successes → stable (HP stays 0, no more
        // saves needed; clears the deathSaves flag so a subsequent
        // heal-and-drop-to-0 starts fresh).
        bool died = false;
        bool stabilized = false;
        if (failures >= 3 && roll != 20)
        {
            p.IsAlive = false;
            died = true;
            world.Flags.Remove("deathSaves");
        }
        else if (successes >= 3 && roll != 20)
        {
            stabilized = true;
            world.Flags["deathSaves"] = "3,0";
        }
        else if (roll != 20)
        {
            world.Flags["deathSaves"] = $"{successes},{failures}";
        }
        else
        {
            // Natural 20 cleared the flag above implicitly (saves reset).
            // Make sure it's not lingering from a previous turn.
            world.Flags.Remove("deathSaves");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"Спасбросок от смерти: d20={roll}. ");
        if (!string.IsNullOrEmpty(specialNote))
            sb.Append($"{specialNote} ");
        sb.Append($"Успехи: {successes}/3, Провалы: {failures}/3.");
        if (died)
            sb.Append(" Игрок погиб!");
        else if (stabilized)
            sb.Append(" Игрок стабилизирован (больше спасбросков не нужно).");
        return ToolResult.Ok(string.Empty, sb.ToString().Trim());
    };

    /// <summary>
    /// Parse the <c>deathSaves</c> flag (<c>"S,F"</c>) into a tuple.
    /// Missing / malformed → (0, 0). Internal so other tools / the UI
    /// can read the same state.
    /// </summary>
    internal static (int Successes, int Failures) ReadDeathSaves(
        Dictionary<string, object> flags)
    {
        if (!flags.TryGetValue("deathSaves", out var v) || v is null)
            return (0, 0);
        var s = v.ToString() ?? "";
        var parts = s.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return (0, 0);
        if (!int.TryParse(parts[0], out var suc)) suc = 0;
        if (!int.TryParse(parts[1], out var fail)) fail = 0;
        return (Math.Clamp(suc, 0, 3), Math.Clamp(fail, 0, 3));
    }
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

// ─── Runtime content authoring: NPC + building templates ───────────────
// (create_item_template already exists above; these two mirror it for NPCs
//  and buildings, letting the GM invent new entity types mid-game without
//  a world rebuild. Useful for AI-generated worlds where the planner may
//  not have anticipated every entity the GM needs.)

/// <summary>
/// Register a custom NPC template at runtime. The GM can then spawn_npc by
/// this new templateId. Lets the GM invent new NPCs mid-game (e.g. a unique
/// boss the planner didn't anticipate, a custom merchant with special stock).
/// </summary>
internal static class CreateNpcTemplateTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "create_npc_template",
        Description = "Создать кастомный шаблон NPC в рантайме. После создания можно спавнить через spawn_npc с этим templateId. Используй для уникальных NPC (боссы, ключевые персонажи), которых не было в изначальном плане мира.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "description": "Уникальный ID, напр. npc_custom_bandit_chief" },
            "name": { "type": "string", "description": "Имя NPC." },
            "race": { "type": "string", "description": "Раса: human, elf, dwarf, orc, goblin, или кастомная." },
            "class": { "type": "string", "description": "Класс/роль: fighter, wizard, rogue, или кастомный." },
            "level": { "type": "integer", "description": "Уровень (1-20)." },
            "attributes": { "type": "object", "description": "Характеристики: {str, dex, con, int, wis, cha}." },
            "resources": { "type": "object", "description": "Ресурсы: {hp, ac}." },
            "disposition": { "type": "string", "description": "Расположение: friendly, neutral, hostile, allied." },
            "behavior": { "type": "string", "description": "Подсказка GM: как NPC себя ведёт в бою/диалоге." },
            "description": { "type": "string", "description": "Атмосферное описание NPC." }
          },
          "required": ["id", "name", "race", "class", "level", "attributes", "disposition", "behavior", "description"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error(string.Empty, "Параметр id обязателен.");

        var name = args.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
        var race = args.TryGetProperty("race", out var rEl) ? rEl.GetString() ?? "human" : "human";
        var cls = args.TryGetProperty("class", out var cEl) ? cEl.GetString() ?? "fighter" : "fighter";
        var level = args.TryGetProperty("level", out var lEl) && lEl.TryGetInt32(out var lv) ? lv : 1;
        var disposition = args.TryGetProperty("disposition", out var dEl) ? dEl.GetString() ?? "neutral" : "neutral";
        var behavior = args.TryGetProperty("behavior", out var bEl) ? bEl.GetString() ?? "" : "";
        var description = args.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "";

        var attributes = new Dictionary<string, int>();
        if (args.TryGetProperty("attributes", out var aEl) && aEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in aEl.EnumerateObject())
            {
                if (prop.Value.TryGetInt32(out var val))
                    attributes[prop.Name] = val;
            }
        }

        var resources = new Dictionary<string, int>();
        if (args.TryGetProperty("resources", out var resEl) && resEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in resEl.EnumerateObject())
            {
                if (prop.Value.TryGetInt32(out var val))
                    resources[prop.Name] = val;
            }
        }

        var tpl = new MyGame.Core.World.Content.NpcTemplate
        {
            Id = id,
            Name = name,
            Race = race,
            Class = cls,
            Level = level,
            Attributes = attributes,
            Resources = resources.Count > 0 ? resources : null,
            Disposition = disposition,
            Behavior = behavior,
            Description = description,
        };

        world.Registries.Npcs.Register(tpl);
        return ToolResult.Ok(string.Empty, $"Создан шаблон NPC «{name}» (id: {id}, раса: {race}, класс: {cls}, ур. {level}).");
    };
}

/// <summary>
/// Register a custom building template at runtime. Lets the GM add new
/// buildings to locations mid-game (e.g. a camp the player builds, a
/// structure that appears after a quest event).
/// </summary>
internal static class CreateBuildingTemplateTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "create_building_template",
        Description = "Создать кастомный шаблон здания в рантайме. После создания можно спавнить через spawn_building с этим templateId.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "description": "Уникальный ID, напр. bld_custom_camp" },
            "name": { "type": "string", "description": "Название здания." },
            "type": { "type": "string", "description": "Тип: tavern, shop, temple, tower, house, ruins, landmark, или кастомный." },
            "description": { "type": "string", "description": "Атмосферное описание." },
            "enterable": { "type": "boolean", "description": "Можно ли войти внутрь." }
          },
          "required": ["id", "name", "type", "description", "enterable"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var id = args.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error(string.Empty, "Параметр id обязателен.");

        var name = args.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
        var type = args.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "landmark" : "landmark";
        var description = args.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
        var enterable = args.TryGetProperty("enterable", out var eEl) && eEl.ValueKind == JsonValueKind.True;

        var tpl = new MyGame.Core.World.Content.BuildingTemplate
        {
            Id = id,
            Name = name,
            Type = type,
            Description = description,
            Enterable = enterable,
        };

        world.Registries.Buildings.Register(tpl);
        return ToolResult.Ok(string.Empty, $"Создан шаблон здания «{name}» (id: {id}, тип: {type}).");
    };
}

// ─── ENGINE-DEPTH: weather / factions / lore (issues #34 / #36 / #43) ─────

/// <summary>
/// Set the world's current weather (issue #34). Activates the weather
/// subsystem on worlds where it was null — once activated, the GM context
/// block surfaces the weather + its mechanical effects (travel time,
/// encounter chance, Perception disadvantage) to the model.
/// </summary>
internal static class SetWeatherTool
{
    // Canonical weather vocabulary. Keys are the lowercase English tags
    // the model emits; values are the RU display labels + emoji used by
    // the UI / GM context. Anything outside this set is rejected.
    internal static readonly IReadOnlyDictionary<string, string> WeatherLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["clear"] = "🌤 Ясно",
        ["rain"] = "🌧 Дождь",
        ["storm"] = "⛈ Гроза",
        ["fog"] = "🌫 Туман",
        ["snow"] = "❄ Снег",
        ["overcast"] = "☁ Облачно",
    };

    public static ToolDefinition Definition { get; } = new()
    {
        Name = "set_weather",
        Description = "Установить текущую погоду мира (clear/rain/storm/fog/snow/overcast) и опциональный прогноз. Активирует подсистему погоды — влияет на время путешествия, шанс случайных встреч, броски Внимательности.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "weather": { "type": "string", "description": "Тип погоды: clear | rain | storm | fog | snow | overcast." },
            "forecast": { "type": "string", "description": "Краткий прогноз на русском (опц., напр. «К вечеру ожидается гроза»)." }
          },
          "required": ["weather"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var weather = args.TryGetProperty("weather", out var wEl) ? wEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(weather))
            return ToolResult.Error(string.Empty, "Параметр weather обязателен.");

        // Normalize to lowercase so model-emitted "Rain"/"RAIN" work too.
        weather = weather.Trim().ToLowerInvariant();
        if (!WeatherLabels.ContainsKey(weather))
        {
            var valid = string.Join(", ", WeatherLabels.Keys.OrderBy(k => k));
            return ToolResult.Error(string.Empty,
                $"Неизвестная погода «{weather}». Допустимо: {valid}.");
        }

        var forecast = args.TryGetProperty("forecast", out var fEl) ? fEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(forecast)) forecast = null;

        world.CurrentWeather = weather;
        world.WeatherForecast = forecast;

        var label = WeatherLabels[weather];
        var text = forecast is null
            ? $"Погода: {label}."
            : $"Погода: {label}. Прогноз: {forecast}.";
        return ToolResult.Ok(string.Empty, text);
    };
}

/// <summary>
/// Read the current weather + forecast (issue #34). Read-only. Returns a
/// "no weather set" message when the subsystem isn't active.
/// </summary>
internal static class GetWeatherTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "get_weather",
        Description = "Возвращает текущую погоду и прогноз. Только чтение.",
        ParametersJson = """
        { "type": "object", "properties": {} }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        if (string.IsNullOrWhiteSpace(world.CurrentWeather))
            return ToolResult.Ok(string.Empty,
                "Погода ещё не задана. Используй set_weather, чтобы активировать подсистему погоды.");

        var label = SetWeatherTool.WeatherLabels.TryGetValue(world.CurrentWeather, out var l)
            ? l
            : world.CurrentWeather;
        var text = $"Погода: {label}.";
        if (!string.IsNullOrWhiteSpace(world.WeatherForecast))
            text += $" Прогноз: {world.WeatherForecast}.";
        return ToolResult.Ok(string.Empty, text);
    };
}

/// <summary>
/// Adjust a faction's reputation with the player (issue #36). Clamps to
/// [-100, 100] and recomputes the alignment bucket (ally / neutral /
/// hostile) so callers always see a consistent pair.
/// </summary>
internal static class AdjustReputationTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "adjust_reputation",
        Description = "Изменить репутацию фракции с игроком на delta (может быть отрицательной). Зажимает в [-100, 100] и пересчитывает выравнивание (>=30 ally, <=-30 hostile, иначе neutral).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "factionName": { "type": "string", "description": "Имя фракции (как в плане мира)." },
            "delta": { "type": "integer", "description": "Изменение репутации (может быть отрицательным)." }
          },
          "required": ["factionName", "delta"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var name = args.TryGetProperty("factionName", out var nEl) ? nEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Error(string.Empty, "Параметр factionName обязателен.");

        if (!args.TryGetProperty("delta", out var dEl) || !dEl.TryGetInt32(out var delta))
            return ToolResult.Error(string.Empty, "Параметр delta обязателен и должен быть целым числом.");

        var faction = world.Factions.FirstOrDefault(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (faction is null)
        {
            var known = world.Factions.Count > 0
                ? string.Join(", ", world.Factions.Select(f => $"«{f.Name}»").OrderBy(s => s))
                : "(нет фракций)";
            return ToolResult.Error(string.Empty,
                $"Фракция «{name}» не найдена. Доступные: {known}.");
        }

        faction.Reputation = Math.Clamp(faction.Reputation + delta, -100, 100);
        faction.RecomputeAlignment();
        return ToolResult.Ok(string.Empty,
            $"Репутация фракции «{faction.Name}»: {faction.Reputation} ({faction.Alignment}).");
    };
}

/// <summary>
/// List all factions with their alignment + reputation (issue #36). Read-only.
/// </summary>
internal static class GetFactionsTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "get_factions",
        Description = "Возвращает список всех фракций мира с выравниванием и репутацией. Только чтение.",
        ParametersJson = """
        { "type": "object", "properties": {} }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        if (world.Factions.Count == 0)
            return ToolResult.Ok(string.Empty,
                "В этом мире нет фракций.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Фракции мира:");
        foreach (var f in world.Factions)
            sb.AppendLine($"- {f.Name} [{f.Alignment}, реп. {f.Reputation}]{(string.IsNullOrWhiteSpace(f.Type) ? "" : $" ({f.Type})")}");
        return ToolResult.Ok(string.Empty, sb.ToString().TrimEnd());
    };
}

/// <summary>
/// Query a lore entry by topic (issue #43). With no topic, returns the
/// list of available topics. Read-only — the canonical lore is set at
/// world-build time and never mutated.
/// </summary>
internal static class GetLoreTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "get_lore",
        Description = "Запросить канонический лор мира по теме (deities/history/magic/cultures/economy/current events). Без темы — список доступных тем. Только чтение. Используй вместо выдумывания фактов о мире.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "topic": { "type": "string", "description": "Тема лора (опц.): deities | history | magic | cultures | economy | current events." }
          }
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        if (world.Lore is null || world.Lore.Entries.Count == 0)
            return ToolResult.Ok(string.Empty,
                "В этом мире нет базы лора. Опирайся на общий сеттинг и контекст сцены.");

        var topic = args.TryGetProperty("topic", out var tEl) ? tEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(topic))
        {
            var topics = string.Join(", ", world.Lore.Topics);
            return ToolResult.Ok(string.Empty,
                $"Доступные темы лора: {topics}. Вызови get_lore с одним из них для деталей.");
        }

        var entry = world.Lore.Get(topic);
        if (entry is null)
        {
            var topics = string.Join(", ", world.Lore.Topics);
            return ToolResult.Error(string.Empty,
                $"Тема «{topic}» не найдена в базе лора. Доступные: {topics}.");
        }

        return ToolResult.Ok(string.Empty, $"## {entry.Topic}\n{entry.Content}");
    };
}

// ─── Economy + crafting + containers (issues #37, #65, #67) ───────────

/// <summary>
/// Craft an item from a recipe. Consumes input items from the player's
/// inventory, produces the output item. Issue #65.
/// </summary>
internal static class CraftItemTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "craft_item",
        Description = "Скрафтить предмет по рецепту. Проверяет наличие ингредиентов в инвентаре игрока, потребляет их, создаёт результат. Если нужен навык — GM должен сначала сделать бросок.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "recipeId": { "type": "string", "description": "ID рецепта, напр. craft_health_potion." }
          },
          "required": ["recipeId"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var recipeId = args.TryGetProperty("recipeId", out var rEl) ? rEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(recipeId))
            return ToolResult.Error(string.Empty, "Параметр recipeId обязателен.");

        var recipe = world.Registries.Recipes.Get(recipeId);
        if (recipe is null)
            return ToolResult.Error(string.Empty, $"Рецепт «{recipeId}» не найден. Доступные: {string.Join(", ", world.Registries.Recipes.All().Select(r => r.Id).Take(20))}.");

        var player = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (player is null)
            return ToolResult.Error(string.Empty, "Игрок не найден.");

        // Check inputs.
        foreach (var (inputId, requiredQty) in recipe.Inputs)
        {
            var have = player.Inventory.Items
                .Where(i => i.TemplateId == inputId)
            #pragma warning disable CA1829
                .Aggregate(0, (sum, i) => sum + i.Quantity);
            #pragma warning restore CA1829
            if (have < requiredQty)
                return ToolResult.Error(string.Empty, $"Недостаточно «{inputId}»: нужно {requiredQty}, есть {have}.");
        }

        // Check required tool.
        if (!string.IsNullOrWhiteSpace(recipe.RequiredTool))
        {
            var hasTool = player.Inventory.Items.Any(i => i.TemplateId == recipe.RequiredTool)
                || player.Equipped.Values.Any(i => i.TemplateId == recipe.RequiredTool);
            if (!hasTool)
                return ToolResult.Error(string.Empty, $"Нужен инструмент: {recipe.RequiredTool}.");
        }

        // Consume inputs.
        foreach (var (inputId, requiredQty) in recipe.Inputs)
        {
            var remaining = requiredQty;
            for (int i = player.Inventory.Items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                var item = player.Inventory.Items[i];
                if (item.TemplateId != inputId) continue;
                if (item.Quantity <= remaining)
                {
                    remaining -= item.Quantity;
                    player.Inventory.Items.RemoveAt(i);
                }
                else
                {
                    item.Quantity -= remaining;
                    remaining = 0;
                }
            }
        }

        // Produce output.
        var outputTpl = world.Registries.Items.Get(recipe.OutputTemplateId);
        if (outputTpl is null)
            return ToolResult.Error(string.Empty, $"Выходной шаблон «{recipe.OutputTemplateId}» не найден.");

        var output = MyGame.Core.World.EntityFactory.InstantiateItem(outputTpl, recipe.OutputQuantity);
        player.Inventory.Items.Add(output);

        return ToolResult.Ok(string.Empty, $"Скрафчен «{outputTpl.Name}» ×{recipe.OutputQuantity} из рецепта «{recipe.Name}».");
    };
}

/// <summary>
/// Search the current location for items on the ground. Returns a list
/// of ground items that the player can pick up. Issue #67 (containers/looting).
/// </summary>
internal static class SearchLocationTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "search_location",
        Description = "Обыскать текущую локацию. Возвращает список предметов на земле, трупы с инвентарём, и возможные контейнеры (сундуки, ящики).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "locationId": { "type": "string", "description": "Локация (ID или имя); пусто = текущая." }
          }
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var idOrName = args.TryGetProperty("locationId", out var el) ? el.GetString() ?? "" : "";
        var loc = GetLocationTool.ResolveLocation(world, idOrName);
        if (loc is null)
            return ToolResult.Error(string.Empty, "Локация не найдена.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Поиск в «{loc.Name}»:");

        // Ground items.
        if (loc.Items.Count > 0)
        {
            sb.AppendLine("Предметы на земле:");
            foreach (var itemId in loc.Items)
            {
                var item = world.GetItem(itemId);
                if (item is not null)
                    sb.AppendLine($"  • {item.Name} ×{item.Quantity} (id: {item.Id})");
            }
        }
        else
            sb.AppendLine("На земле ничего нет.");

        // Dead NPCs (lootable corpses).
        var corpses = loc.Npcs
            .Select(id => world.GetNpc(id))
            .Where(n => n is not null && !n!.IsAlive)
            .ToList();
        if (corpses.Count > 0)
        {
            sb.AppendLine("Трупы (можно обыскать):");
            foreach (var corpse in corpses)
            {
                var invCount = corpse!.Inventory.Items.Count;
                var currency = corpse.Inventory.Currency;
                sb.AppendLine($"  • {corpse.Name} — {invCount} предм., {currency} зол.");
            }
        }

        // Containers (items flagged as containers — use Flags["container"]).
        var containers = loc.Items
            .Select(id => world.GetItem(id))
            .Where(i => i is not null && i.Flags?.TryGetValue("container", out var v) == true && v is true)
            .ToList();
        if (containers.Count > 0)
        {
            sb.AppendLine("Контейнеры:");
            foreach (var c in containers)
                sb.AppendLine($"  • {c!.Name} (id: {c.Id})");
        }

        return ToolResult.Ok(string.Empty, sb.ToString().TrimEnd());
    };
}

/// <summary>
/// Set a market price modifier for an item category at a location. Issue #37.
/// </summary>
internal static class SetMarketPriceTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "set_market_price",
        Description = "Установить модификатор цены для категории предметов в локации. 1.0 = базовая цена, 1.5 = дорого, 0.7 = дёшево. Влияет на торговлю.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "locationId": { "type": "string", "description": "Локация (ID или имя); пусто = текущая." },
            "category": { "type": "string", "description": "Категория: weapon, armor, consumable, tool, misc, и т.д." },
            "multiplier": { "type": "number", "description": "Множитель цены (0.1–5.0)." }
          },
          "required": ["category", "multiplier"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var idOrName = args.TryGetProperty("locationId", out var lEl) ? lEl.GetString() ?? "" : "";
        var loc = GetLocationTool.ResolveLocation(world, idOrName);
        if (loc is null)
            return ToolResult.Error(string.Empty, "Локация не найдена.");

        var category = args.TryGetProperty("category", out var cEl) ? cEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(category))
            return ToolResult.Error(string.Empty, "Параметр category обязателен.");

        if (!args.TryGetProperty("multiplier", out var mEl) || !mEl.TryGetDouble(out var multiplier))
            return ToolResult.Error(string.Empty, "Параметр multiplier обязателен.");

        multiplier = Math.Clamp(multiplier, 0.1, 5.0);
        loc.MarketModifiers ??= new();
        loc.MarketModifiers[category] = multiplier;

        return ToolResult.Ok(string.Empty, $"Цена на «{category}» в «{loc.Name}» установлена: ×{multiplier:F1}.");
    };
}

/// <summary>
/// Get market price modifiers at a location. Issue #37.
/// </summary>
internal static class GetMarketPriceTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "get_market_price",
        Description = "Получить модификаторы цен для локации. Показывает, какие категории товаров дороже/дешевле в этом поселении.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "locationId": { "type": "string", "description": "Локация (ID или имя); пусто = текущая." }
          }
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var idOrName = args.TryGetProperty("locationId", out var el) ? el.GetString() ?? "" : "";
        var loc = GetLocationTool.ResolveLocation(world, idOrName);
        if (loc is null)
            return ToolResult.Error(string.Empty, "Локация не найдена.");

        if (loc.MarketModifiers is null || loc.MarketModifiers.Count == 0)
            return ToolResult.Ok(string.Empty, $"В «{loc.Name}» нет рыночных модификаторов (все цены базовые).");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Цены в «{loc.Name}»:");
        foreach (var kv in loc.MarketModifiers.OrderBy(kv => kv.Key))
        {
            var label = kv.Value switch
            {
                >= 1.5 => "очень дорого",
                >= 1.2 => "дорого",
                <= 0.7 => "очень дёшево",
                <= 0.9 => "дёшево",
                _ => "базовая",
            };
            sb.AppendLine($"  • {kv.Key}: ×{kv.Value:F1} ({label})");
        }
        return ToolResult.Ok(string.Empty, sb.ToString().TrimEnd());
    };
}

// ─── Procedural dungeon generation (issue #38) ────────────────────────

/// <summary>
/// Generate a procedural dungeon (5-15 rooms) as sub-locations connected
/// to the current "dungeon" location. Each room gets a description, exits,
/// possible inhabitants, and possible loot. Uses the world's Rng for
/// determinism. The dungeon layout is persisted (sub-locations become real
/// World.Locations), so re-entry is consistent.
/// </summary>
internal static class GenerateDungeonTool
{
    private static readonly string[] RoomDescriptions = new[]
    {
        "Сырой каменный коридор, покрытый мхом. На стенах — следы когтей.",
        "Круглая комната с обрушенным сводом. Сквозь трещины сочится вода.",
        "Узкий проход, заваленный костями. В воздухе стоит запах гнили.",
        "Просторный зал с колоннами. На полу — высохшие пятна крови.",
        "Тупик. В стене — замурованная дверь с ржавым замком.",
        "Перекрёсток трёх коридоров. На стенах — стрелы-указатели, стёршиеся от времени.",
        "Кладовая с гнилыми бочками. В углу — крысиные гнёзда.",
        "Алтарная комната. На каменном алтаре — засохшая кровь и потухшие свечи.",
        "Естественная пещера, расширенная руками. Сталактиты свисают с потолка.",
        "Тюремная камера. Ржавые решётки, цепи, кости в углу.",
    };

    private static readonly string[] DungeonNpcTemplates = new[]
    {
        "npc_goblin", "npc_skeleton", "npc_zombie", "npc_giant_spider",
    };

    public static ToolDefinition Definition { get; } = new()
    {
        Name = "generate_dungeon",
        Description = "Сгенерировать подземелье (5-15 комнат) как под-локации, связанные с текущей локацией. Используй когда игрок входит в подземелье/пещеру/руины впервые. Каждая комната получает описание, выходы, возможных обитателей и добычу.",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "locationId": { "type": "string", "description": "Локация-вход в подземелье (ID или имя); пусто = текущая." },
            "roomCount": { "type": "integer", "description": "Количество комнат (5-15). По умолчанию случайно." },
            "theme": { "type": "string", "description": "Тема подземелья: catacombs, cave, ruins, mine, temple. По умолчанию по terrain." }
          }
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var idOrName = args.TryGetProperty("locationId", out var lEl) ? lEl.GetString() ?? "" : "";
        var entrance = GetLocationTool.ResolveLocation(world, idOrName);
        if (entrance is null)
            return ToolResult.Error(string.Empty, "Локация не найдена.");

        // Don't re-generate if the dungeon already has sub-locations.
        if (entrance.Flags?.TryGetValue("dungeonGenerated", out var v) == true && v is true)
            return ToolResult.Ok(string.Empty, $"Подземелье «{entrance.Name}» уже сгенерировано.");

        var roomCount = args.TryGetProperty("roomCount", out var rEl) && rEl.TryGetInt32(out var rc)
            ? Math.Clamp(rc, 5, 15)
            : world.Rng.NextInt(5, 16);

        var theme = args.TryGetProperty("theme", out var tEl) ? tEl.GetString() ?? "catacombs" : "catacombs";

        // Generate rooms as a linear graph with some branches.
        var rooms = new List<MyGame.Core.World.Entities.Location>();
        var prevRoom = entrance;

        for (int i = 0; i < roomCount; i++)
        {
            var roomName = $"{entrance.Name}: Комната {i + 1}";
            var desc = RoomDescriptions[world.Rng.NextInt(RoomDescriptions.Length)];
            var room = new MyGame.Core.World.Entities.Location
            {
                Id = EntityId.NewId(),
                Name = roomName,
                Description = desc,
                Terrain = "underground",
                Danger = Math.Min(10, entrance.Danger + world.Rng.NextInt(0, 3)),
                Visited = i == 0, // first room is immediately visible
                Discovered = i == 0,
            };
            room.Flags ??= new();
            room.Flags["dungeonRoom"] = true;
            room.Flags["dungeonTheme"] = theme;

            // Connect to previous room (bidirectional).
            var dirTo = i == 0 ? "вглубь" : $"дальше";
            var dirBack = i == 0 ? $"наружу (к «{entrance.Name}»)" : "назад";
            var targetId = i == 0 ? entrance.Id : rooms[i - 1].Id;

            room.Exits.Add(new MyGame.Core.World.Entities.LocationExit
            {
                To = targetId,
                Direction = dirBack,
            });

            if (i == 0)
            {
                // Connect entrance → first room.
                entrance.Exits.Add(new MyGame.Core.World.Entities.LocationExit
                {
                    To = room.Id,
                    Direction = dirTo,
                });
            }
            else
            {
                // Connect previous room → this room.
                rooms[i - 1].Exits.Add(new MyGame.Core.World.Entities.LocationExit
                {
                    To = room.Id,
                    Direction = dirTo,
                });
            }

            // 40% chance to spawn an NPC in this room.
            if (world.Rng.NextInt(100) < 40)
            {
                var tplId = DungeonNpcTemplates[world.Rng.NextInt(DungeonNpcTemplates.Length)];
                var npc = world.SpawnNpcFromTemplate(tplId, room.Id);
                if (npc is not null && !string.IsNullOrWhiteSpace(npc.Disposition))
                    npc.Disposition = "hostile";
            }

            // 30% chance to place loot on the ground.
            if (world.Rng.NextInt(100) < 30)
            {
                var itemTpl = world.Registries.Items.All()
                    .Where(t => t.Rarity is "common" or "uncommon")
                    .OrderBy(_ => world.Rng.NextInt(1000))
                    .FirstOrDefault();
                if (itemTpl is not null)
                {
                    var item = MyGame.Core.World.EntityFactory.InstantiateItem(itemTpl, world.Rng.NextInt(1, 3));
                    world.SpawnItemOnGround(item, room.Id);
                }
            }

            rooms.Add(room);
            world.AddLocation(room);
        }

        // Mark entrance as generated.
        entrance.Flags ??= new();
        entrance.Flags["dungeonGenerated"] = true;

        return ToolResult.Ok(string.Empty,
            $"Сгенерировано подземелье «{entrance.Name}»: {roomCount} комнат, тема: {theme}. " +
            $"{rooms.Count(r => r.Npcs.Count > 0)} комнат с обитателями, " +
            $"{rooms.Count(r => r.Items.Count > 0)} комнат с добычей.");
    };
}

// ─── Start-scene / world-setup tools (create_location + give_currency) ──────

/// <summary>
/// Create a new location in the world and optionally connect it to an
/// existing location via a bidirectional exit. Used by the StartSceneAgent
/// when the player's role requires a location that doesn't exist in the
/// world (e.g. king → throne room, mage → tower). The GM can also use it
/// mid-game to expand the world.
/// </summary>
internal static class CreateLocationTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "create_location",
        Description = "Создать новую локацию в мире. Опционально соединить с существующей локацией двусторонним выходом. Используй для ролей, которым нужна локация, отсутствующая в мире (тронный зал для короля, башня для мага).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Название локации (напр. «Тронный зал Велмарка»)." },
            "description": { "type": "string", "description": "Описание локации (атмосферный текст)." },
            "terrain": { "type": "string", "description": "Тип местности (castle/tower/forest/city/...). По умолчанию «building»." },
            "danger": { "type": "integer", "description": "Уровень опасности 0-10 (0 = безопасно). По умолчанию 0." },
            "connectTo": { "type": "string", "description": "ID существующей локации для соединения (опционально). Движок создаст двусторонний выход." },
            "direction": { "type": "string", "description": "Направление выхода к новой локации (север/юг/восток/запад/вглубь/...). По умолчанию «вход»." }
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

        var terrain = args.TryGetProperty("terrain", out var tEl) ? tEl.GetString() ?? "building" : "building";
        var danger = args.TryGetProperty("danger", out var dgEl) && dgEl.TryGetInt32(out var d) ? d : 0;
        var connectTo = args.TryGetProperty("connectTo", out var cEl) ? cEl.GetString() ?? "" : "";
        var direction = args.TryGetProperty("direction", out var dirEl) ? dirEl.GetString() ?? "вход" : "вход";

        var loc = new MyGame.Core.World.Entities.Location
        {
            Id = MyGame.Core.Common.EntityId.NewId(),
            Name = name,
            Description = description,
            Terrain = terrain,
            Danger = danger,
            Visited = false,
            Discovered = false,
        };
        world.AddLocation(loc);

        // Optionally connect to an existing location.
        if (!string.IsNullOrEmpty(connectTo))
        {
            if (MyGame.Core.Common.EntityId.TryParse(connectTo, out var connectId))
            {
                var existing = world.GetLocation(connectId);
                if (existing is not null)
                {
                    existing.Exits.Add(new MyGame.Core.World.Entities.LocationExit
                    {
                        To = loc.Id,
                        Direction = direction,
                    });
                    loc.Exits.Add(new MyGame.Core.World.Entities.LocationExit
                    {
                        To = existing.Id,
                        Direction = "назад",
                    });
                    return ToolResult.Ok(string.Empty,
                        $"Создана локация «{name}» (id: {loc.Id}), соединена с «{existing.Name}» в направлении «{direction}».");
                }
            }
            return ToolResult.Ok(string.Empty,
                $"Создана локация «{name}» (id: {loc.Id}). ВНИМАНИЕ: не удалось соединить с локацией «{connectTo}» — она не найдена. Используй move_player для перемещения.");
        }

        return ToolResult.Ok(string.Empty,
            $"Создана локация «{name}» (id: {loc.Id}). Используй move_player с force:true для перемещения игрока туда.");
    };
}

/// <summary>
/// Give the player starting currency (gold). Used by the StartSceneAgent
/// to grant role-appropriate wealth (a king gets more than a beggar).
/// </summary>
internal static class GiveCurrencyTool
{
    public static ToolDefinition Definition { get; } = new()
    {
        Name = "give_currency",
        Description = "Выдать игроку золото. Используй для стартового капитала по роли (король — много, нищий — ноль).",
        ParametersJson = """
        {
          "type": "object",
          "properties": {
            "amount": { "type": "integer", "description": "Количество золота (>= 0)." }
          },
          "required": ["amount"]
        }
        """,
    };

    public static ToolHandler Handle(MyGame.Core.World.World world) => async (args, ct) =>
    {
        var p = world.ActivePlayer ?? world.Players.FirstOrDefault();
        if (p is null)
            return ToolResult.Error(string.Empty, "В мире ещё нет игрока.");

        var amount = args.TryGetProperty("amount", out var aEl) && aEl.TryGetInt32(out var a) ? a : 0;
        if (amount < 0) amount = 0;

        p.Inventory.Currency += amount;
        return ToolResult.Ok(string.Empty,
            $"Выдано {amount} золота (всего: {p.Inventory.Currency}).");
    };
}
