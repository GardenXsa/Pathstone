using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Common;
using MyGame.Core.Saves;
using MyGame.Core.World;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). Alias to disambiguate the type reference.
using GameWorld = MyGame.Core.World.World;

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
/// Snapshot of the orchestrator's progress through the world-build
/// pipeline, used by the pause/resume feature (issue #19).
///
/// <para>
/// The orchestrator publishes a fresh <see cref="WorldBuilderState"/>
/// after each stage / sub-stage via <see cref="WorldBuilderOrchestrator.SaveState"/>,
/// and a caller can restore one via <see cref="WorldBuilderOrchestrator.LoadState"/>
/// so a resumed run continues from where the previous run left off
/// (rather than starting over from the planning stage).
/// </para>
///
/// <para>
/// The fields capture:
/// <list type="bullet">
///   <item><see cref="Stage"/> — a stable string identifier for the
///     current point in the pipeline (<c>"planning"</c>,
///     <c>"planning_done"</c>, <c>"ruleset"</c>, <c>"committer"</c>,
///     <c>"committer_done"</c>, <c>"pets"</c>, <c>"pets_done"</c>,
///     <c>"narration"</c>, <c>"done"</c>). On <see cref="WorldBuilderOrchestrator.LoadState"/>,
///     the orchestrator skips any stage whose <c>*_done</c> marker
///     precedes or matches <see cref="Stage"/>.</item>
///   <item><see cref="Plan"/> — the committed <see cref="WorldPlan"/>
///     from the planner stage. Null before planning has run; non-null
///     afterwards so a resumed run can skip the planner AI call.</item>
///   <item><see cref="Messages"/> — the orchestrator builds messages
///     fresh each run (no per-stage conversation history accumulates
///     between planner + narrator), so this list is kept for API
///     symmetry with the spec but is typically empty. Pet-agent
///     conversations live on their own <c>PetAgent</c> instances and
///     are not serialised here.</item>
///   <item><see cref="Iteration"/> — the pet-delegation index (0-based)
///     of the next delegation to run. Resumed runs skip already-completed
///     delegations 0..<see cref="Iteration"/>-1.</item>
/// </list>
/// </para>
/// </summary>
public sealed record WorldBuilderState
{
    /// <summary>
    /// Stable identifier for the orchestrator's current point in the
    /// pipeline. See the class-level doc for the enumeration of values.
    /// </summary>
    public required string Stage { get; init; } = "planning";

    /// <summary>
    /// The committed <see cref="WorldPlan"/> (null before planning has
    /// run). Restored via <see cref="WorldBuilderOrchestrator.LoadState"/>
    /// so a resumed run can skip the planner AI call.
    /// </summary>
    public WorldPlan? Plan { get; init; }

    /// <summary>
    /// Conversation history (kept for API symmetry with the spec; the
    /// orchestrator builds messages fresh each run, so this is typically
    /// empty). Pet-agent conversations are not serialised here.
    /// </summary>
    public List<ChatMessage> Messages { get; init; } = new();

    /// <summary>
    /// 0-based index of the next pet-delegation to run. Resumed runs
    /// skip already-completed delegations 0..<c>Iteration-1</c>.
    /// </summary>
    public int Iteration { get; init; }
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

    /// <summary>
    /// True when the run paused mid-flight and returned without
    /// completing the pipeline. Set by <see cref="WorldBuilderOrchestrator.Pause"/>
    /// + the orchestrator's pause-checkpoints; the host should treat
    /// this as "the build is paused, not failed" and offer a resume.
    /// </summary>
    public bool Paused { get; init; }
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
/// <b>Pause / resume (issue #19):</b> the orchestrator exposes
/// <see cref="Pause"/> / <see cref="Resume"/> and checks
/// <see cref="_paused"/> between every stage + sub-stage (the polling
/// loop yields the thread every 100ms so the UI stays responsive). The
/// host can snapshot progress via <see cref="SaveState"/> and restore it
/// via <see cref="LoadState"/> so a resumed run skips already-completed
/// stages (planning → committer → pets → narration).
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

    // ─── Pause/resume state (issue #19) ──────────────────────────────
    //
    // _paused is volatile so writes from the UI thread (Pause() /
    // Resume()) are visible to the background task running RunAsync.
    // The pause-checkpoints in RunAsync poll this flag in a 100ms loop
    // (WaitIfPausedAsync) that also checks the CancellationToken so a
    // cancel during pause propagates immediately.
    //
    // _currentStage / _currentPlan / _currentIteration are mutated by
    // RunAsync as it crosses stage boundaries; SaveState() snapshots
    // them into a WorldBuilderState record. LoadState() restores them
    // AND sets _loadedStage so RunAsync knows which stages to skip on
    // the next run.
    private volatile bool _paused;
    private string _currentStage = "planning";
    private WorldPlan? _currentPlan;
    private int _currentIteration;
    private string? _loadedStage; // when non-null, skip stages ≤ this

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

    // ─── Pause / resume API (issue #19) ──────────────────────────────

    /// <summary>
    /// True while the orchestrator is paused (the background task is
    /// blocked in <see cref="WaitIfPausedAsync"/>, polling every 100ms
    /// for either a resume or a cancel). Volatile so the read in the
    /// polling loop observes writes from the UI thread.
    /// </summary>
    public bool Paused => _paused;

    /// <summary>
    /// Pause the orchestrator at the next pause-checkpoint. The
    /// currently-running AI call (if any) continues to completion —
    /// pause takes effect BETWEEN stages, not mid-API-call. Safe to
    /// call from the UI thread while <see cref="RunAsync"/> runs on a
    /// background task.
    /// </summary>
    public void Pause() => _paused = true;

    /// <summary>
    /// Resume the orchestrator from a paused state. The polling loop in
    /// <see cref="WaitIfPausedAsync"/> exits within ~100ms and the next
    /// stage begins. No-op if not paused.
    /// </summary>
    public void Resume() => _paused = false;

    /// <summary>
    /// Snapshot the orchestrator's current progress into a
    /// <see cref="WorldBuilderState"/>. Safe to call from any thread;
    /// captures the current <c>_currentStage</c> / <c>_currentPlan</c>
    /// / <c>_currentIteration</c>. The returned record is safe to JSON-
    /// serialise + reload in a later process via
    /// <see cref="LoadState"/>.
    /// </summary>
    public WorldBuilderState SaveState() => new()
    {
        Stage = _currentStage,
        Plan = _currentPlan,
        Messages = new List<ChatMessage>(),
        Iteration = _currentIteration,
    };

    /// <summary>
    /// Restore orchestrator progress from a previously-saved
    /// <see cref="WorldBuilderState"/>. The next <see cref="RunAsync"/>
    /// call skips any stage whose <c>*_done</c> marker precedes or
    /// matches <see cref="WorldBuilderState.Stage"/>; the planner AI
    /// call is skipped when <see cref="WorldBuilderState.Plan"/> is
    /// non-null; the first <see cref="WorldBuilderState.Iteration"/>
    /// pet-delegations are skipped.
    /// </summary>
    /// <param name="state">Saved state. Must not be null.</param>
    public void LoadState(WorldBuilderState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        _currentStage = state.Stage;
        _currentPlan = state.Plan;
        _currentIteration = state.Iteration;
        _loadedStage = state.Stage;
    }

