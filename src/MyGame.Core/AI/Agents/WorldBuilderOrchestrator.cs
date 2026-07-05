using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Saves;
using MyGame.Core.World;


namespace MyGame.Core.AI.Agents;

/// <summary>
/// Result of <see cref="WorldBuilderOrchestrator.RunAsync"/>.
/// </summary>
public enum WorldBuilderResultKind
{
    /// <summary>The build completed; the world is playable.</summary>
    Complete,

    /// <summary>The build was cancelled mid-flight.</summary>
    Cancelled,

    /// <summary>The build failed (AI error, timeout, etc.).</summary>
    Failed,
}

/// <summary>
/// Result of <see cref="WorldBuilderOrchestrator.RunAsync"/>.
/// </summary>
public sealed record WorldBuilderResult
{
    public required WorldBuilderResultKind Kind { get; init; }

    /// <summary>Summary text (success message or error).</summary>
    public string? Summary { get; init; }

    /// <summary>Final plan committed by the planner, if any.</summary>
    public WorldPlan? Plan { get; init; }

    /// <summary>Opening narration produced by the narrator stage (if any).</summary>
    public string? OpeningNarration { get; init; }

    /// <summary>Stats from the deterministic committer stage (if reached).</summary>
    public CommitStats? Stats { get; init; }
}

/// <summary>
/// World-Builder Orchestrator. Port of
/// <c>ai/agents/worldBuilderOrchestrator.ts</c>, rewritten for the desktop
/// MVP as a three-phase pipeline:
///
/// <list type="number">
/// <item><b>Planner</b> — one AI call with the <c>world-planner.md</c>
///   prompt produces a <see cref="WorldPlan"/> (JSON in the response
///   content). This is the only creative call that designs the world.</item>
/// <item><b>Committer</b> — <see cref="WorldBuilderCommitter"/> mutates the
///   live <see cref="MyGame.Core.World.World"/> deterministically from the
///   plan: registers custom templates, creates locations + wires exits,
///   spawns NPCs / buildings, grants starter gear, sets the world title.
///   No AI calls — pure data transformation. Fault-tolerant: broken
///   entries are skipped and counted in <see cref="CommitStats.Errors"/>.</item>
/// <item><b>Narrator</b> — one AI call with the <c>world-narrator.md</c>
///   prompt produces the atmospheric opening narration (2 short
///   paragraphs) shown to the player when they enter the game. The
///   narration is also stored as the first <see cref="LogEntry"/> on the
///   world log.</item>
/// </list>
///
/// <para>
/// This is intentionally simpler than the TS source's TODO-driven
/// single-agent loop (which needed <c>add_todo</c> / <c>mark_todo_done</c>
/// / <c>end_worldbuilding</c> tools + ~200 iterations + supervisor pause /
/// resume). The desktop MVP trades flexibility for reliability: three
/// deterministic stages, no resume semantics, no pause, no anti-loop
/// detection. A later task can layer the TODO-driven loop on top if the
/// deterministic committer turns out too rigid.
/// </para>
/// </summary>
public sealed class WorldBuilderOrchestrator
{
    /// <summary>Default iteration cap for the planner call.</summary>
    public const int DefaultPlannerIterations = 4;

    private readonly AiClient _ai;
    private readonly AiSettings? _aiSettings;
    private readonly MyGame.Core.World.World _world;
    private readonly PromptLoader _prompts;
    private readonly ToolRegistry _tools;
    private readonly int _plannerIterations;
    private readonly List<PetDelegation> _petDelegations;

    /// <summary>Create an orchestrator bound to the given AI client, world, prompt loader, and tool registry.</summary>
    /// <param name="ai">Base AI client. When <paramref name="aiSettings"/>
    /// is provided, role-specific clients are derived via
    /// <see cref="AiClient.WithModel"/> for the planner + narrator stages
    /// (issue #26).</param>
    /// <param name="aiSettings">Optional AI settings for per-role model
    /// overrides (<see cref="AiSettings.PlannerModel"/>,
    /// <see cref="AiSettings.NarratorModel"/>, <see cref="AiSettings.PetModel"/>).
    /// When null, the base <paramref name="ai"/> client is used as-is.</param>
    public WorldBuilderOrchestrator(
        AiClient ai,
        MyGame.Core.World.World world,
        PromptLoader prompts,
        ToolRegistry tools,
        int? plannerIterations = null,
        IReadOnlyCollection<PetDelegation>? petDelegations = null,
        AiSettings? aiSettings = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _plannerIterations = Math.Max(1, Math.Min(20, plannerIterations ?? DefaultPlannerIterations));
        _petDelegations = petDelegations?.ToList() ?? new();
        _aiSettings = aiSettings;
    }

