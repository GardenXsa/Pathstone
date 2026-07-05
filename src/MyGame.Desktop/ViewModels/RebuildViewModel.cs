using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Rebuild dialog (issue #23). Lets the user regenerate parts of an
/// existing world without starting over. Surfaces checkboxes for the
/// rebuild categories (locations / NPC / items / narration / full
/// rebuild), an optional rebuild brief, and a Run button that calls
/// <see cref="WorldBuilderOrchestrator.RebuildAsync"/> on a background
/// task. On success, navigates into the game with the rebuilt save.
/// </summary>
/// <remarks>
/// <b>Partial vs full rebuild (issue #23 conventions):</b>
/// <list type="bullet">
///   <item>Partial flags (Locations / Npcs / Items / Narration) are
///     ADDITIVE — the existing world is preserved, the AI generates
///     additional content in the selected categories, and the
///     committer (idempotent) commits only the new entries. The
///     player's character + progress survive.</item>
///   <item><see cref="FullRebuild"/> is the only destructive option —
///     it discards all entities and regenerates from scratch. The
///     dialog shows a prominent warning when this is checked.</item>
/// </list>
/// </remarks>
public partial class RebuildViewModel : ViewModelBase
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly SaveManager _saveManager;
    private readonly MainViewModel _shell;
    private readonly string _saveId;

    private bool _rebuildLocations;
    private bool _rebuildNpcs;
    private bool _rebuildItems;
    private bool _rebuildNarration = true; // narration-only is a cheap, common case
    private bool _fullRebuild;
    private string _brief = string.Empty;
    private string? _progressLabel;
    private int _progressPercent;
    private bool _isRunning;
    private string? _resultSummary;

    public RebuildViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        SaveManager saveManager,
        MainViewModel shell,
        string saveId)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _saveId = saveId ?? string.Empty;
        Title = "Перестроить мир";

        // Surface the save's name in the title bar so the user knows
        // which world they're rebuilding.
        try
        {
            var meta = _saveManager.LoadMeta(_saveId);
            if (meta is not null && !string.IsNullOrWhiteSpace(meta.Name))
                Title = $"Перестроить: {meta.Name}";
        }
        catch { /* fall back to generic title */ }
    }

    /// <summary>Regenerate locations (additive — keeps existing).</summary>
    public bool RebuildLocations
    {
        get => _rebuildLocations;
        set => SetProperty(ref _rebuildLocations, value);
    }

    /// <summary>Regenerate population (additive — keeps existing NPCs).</summary>
    public bool RebuildNpcs
    {
        get => _rebuildNpcs;
        set => SetProperty(ref _rebuildNpcs, value);
    }

    /// <summary>Regenerate loot/items (additive — keeps existing).</summary>
    public bool RebuildItems
    {
        get => _rebuildItems;
        set => SetProperty(ref _rebuildItems, value);
    }

    /// <summary>Re-write the opening narration from the current state.</summary>
    public bool RebuildNarration
    {
        get => _rebuildNarration;
        set => SetProperty(ref _rebuildNarration, value);
    }

    /// <summary>
    /// Full rebuild — discard all entities and regenerate from scratch.
    /// The only destructive option. When checked, the partial flags are
    /// ignored (the orchestrator runs the full pipeline regardless).
    /// </summary>
    public bool FullRebuild
    {
        get => _fullRebuild;
        set
        {
            if (SetProperty(ref _fullRebuild, value))
            {
                // Reflect the new state in derived flags.
                OnPropertyChanged(nameof(IsPartialDisabled));
                OnPropertyChanged(nameof(IsFullRebuildWarningVisible));
                RebuildCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Optional rebuild brief. When empty, the orchestrator falls back
    /// to the original world's theme/setting (stashed on World.Flags).
    /// </summary>
    public string Brief
    {
        get => _brief;
        set => SetProperty(ref _brief, value);
    }

    /// <summary>Live progress label (bound to a TextBlock under the bar).</summary>
    public string? ProgressLabel
    {
        get => _progressLabel;
        private set => SetProperty(ref _progressLabel, value);
    }

    /// <summary>0–100 percent complete for the current rebuild.</summary>
    public int ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    /// <summary>True while the rebuild task is in flight.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
                RebuildCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Final summary (success message or error).</summary>
    public string? ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    /// <summary>
    /// True when the partial checkboxes should be disabled (because
    /// <see cref="FullRebuild"/> is checked — the orchestrator ignores
    /// them in that case anyway, so we grey them out for clarity).
    /// </summary>
    public bool IsPartialDisabled => FullRebuild || IsRunning;

    /// <summary>True when the destructive full-rebuild warning should show.</summary>
    public bool IsFullRebuildWarningVisible => FullRebuild;

    /// <summary>
    /// Run the rebuild. Validates that at least one category is selected
    /// (or full rebuild), constructs the orchestrator bound to the
    /// loaded world, calls <see cref="WorldBuilderOrchestrator.RebuildAsync"/>,
    /// and on success navigates into the game with the rebuilt save.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRebuild))]
    private async Task RebuildAsync()
    {
        if (!FullRebuild && !RebuildLocations && !RebuildNpcs && !RebuildItems && !RebuildNarration)
        {
            ResultSummary = "Выберите хотя бы одну категорию для перестройки.";
            return;
        }

        IsRunning = true;
        ResultSummary = null;
        ProgressLabel = "Подготовка…";
        ProgressPercent = 0;

        try
        {
            var settings = _settingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.Ai.ApiKey))
            {
                ResultSummary = "Не задан API-ключ. Откройте Настройки AI и введите ключ.";
                return;
            }

            // The orchestrator loads the world itself via the SaveManager
            // (we pass the SaveManager into RebuildAsync). We need a
            // throwaway World for the orchestrator's ctor — the actual
            // world is loaded inside RebuildAsync.
            var placeholderWorld = new MyGame.Core.World.World();
            var ai = new AiClient(settings.Ai);
            var prompts = ServiceHost.Resolve<PromptLoader>();
            var tools = new ToolRegistry(placeholderWorld);

            var orchestrator = new WorldBuilderOrchestrator(
                ai, placeholderWorld, prompts, tools, aiSettings: settings.Ai);

            var options = new WorldBuilderOrchestrator.RebuildOptions
            {
                Locations = RebuildLocations,
                Npcs = RebuildNpcs,
                Items = RebuildItems,
                Narration = RebuildNarration,
                FullRebuild = FullRebuild,
            };

            var progress = new Progress<WorldBuildProgress>(p =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressLabel = p.Label;
                    ProgressPercent = p.Percent;
                }));

            var result = await Task.Run(() => orchestrator.RebuildAsync(
                _saveManager, _saveId, options,
                brief: string.IsNullOrWhiteSpace(Brief) ? null : Brief,
                log: null,
                progress: progress,
                ct: default));

            if (result.Success)
            {
                ResultSummary = result.Summary ?? "Перестройка завершена.";
                ProgressPercent = 100;
                // Navigate into the game with the rebuilt save.
                await _shell.NavigateToGame(_saveId);
            }
            else
            {
                ResultSummary = "Ошибка: " + (result.Error ?? "неизвестная ошибка");
            }
        }
        catch (System.Exception ex)
        {
            ResultSummary = "Ошибка: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanRebuild() => !IsRunning;

    /// <summary>Cancel — back to the main menu without rebuilding.</summary>
    [RelayCommand]
    private void Back() => _shell.NavigateToMenu();
}