    /// <summary>
    /// Block the calling background task while <see cref="_paused"/> is
    /// true. Polls every 100ms so a resume takes effect quickly. The
    /// cancellation token is checked on every iteration so a cancel
    /// during pause propagates immediately (rather than after the next
    /// stage starts).
    /// </summary>
    private async Task WaitIfPausedAsync(CancellationToken ct)
    {
        while (_paused)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            // Task.Delay can throw OperationCanceledException when the
            // token fires during the delay — propagate. Any other
            // exception is unexpected; let it bubble.
        }
    }

    /// <summary>
    /// Determine whether a stage has already been completed (according
    /// to the loaded-state marker) and should be skipped on a resumed
    /// run. The ordering is:
    /// <code>
    /// planning &lt; planning_done &lt; ruleset &lt; committer &lt;
    /// committer_done &lt; pets &lt; pets_done &lt; narration &lt; done
    /// </code>
    /// A null loaded-stage means "fresh run" → nothing is skipped.
    /// </summary>
    private bool IsStageAlreadyDone(string stageMarker, string loadedStage)
    {
        if (string.IsNullOrEmpty(loadedStage)) return false;
        return StageOrder(stageMarker) <= StageOrder(loadedStage);
    }

    /// <summary>
    /// Map a stage name to its position in the pipeline order. Unknown
    /// stages get int.MaxValue so they're never considered "already
    /// done" relative to a known marker (defensive — keeps a corrupt
    /// state file from skipping the entire pipeline).
    /// </summary>
    private static int StageOrder(string? stage) => stage switch
    {
        "planning"        => 1,
        "planning_done"   => 2,
        "ruleset"         => 3,
        "committer"       => 4,
        "committer_done"  => 5,
        "pets"            => 6,
        "pets_done"       => 7,
        "narration"       => 8,
        "done"            => 9,
        _                 => int.MaxValue,
    };

    /// <summary>
    /// Run the world-build pipeline. Reports progress via
    /// <paramref name="progress"/>; respects <paramref name="ct"/> for
    /// cancellation. Returns a <see cref="WorldBuilderResult"/> with the
    /// final plan (if planning succeeded) and a summary.
    ///
    /// <para>
    /// <b>Pause / resume (issue #19):</b> between every stage + sub-stage
    /// the orchestrator calls <see cref="WaitIfPausedAsync"/> which blocks
    /// while <see cref="Paused"/> is true. A loaded state (set via
    /// <see cref="LoadState"/>) causes already-completed stages to be
    /// skipped so a resumed run continues from where the previous run
    /// left off. The pet-delegation loop also skips the first
    /// <see cref="WorldBuilderState.Iteration"/> delegations.
    /// </para>
    /// </summary>
    public async Task<WorldBuilderResult> RunAsync(
        WorldPlanRequest request,
        IProgress<WorldBuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // The loaded stage marker (null for a fresh run). Used by
        // IsStageAlreadyDone to skip stages that were already completed
        // in a previous run.
        var loadedStage = _loadedStage;

        // ── Stage 1: planning ─────────────────────────────────────────────
        WorldPlan? plan = _currentPlan;
        if (!IsStageAlreadyDone("planning_done", loadedStage ?? ""))
        {
            _currentStage = "planning";
            progress?.Report(new WorldBuildProgress
            {
                Stage = "planning",
                Status = ProgressStatus.Active,
                Label = "Планировщик анализирует бриф",
                Percent = 5,
            });

            try
            {
                plan = await RunPlannerAsync(request, progress, ct).ConfigureAwait(false);
                _currentPlan = plan;
                _currentStage = "planning_done";
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
        }
        else
        {
            // Resumed run: planning was already done — reuse the saved
            // plan + publish a Done tick so the UI shows the right state.
            plan = _currentPlan ?? throw new InvalidOperationException(
                "Loaded state marked planning_done but no plan was restored.");
            progress?.Report(new WorldBuildProgress
            {
                Stage = "planning",
                Status = ProgressStatus.Done,
                Label = "План мира восстановлен",
                Detail = $"«{plan.Title}» — {plan.Locations.Count} локаций, {plan.Npcs.Count} NPC",
                Percent = 15,
            });
        }

        // Pause-checkpoint after planning.
        await WaitIfPausedAsync(ct).ConfigureAwait(false);

        // ── Stage 2: ruleset ──────────────────────────────────────────────
        // Issue #21: design a custom ruleset overlay (attribute display
        // names + resource pool names + skill list) for non-fantasy
        // themes. For standard fantasy (the default) we keep DefaultDnd.
        // The AI call is opt-in: it only fires when the plan's theme
        // contains one of the non-fantasy genre keywords. Failures here
        // are non-fatal — the world is still playable with the default
        // ruleset; we just log the error.
        _currentStage = "ruleset";
        progress?.Report(new WorldBuildProgress
        {
            Stage = "ruleset",
            Status = ProgressStatus.Active,
            Label = "Применение правил мира",
            Percent = 22,
        });
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (IsNonFantasyTheme(plan.Theme))
        {
            try
            {
                var overlay = await RunRulesetDesignerAsync(plan, ct).ConfigureAwait(false);
                if (overlay is not null)
                {
                    _world.Ruleset = _world.Ruleset with
                    {
                        AttributeNames = overlay.AttributeNames,
                        ResourcePools = overlay.ResourcePools,
                        Skills = overlay.Skills,
                    };
                    progress?.Report(new WorldBuildProgress
                    {
                        Stage = "ruleset",
                        Status = ProgressStatus.Done,
                        Label = "Правила мира: кастомная система",
                        Detail = DescribeRulesetOverlay(overlay),
                        Percent = 25,
                    });
                }
                else
                {
                    progress?.Report(new WorldBuildProgress
                    {
                        Stage = "ruleset",
                        Status = ProgressStatus.Done,
                        Label = "Правила установлены (DnD 5e по умолчанию)",
                        Detail = "AI вернул пустой ruleset — использованы стандартные правила.",
                        Percent = 25,
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (AiException ex)
            {
                // Non-fatal: fall back to DefaultDnd.
                progress?.Report(new WorldBuildProgress
                {
                    Stage = "ruleset",
                    Status = ProgressStatus.Error,
                    Label = "Не удалось спроектировать ruleset — использованы стандартные правила",
                    Detail = ex.Message,
                    Percent = 25,
                });
            }
        }
        else
        {
            progress?.Report(new WorldBuildProgress
            {
                Stage = "ruleset",
                Status = ProgressStatus.Done,
                Label = "Правила установлены (DnD 5e по умолчанию)",
                Percent = 25,
            });
        }

        // Pause-checkpoint after ruleset.
        await WaitIfPausedAsync(ct).ConfigureAwait(false);

        // ── Stage 3-6: deterministic commit ───────────────────────────────
        // One committer instance runs all four sub-stages (templates,
        // locations, population, buildings, content, title) in order. We
        // publish a progress tick before each sub-stage so the UI shows
        // the build progressing even though the work itself is
        // synchronous and fast (no AI calls).
        //
        // PAUSE/RESUME: the committer mutates the live World in place
        // and is idempotent (re-commit matches by name, no duplicates).
        // On a resumed run we always re-run the committer against the
        // (possibly fresh) World passed in by the host — this is cheap
        // (no AI calls) and ensures the live World reflects the plan
        // even if the host spun up a new World instance for the resume.
        _currentStage = "committer";
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

        _currentStage = "committer_done";

        // Pause-checkpoint after the committer (before pets).
        await WaitIfPausedAsync(ct).ConfigureAwait(false);

        // ── Stage 6b: pet-agent delegations (optional) ──────────────────
        // If the caller provided any PetDelegations, run each as a
        // separate PetAgent with its own LLM conversation + tool loop.
        // The pet agent can spawn NPCs, create items, set flags — full
        // tool access. Failures are non-fatal (the world is already
        // playable from the committer stage); we log the error and move
        // on to the next delegation.
        //
        // PAUSE/RESUME: _currentIteration holds the index of the next
        // delegation to run (0 for a fresh run; restored to a non-zero
        // value by LoadState on a resume). Delegations 0..Iteration-1
        // are skipped.
        if (_petDelegations.Count > 0)
        {
            _currentStage = "pets";
            var petSummaries = new List<string>();
            int petTotal = _petDelegations.Count;
            // Skip already-completed delegations on a resumed run.
            int petIndex = _currentIteration;
            for (; petIndex < _petDelegations.Count; petIndex++)
            {
                var del = _petDelegations[petIndex];
                // Pause-checkpoint BEFORE each pet delegation. The
                // current iteration marker is updated before the call so
                // SaveState() during pause picks the right "next to run"
                // index.
                _currentIteration = petIndex;
                await WaitIfPausedAsync(ct).ConfigureAwait(false);

                int petPercent = 80 + (int)((8.0 * (petIndex + 1)) / petTotal); // 80→88
                progress?.Report(new WorldBuildProgress
                {
                    Stage = "pet",
                    Status = ProgressStatus.Active,
                    Label = $"Pet-агент: {del.Label}",
                    Detail = $"Делегация {petIndex + 1}/{petTotal}",
                    Percent = petPercent,
                });

                try
                {
                    var petConfig = new PetAgentConfig
                    {
                        Id = $"pet_{petIndex + 1}",
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

            // All delegations completed (or skipped on resume) — advance
            // the iteration counter past the end so a subsequent
            // SaveState doesn't try to re-run them.
            _currentIteration = petIndex;
            _currentStage = "pets_done";

            // Stash the pet summaries on the result so the UI can show
            // what each delegation accomplished.
            if (petSummaries.Count > 0)
            {
                stats.Errors.Insert(0, "Pet-делегации:\n" + string.Join("\n", petSummaries));
            }
        }

        // Pause-checkpoint after pets (before narration).
        await WaitIfPausedAsync(ct).ConfigureAwait(false);

        // ── Stage 7: narration ─────────────────────────────────────────────
        // One AI call with the world-narrator prompt. Produces the
        // atmospheric opening narration shown to the player when they
        // enter the game. Failures here are non-fatal — the world is
        // already playable; we just fall back to a plain hook line.
        _currentStage = "narration";
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

        _currentStage = "done";
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

        // Clear the loaded-stage marker so a subsequent RunAsync (without
        // a fresh LoadState) starts over from planning.
        _loadedStage = null;

        return new WorldBuilderResult
        {
            Kind = WorldBuilderResultKind.Complete,
            Summary = $"Мир «{plan.Title}» построен. {stats.Summary()}",
            Plan = plan,
            OpeningNarration = narration,
            Stats = stats,
        };
    }

    // ─── Chunked generation: on-demand region fill (issue #20) ──────────

    /// <summary>
    /// Generate a cold region on-demand. Used by the travel handler when
    /// the player crosses into a region that wasn't detailed by the
    /// initial planner pass (chunked mode). Reuses the original
    /// <see cref="WorldPlan"/> (stashed on the save's
    /// <see cref="SaveMeta.OriginalPlanJson"/>) so the new region matches
    /// the world's established lore; asks the AI to detail the region's
    /// locations, NPCs, and buildings; commits via
    /// <see cref="WorldBuilderCommitter"/> (idempotent — won't duplicate
    /// existing locations); marks the region GenStatus="ready" in
    /// <see cref="World.Flags"/>.
    /// </summary>
    /// <param name="regionName">Name of the cold region to generate
    /// (must match a <see cref="PlanRegion.Name"/> in the original plan).</param>
    /// <param name="originalPlan">The original <see cref="WorldPlan"/>
    /// from the initial build (reloaded from
    /// <see cref="SaveMeta.OriginalPlanJson"/> by the caller). Null
    /// triggers a graceful no-op return — the travel handler logs the
    /// skip.</param>
    /// <param name="progress">Optional progress callback (the travel UI
    /// shows a "Генерация региона…" overlay while this runs).</param>
    /// <param name="ct">Cancellation token (travel overlay Cancel button).</param>
    /// <returns>A <see cref="RegionGenerationResult"/> with the count of
    /// new locations / NPCs / buildings committed and any errors. The
    /// <see cref="RegionGenerationResult.Success"/> flag is false on AI
    /// failure or when the region name doesn't match a cold region in
    /// the plan.</returns>
    public async Task<RegionGenerationResult> GenerateRegionAsync(
        string regionName,
        WorldPlan? originalPlan,
        IProgress<WorldBuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(regionName))
            return new RegionGenerationResult { Success = false, Error = "empty region name" };
        if (originalPlan is null)
            return new RegionGenerationResult { Success = false, Error = "no original plan available" };

        // Find the region in the original plan. If it's already marked
        // "ready", this is a no-op (idempotent — the travel handler may
        // double-call after a race).
        var region = (originalPlan.Regions ?? new())
            .FirstOrDefault(r => string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));
        if (region is null)
            return new RegionGenerationResult { Success = false, Error = $"region «{regionName}» not found in plan" };
        if (string.Equals(region.GenStatus, "ready", StringComparison.OrdinalIgnoreCase))
            return new RegionGenerationResult { Success = true, AlreadyReady = true, Summary = "region already ready" };

        // Check World.Flags for an existing "ready" marker — the region
        // may have been generated by a previous call in this session.
        if (_world.Flags is not null
            && _world.Flags.TryGetValue("readyRegions", out var rr)
            && rr is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var existingReady = je.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (existingReady.Any(n => string.Equals(n, regionName, StringComparison.OrdinalIgnoreCase)))
                return new RegionGenerationResult { Success = true, AlreadyReady = true, Summary = "region already ready" };
        }

        progress?.Report(new WorldBuildProgress
        {
            Stage = "region",
            Status = ProgressStatus.Active,
            Label = $"Генерация региона «{regionName}»",
            Detail = "Запрос к планировщику…",
            Percent = 30,
        });

        // Build the focused planner prompt. We give the AI:
        //   - the world title + theme + setting + atmosphere
        //   - the cold region's high-level description from the plan
        //   - the names of existing locations on the boundary (so the
        //     AI can connect new locations to them)
        var worldTitle = originalPlan.Title;
        var theme = originalPlan.Theme;
        var boundaryLocs = _world.Locations
            .Where(l => l.Discovered)
            .Select(l => l.Name)
            .Take(20)
            .ToList();
        var boundaryBlock = boundaryLocs.Count > 0
            ? string.Join(", ", boundaryLocs)
            : "(нет видимых пограничных локаций — соедини с любой существующей)";

        var systemPrompt =
            "Ты — Продолжатель Мира. Игрок подошёл к границе нового региона в уже существующем мире. " +
            "Сгенерируй локации, NPC и здания для этого региона и соедини их с пограничными локациями. " +
            "Работай на русском. Верни ТОЛЬКО JSON-объект PartialWorldPlan в блоке ```json ... ```. " +
            "Без другого текста.\n\n" +
            "Формат JSON:\n" +
            "```json\n" +
            "{\n" +
            "  \"locations\": [{\"name\":\"...\",\"terrain\":\"...\",\"danger\":N,\"role\":\"settlement\", " +
            "\"description\":\"...\",\"connections\":[\"<имя существующей пограничной локации>\"], " +
            "\"directionFromHub\":\"<направление>\",\"region\":\"<имя региона>\"}],\n" +
            "  \"npcs\": [{\"name\":\"...\",\"template\":\"<customNpcTemplate.Id или стандартный>\"," +
            "\"location\":\"<имя локации из locations>\",\"role\":\"...\",\"disposition\":\"...\"," +
            "\"behavior\":\"...\",\"level\":N,\"notes\":\"...\"}],\n" +
            "  \"buildings\": [{\"template\":\"<customBuildingTemplate.Id или стандартный>\"," +
            "\"location\":\"<имя локации>\",\"nameOverride\":\"...\"}],\n" +
            "  \"customNpcTemplates\": [...],\n" +
            "  \"customItemTemplates\": [...],\n" +
            "  \"customBuildingTemplates\": [...]\n" +
            "}\n" +
            "```\n" +
            "Все новые локации должны иметь field region равным имени генерируемого региона. " +
            "В connections указывай ИМЕНА существующих локаций (из списка границы) — иначе выход не подключится.";

        var userPrompt =
            $"Сгенерируй локации, NPC, здания для региона «{regionName}» мира «{worldTitle}».\n" +
            $"Тема мира: {theme}. Сеттинг: {originalPlan.Setting}. Атмосфера: {originalPlan.Atmosphere}.\n" +
            $"Описание региона из плана: тип={region.Type}, климат={region.Climate}, " +
            $"население={region.Population}, экономика={region.Economy}, политика={region.Politics}, " +
            $"культура={region.Culture}.\n" +
            $"Существующие локации на границе: {boundaryBlock}.\n" +
            $"Соедини новые локации с границей (минимум один выход к существующей локации).\n" +
            "Верни JSON-объект PartialWorldPlan в блоке ```json ... ```.";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userPrompt),
        };

        var plannerAi = _aiSettings is null ? _ai : _ai.WithModel(_aiSettings.GetModelForRole(AiRole.Planner));
        ChatResponse response;
        try
        {
            response = await plannerAi.ChatAsync(messages, ct).ConfigureAwait(false);
        }
        catch (AiException ex)
        {
            return new RegionGenerationResult { Success = false, Error = $"planner AI: {ex.Message}" };
        }

        var json = ExtractJsonBlock(response.Content ?? string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return new RegionGenerationResult { Success = false, Error = "planner returned no JSON block" };

        // Parse the partial plan. We deserialize into a WorldPlan (the
        // partial plan only fills Locations/Npcs/Buildings/Custom* fields;
        // the required Title/Theme/Setting/Atmosphere/StartingHook fields
        // are missing, so we use a permissive parse + manual fill).
        WorldPlan? partial;
        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            // Wrap the partial JSON in a full-plan shell so the required
            // fields are present. The wrapper's Title/Theme/etc. come from
            // the original plan; the partial's Locations/Npcs/Buildings
            // override the wrapper's empty lists via a merge step.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var merged = new JsonObject();
            merged["title"] = originalPlan.Title;
            merged["theme"] = originalPlan.Theme;
            merged["setting"] = originalPlan.Setting;
            merged["atmosphere"] = originalPlan.Atmosphere;
            merged["startingHook"] = originalPlan.StartingHook;
            merged["generationMode"] = "chunked";
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                merged[prop.Name] = JsonSerializer.Deserialize<JsonNode>(prop.Value.GetRawText());
            }
            partial = System.Text.Json.JsonSerializer.Deserialize<WorldPlan>(merged, opts);
            if (partial is null)
                return new RegionGenerationResult { Success = false, Error = "partial plan deserialized to null" };
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new RegionGenerationResult { Success = false, Error = $"partial plan JSON malformed: {ex.Message}" };
        }

        // Tag all parsed locations with the target region (defensive —
        // the planner should already set region=regionName, but LLMs
        // don't always comply).
        partial = partial with
        {
            Locations = (partial.Locations ?? new())
                .Select(pl => string.IsNullOrWhiteSpace(pl.Region)
                    ? pl with { Region = regionName }
                    : pl)
                .ToList(),
        };

        progress?.Report(new WorldBuildProgress
        {
            Stage = "region",
            Status = ProgressStatus.Active,
            Label = $"Генерация региона «{regionName}»",
            Detail = $"Получено {partial.Locations.Count} локаций, {partial.Npcs.Count} NPC, {partial.Buildings.Count} зданий. Коммит…",
            Percent = 70,
        });

        // Commit via the WorldBuilderCommitter. It's idempotent — won't
        // duplicate locations/NPCs that already exist by name. We pass
        // the partial plan with GenerationMode="chunked" so the
        // committer's CommitLocations correctly filters (the region is
        // not in coldRegions anymore since we'll mark it ready after
        // commit; the committer's filter is: skip if Region is in
        // coldRegions. Since regionName was in coldRegions, the filter
        // would WRONGLY skip the new locations. So we clear
        // coldRegions/readyRegions on the world BEFORE committing, then
        // re-set them with regionName moved to readyRegions.)
        if (_world.Flags is not null && _world.Flags.ContainsKey("coldRegions"))
        {
            var cold = ReadFlagStringList(_world.Flags, "coldRegions");
            cold = cold.Where(n => !string.Equals(n, regionName, StringComparison.OrdinalIgnoreCase)).ToList();
            _world.Flags["coldRegions"] = cold;
        }

        var committer = new WorldBuilderCommitter(_world);
        var stats = new CommitStats();
        // Re-set GenerationMode to null on the partial plan so the
        // committer treats ALL locations as committable (we've already
        // removed regionName from coldRegions; the partial plan's other
        // regions don't matter here).
        var commitPlan = partial with { GenerationMode = null };
        committer.CommitCustomTemplates(commitPlan, stats);
        committer.CommitLocations(commitPlan, stats);
        committer.CommitPopulation(commitPlan, stats);
        committer.CommitBuildings(commitPlan, stats);

        // Mark the region ready in World.Flags (move it to readyRegions).
        if (_world.Flags is null) _world.Flags = new();
        var readyList = ReadFlagStringList(_world.Flags, "readyRegions");
        if (!readyList.Any(n => string.Equals(n, regionName, StringComparison.OrdinalIgnoreCase)))
            readyList.Add(regionName);
        _world.Flags["readyRegions"] = readyList;

        progress?.Report(new WorldBuildProgress
        {
            Stage = "region",
            Status = ProgressStatus.Done,
            Label = $"Регион «{regionName}» готов",
            Detail = stats.Summary(),
            Percent = 100,
        });

        return new RegionGenerationResult
        {
            Success = true,
            LocationsAdded = stats.Locations,
            NpcsAdded = stats.Npcs,
            BuildingsAdded = stats.Buildings,
            Errors = stats.Errors,
            Summary = stats.Summary(),
        };
    }

    /// <summary>
    /// Result of <see cref="GenerateRegionAsync"/>. Returned to the
    /// travel handler so it can log what was added (and surface any
    /// errors to the player via the in-game log).
    /// </summary>
    public sealed record RegionGenerationResult
    {
        public bool Success { get; init; }
        public bool AlreadyReady { get; init; }
        public int LocationsAdded { get; init; }
        public int NpcsAdded { get; init; }
        public int BuildingsAdded { get; init; }
        public List<string> Errors { get; init; } = new();
        public string? Error { get; init; }
        public string? Summary { get; init; }
    }

    /// <summary>
    /// Read a <c>World.Flags</c> string-list entry into a fresh
    /// <see cref="List{T}"/>. Tolerates values stored as either a
    /// <see cref="string"/> (single-item, treated as a one-element list)
    /// or a <see cref="System.Text.Json.JsonElement"/> array (the form
    /// produced by JSON serialization). Returns an empty list when the
    /// key is missing or the value shape is unrecognized.
    /// </summary>
    private static List<string> ReadFlagStringList(
        System.Collections.Generic.Dictionary<string, object>? flags, string key)
    {
        if (flags is null) return new();
        if (!flags.TryGetValue(key, out var v) || v is null) return new();
        switch (v)
        {
            case string s:
                return string.IsNullOrWhiteSpace(s) ? new() : new() { s };
            case System.Collections.IEnumerable e when v is not string:
                var result = new List<string>();
                foreach (var item in e)
                {
                    if (item is string ss && !string.IsNullOrWhiteSpace(ss)) result.Add(ss);
                    else if (item is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var sv = je.GetString();
                        if (!string.IsNullOrWhiteSpace(sv)) result.Add(sv);
                    }
                }
                return result;
            default:
                return new();
        }
    }

    // ─── Rebuild existing world (issue #23) ──────────────────────────────

    /// <summary>
    /// Rebuild options for <see cref="RebuildAsync"/>. Each flag selects
    /// a category to regenerate. <see cref="FullRebuild"/> is the only
    /// destructive option — it discards all existing entities and
    /// regenerates from scratch. The partial flags are additive: the
    /// existing world is preserved, the AI generates additional content
    /// in the selected categories, and the committer (idempotent)
    /// commits only the new entries.
    /// </summary>
    public sealed record RebuildOptions
    {
        /// <summary>Regenerate locations (additive — keeps existing).</summary>
        public bool Locations { get; init; }
        /// <summary>Regenerate population (additive — keeps existing NPCs).</summary>
        public bool Npcs { get; init; }
        /// <summary>Regenerate loot/items (additive — keeps existing).</summary>
        public bool Items { get; init; }
        /// <summary>Re-write the opening narration from the current state.</summary>
        public bool Narration { get; init; }
        /// <summary>
        /// Full rebuild — discard all entities and regenerate from
        /// scratch. The only destructive option.
        /// </summary>
        public bool FullRebuild { get; init; }
    }

    /// <summary>
    /// Rebuild an existing world. Loads the save's World + meta, applies
    /// the requested categories (additive for partial flags, destructive
    /// for <see cref="RebuildOptions.FullRebuild"/>), runs the planner +
    /// committer + narrator as needed, persists the result back to the
    /// same saveId via the supplied <paramref name="saveManager"/>.
    /// </summary>
    /// <param name="saveManager">Save manager (used to load + persist the
    /// world). Caller passes the live SaveManager so the orchestrator
    /// doesn't take a direct dependency on the filesystem layout.</param>
    /// <param name="saveId">Existing save id to rebuild.</param>
    /// <param name="options">Which categories to regenerate.</param>
    /// <param name="brief">Optional rebuild brief. When empty, the
    /// orchestrator falls back to the world's original theme/setting
    /// from <see cref="World.Flags"/>.</param>
    /// <param name="log">Existing log entries (preserved on partial
    /// rebuilds; replaced on full rebuild). Null = empty log.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="RebuildResult"/> with the final plan (if
    /// the planner ran), the new narration (if the narrator ran), the
    /// commit stats, and a summary.</returns>
    public async Task<RebuildResult> RebuildAsync(
        SaveManager saveManager,
        string saveId,
        RebuildOptions options,
        string? brief = null,
        LogEntry[]? log = null,
        IProgress<WorldBuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (saveManager is null) throw new ArgumentNullException(nameof(saveManager));
        if (string.IsNullOrWhiteSpace(saveId))
            return new RebuildResult { Success = false, Error = "empty saveId" };
        if (options is null) throw new ArgumentNullException(nameof(options));

        var loaded = saveManager.LoadAll(saveId);
        if (loaded is null)
            return new RebuildResult { Success = false, Error = $"save «{saveId}» not found" };
        var (world, meta, existingLog) = loaded.Value;

        // Resolve the rebuild brief: explicit > original theme/setting
        // (stashed on World.Flags by CommitTitle) > generic fallback.
        var rebuildBrief = string.IsNullOrWhiteSpace(brief)
            ? ResolveOriginalBrief(world)
            : brief.Trim();
        var logEntries = log ?? existingLog ?? Array.Empty<LogEntry>();

        // Full rebuild: discard all entities + reset the player. This is
        // the only destructive path — it discards the player's progress.
        if (options.FullRebuild)
        {
            progress?.Report(new WorldBuildProgress
            {
                Stage = "rebuild",
                Status = ProgressStatus.Active,
                Label = "Полный пересбор мира",
                Detail = "Очистка существующего мира…",
                Percent = 5,
            });

            var seed = world.Seed;
            var registries = world.Registries;
            world.Players.Clear();
            world.Npcs.Clear();
            world.Items.Clear();
            world.Buildings.Clear();
            world.Locations.Clear();
            world.Quests.Clear();
            world.Flags = new();
            world.Combat = null;
            world.Turn = 0;
            world.Rng = Rng.FromState(seed);
            world.Ruleset = Rulesets.DefaultDnd;

            // Run a full build (planner → ruleset → committer → narrator)
            // using the rebuild brief. We reuse RunAsync by setting up
            // the orchestrator state to a fresh plan.
            _currentPlan = null;
            _currentStage = "planning";
            _currentIteration = 0;
            _loadedStage = null;

            var request = new WorldPlanRequest { Brief = rebuildBrief };
            var runResult = await RunAsync(request, progress, ct).ConfigureAwait(false);

            if (runResult.Kind != WorldBuilderResultKind.Complete)
            {
                return new RebuildResult
                {
                    Success = false,
                    Error = $"full rebuild failed: {runResult.Summary}",
                };
            }

            // Persist the rebuilt world. Use the existing log (or empty).
            var newNarration = runResult.OpeningNarration;
            var newLog = newNarration is null
                ? logEntries
                : new[] { LogEntry.Narrative(newNarration, authorId: null) }
                    .Concat(logEntries.Where(l => l.Kind != "narrative"))
                    .ToArray();
            var updatedMeta = meta with { BuildStatus = BuildStatus.Done };
            saveManager.SaveAll(saveId, world, updatedMeta, newLog);

            return new RebuildResult
            {
                Success = true,
                Plan = runResult.Plan,
                OpeningNarration = newNarration,
                Stats = runResult.Stats,
                Summary = $"Полный пересбор завершён. {runResult.Summary}",
            };
        }

        // Partial rebuild: additive. Run the planner with a prompt that
        // shows the existing state + asks for additions in the selected
        // categories. The committer is idempotent — only new entries are
        // added. Narration-only rebuild skips the planner entirely.
        string? narration2 = null;
        CommitStats? stats2 = null;
        WorldPlan? plan2 = null;

        if (options.Locations || options.Npcs || options.Items)
        {
            progress?.Report(new WorldBuildProgress
            {
                Stage = "rebuild",
                Status = ProgressStatus.Active,
                Label = "Партиальный пересбор",
                Detail = "Запрос к планировщику…",
                Percent = 20,
            });

            var cats = new List<string>();
            if (options.Locations) cats.Add("новые локации");
            if (options.Npcs) cats.Add("дополнительные NPC");
            if (options.Items) cats.Add("новые предметы");
            var catsBlock = string.Join(", ", cats);

            var stateSummary = BuildRebuildStateSummary(world);
            var systemPrompt =
                "Ты — Достраиватель Мира. Игрок попросил дополнить существующий мир. " +
                "Работай на русском. НЕ удаляй и не изменяй существующее — только добавляй. " +
                "Верни ТОЛЬКО JSON-объект PartialWorldPlan в блоке ```json ... ```. " +
                "Без другого текста.\n\n" +
                "Формат JSON: {\"locations\":[...],\"npcs\":[...],\"buildings\":[...]," +
                "\"customNpcTemplates\":[...],\"customItemTemplates\":[...]," +
                "\"customBuildingTemplates\":[...],\"starterGear\":[...],\"starterCurrency\":N}.";

            var userPrompt =
                $"Существующий мир:\n{stateSummary}\n\n" +
                $"Дополнительно сгенерируй: {catsBlock}.\n" +
                $"Бриф для перестройки: {rebuildBrief}\n" +
                "Не удаляй существующее. Новые локации соедини с существующими (укажи их имена в connections). " +
                "Имена новых сущностей не должны совпадать с уже существующими. " +
                "Верни JSON-объект PartialWorldPlan в блоке ```json ... ```.";

            var messages = new List<ChatMessage>
            {
                ChatMessage.System(systemPrompt),
                ChatMessage.User(userPrompt),
            };

            var plannerAi = _aiSettings is null ? _ai : _ai.WithModel(_aiSettings.GetModelForRole(AiRole.Planner));
            ChatResponse response;
            try
            {
                response = await plannerAi.ChatAsync(messages, ct).ConfigureAwait(false);
            }
            catch (AiException ex)
            {
                return new RebuildResult { Success = false, Error = $"planner AI: {ex.Message}" };
            }

            var json = ExtractJsonBlock(response.Content ?? string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return new RebuildResult { Success = false, Error = "planner returned no JSON block" };

            try
            {
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                };
                // Wrap into a full-plan shell (same trick as
                // GenerateRegionAsync — the partial JSON only fills the
                // collection fields).
                var worldTitle = (world.Flags is not null && world.Flags.TryGetValue("worldTitle", out var wt) && wt is string wts)
                    ? wts : (meta.WorldTitle ?? "Мир");
                var worldTheme = (world.Flags is not null && world.Flags.TryGetValue("worldTheme", out var th) && th is string ths)
                    ? ths : "custom";
                var worldSetting = (world.Flags is not null && world.Flags.TryGetValue("worldSetting", out var st) && st is string sts)
                    ? sts : "";
                var worldAtm = (world.Flags is not null && world.Flags.TryGetValue("worldAtmosphere", out var at) && at is string ats)
                    ? ats : "";
                var worldHook = (world.Flags is not null && world.Flags.TryGetValue("startingHook", out var hk) && hk is string hks)
                    ? hks : "";

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var merged = new JsonObject();
                merged["title"] = worldTitle;
                merged["theme"] = worldTheme;
                merged["setting"] = worldSetting;
                merged["atmosphere"] = worldAtm;
                merged["startingHook"] = worldHook;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    merged[prop.Name] = JsonSerializer.Deserialize<JsonNode>(prop.Value.GetRawText());
                }
                plan2 = System.Text.Json.JsonSerializer.Deserialize<WorldPlan>(merged, opts);
                if (plan2 is null)
                    return new RebuildResult { Success = false, Error = "partial plan deserialized to null" };
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new RebuildResult { Success = false, Error = $"partial plan JSON malformed: {ex.Message}" };
            }

            progress?.Report(new WorldBuildProgress
            {
                Stage = "rebuild",
                Status = ProgressStatus.Active,
                Label = "Коммит дополнений",
                Detail = $"Получено: {plan2!.Locations.Count} локаций, {plan2.Npcs.Count} NPC, {plan2.Buildings.Count} зданий",
                Percent = 60,
            });

            // Commit additively. The committer's idempotency (match by
            // name) keeps existing entities intact; only new ones are added.
            var committer = new WorldBuilderCommitter(world);
            stats2 = new CommitStats();
            committer.CommitCustomTemplates(plan2, stats2);
            committer.CommitLocations(plan2, stats2);
            committer.CommitPopulation(plan2, stats2);
            committer.CommitBuildings(plan2, stats2);
            if (options.Items)
            {
                committer.CommitContent(plan2, stats2);
            }
        }

        if (options.Narration)
        {
            progress?.Report(new WorldBuildProgress
            {
                Stage = "rebuild",
                Status = ProgressStatus.Active,
                Label = "Переписываем вступление",
                Percent = 80,
            });

            // Re-run the narrator with the current world state.
            var narrPlan = plan2 ?? new WorldPlan
            {
                Title = meta.WorldTitle ?? "Мир",
                Theme = TryGetFlag(world.Flags, "worldTheme") ?? "custom",
                Setting = TryGetFlag(world.Flags, "worldSetting") ?? "",
                Atmosphere = TryGetFlag(world.Flags, "worldAtmosphere") ?? "",
                StartingHook = TryGetFlag(world.Flags, "startingHook") ?? "",
            };
            try
            {
                narration2 = await RunNarratorAsync(narrPlan, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (AiException ex)
            {
                // Non-fatal — narration is best-effort.
                progress?.Report(new WorldBuildProgress
                {
                    Stage = "rebuild",
                    Status = ProgressStatus.Error,
                    Label = "Не удалось переписать вступление",
                    Detail = ex.Message,
                    Percent = 90,
                });
            }
        }

        // Persist the rebuilt world. On a narration-only rebuild, the
        // new narration replaces any existing narrative log entries.
        LogEntry[] finalLog;
        if (narration2 is null)
        {
            finalLog = logEntries;
        }
        else
        {
            finalLog = new[] { LogEntry.Narrative(narration2, authorId: null) }
                .Concat(logEntries.Where(l => l.Kind != "narrative"))
                .ToArray();
        }

        var rebuiltMeta = meta with { BuildStatus = BuildStatus.Done };
        saveManager.SaveAll(saveId, world, rebuiltMeta, finalLog);

        progress?.Report(new WorldBuildProgress
        {
            Stage = "rebuild",
            Status = ProgressStatus.Done,
            Label = "Перестройка завершена",
            Detail = stats2?.Summary() ?? (narration2 is null ? "без изменений" : "только наррация"),
            Percent = 100,
        });

        var summary = stats2 is null
            ? (narration2 is null ? "Ничего не сделано." : "Наррация обновлена.")
            : $"Добавлено: {stats2.Summary()}" + (narration2 is null ? "" : " Наррация обновлена.");
        return new RebuildResult
        {
            Success = true,
            Plan = plan2,
            OpeningNarration = narration2,
            Stats = stats2,
            Summary = summary,
        };
    }

    /// <summary>
    /// Build a short text summary of the current world state for the
    /// rebuild planner prompt. Lists existing location names, NPC names,
    /// building names (capped at 20 each to keep the prompt bounded).
    /// </summary>
    private static string BuildRebuildStateSummary(GameWorld world)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Существующий мир");
        sb.AppendLine($"Локаций: {world.Locations.Count} | NPC: {world.Npcs.Count} | Зданий: {world.Buildings.Count}");
        if (world.Locations.Count > 0)
        {
            sb.AppendLine("Существующие локации:");
            foreach (var l in world.Locations.Take(20))
                sb.AppendLine($"- {l.Name} ({l.Terrain}, опасность {l.Danger})");
        }
        if (world.Npcs.Count > 0)
        {
            sb.AppendLine("Существующие NPC:");
            foreach (var n in world.Npcs.Take(20))
                sb.AppendLine($"- {n.Name} [{n.TemplateId}] @ {world.GetLocation(n.LocationId)?.Name ?? "?"}");
        }
        if (world.Buildings.Count > 0)
        {
            sb.AppendLine("Существующие здания:");
            foreach (var b in world.Buildings.Take(20))
                sb.AppendLine($"- {b.Name} [{b.TemplateId}] @ {world.GetLocation(b.LocationId)?.Name ?? "?"}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolve the original brief from World.Flags. Reconstructs a
    /// human-readable brief from the stashed worldTitle/worldTheme/
    /// worldSetting/worldAtmosphere/startingHook flags (set by
    /// CommitTitle). Used when the rebuild caller passes no explicit
    /// brief — we want the rebuild to stay on-theme with the original.
    /// </summary>
    private static string ResolveOriginalBrief(GameWorld world)
    {
        var title = TryGetFlag(world.Flags, "worldTitle") ?? "";
        var theme = TryGetFlag(world.Flags, "worldTheme") ?? "";
        var setting = TryGetFlag(world.Flags, "worldSetting") ?? "";
        var atmosphere = TryGetFlag(world.Flags, "worldAtmosphere") ?? "";
        var hook = TryGetFlag(world.Flags, "startingHook") ?? "";
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.AppendLine($"Название мира: {title}");
        if (!string.IsNullOrWhiteSpace(theme)) sb.AppendLine($"Тема: {theme}");
        if (!string.IsNullOrWhiteSpace(setting)) sb.AppendLine($"Сеттинг: {setting}");
        if (!string.IsNullOrWhiteSpace(atmosphere)) sb.AppendLine($"Атмосфера: {atmosphere}");
        if (!string.IsNullOrWhiteSpace(hook)) sb.AppendLine($"Сюжетный крючок: {hook}");
        return sb.Length == 0 ? "(без брифа — сгенерируй дополнения по своему усмотрению)" : sb.ToString();
    }

    /// <summary>
    /// Best-effort string extraction from a World.Flags entry. Tolerates
    /// both raw strings (in-memory) and JsonElement values (after a save
    /// round-trip). Returns null when missing or non-string.
    /// </summary>
    private static string? TryGetFlag(System.Collections.Generic.Dictionary<string, object>? flags, string key)
    {
        if (flags is null) return null;
        if (!flags.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return s;
        if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
            return je.GetString();
        return v.ToString();
    }

    /// <summary>Result of <see cref="RebuildAsync"/>.</summary>
    public sealed record RebuildResult
    {
        public bool Success { get; init; }
        public WorldPlan? Plan { get; init; }
        public string? OpeningNarration { get; init; }
        public CommitStats? Stats { get; init; }
        public string? Summary { get; init; }
        public string? Error { get; init; }
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

        // Issue #20 (chunked generation): if the request specifies a
        // generation mode, tell the planner. In chunked mode the planner
        // should ONLY detail the start region's locations / NPCs / buildings;
        // other regions go into plan.Regions with GenStatus="cold" (no
        // locations). The committer skips cold-region locations; the
        // travel handler generates them on-demand via
        // GenerateRegionAsync.
        var modeInstruction = string.Equals(request.GenerationMode, "chunked", StringComparison.OrdinalIgnoreCase)
            ? "\n\nРЕЖИМ ГЕНЕРАЦИИ: по регионам (chunked). " +
              "В плане подробно опиши ТОЛЬКО стартовую область: её локации, NPC, здания " +
              "и выходы на границу региона. Остальные регионы перечисли в plan.regions с " +
              "полем genStatus=\"cold\" — без локаций, только общее описание (тип, климат, " +
              "население, экономика, политика, культура). Для стартового региона установи " +
              "genStatus=\"ready\" и containsStart=true. Установи plan.generationMode=\"chunked\". " +
              "Соедини стартовые локации выходами между собой. ДОПОЛНИТЕЛЬНО добавь 1-2 выхода " +
              "из пограничных стартовых локаций на холодные регионы: в connections укажи ИМЯ " +
              "региона (не локации) — движок создаст фантомный выход, который запустит " +
              "генерацию региона когда игрок к нему приблизится."
            : string.IsNullOrWhiteSpace(request.GenerationMode)
                ? ""
                : $"\n\nРЕЖИМ ГЕНЕРАЦИИ: {request.GenerationMode}.";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(
                "Построй мир по следующему брифу. Верни результат как JSON-объект WorldPlan " +
                "в блоке ```json ... ```. Без другого текста.\n\n" +
                $"БРИФ:\n{request.Brief}{modeInstruction}"),
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

            // Issue #20: enforce the requested generation mode on the
            // parsed plan. If the planner forgot to set GenerationMode
            // (or set it wrong), we override it from the request so the
            // committer + travel handler can rely on it. In chunked mode
            // we also normalize region genStatus: the start region (the
            // one with containsStart=true OR the first region if none is
            // marked) becomes "ready"; all others become "cold".
            if (string.Equals(request.GenerationMode, "chunked", StringComparison.OrdinalIgnoreCase))
            {
                plan = NormalizeChunkedPlan(plan);
            }
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
            // NOTE: Standard template lists (ITEM_TEMPLATES, NPC_TEMPLATES,
            // BUILDING_TEMPLATES) are intentionally NOT provided. AI-generated
            // worlds must be created from scratch — all entities come from the
            // plan's customNpcTemplates / customItemTemplates / customBuildingTemplates.
            // The planner prompt explicitly tells the AI to create everything
            // custom; providing standard template ids would let it reference
            // them, defeating the "unique world" design goal.
        };
        var prompt = PromptLoader.Substitute(template, vars);
        // Append the tools guide so the planner knows the available tool
        // signatures — even though the desktop MVP's committer doesn't let
        // the planner call them directly, the planner prompt expects this
        // context to inform its plan structure.
        var guide = _prompts.Get("tools-guide");
        return prompt + "\n\n---\n\n" + guide;
    }

    /// <summary>
    /// Normalize a chunked plan after parsing. Ensures:
    /// <list type="bullet">
    ///   <item><see cref="WorldPlan.GenerationMode"/> is set to "chunked".</item>
    ///   <item>Exactly one region is marked <c>genStatus="ready"</c>
    ///     (the start region). The start region is the one with
    ///     <see cref="PlanRegion.ContainsStart"/>=true; if none is
    ///     marked, the first region (if any) is promoted.</item>
    ///   <item>All other regions are marked <c>genStatus="cold"</c>.</item>
    ///   <item>Locations with a Region that's now cold are dropped from
    ///     <see cref="WorldPlan.Locations"/> (the committer would skip
    ///     them anyway, but dropping keeps the plan consistent for the
    ///     narrator + saves).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// This is defensive — the planner prompt asks for these invariants,
    /// but LLMs don't always comply. Forcing them on the parsed plan
    /// keeps the downstream committer + travel handler logic simple
    /// (they can trust the markers).
    /// </remarks>
    private static WorldPlan NormalizeChunkedPlan(WorldPlan plan)
    {
        // Force the GenerationMode flag on the plan.
        plan = plan with { GenerationMode = "chunked" };

        var regions = (plan.Regions ?? new()).ToList();
        if (regions.Count == 0)
        {
            // No regions in the plan — nothing to normalize. The plan
            // is treated as "all start, no cold regions" (effectively
            // a full build despite the chunked flag). This is a
            // graceful fallback for malformed planner output.
            return plan;
        }

        // Find the start region: prefer containsStart=true; else the
        // first region that already has genStatus="ready"; else the
        // first region in the list.
        var startIdx = regions.FindIndex(r => (r.ContainsStart ?? false));
        if (startIdx < 0)
            startIdx = regions.FindIndex(r => string.Equals(r.GenStatus, "ready", StringComparison.OrdinalIgnoreCase));
        if (startIdx < 0) startIdx = 0;

        var coldNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            var status = i == startIdx ? "ready" : "cold";
            if (!string.Equals(r.GenStatus, status, StringComparison.OrdinalIgnoreCase))
            {
                regions[i] = r with { GenStatus = status };
            }
            if (i != startIdx && !string.IsNullOrWhiteSpace(regions[i].Name))
                coldNames.Add(regions[i].Name);
        }

        // Drop locations that belong to a cold region (defensive — the
        // committer would skip them anyway, but this keeps the plan
        // consistent for the narrator + saves).
        var filteredLocations = (plan.Locations ?? new())
            .Where(pl => string.IsNullOrWhiteSpace(pl.Region) || !coldNames.Contains(pl.Region))
            .ToList();

        return plan with { Regions = regions, Locations = filteredLocations };
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
            // NOTE: Template lists intentionally NOT provided to the narrator.
            // AI-generated worlds are built from scratch; the narrator doesn't
            // create entities, it only narrates. The narrator prompt says
            // "standard templates don't exist — this world is custom."
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

    // ─── Stage 2 helper: ruleset designer (issue #21) ──────────────────

    /// <summary>
    /// Heuristic: detect non-fantasy themes that warrant a custom ruleset
    /// overlay. The list covers the genre keywords the planner prompt
    /// uses (cyberpunk / sci-fi / steampunk / postapoc / horror / modern).
    /// Standard fantasy themes (dark fantasy / sword &amp; sorcery / high
    /// fantasy / etc.) fall through and use DefaultDnd as-is.
    /// </summary>
    private static bool IsNonFantasyTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return false;
        var lower = theme.ToLowerInvariant();
        return lower.Contains("cyber")
            || lower.Contains("sci-fi") || lower.Contains("scifi") || lower.Contains("sci ")
            || lower.Contains("steam")
            || lower.Contains("postapoc") || lower.Contains("post-apoc") || lower.Contains("постапок")
            || lower.Contains("horror") || lower.Contains("хоррор")
            || lower.Contains("modern") || lower.Contains("современ")
            || lower.Contains("space") || lower.Contains("косм")
            || lower.Contains("cyberpunk") || lower.Contains("кибер");
    }

    /// <summary>
    /// Call the ruleset-designer AI for a non-fantasy world. Asks the
    /// model for a lightweight JSON overlay (custom attribute display
    /// names + custom resource pool names + custom skill list). Returns
    /// null when the model doesn't return a valid JSON block (non-fatal
    /// — the caller falls back to DefaultDnd).
    /// </summary>
    private async Task<RulesetDesignOverlay?> RunRulesetDesignerAsync(
        WorldPlan plan,
        CancellationToken ct)
    {
        var systemPrompt =
            "Ты — архитектор правил мира. Подбери подходящую систему для не-фэнтезийного мира. " +
            "Работай на русском. Верни ТОЛЬКО JSON-объект в блоке ```json ... ```. Без другого текста.\n\n" +
            "Формат JSON:\n" +
            "```json\n" +
            "{\n" +
            "  \"attributeNames\": {\"str\": \"Сила\", \"dex\": \"Ловкость\", \"con\": \"Телосложение\", " +
            "\"int\": \"Интеллект\", \"wis\": \"Восприятие\", \"cha\": \"Харизма\"},\n" +
            "  \"resourcePools\": {\"hp\": \"Здоровье\"},\n" +
            "  \"skills\": [\"атлетика\", \"обман\", \"история\", ...]\n" +
            "}\n" +
            "```\n\n" +
            "Ключи в attributeNames/resourcePools — стандартные (str/dex/con/int/wis/cha и hp/ac). " +
            "Значения — человекочитаемые имена для UI и для GM-контекста. " +
            "Список skills — 6-15 ключевых навыков мира (например, для киберпанка: «хакинг», «стрельба», « streetwise», «техника», «обман», «запугивание»). " +
            "Адаптируй под тему мира; не дублируй стандартный D&D-список вслепую.";

        var userPrompt =
            $"Разработай ruleset для мира темы «{plan.Theme}». " +
            $"Название мира: «{plan.Title}». Сеттинг: {plan.Setting}. Атмосфера: {plan.Atmosphere}.\n\n" +
            "Определи:\n" +
            "1. Имена характеристик (вместо STR/DEX/CON/INT/WIS/CHA — например для киберпанка: Cool/Edge/Meat/Tech/Luck/Will).\n" +
            "2. Пулы ресурсов (вместо hp/ac — например stress/cred/humanity для киберпанка, sanity для хоррора).\n" +
            "3. Список навыков (6-15) под тему мира.\n\n" +
            "Верни JSON-объект в блоке ```json ... ```.";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userPrompt),
        };

        // Use the planner role's model for the ruleset design (creative
        // single-shot call — same profile as the planner).
        var rulesetAi = _aiSettings is null ? _ai : _ai.WithModel(_aiSettings.GetModelForRole(AiRole.Planner));
        var response = await rulesetAi.ChatAsync(messages, ct).ConfigureAwait(false);
        var json = ExtractJsonBlock(response.Content ?? string.Empty);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            return System.Text.Json.JsonSerializer.Deserialize<RulesetDesignOverlay>(json, opts);
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON — return null so the caller falls back to DefaultDnd.
            return null;
        }
    }

    /// <summary>
    /// Render a one-line summary of the applied ruleset overlay for the
    /// progress UI (e.g. «Атрибуты: Cool/Edge/Meat/Tech/Luck/Will · Ресурсы: Stress · Навыков: 8»).
    /// </summary>
    private static string DescribeRulesetOverlay(RulesetDesignOverlay overlay)
    {
        var sb = new StringBuilder();
        if (overlay.AttributeNames is { Count: > 0 } an)
        {
            sb.Append("Атрибуты: ");
            sb.Append(string.Join("/", an.Values));
        }
        if (overlay.ResourcePools is { Count: > 0 } rp)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append("Ресурсы: ");
            sb.Append(string.Join("/", rp.Values));
        }
        if (overlay.Skills is { Count: > 0 } sk)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append($"Навыков: {sk.Count}");
        }
        return sb.Length == 0 ? "кастомная система" : sb.ToString();
    }

    /// <summary>
    /// Lightweight ruleset design returned by the ruleset-designer AI
    /// call. Applied as an overlay on top of
    /// <see cref="Rulesets.DefaultDnd"/> (the underlying attribute /
    /// resource / formula structure stays D&amp;D 5e; only the display
    /// names + skill list change). All fields optional.
    /// </summary>
    public sealed record RulesetDesignOverlay
    {
        public Dictionary<string, string>? AttributeNames { get; init; }
        public Dictionary<string, string>? ResourcePools { get; init; }
        public List<string>? Skills { get; init; }
    }
}