    /// <summary>
    /// Run the world-build pipeline. Reports progress via
    /// <paramref name="progress"/>; respects <paramref name="ct"/> for
    /// cancellation. Returns a <see cref="WorldBuilderResult"/> with the
    /// final plan (if planning succeeded) and a summary.
    ///
    /// <para>
    /// Stages 2–7 are TODOs; only stage 1 (planning) currently makes an
    /// AI call. Stages 2–7 publish progress with
    /// <see cref="ProgressStatus.Skipped"/> and return immediately.
    /// </para>
    /// </summary>
    public async Task<WorldBuilderResult> RunAsync(
        WorldPlanRequest request,
        IProgress<WorldBuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // ── Stage 1: planning ─────────────────────────────────────────────
        progress?.Report(new WorldBuildProgress
        {
            Stage = "planning",
            Status = ProgressStatus.Active,
            Label = "Планировщик анализирует бриф",
            Percent = 5,
        });

        WorldPlan? plan = null;
        try
        {
            plan = await RunPlannerAsync(request, progress, ct).ConfigureAwait(false);
            progress?.Report(new WorldBuildProgress
            {
                Stage = "planning",
                Status = ProgressStatus.Done,
                Label = "План мира зафиксирован",
                Detail = $"«{plan.Title}» — {plan.Locations.Count} локаций, {plan.Npcs.Count} NPC",
                Percent = 15,
            });
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new WorldBuildProgress
            {
                Stage = "planning",
                Status = ProgressStatus.Error,
                Label = "Отменено",
                Percent = 0,
            });
            return new WorldBuilderResult
            {
                Kind = WorldBuilderResultKind.Cancelled,
                Summary = "Build cancelled during planning.",
            };
        }
        catch (AiException ex)
        {
            progress?.Report(new WorldBuildProgress
            {
                Stage = "planning",
                Status = ProgressStatus.Error,
                Label = "Ошибка планирования",
                Detail = ex.Message,
                Percent = 0,
            });
            return new WorldBuilderResult
            {
                Kind = WorldBuilderResultKind.Failed,
                Summary = "Planning failed: " + ex.Message,
            };
        }

        // ── Stage 2: ruleset ──────────────────────────────────────────────
        // The desktop MVP uses the default DnD 5e-style ruleset for every
        // world. The TS source had a per-world ruleset design stage (via
        // world-ruleset prompt + commit_ruleset tool); that's deferred. We
        // still publish a progress tick so the UI shows the stage.
        progress?.Report(new WorldBuildProgress
        {
            Stage = "ruleset",
            Status = ProgressStatus.Active,
            Label = "Применение правил мира",
            Percent = 22,
        });
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        progress?.Report(new WorldBuildProgress
        {
            Stage = "ruleset",
            Status = ProgressStatus.Done,
            Label = "Правила установлены (DnD 5e по умолчанию)",
            Percent = 25,
        });

        // ── Stage 3-6: deterministic commit ───────────────────────────────
        // One committer instance runs all four sub-stages (templates,
        // locations, population, buildings, content, title) in order. We
        // publish a progress tick before each sub-stage so the UI shows
        // the build progressing even though the work itself is
        // synchronous and fast (no AI calls).
        var committer = new WorldBuilderCommitter(_world);
        var stats = new CommitStats();

        // 3a: custom templates (registered so later stages can reference
        // them by id).
        progress?.Report(new WorldBuildProgress
        {
            Stage = "templates",
            Status = ProgressStatus.Active,
            Label = "Регистрация кастомных шаблонов",
            Percent = 30,
        });
        committer.CommitCustomTemplates(plan, stats);
        progress?.Report(new WorldBuildProgress
        {
            Stage = "templates",
            Status = ProgressStatus.Done,
            Label = $"Шаблоны: {stats.CustomItems} предм. / {stats.CustomNpcs} NPC / {stats.CustomBuildings} зд.",
            Percent = 33,
        });

        // 3b: locations.
        progress?.Report(new WorldBuildProgress
        {
            Stage = "locations",
            Status = ProgressStatus.Active,
            Label = "Каркас локаций",
            Percent = 38,
        });
        committer.CommitLocations(plan, stats);
        progress?.Report(new WorldBuildProgress
        {
            Stage = "locations",
            Status = ProgressStatus.Done,
            Label = $"Создано локаций: {stats.Locations}",
            Percent = 45,
        });

