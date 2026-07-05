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
        // The desktop MVP uses the default DnD 5e-style ruleset for every
        // world. The TS source had a per-world ruleset design stage (via
        // world-ruleset prompt + commit_ruleset tool); that's deferred. We
        // still publish a progress tick so the UI shows the stage.
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
        progress?.Report(new WorldBuildProgress
        {
            Stage = "ruleset",
            Status = ProgressStatus.Done,
            Label = "Правила установлены (DnD 5e по умолчанию)",
            Percent = 25,
        });

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
