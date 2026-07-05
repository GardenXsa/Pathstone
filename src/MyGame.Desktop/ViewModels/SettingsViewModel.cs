using System;
using System.Collections.Generic;
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

    // ─── Provider presets (issue #27) ───────────────────────────────
    //
    // The presets list is bound to a row of buttons in the settings
    // view. Clicking a preset fills in BaseUrl + Model (+ ApiKey for
    // cloud providers) so the user doesn't have to look up the endpoint
    // URLs for OpenAI / DeepSeek / Ollama / llama.cpp. The presets are
    // defined statically here (no settings file round-trip) — they
    // represent known provider configurations.

    /// <summary>
    /// Read-only list of provider presets shown in the settings UI
    /// (issue #27). Each preset is a <see cref="PresetCommand"/> relay
    /// that fills in BaseUrl + Model (+ ApiKey when
    /// <see cref="AiPreset.ClearsApiKey"/> is true).
    /// </summary>
    public IReadOnlyList<AiPreset> Presets { get; } = new[]
    {
        new AiPreset("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini",
            ClearsApiKey: false,
            Tooltip: "OpenAI — облачный API, требуется ключ."),
        new AiPreset("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat",
            ClearsApiKey: false,
            Tooltip: "DeepSeek — облачный API, требуется ключ."),
        new AiPreset("Ollama (локально)", "http://localhost:11434/v1", "llama3.1:8b",
            ClearsApiKey: true,
            Tooltip: "Ollama — локальный сервер, ключ не нужен. Сначала установите Ollama и выполните `ollama pull llama3.1:8b`."),
        new AiPreset("llama.cpp", "http://localhost:8080/v1", "local-model",
            ClearsApiKey: true,
            Tooltip: "llama.cpp server — локальный сервер, ключ не нужен. Запустите сервер с флагом --host 0.0.0.0."),
    };

    /// <summary>
    /// Apply a preset: set BaseUrl + Model from the preset, and clear
    /// ApiKey when the preset is for a local provider (Ollama /
    /// llama.cpp). Cloud-provider presets (OpenAI, DeepSeek) leave the
    /// existing ApiKey intact so the user doesn't have to re-enter it
    /// when switching between cloud providers.
    /// </summary>
    /// <param name="preset">The preset to apply. Must not be null.</param>
    [RelayCommand]
    private void ApplyPreset(AiPreset? preset)
    {
        if (preset is null) return;
        BaseUrl = preset.BaseUrl;
        Model = preset.Model;
        if (preset.ClearsApiKey)
            ApiKey = null;
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

/// <summary>
/// One AI-provider preset for the settings UI (issue #27). Pairs a
/// human-readable label (shown on the preset button) with a BaseUrl +
/// Model + a flag for whether the preset should clear the existing
/// ApiKey (local providers don't use one).
/// </summary>
public sealed record AiPreset(
    string Label,
    string BaseUrl,
    string Model,
    bool ClearsApiKey = false,
    string? Tooltip = null);