        // 4: population.
        progress?.Report(new WorldBuildProgress
        {
            Stage = "population",
            Status = ProgressStatus.Active,
            Label = "Заселение мира",
            Percent = 50,
        });
        committer.CommitPopulation(plan, stats);
        progress?.Report(new WorldBuildProgress
        {
            Stage = "population",
            Status = ProgressStatus.Done,
            Label = $"Заселено NPC: {stats.Npcs}",
            Percent = 58,
        });

        // 5: buildings.
        progress?.Report(new WorldBuildProgress
        {
            Stage = "buildings",
            Status = ProgressStatus.Active,
            Label = "Возведение зданий",
            Percent = 62,
        });
        committer.CommitBuildings(plan, stats);
        progress?.Report(new WorldBuildProgress
        {
            Stage = "buildings",
            Status = ProgressStatus.Done,
            Label = $"Построено зданий: {stats.Buildings}",
            Percent = 70,
        });

        // 6: content (starter gear + player).
        progress?.Report(new WorldBuildProgress
        {
            Stage = "content",
            Status = ProgressStatus.Active,
            Label = "Наполнение предметами",
            Percent = 73,
        });
        committer.CommitContent(plan, stats);
        committer.CommitTitle(plan);
        progress?.Report(new WorldBuildProgress
        {
            Stage = "content",
            Status = ProgressStatus.Done,
            Label = $"Стартовых предметов: {stats.StarterItems}",
            Percent = 80,
        });

        // ── Stage 6b: pet-agent delegations (optional) ──────────────────
        // If the caller provided any PetDelegations, run each as a
        // separate PetAgent with its own LLM conversation + tool loop.
        // The pet agent can spawn NPCs, create items, set flags — full
        // tool access. Failures are non-fatal (the world is already
        // playable from the committer stage); we log the error and move
        // on to the next delegation.
        if (_petDelegations.Count > 0)
        {
            var petSummaries = new List<string>();
            int petIndex = 0;
            int petTotal = _petDelegations.Count;
            foreach (var del in _petDelegations)
            {
                petIndex++;
                int petPercent = 80 + (int)((8.0 * petIndex) / petTotal); // 80→88
                progress?.Report(new WorldBuildProgress
                {
                    Stage = "pet",
                    Status = ProgressStatus.Active,
                    Label = $"Pet-агент: {del.Label}",
                    Detail = $"Делегация {petIndex}/{petTotal}",
                    Percent = petPercent,
                });

                try
                {
                    var petConfig = new PetAgentConfig
                    {
                        Id = $"pet_{petIndex}",
                        Name = del.Label,
                        Settings = del.Settings,
                    };
                    // Pass the orchestrator's AI settings so the PetAgent
                    // can derive a PetModel-override client (issue #26).
                    // When _aiSettings is null OR PetModel is unset, the
                    // pet agent falls back to the base _ai client.
                    var pet = new PetAgent(_ai, _world, _tools, petConfig, del.MaxIterations, _aiSettings);
                    var result = await pet.RunAsync(del.Task, ct: ct).ConfigureAwait(false);
                    petSummaries.Add($"{del.Label}: {result.Summary}");
                    progress?.Report(new WorldBuildProgress
                    {
                        Stage = "pet",
                        Status = ProgressStatus.Done,
                        Label = $"Pet: {del.Label} — готово",
                        Detail = result.Success ? result.Summary : $"частично ({result.Error})",
                        Percent = petPercent,
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    stats.Errors.Add($"pet «{del.Label}»: {ex.Message}");
                    progress?.Report(new WorldBuildProgress
                    {
                        Stage = "pet",
                        Status = ProgressStatus.Error,
                        Label = $"Pet: {del.Label} — ошибка",
                        Detail = ex.Message,
                        Percent = petPercent,
                    });
                }
            }

            // Stash the pet summaries on the result so the UI can show
            // what each delegation accomplished.
            if (petSummaries.Count > 0)
            {
                stats.Errors.Insert(0, "Pet-делегации:\n" + string.Join("\n", petSummaries));
            }
        }

        // ── Stage 7: narration ─────────────────────────────────────────────
        // One AI call with the world-narrator prompt. Produces the
        // atmospheric opening narration shown to the player when they
        // enter the game. Failures here are non-fatal — the world is
        // already playable; we just fall back to a plain hook line.
        string? narration = null;
        try
        {
            narration = await RunNarratorAsync(plan, progress, ct).ConfigureAwait(false);
            progress?.Report(new WorldBuildProgress
            {
                Stage = "narration",
                Status = ProgressStatus.Done,
                Label = "Наррация готова",
                Percent = 97,
            });
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new WorldBuildProgress
            {
                Stage = "narration",
                Status = ProgressStatus.Error,
                Label = "Отменено",
                Percent = 80,
            });
            return new WorldBuilderResult
            {
                Kind = WorldBuilderResultKind.Cancelled,
                Summary = "Build cancelled during narration.",
                Plan = plan,
                Stats = stats,
            };
        }
        catch (AiException ex)
        {
            // Non-fatal: the world is playable; just log the error.
            progress?.Report(new WorldBuildProgress
            {
                Stage = "narration",
                Status = ProgressStatus.Error,
                Label = "Наррация не удалась (мир играбелен)",
                Detail = ex.Message,
                Percent = 95,
            });
        }

        progress?.Report(new WorldBuildProgress
        {
            Stage = "done",
            Status = ProgressStatus.Done,
            Label = $"Мир «{plan.Title}» готов",
            Detail = stats.Summary() + (stats.Errors.Count > 0
                ? $" | Первая ошибка: {stats.Errors[0]}"
                : ""),
            Percent = 100,
        });

        return new WorldBuilderResult
        {
            Kind = WorldBuilderResultKind.Complete,
            Summary = $"Мир «{plan.Title}» построен. {stats.Summary()}",
            Plan = plan,
            OpeningNarration = narration,
            Stats = stats,
        };
    }

