using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
                ai, world, prompts, tools, petDelegations: _petDelegations);

            // Progress marshals to UI thread via Avalonia's dispatcher.
            var progress = new Progress<WorldBuildProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Percent = p.Percent;
                    StageLabel = p.Label;
                    StageDetail = p.Detail;
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
}
