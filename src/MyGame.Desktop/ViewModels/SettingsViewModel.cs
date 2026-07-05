using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI;
using MyGame.Core.Profile;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Settings screen. Edits the OpenAI-compatible AI connection (BaseUrl,
/// ApiKey, Model, Temperature, MaxTokens) plus per-role model overrides
/// and the context-window threshold (issue #25 + #26). The nickname is
/// NOT edited here — it lives inline on the main menu (no separate
/// profile screen, since the nickname is the only profile field and
/// there's no auth).
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly MainViewModel _shell;

    // ─── AI settings ──────────────────────────────────────────────────
    private string _baseUrl = string.Empty;
    private string? _apiKey;
    private string _model = string.Empty;
    private double _temperature;
    private int _maxTokens;

    // ─── Per-role model overrides (issue #26) ─────────────────────────
    // Each is OPTIONAL — when blank (null/empty), the role falls back to
    // the main Model. The settings file persists nulls for missing fields
    // so existing settings.json files load fine.
    private string? _plannerModel;
    private string? _gmModel;
    private string? _narratorModel;
    private string? _petModel;

    // ─── Context-window threshold (issue #25) ─────────────────────────
    private int _maxContextTokens;

    public SettingsViewModel(
        SettingsStore settingsStore,
        MainViewModel shell)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        // Pre-fill the form from the on-disk state.
        try
        {
            var settings = _settingsStore.Load();
            _baseUrl = settings.Ai.BaseUrl;
            _apiKey = settings.Ai.ApiKey;
            _model = settings.Ai.Model;
            _temperature = settings.Ai.Temperature;
            _maxTokens = settings.Ai.MaxTokens;
            _plannerModel = settings.Ai.PlannerModel;
            _gmModel = settings.Ai.GMModel;
            _narratorModel = settings.Ai.NarratorModel;
            _petModel = settings.Ai.PetModel;
            _maxContextTokens = settings.MaxContextTokens;
        }
        catch (Exception ex)
        {
            // Defaults would still load if SettingsStore.Load failed,
            // but be defensive anyway.
            var defaults = new AiSettings();
            _baseUrl = defaults.BaseUrl;
            _model = defaults.Model;
            _temperature = defaults.Temperature;
            _maxTokens = defaults.MaxTokens;
            _apiKey = null;
            _plannerModel = null;
            _gmModel = null;
            _narratorModel = null;
            _petModel = null;
            _maxContextTokens = new Settings().MaxContextTokens;
            ErrorMessage = $"Не удалось загрузить настройки: {ex.Message}";
        }
    }

    // ─── Bindable properties ─────────────────────────────────────────

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string? ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    public double Temperature
    {
        get => _temperature;
        set => SetProperty(ref _temperature, value);
    }

    public int MaxTokens
    {
        get => _maxTokens;
        set => SetProperty(ref _maxTokens, value);
    }

    /// <summary>
    /// Optional model override for the world-builder planner role
    /// (issue #26). Blank = use the main <see cref="Model"/>.
    /// </summary>
    public string? PlannerModel
    {
        get => _plannerModel;
        set => SetProperty(ref _plannerModel, value);
    }

    /// <summary>
    /// Optional model override for the in-session Game Master role
    /// (issue #26). Blank = use the main <see cref="Model"/>.
    /// </summary>
    public string? GMModel
    {
        get => _gmModel;
        set => SetProperty(ref _gmModel, value);
    }

    /// <summary>
    /// Optional model override for the world-builder narrator role
    /// (issue #26). Blank = use the main <see cref="Model"/>.
    /// </summary>
    public string? NarratorModel
    {
        get => _narratorModel;
        set => SetProperty(ref _narratorModel, value);
    }

    /// <summary>
    /// Optional model override for the pet-agent role (issue #26).
    /// Blank = use the main <see cref="Model"/>.
    /// </summary>
    public string? PetModel
    {
        get => _petModel;
        set => SetProperty(ref _petModel, value);
    }

    /// <summary>
    /// Estimated-token threshold for early history summarization
    /// (issue #25). When the rough token count of (system prompt +
    /// summary + history + world state) exceeds 80% of this value, the
    /// GM folds the oldest half of the history into a short text recap.
    /// </summary>
    public int MaxContextTokens
    {
        get => _maxContextTokens;
        set => SetProperty(ref _maxContextTokens, value);
    }

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Persist the AI settings, then return to the main menu. Catches
    /// disk errors and surfaces them inline rather than crashing.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            // Patch AI settings via the Update helper (preserves other
            // settings like MaxToolIterations, AutosaveInterval, etc.).
            // Per-role model overrides are persisted as null when blank
            // so GetModelForRole falls back to Model cleanly.
            var patchedAi = new AiSettings
            {
                BaseUrl = string.IsNullOrWhiteSpace(_baseUrl) ? new AiSettings().BaseUrl : _baseUrl.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(_apiKey) ? null : _apiKey,
                Model = string.IsNullOrWhiteSpace(_model) ? new AiSettings().Model : _model.Trim(),
                Temperature = _temperature,
                MaxTokens = Math.Max(64, _maxTokens),
                PlannerModel = string.IsNullOrWhiteSpace(_plannerModel) ? null : _plannerModel.Trim(),
                GMModel = string.IsNullOrWhiteSpace(_gmModel) ? null : _gmModel.Trim(),
                NarratorModel = string.IsNullOrWhiteSpace(_narratorModel) ? null : _narratorModel.Trim(),
                PetModel = string.IsNullOrWhiteSpace(_petModel) ? null : _petModel.Trim(),
            };
            // Clamp MaxContextTokens to a sane range — too small and
            // summarization fires on every turn; too large and the model
            // runs out of context. 1024 is the floor (just enough for the
            // system prompt + a couple turns); 200000 is the ceiling
            // (covers even 200k-context models like gpt-4-turbo-long).
            var clampedContext = Math.Max(1024, Math.Min(200000, _maxContextTokens));
            _settingsStore.Update(s => s with
            {
                Ai = patchedAi,
                MaxContextTokens = clampedContext,
            });

            await Task.CompletedTask;
            _shell.NavigateToMenu();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось сохранить: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Discard changes and return to the menu.</summary>
    [RelayCommand]
    private void Cancel() => _shell.NavigateToMenu();
}