    /// <summary>
    /// Stage 1: call the planner. Loads the <c>world-planner.md</c> prompt,
    /// fills in <c>{{WORLD_BRIEF}}</c> + <c>{{WORLD_STATE}}</c> +
    /// <c>{{ITEM_TEMPLATES}}</c> + <c>{{NPC_TEMPLATES}}</c> +
    /// <c>{{BUILDING_TEMPLATES}}</c>, and asks the model to produce a
    /// <see cref="WorldPlan"/> as JSON in its response content.
    ///
    /// <para>
    /// The TS source had the planner call a <c>commit_world_plan</c> tool
    /// with the plan as args. Since the desktop MVP doesn't wire that
    /// tool, we parse the plan directly from the model's response content
    /// (asking it to emit a fenced JSON block). This is less robust than
    /// tool-call args but adequate for the three-stage pipeline.
    /// </para>
    /// </summary>
    private async Task<WorldPlan> RunPlannerAsync(
        WorldPlanRequest request,
        IProgress<WorldBuildProgress>? progress,
        CancellationToken ct)
    {
        var systemPrompt = BuildPlannerPrompt(request.Brief);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(
                "Построй мир по следующему брифу. Верни результат как JSON-объект WorldPlan " +
                "в блоке ```json ... ```. Без другого текста.\n\n" +
                $"БРИФ:\n{request.Brief}"),
        };

