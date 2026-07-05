using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI;
using MyGame.Core.Profile;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Profile editor screen. Lets the user set their nickname + the
/// OpenAI-compatible AI connection (BaseUrl, ApiKey, Model,
/// Temperature). The MaxTokens field is included for completeness
/// (the AI client reads it from Settings).
/// </summary>
public partial class ProfileViewModel : ViewModelBase
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly MainViewModel _shell;

    // ─── Nickname ─────────────────────────────────────────────────────
    private string _nickname = string.Empty;

    // ─── AI settings ──────────────────────────────────────────────────
    private string _baseUrl = string.Empty;
    private string? _apiKey;
    private string _model = string.Empty;
    private double _temperature;
    private int _maxTokens;

    public ProfileViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        MainViewModel shell)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        // Pre-fill the form from the on-disk state.
        try
        {
            var profile = _profileStore.GetOrCreate();
            _nickname = profile.Nickname;
        }
        catch (Exception ex)
        {
            _nickname = string.Empty;
            ErrorMessage = $"Не удалось загрузить профиль: {ex.Message}";
        }

        try
        {
            var settings = _settingsStore.Load();
            _baseUrl = settings.Ai.BaseUrl;
            _apiKey = settings.Ai.ApiKey;
            _model = settings.Ai.Model;
            _temperature = settings.Ai.Temperature;
            _maxTokens = settings.Ai.MaxTokens;
        }
        catch (Exception ex)
        {
            // Defaults would still load if SettingsStore.Load failed,
            // but be defensive anyway.
            _baseUrl = new AiSettings().BaseUrl;
            _model = new AiSettings().Model;
            _temperature = new AiSettings().Temperature;
            _maxTokens = new AiSettings().MaxTokens;
            _apiKey = null;
            ErrorMessage = $"Не удалось загрузить настройки: {ex.Message}";
        }
    }

    // ─── Bindable properties ─────────────────────────────────────────

    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value);
    }

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

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Persist the nickname + AI settings, then return to the main menu.
    /// Catches validation/disk errors and surfaces them inline rather
    /// than crashing.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            // 1) Validate + rename profile. The Core type is
            // MyGame.Core.Profile.Profile (same name as its namespace).
            if (!MyGame.Core.Profile.Profile.ValidateNickname(_nickname, out var nickError))
            {
                ErrorMessage = nickError;
                return;
            }
            _profileStore.Rename(_nickname.Trim());

            // 2) Patch AI settings via the Update helper (preserves
            //    other settings like MaxToolIterations, AutosaveInterval,
            //    etc.).
            var patchedAi = new AiSettings
            {
                BaseUrl = string.IsNullOrWhiteSpace(_baseUrl) ? new AiSettings().BaseUrl : _baseUrl.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(_apiKey) ? null : _apiKey,
                Model = string.IsNullOrWhiteSpace(_model) ? new AiSettings().Model : _model.Trim(),
                Temperature = _temperature,
                MaxTokens = Math.Max(64, _maxTokens),
            };
            _settingsStore.Update(s => s with { Ai = patchedAi });

            await Task.CompletedTask;
            _shell.NavigateToMenu();
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
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
