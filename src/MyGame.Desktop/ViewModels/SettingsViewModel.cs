using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI;
using MyGame.Core.Profile;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Settings screen. Edits the OpenAI-compatible AI connection (BaseUrl,
/// ApiKey, Model, Temperature, MaxTokens) plus per-role model overrides
/// and the context-window threshold (issue #25 + #26). The nickname is
/// NOT edited here — it lives inline on the main menu (no separate
/// profile screen, since the nickname is the only profile field and
/// there's no auth).
///
/// <para>
/// <b>Tabbed dialog (issue #78):</b> the screen exposes four groups of
/// settings — AI (existing), Оформление (theme + accent + animations,
/// issue #47), Мультиплеер (server host/port + auto-reconnect), and
/// Продвинутое (autosave, max tool iterations, stream-narrative). All
/// four persist on <see cref="SaveCommand"/>; the Cancel command
/// discards every tab's edits.
/// </para>
///
/// <para>
/// <b>Live theme preview (issue #47):</b> changing <see cref="ThemeMode"/>,
/// <see cref="AccentColor"/>, or <see cref="EnableAnimations"/> calls
/// <see cref="ThemeService.ApplyTheme"/> immediately so the user sees the
/// effect without saving. If they hit Cancel, the previous settings are
/// re-applied (because Cancel doesn't persist, but the live preview has
/// already mutated the running app — we re-apply from the on-disk
/// settings to revert).
/// </para>
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

    // ─── Appearance (issue #47) ───────────────────────────────────────
    private string _themeMode = "Dark";
    private string _accentColor = "Amber";
    private bool _enableAnimations = true;

    // ─── Multiplayer (issue #78) ──────────────────────────────────────
    private string? _lastServerHost;
    private int? _lastServerPort;
    private bool _autoReconnect;

    // ─── Advanced (issue #78) ─────────────────────────────────────────
    private int _autosaveIntervalSeconds;
    private int _maxToolIterations;
    private bool _streamNarrative;

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

            // Appearance
            _themeMode = string.IsNullOrWhiteSpace(settings.ThemeMode) ? "Dark" : settings.ThemeMode;
            _accentColor = string.IsNullOrWhiteSpace(settings.AccentColor) ? "Amber" : settings.AccentColor;
            _enableAnimations = settings.EnableAnimations;

            // Multiplayer
            _lastServerHost = settings.LastServerHost;
            _lastServerPort = settings.LastServerPort;
            _autoReconnect = settings.AutoReconnect;

            // Advanced
            _autosaveIntervalSeconds = settings.AutosaveIntervalSeconds;
            _maxToolIterations = settings.MaxToolIterations;
            _streamNarrative = settings.StreamNarrative;
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

            var s = new Settings();
            _themeMode = s.ThemeMode;
            _accentColor = s.AccentColor;
            _enableAnimations = s.EnableAnimations;
            _autoReconnect = s.AutoReconnect;
            _autosaveIntervalSeconds = s.AutosaveIntervalSeconds;
            _maxToolIterations = s.MaxToolIterations;
            _streamNarrative = s.StreamNarrative;
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

    // ─── Appearance (issue #47) ──────────────────────────────────────

    /// <summary>
    /// "Dark", "Light", or "System". Changing this re-applies the theme
    /// immediately (live preview) — the user sees the swap before they
    /// hit Save.
    /// </summary>
    public string ThemeMode
    {
        get => _themeMode;
        set
        {
            if (SetProperty(ref _themeMode, value))
                ApplyLiveTheme();
        }
    }

    /// <summary>
    /// Accent preset name (Indigo / Emerald / Amber / Rose / Cyan /
    /// Violet). Changing this re-applies the accent immediately.
    /// </summary>
    public string AccentColor
    {
        get => _accentColor;
        set
        {
            if (SetProperty(ref _accentColor, value))
                ApplyLiveTheme();
        }
    }

    /// <summary>
    /// Whether transitions/animations play across the app. Toggling
    /// adds/removes the Window's <c>Anim</c> class via ThemeService,
    /// turning every <c>Window.Anim</c>-scoped transition style on/off.
    /// </summary>
    public bool EnableAnimations
    {
        get => _enableAnimations;
        set
        {
            if (SetProperty(ref _enableAnimations, value))
                ApplyLiveTheme();
        }
    }

    /// <summary>
    /// Read-only list of the 6 accent preset names. Bound to the row of
    /// color-swatch buttons in the «Оформление» tab; clicking a swatch
    /// sets <see cref="AccentColor"/> via <see cref="SelectAccentCommand"/>.
    /// </summary>
    public IReadOnlyList<string> AccentPresets => ThemeService.AccentPresetNames;

    /// <summary>
    /// Hex color for a given accent preset name. Used by the view's
    /// accent-swatch buttons (Background binding) so each swatch shows
    /// its actual color.
    /// </summary>
    public string AccentHex(string name) => ThemeService.GetAccentHex(name);

    /// <summary>
    /// Apply the live (in-memory) theme values to the running app. Used
    /// by the property setters above for live preview. ThemeService is
    /// idempotent so calling it on every keystroke is fine.
    /// </summary>
    private void ApplyLiveTheme()
    {
        try
        {
            ThemeService.ApplyTheme(_themeMode ?? "Dark", _accentColor ?? "Indigo", _enableAnimations);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[SettingsViewModel] ApplyLiveTheme failed: {ex}");
        }
    }

    /// <summary>
    /// Apply the accent preset matching the given name and update the
    /// bound AccentColor property. Bound to each swatch button in the
    /// appearance tab.
    /// </summary>
    [RelayCommand]
    private void SelectAccent(string? name)
    {
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Click);
        if (string.IsNullOrWhiteSpace(name)) return;
        AccentColor = name;
    }

    // ─── Multiplayer (issue #78) ─────────────────────────────────────

    /// <summary>
    /// Last server host the user connected to (for the "Recent servers"
    /// dropdown). Null if the user has never joined a remote host.
    /// </summary>
    public string? LastServerHost
    {
        get => _lastServerHost;
        set => SetProperty(ref _lastServerHost, value);
    }

    /// <summary>
    /// Last server port. Null if the user has never joined a remote host.
    /// </summary>
    public int? LastServerPort
    {
        get => _lastServerPort;
        set => SetProperty(ref _lastServerPort, value);
    }

    /// <summary>
    /// Whether the multiplayer client should auto-reconnect on an
    /// unexpected WebSocket drop. Default false.
    /// </summary>
    public bool AutoReconnect
    {
        get => _autoReconnect;
        set => SetProperty(ref _autoReconnect, value);
    }

    // ─── Advanced (issue #78) ────────────────────────────────────────

    /// <summary>
    /// Autosave cadence in seconds. 0 = disabled (user must save
    /// manually). Default 120 (every 2 minutes).
    /// </summary>
    public int AutosaveIntervalSeconds
    {
        get => _autosaveIntervalSeconds;
        set => SetProperty(ref _autosaveIntervalSeconds, value);
    }

    /// <summary>
    /// Hard cap on tool-call iterations per GM turn. Stops a misbehaving
    /// model from looping spawn-NPC forever. Default 8.
    /// </summary>
    public int MaxToolIterations
    {
        get => _maxToolIterations;
        set => SetProperty(ref _maxToolIterations, value);
    }

    /// <summary>
    /// Whether narrative events stream into the UI as they're generated
    /// (true) or only after the GM turn completes (false).
    /// </summary>
    public bool StreamNarrative
    {
        get => _streamNarrative;
        set => SetProperty(ref _streamNarrative, value);
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
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Click);
        if (preset is null) return;
        BaseUrl = preset.BaseUrl;
        Model = preset.Model;
        if (preset.ClearsApiKey)
            ApiKey = null;
    }

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Persist every tab's settings (AI + appearance + multiplayer +
    /// advanced), then return to the main menu. Catches disk errors and
    /// surfaces them inline rather than crashing.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Chime);
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

            // Clamp autosave interval to a sane range — 0 (disabled) to
            // 3600 (hour). Negative values are coerced to 0.
            var clampedAutosave = Math.Max(0, Math.Min(3600, _autosaveIntervalSeconds));
            // Clamp MaxToolIterations to 1..50 — too small and the GM
            // can't finish a turn; too large and a misbehaving model
            // could burn the whole request budget.
            var clampedIterations = Math.Max(1, Math.Min(50, _maxToolIterations));

            // Server port range — 1..65535. Null is preserved (no last
            // server). 0 is treated as null.
            int? clampedPort = _lastServerPort is int p && p > 0 && p <= 65535 ? p : null;

            _settingsStore.Update(s => s with
            {
                Ai = patchedAi,
                MaxContextTokens = clampedContext,
                ThemeMode = _themeMode,
                AccentColor = _accentColor,
                EnableAnimations = _enableAnimations,
                LastServerHost = string.IsNullOrWhiteSpace(_lastServerHost) ? null : _lastServerHost.Trim(),
                LastServerPort = clampedPort,
                AutoReconnect = _autoReconnect,
                AutosaveIntervalSeconds = clampedAutosave,
                MaxToolIterations = clampedIterations,
                StreamNarrative = _streamNarrative,
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

    /// <summary>
    /// Discard changes and return to the menu. Live-preview theme
    /// changes are reverted by re-applying the on-disk settings (the
    /// user's last saved state).
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.PageTurn);
        // Revert live-preview theme changes by re-applying the on-disk
        // settings. If the load fails, fall back to defaults so the UI
        // is at least in a known state.
        try { ThemeService.ApplyFromSettings(_settingsStore.Load()); }
        catch { ThemeService.ApplyTheme("Dark", "Indigo", enableAnimations: true); }
        _shell.NavigateToMenu();
    }
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