        // Derive a role-specific client for the planner (issue #26).
        // WithModel returns the same instance when no override is set.
        var plannerAi = _aiSettings is null ? _ai : _ai.WithModel(_aiSettings.GetModelForRole(AiRole.Planner));
        var response = await plannerAi.ChatAsync(messages, ct).ConfigureAwait(false);
        var planJson = ExtractJsonBlock(response.Content ?? string.Empty);
        if (string.IsNullOrWhiteSpace(planJson))
            throw new AiException(AiErrorKind.Parse,
                "Planner did not return a JSON block. Response was: " +
                (response.Content?.Length > 500 ? response.Content[..500] : response.Content));

        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            var plan = System.Text.Json.JsonSerializer.Deserialize<WorldPlan>(planJson, opts)
                ?? throw new AiException(AiErrorKind.Parse, "Planner JSON deserialized to null.");
            return plan;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new AiException(AiErrorKind.Parse,
                "Planner JSON was malformed: " + ex.Message, inner: ex);
        }
    }

    private string BuildPlannerPrompt(string brief)
    {
        var template = _prompts.Get("world-planner");
        var stateBlock = BuildMinimalWorldState();
        var vars = new Dictionary<string, string>
        {
            ["WORLD_BRIEF"] = string.IsNullOrWhiteSpace(brief)
                ? "(бриф пуст — создай мир по своему усмотрению, тёмное фэнтези)"
                : brief.Trim(),
            ["WORLD_STATE"] = stateBlock,
            ["ITEM_TEMPLATES"] = string.Join(", ", _world.Registries.Items.All().Select(t => t.Id).OrderBy(s => s)),
            ["NPC_TEMPLATES"] = string.Join(", ", _world.Registries.Npcs.All().Select(t => t.Id).OrderBy(s => s)),
            ["BUILDING_TEMPLATES"] = string.Join(", ", _world.Registries.Buildings.All().Select(t => t.Id).OrderBy(s => s)),
        };
        var prompt = PromptLoader.Substitute(template, vars);
        // Append the tools guide so the planner knows the available tool
        // signatures — even though the desktop MVP's committer doesn't let
        // the planner call them directly, the planner prompt expects this
        // context to inform its plan structure.
        var guide = _prompts.Get("tools-guide");
        return prompt + "\n\n---\n\n" + guide;
    }

    private string BuildMinimalWorldState()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# ТЕКУЩЕЕ СОСТОЯНИЕ МИРА (стартовое — возможно пустое)");
        sb.AppendLine($"Часы мира: {_world.Clock}");
        sb.AppendLine($"Локаций: {_world.Locations.Count} | NPC: {_world.Npcs.Count} | Зданий: {_world.Buildings.Count}");
        return sb.ToString();
    }

    /// <summary>
    /// Extract the first fenced JSON block (```json ... ```) from the
    /// model's response. Falls back to the whole response if no fence is
    /// found. Returns null if the response is empty.
    /// </summary>
    private static string? ExtractJsonBlock(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var start = content.IndexOf("```", StringComparison.Ordinal);
        if (start < 0) return content.Trim();
        // Skip the opening fence + optional language tag.
        var afterFence = content.IndexOf('\n', start);
        if (afterFence < 0) return content.Trim();
        var bodyStart = afterFence + 1;
        var end = content.IndexOf("```", bodyStart, StringComparison.Ordinal);
        if (end < 0) return content[(bodyStart)..].Trim();
        return content[bodyStart..end].Trim();
    }

    /// <summary>
    /// Stage 7: call the narrator. Loads the <c>world-narrator.md</c>
    /// prompt, fills in <c>{{WORLD_PLAN}}</c> (a readable summary of the
    /// committed plan) + <c>{{WORLD_STATE}}</c> (a snapshot of the live
    /// world after the committer ran — real location descriptions, real
    /// NPC names) + the three template-list vars. Asks the model to write
    /// the 2-paragraph atmospheric opening narration.
    ///
    /// <para>
    /// The TS narrator called <c>end_worldbuilding</c> as a final tool.
    /// The desktop MVP has no such tool (no TODO-driven loop) — we just
    /// ask the model for plain text and use it verbatim as the opening
    /// narration. If the model wraps the text in prose, we strip any
    /// leading "Вступление:"-style labels.
    /// </para>
    /// </summary>
    private async Task<string> RunNarratorAsync(
        WorldPlan plan,
        IProgress<WorldBuildProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new WorldBuildProgress
        {
            Stage = "narration",
            Status = ProgressStatus.Active,
            Label = "Нарратор пишет вступление",
            Percent = 85,
        });

        var systemPrompt = BuildNarratorPrompt(plan);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(
                "Напиши атмосферное вступление для игрока. 2 коротких абзаца на русском. " +
                "Без заголовков, без markdown, без рассуждений о процессе — только готовый текст. " +
                "Стиль под тему мира. Не озвучивай механику."),
        };

        // Derive a role-specific client for the narrator (issue #26).
        var narratorAi = _aiSettings is null ? _ai : _ai.WithModel(_aiSettings.GetModelForRole(AiRole.Narrator));
        var response = await narratorAi.ChatAsync(messages, ct).ConfigureAwait(false);
        var text = (response.Content ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new AiException(AiErrorKind.Parse, "Narrator returned empty content.");

        // Strip a leading label the model sometimes adds ("Вступление:",
        // "Наррация:", etc.). Keep the rest verbatim.
        var colonIdx = text.IndexOf('\n');
        if (colonIdx > 0 && colonIdx < 40)
        {
            var firstLine = text[..colonIdx].TrimEnd(':');
            if (firstLine.Length < 30 &&
                (firstLine.Contains("Вступление", StringComparison.OrdinalIgnoreCase) ||
                 firstLine.Contains("Наррац", StringComparison.OrdinalIgnoreCase) ||
                 firstLine.Contains("Начало", StringComparison.OrdinalIgnoreCase)))
            {
                text = text[(colonIdx + 1)..].TrimStart();
            }
        }

        return text;
    }

    private string BuildNarratorPrompt(WorldPlan plan)
    {
        var template = _prompts.Get("world-narrator");
        var planBlock = BuildReadablePlanSummary(plan);
        var stateBlock = BuildLiveWorldState();
        var vars = new Dictionary<string, string>
        {
            ["WORLD_PLAN"] = planBlock,
            ["WORLD_STATE"] = stateBlock,
            ["ITEM_TEMPLATES"] = string.Join(", ", _world.Registries.Items.All().Select(t => t.Id).OrderBy(s => s).Take(40)),
            ["NPC_TEMPLATES"] = string.Join(", ", _world.Registries.Npcs.All().Select(t => t.Id).OrderBy(s => s).Take(40)),
            ["BUILDING_TEMPLATES"] = string.Join(", ", _world.Registries.Buildings.All().Select(t => t.Id).OrderBy(s => s).Take(40)),
        };
        return PromptLoader.Substitute(template, vars);
    }

    /// <summary>
    /// Render the plan as a readable text block for the narrator prompt.
    /// The narrator needs theme / atmosphere / setting / startingHook +
    /// the planned start-location name + the NPCs at that location.
    /// </summary>
    private static string BuildReadablePlanSummary(WorldPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# План мира: {plan.Title}");
        sb.AppendLine($"Тема: {plan.Theme}");
        sb.AppendLine($"Сеттинг: {plan.Setting}");
        sb.AppendLine($"Атмосфера: {plan.Atmosphere}");
        sb.AppendLine($"Сюжетный крючок: {plan.StartingHook}");
        sb.AppendLine();
        sb.AppendLine("## Локации (план):");
        foreach (var loc in plan.Locations ?? new())
        {
            var marker = loc.Role == LocationRole.Start ? " [СТАРТ]" : "";
            sb.AppendLine($"- {loc.Name}{marker} ({loc.Terrain}, опасность {loc.Danger}): {loc.Description}");
        }
        sb.AppendLine();
        sb.AppendLine("## NPC (план):");
        foreach (var n in plan.Npcs ?? new())
        {
            sb.AppendLine($"- {n.Name} [{n.Template}] @ {n.Location} — {n.Notes}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render the LIVE world state (after the committer ran) for the
    /// narrator. The narrator must use REAL world descriptions, not the
    /// plan's, so the opening narration matches what the player actually
    /// sees. Focuses on the start location + its inhabitants + exits.
    /// </summary>
    private string BuildLiveWorldState()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ТЕКУЩЕЕ СОСТОЯНИЕ МИРА (после строительства)");
        sb.AppendLine($"Время: {_world.Clock}");
        sb.AppendLine($"Всего: {_world.Locations.Count} лок., {_world.Npcs.Count} NPC, {_world.Buildings.Count} зд.");
        sb.AppendLine();

        var startLoc = _world.Locations.FirstOrDefault(l => l.Visited)
            ?? _world.Locations.FirstOrDefault();
        if (startLoc is null)
        {
            sb.AppendLine("(нет локаций)");
            return sb.ToString();
        }

        sb.AppendLine($"## Текущая локация: {startLoc.Name}");
        sb.AppendLine($"Описание: {startLoc.Description ?? "—"}");
        sb.AppendLine($"Местность: {startLoc.Terrain} | Опасность: {startLoc.Danger}/10");

        var exits = startLoc.Exits.Count > 0
            ? string.Join(", ", startLoc.Exits.Select(e =>
            {
                var toName = _world.GetLocation(e.To)?.Name ?? e.To.ToString();
                return $"{e.Direction} → {toName}";
            }))
            : "нет";
        sb.AppendLine($"Выходы: {exits}");

        var npcs = startLoc.Npcs.Count > 0
            ? string.Join(", ", startLoc.Npcs.Select(id => _world.GetNpc(id)).Where(n => n is not null).Select(n => $"{n!.Name} ({n!.Race ?? "?"}/{n!.Class ?? "?"})"))
            : "нет";
        sb.AppendLine($"Обитатели: {npcs}");

        var buildings = startLoc.Buildings.Count > 0
            ? string.Join(", ", startLoc.Buildings.Select(id => _world.GetBuilding(id)).Where(b => b is not null).Select(b => b!.Name))
            : "нет";
        sb.AppendLine($"Здания: {buildings}");
        return sb.ToString();
    }
}
