using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Common;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// World-build screen. Runs the WorldBuilderOrchestrator in the
/// background and shows live progress (stage / label / percent / detail)
/// to the user. Supports cancellation. On success, creates a save with the
/// opening narration as the first log entry and navigates to the game
/// screen in single-player mode.
///
/// <para>
/// <b>Pause / resume (issue #19):</b> the user can pause the build
/// mid-flight via <see cref="PauseCommand"/>; the orchestrator blocks at
/// the next checkpoint and the UI shows «Приостановлено: {stage}».
/// <see cref="ResumeCommand"/> continues from where it stopped. On
/// <see cref="CancelCommand"/> (cancel — distinct from pause), the
/// orchestrator's saved state is written to
/// <c>%APPDATA%/MyGame/worldbuilder-state.json</c> so a future session
/// can offer to resume.
/// </para>
/// </summary>
public partial class WorldBuildViewModel : ViewModelBase
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly SaveManager _saveManager;
    private readonly MainViewModel _shell;
    private readonly bool _forHost;

    private readonly string _brief;
    private readonly IReadOnlyCollection<MyGame.Core.AI.Agents.PetDelegation>? _petDelegations;
    private readonly string? _generationMode;
    private CancellationTokenSource? _cts;
    private WorldBuilderResult? _result;

    // The orchestrator is created inside StartAsync but Pause/Resume need
    // to reach it from the UI thread → keep a field reference. Cleared in
    // the finally block of StartAsync so a stale orchestrator can't be
    // paused/resumed after the build has finished.
    private WorldBuilderOrchestrator? _orchestrator;

    public WorldBuildViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        SaveManager saveManager,
        MainViewModel shell,
        string brief,
        IReadOnlyCollection<MyGame.Core.AI.Agents.PetDelegation>? petDelegations = null,
        string? generationMode = null,
        bool forHost = false)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _brief = brief ?? string.Empty;
        _petDelegations = petDelegations;
        _generationMode = generationMode;
        _forHost = forHost;

        Title = "Создание мира";

        // Pre-populate the pet-progress rows so the user sees the
        // upcoming delegations immediately (issue #76). Each starts in
        // "pending" status; the orchestrator's pet-stage progress events
        // flip them to running/done/error as the build progresses.
        if (_petDelegations is not null)
        {
            foreach (var del in _petDelegations)
            {
                PetProgress.Add(new PetDelegationProgress
                {
                    Label = del.Label,
                    Status = "pending",
                });
            }
        }
    }

    // ─── Live progress state ─────────────────────────────────────────

    private int _percent;
    private string _stageLabel = "Подготовка…";
    private string? _stageDetail;
    private string? _finalSummary;
    private bool _completed;
    private bool _failed;
    private bool _isPaused;

    /// <summary>0–100 percent complete for the whole build.</summary>
    public int Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, value);
    }

    /// <summary>Human-readable current stage label (RU).</summary>
    public string StageLabel
    {
        get => _stageLabel;
        private set => SetProperty(ref _stageLabel, value);
    }

    /// <summary>Optional sub-step detail text.</summary>
    public string? StageDetail
    {
        get => _stageDetail;
        private set => SetProperty(ref _stageDetail, value);
    }

    /// <summary>
    /// Set when the build completes successfully. Drives the
    /// «Продолжить» button visibility.
    /// </summary>
    public bool Completed
    {
        get => _completed;
        private set
        {
            if (SetProperty(ref _completed, value))
                EnterGameCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Set when the build fails or is cancelled. Drives the
    /// «Назад» button prominence.
    /// </summary>
    public bool Failed
    {
        get => _failed;
        private set => SetProperty(ref _failed, value);
    }

    /// <summary>
    /// True while the user has paused the build (issue #19). Drives the
    /// «Пауза» / «Продолжить» button toggle in the View. Set by
    /// <see cref="Pause"/> / <see cref="Resume"/>; cleared on
    /// completion / failure / cancel. When true, <see cref="StageLabel"/>
    /// is prefixed with «Приостановлено: » so the user sees what stage
    /// the build is waiting on.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                PauseCommand.NotifyCanExecuteChanged();
                ResumeCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsRunning));
            }
        }
    }

    /// <summary>
    /// Hide the inherited <see cref="ViewModelBase.IsBusy"/> so we can
    /// also notify <see cref="IsRunning"/> (which depends on IsBusy)
    /// when the build starts / finishes. The backing field + base
    /// setter still do the heavy lifting; we just add the extra
    /// PropertyChanged notification.
    /// </summary>
    public new bool IsBusy
    {
        get => base.IsBusy;
        private set
        {
            if (base.IsBusy != value)
            {
                base.IsBusy = value;
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsRunning));
                PauseCommand.NotifyCanExecuteChanged();
                ResumeCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                StartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// True while the build is in flight and not paused (i.e. the
    /// orchestrator is actively making progress). Used by the View to
    /// decide whether to show the «Пауза» button.
    /// </summary>
    public bool IsRunning => IsBusy && !IsPaused && !Completed && !Failed;

    /// <summary>
    /// The pre-pause stage label — captured when the user pauses so
    /// <see cref="Resume"/> can restore the orchestrator's last reported
    /// stage text (the orchestrator doesn't emit new progress events
    /// while paused, so the View would otherwise show the
    /// «Приостановлено: …» text indefinitely after resume).
    /// </summary>
    private string? _prePauseStageLabel;

    /// <summary>
    /// Final summary (success message + commit stats, or error text).
    /// Bound to a read-only text block under the progress bar.
    /// </summary>
    public string? FinalSummary
    {
        get => _finalSummary;
        private set => SetProperty(ref _finalSummary, value);
    }

    // ─── Pet-agent progress (issue #76) ──────────────────────────────
    //
    // When the user opts into pet delegations (WorldBrief screen), the
    // orchestrator runs each as a separate AI sub-task after the
    // deterministic committer stage. The progress dialog shows a
    // collapsible «Pet-агенты» section with one row per delegation, so
    // the user can see exactly what each sub-agent is doing (and review
    // the result after the build completes).
    //
    // PetProgress is pre-populated at construction time (one entry per
    // delegation, status="pending"). The orchestrator's progress callback
    // translates each Stage=="pet" event into an update on the matching
    // row (matched by the delegation label embedded in the event's Label
    // field). The section auto-expands when the first delegation starts.

    /// <summary>
    /// One row per <c>PetDelegation</c> passed to the orchestrator.
    /// Pre-populated at construction; updated by the progress callback as
    /// each delegation moves from pending → running → done/error.
    /// </summary>
    public ObservableCollection<PetDelegationProgress> PetProgress { get; } = new();

    /// <summary>True when there's at least one pet delegation to show.</summary>
    public bool HasPetProgress => PetProgress.Count > 0;

    private bool _isPetSectionExpanded;
    /// <summary>
    /// Whether the «Pet-агенты» section is expanded. Auto-set to true
    /// when the first delegation moves to <c>running</c> status. Bound to
    /// a toggle button so the user can fold/unfold the section manually.
    /// </summary>
    public bool IsPetSectionExpanded
    {
        get => _isPetSectionExpanded;
        private set => SetProperty(ref _isPetSectionExpanded, value);
    }

    /// <summary>
    /// Toggle the pet-progress section's expanded state. Bound to the
    /// section header button (so the user can fold/unfold it manually
    /// after the auto-expand on first Active event).
    /// </summary>
    [RelayCommand]
    private void TogglePetSection()
    {
        IsPetSectionExpanded = !IsPetSectionExpanded;
    }

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Auto-invoked on view load (via Avalonia's Loaded event or
    /// code-behind). Starts the orchestrator. Can be re-invoked after
    /// a failure via the «Повторить» button.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        Failed = false;
        Completed = false;
        IsPaused = false;
        _prePauseStageLabel = null;
        Percent = 0;
        StageLabel = "Запуск планировщика…";
        StageDetail = null;
        FinalSummary = null;

        _cts = new CancellationTokenSource();
        try
        {
            var profile = _profileStore.GetOrCreate();
            var settings = _settingsStore.Load();

            // Validate AI settings — the planner + narrator both need them.
            if (string.IsNullOrWhiteSpace(settings.Ai.ApiKey))
            {
                Failed = true;
                FinalSummary = "Не задан API-ключ. Откройте Профиль → Настройки AI и введите ключ.";
                return;
            }

            // Build a fresh, clean world without pre-populated default valley entities.
            var world = new World
            {
                Seed = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Rng = new Rng((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Clock = GameTime.Start,
                CalendarSpec = Calendar.DefaultFantasyCalendar,
                Ruleset = Rulesets.DefaultDnd,
                Turn = 0,
                Registries = ServiceHost.Resolve<ContentRegistry>()
            };
            var ai = new AiClient(settings.Ai);
            var prompts = ServiceHost.Resolve<PromptLoader>();
            var tools = new ToolRegistry(world);

            var orchestrator = new WorldBuilderOrchestrator(
                ai, world, prompts, tools, petDelegations: _petDelegations,
                aiSettings: settings.Ai);
            _orchestrator = orchestrator;

            // If a saved state was loaded into this VM (via
            // LoadStateForResume), restore it on the orchestrator so the
            // resumed run skips already-completed stages.
            if (_pendingLoadedState is not null)
            {
                orchestrator.LoadState(_pendingLoadedState);
                _pendingLoadedState = null;
            }

            // Progress marshals to UI thread via Avalonia's dispatcher.
            var progress = new Progress<WorldBuildProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // While paused, ignore orchestrator progress events
                    // so the «Приостановлено: …» label stays put (the
                    // orchestrator publishes a Done tick when resuming
                    // from a paused state, which would otherwise
                    // overwrite the user-facing pause indicator).
                    if (IsPaused) return;

                    Percent = p.Percent;
                    StageLabel = p.Label;
                    StageDetail = p.Detail;

                    // Translate pet-stage events into per-delegation row
                    // updates (issue #76). The orchestrator emits one
                    // Active event per delegation at the start, then a
                    // Done or Error event when it finishes. We match by
                    // the delegation label embedded in the event's Label
                    // field (format: "Pet-агент: {label}" for Active,
                    // "Pet: {label} — готово" / "Pet: {label} — ошибка"
                    // for Done/Error).
                    if (p.Stage == "pet")
                    {
                        UpdatePetProgress(p);
                    }
                });
            });

            _result = await orchestrator.RunAsync(
                new WorldPlanRequest { Brief = _brief, GenerationMode = _generationMode },
                progress,
                _cts.Token);

            if (_result.Kind == WorldBuilderResultKind.Complete)
            {
                MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Fanfare);
                // Persist the built world as a save, with the opening
                // narration as the first log entry so the game screen
                // shows it immediately.
                var title = _result.Plan?.Title ?? "Новый мир";
                var meta = _saveManager.CreateSave(title, world, profile.Id);

                // Issue #20 (chunked generation): stash the original
                // WorldPlan JSON on the save's meta so the travel handler
                // can reload it + ask the AI to fill in cold regions
                // on-demand. We serialize with the same options the
                // orchestrator uses for parsing (camelCase).
                if (_result.Plan is not null)
                {
                    var planJson = System.Text.Json.JsonSerializer.Serialize(
                        _result.Plan,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                            WriteIndented = false,
                        });
                    meta = meta with { OriginalPlanJson = planJson };
                }

                if (!string.IsNullOrWhiteSpace(_result.OpeningNarration))
                {
                    var log = new[]
                    {
                        LogEntry.Narrative(_result.OpeningNarration, authorId: null),
                    };
                    _saveManager.SaveAll(meta.Id, world, meta, log);
                }
                else
                {
                    // No narration — still persist the meta update (the
                    // OriginalPlanJson field was just set on the meta).
                    _saveManager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());
                }

                FinalSummary = _result.Summary ?? "Мир готов.";
                Completed = true;
                Percent = 100;

                // Build is complete — clear any saved state file so the
                // app doesn't offer to resume a finished build on next
                // launch.
                try { WorldBuilderStateStore.Delete(); }
                catch { /* non-fatal */ }
            }
            else if (_result.Kind == WorldBuilderResultKind.Cancelled)
            {
                Failed = true;
                FinalSummary = "Генерация отменена: " + (_result.Summary ?? "");
            }
            else
            {
                Failed = true;
                FinalSummary = "Генерация не удалась: " + (_result.Summary ?? "неизвестная ошибка");
            }
        }
        catch (OperationCanceledException)
        {
            Failed = true;
            FinalSummary = "Генерация отменена.";
        }
        catch (Exception ex)
        {
            Failed = true;
            FinalSummary = "Ошибка: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsPaused = false;
            _orchestrator = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanStart() => !IsBusy && !Completed;

    /// <summary>
    /// Pause the in-flight build (issue #19). The orchestrator blocks at
    /// the next checkpoint (between stages / sub-stages); the UI label
    /// switches to «Приостановлено: {stage}» so the user knows what
    /// stage is waiting. No-op when not running or already paused.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Click);
        if (_orchestrator is null) return;
        _prePauseStageLabel = StageLabel;
        _orchestrator.Pause();
        IsPaused = true;
        // Surface the pause state to the user immediately — the
        // orchestrator won't emit a new progress event until it resumes.
        StageLabel = $"Приостановлено: {_prePauseStageLabel ?? "текущая стадия"}";
    }

    private bool CanPause() => IsBusy && !IsPaused && !Completed && !Failed;

    /// <summary>
    /// Resume a paused build (issue #19). The orchestrator's polling
    /// loop exits within ~100ms and the next stage begins. Restores the
    /// pre-pause stage label so the user sees the live stage again as
    /// soon as the next progress event arrives. No-op when not paused.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Click);
        if (_orchestrator is null) return;
        _orchestrator.Resume();
        IsPaused = false;
        // Restore the pre-pause label; the next progress tick will
        // overwrite it with the new stage's label.
        if (_prePauseStageLabel is not null)
            StageLabel = _prePauseStageLabel;
        _prePauseStageLabel = null;
    }

    private bool CanResume() => IsBusy && IsPaused && !Completed && !Failed;

    /// <summary>
    /// Cancel an in-flight build (issue #19). Distinguished from
    /// <see cref="Pause"/>: cancel terminates the run and the
    /// orchestrator's saved state is written to
    /// <c>%APPDATA%/MyGame/worldbuilder-state.json</c> so a future
    /// session can offer to resume.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        // Persist the orchestrator's current state to disk BEFORE
        // triggering the cancellation — once the token fires, the
        // orchestrator returns Cancelled and its in-memory state
        // snapshots the LAST completed stage (which is exactly what we
        // want to resume from). The state file is what
        // WorldBuilderStateStore.Load() reads on next app launch.
        if (_orchestrator is not null)
        {
            try
            {
                var state = _orchestrator.SaveState();
                WorldBuilderStateStore.Save(state, _brief);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[WorldBuildViewModel] failed to save pause state: {ex.Message}");
            }
        }

        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private bool CanCancel() => IsBusy && _cts is not null;

    /// <summary>
    /// After a successful build, navigate to the character-creation
    /// screen. The CC screen loads the freshly-created save, swaps the
    /// auto-created «Странник» for the player's chosen character
    /// (name/race/class/background + class-appropriate starter gear),
    /// persists, and then navigates into the game itself.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEnterGame))]
    private async Task EnterGameAsync()
    {
        // The save was created in StartAsync; re-read its id from the
        // SaveManager. (We could also stash the meta id on this VM, but
        // the SaveManager already has it as the most recent save.)
        var saves = _saveManager.ListSaves();
        if (saves.Count == 0)
        {
            Failed = true;
            FinalSummary = "Не удалось найти созданный сейв.";
            return;
        }
        // Route through character creation instead of jumping straight
        // into the game. The CC screen will navigate to the game itself
        // once the player picks a class / name / background. When this
        // build was launched for the host flow (issue #107), CC runs with
        // forHost=true → CompleteHostStartAsync starts the HostSession
        // and navigates to the host lobby instead of the single-player game.
        _shell.NavigateToCharacterCreation(saves[0].Id, _forHost);
        await Task.CompletedTask;
    }

    private bool CanEnterGame() => Completed;

    /// <summary>Back to the main menu (also used after failure).</summary>
    [RelayCommand]
    private void Back() => _shell.NavigateToMenu();

    // ─── Resume from saved state (issue #19) ─────────────────────────
    //
    // When the app launches with a worldbuilder-state.json file on disk
    // (written by a previous Cancel()), MainMenuViewModel prompts the
    // user to resume; if they accept, it loads the state file and calls
    // MainViewModel.NavigateToWorldBuildForResume(state, brief). The
    // shell constructs this VM with the brief, then calls
    // LoadStateForResume(state) BEFORE invoking StartCommand. The state
    // is stashed in _pendingLoadedState and applied to the orchestrator
    // inside StartAsync (after the orchestrator is constructed).

    private WorldBuilderState? _pendingLoadedState;

    /// <summary>
    /// Stage a saved <see cref="WorldBuilderState"/> to be loaded into
    /// the orchestrator on the next <see cref="StartAsync"/> call. Used
    /// by the resume-from-file flow (issue #19): the main menu detects
    /// a leftover <c>worldbuilder-state.json</c>, asks the user whether
    /// to resume, and if yes, navigates to this VM with the state. The
    /// VM then runs <see cref="StartAsync"/> which calls
    /// <see cref="WorldBuilderOrchestrator.LoadState"/> before
    /// <see cref="WorldBuilderOrchestrator.RunAsync"/> so the resumed
    /// run skips already-completed stages.
    /// </summary>
    /// <param name="state">Saved state (must not be null).</param>
    /// <param name="brief">The original world brief (so the resumed
    /// run's planner call — if planning hasn't completed yet — uses the
    /// same brief as the original).</param>
    public void LoadStateForResume(WorldBuilderState state, string brief)
    {
        _pendingLoadedState = state ?? throw new ArgumentNullException(nameof(state));
        // The brief is read-only (readonly field set in ctor); we can't
        // reassign it. The caller is expected to have constructed this
        // VM with the original brief already (MainMenu reads it from
        // the state file and passes it to the NavigateToWorldBuildForResume
        // helper, which forwards it to the ctor). Defensive check: if
        // the caller passed a different brief, log + ignore — using
        // the original ctor brief keeps things simple.
        if (!string.IsNullOrWhiteSpace(brief) && brief != _brief)
        {
            System.Diagnostics.Trace.WriteLine(
                "[WorldBuildViewModel] LoadStateForResume: brief differs from ctor brief — using ctor brief.");
        }
    }

    // ─── Pet-progress helpers (issue #76) ────────────────────────────

    /// <summary>
    /// Translate a <c>Stage == "pet"</c> progress event into an update
    /// on the matching <see cref="PetProgress"/> row. Matched by the
    /// delegation label embedded in the event's Label field — the
    /// orchestrator formats these as
    /// <list type="bullet">
    ///   <item><c>"Pet-агент: {label}"</c> (Active)</item>
    ///   <item><c>"Pet: {label} — готово"</c> (Done)</item>
    ///   <item><c>"Pet: {label} — ошибка"</c> (Error)</item>
    /// </list>
    /// so we extract the <c>{label}</c> portion and look it up by
    /// <see cref="PetDelegationProgress.Label"/>.
    /// </summary>
    private void UpdatePetProgress(WorldBuildProgress p)
    {
        var label = ExtractPetDelegationLabel(p.Label);
        if (label is null) return;
        var row = PetProgress.FirstOrDefault(r => r.Label == label);
        if (row is null) return;

        switch (p.Status)
        {
            case ProgressStatus.Active:
                row.SetStatus("running");
                row.Summary = null;
                row.Error = null;
                // Auto-expand the section on the first Active event so
                // the user immediately sees what's running.
                if (!IsPetSectionExpanded) IsPetSectionExpanded = true;
                break;

            case ProgressStatus.Done:
                row.SetStatus("done");
                row.Summary = p.Detail;
                row.Error = null;
                break;

            case ProgressStatus.Error:
                row.SetStatus("error");
                row.Error = p.Detail;
                row.Summary = null;
                break;

            case ProgressStatus.Skipped:
                // The orchestrator doesn't currently emit Skipped for the
                // pet stage (it just runs each delegation or fails it),
                // but handle it defensively: mark as done with no
                // summary so the row doesn't stay "running" forever.
                row.SetStatus("done");
                row.Summary = "(пропущено)";
                row.Error = null;
                break;
        }
    }

    /// <summary>
    /// Extract the underlying <c>PetDelegation.Label</c> from a
    /// pet-stage progress event's Label field. Returns null if the input
    /// doesn't match either of the orchestrator's two label formats.
    /// </summary>
    private static string? ExtractPetDelegationLabel(string? eventLabel)
    {
        if (string.IsNullOrEmpty(eventLabel)) return null;
        string? rest = null;
        if (eventLabel.StartsWith("Pet-агент: ", StringComparison.Ordinal))
            rest = eventLabel["Pet-агент: ".Length..];
        else if (eventLabel.StartsWith("Pet: ", StringComparison.Ordinal))
            rest = eventLabel["Pet: ".Length..];
        if (rest is null) return null;
        // Strip the trailing " — готово" / " — ошибка" suffix the Done /
        // Error events append. Use IndexOf (not Split) so a label
        // legitimately containing " — " keeps its earlier segment.
        var dashIdx = rest.IndexOf(" — ", StringComparison.Ordinal);
        if (dashIdx >= 0) rest = rest[..dashIdx];
        return rest.Trim();
    }
}
