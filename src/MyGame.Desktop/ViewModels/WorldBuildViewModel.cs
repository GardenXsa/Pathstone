using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// World-build screen. Runs the WorldBuilderOrchestrator in the
/// background and shows live progress (stage / label / percent / detail)
/// to the user. Supports cancellation. On success, creates a save with the
/// opening narration as the first log entry and navigates to the game
/// screen in single-player mode.
/// </summary>
public partial class WorldBuildViewModel : ViewModelBase
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly SaveManager _saveManager;
    private readonly MainViewModel _shell;

    private readonly string _brief;
    private readonly IReadOnlyCollection<MyGame.Core.AI.Agents.PetDelegation>? _petDelegations;
    private CancellationTokenSource? _cts;
    private WorldBuilderResult? _result;

    public WorldBuildViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        SaveManager saveManager,
        MainViewModel shell,
        string brief,
        IReadOnlyCollection<MyGame.Core.AI.Agents.PetDelegation>? petDelegations = null)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _brief = brief ?? string.Empty;
        _petDelegations = petDelegations;

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

            // Build a fresh world + AI client + orchestrator.
            var world = DefaultWorld.Create(seed: (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var ai = new AiClient(settings.Ai);
            var prompts = ServiceHost.Resolve<PromptLoader>();
            var tools = new ToolRegistry(world);

            var orchestrator = new WorldBuilderOrchestrator(
                ai, world, prompts, tools, petDelegations: _petDelegations,
                aiSettings: settings.Ai);

            // Progress marshals to UI thread via Avalonia's dispatcher.
            var progress = new Progress<WorldBuildProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
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
                new WorldPlanRequest { Brief = _brief },
                progress,
                _cts.Token);

            if (_result.Kind == WorldBuilderResultKind.Complete)
            {
                // Persist the built world as a save, with the opening
                // narration as the first log entry so the game screen
                // shows it immediately.
                var title = _result.Plan?.Title ?? "Новый мир";
                var meta = _saveManager.CreateSave(title, world, profile.Id);

                if (!string.IsNullOrWhiteSpace(_result.OpeningNarration))
                {
                    var log = new[]
                    {
                        LogEntry.Narrative(_result.OpeningNarration, authorId: null),
                    };
                    _saveManager.SaveAll(meta.Id, world, meta, log);
                }

                FinalSummary = _result.Summary ?? "Мир готов.";
                Completed = true;
                Percent = 100;
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
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanStart() => !IsBusy && !Completed;

    /// <summary>Cancel an in-flight build.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
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
        // once the player picks a class / name / background.
        _shell.NavigateToCharacterCreation(saves[0].Id);
        await Task.CompletedTask;
    }

    private bool CanEnterGame() => Completed;

    /// <summary>Back to the main menu (also used after failure).</summary>
    [RelayCommand]
    private void Back() => _shell.NavigateToMenu();

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
