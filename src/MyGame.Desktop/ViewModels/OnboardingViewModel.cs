using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Profile;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// First-run welcome wizard (issue #73). Three steps:
/// <list type="number">
///   <item><b>Nickname</b> — validate + persist via
///     <see cref="ProfileStore.Rename"/>. The user can't skip this step
///     (a valid nickname is required to play).</item>
///   <item><b>AI setup</b> — optional API key entry for the OpenAI-
///     compatible provider. «Пропустить» lets the user defer to the
///     Settings screen; «Далее» saves the key to
///     <see cref="Settings.Ai.ApiKey"/> via <see cref="SettingsStore.Update"/>.
///     A «Расширенные настройки» link opens the Settings screen for
///     BaseUrl / model / per-role overrides.</item>
///   <item><b>Ready</b> — short summary of what the user can do next
///     (build a world via AI, start a standard game, join a friend).
///     «Начать» navigates to the main menu.</item>
/// </list>
///
/// <para>
/// The wizard is shown by <see cref="MainViewModel.NavigateToMenu"/> when
/// <see cref="ProfileStore.ProfileExists"/> returns false (no
/// <c>profile.json</c> yet). On finishing step 1 the profile is created
/// (Rename calls GetOrCreate internally), so the next launch goes
/// straight to the menu.
/// </para>
/// <para>
/// Step transitions fire <see cref="OnStepChanged"/> so the view can
/// refresh its <c>IsStep1/2/3</c> visibility bindings.
/// </para>
/// </summary>
public partial class OnboardingViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;

    private int _step = 1;
    private string _nickname = string.Empty;
    private string? _nicknameError;
    private string? _apiKey;
    private string? _apiKeyError;

    public OnboardingViewModel(
        MainViewModel shell,
        ProfileStore profileStore,
        SettingsStore settingsStore)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        // Pre-fill the nickname with a random suggestion (NOT loaded
        // from a profile — we intentionally DON'T call GetOrCreate here
        // because that would create profile.json as a side effect, and
        // we want the first-run check (ProfileStore.ProfileExists) to
        // remain false until the user actually confirms their nickname
        // on step 1). The user can keep the suggestion or overwrite it.
        _nickname = ProfileStore.GenerateRandomNickname();

        Title = "Добро пожаловать";
    }

    // ─── Step state ──────────────────────────────────────────────────

    /// <summary>
    /// Current step (1, 2, or 3). Bound to the view's step visibility
    /// via <see cref="IsStep1"/> / <see cref="IsStep2"/> / <see cref="IsStep3"/>.
    /// </summary>
    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsStep1));
                OnPropertyChanged(nameof(IsStep2));
                OnPropertyChanged(nameof(IsStep3));
            }
        }
    }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;

    // ─── Step 1: nickname ─────────────────────────────────────────────

    /// <summary>
    /// Editable nickname. Pre-filled with the auto-generated profile
    /// nickname. Validated (2-20 chars, Latin/Cyrillic letters, digits,
    /// spaces, hyphens, underscores — see <see cref="Profile.ValidateNickname"/>)
    /// on «Далее».
    /// </summary>
    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value);
    }

    /// <summary>
    /// Inline validation error for the nickname. Null when the current
    /// value is valid or unchanged.
    /// </summary>
    public string? NicknameError
    {
        get => _nicknameError;
        private set => SetProperty(ref _nicknameError, value);
    }

    // ─── Step 2: AI key ───────────────────────────────────────────────

    /// <summary>
    /// Optional API key. Saved to <see cref="Settings.Ai.ApiKey"/> on
    /// «Далее». The user can skip this step (key can be added later in
    /// Settings).
    /// </summary>
    public string? ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    /// <summary>Inline error for the API key (currently unused — kept
    /// for future validation). Null when no error.</summary>
    public string? ApiKeyError
    {
        get => _apiKeyError;
        private set => SetProperty(ref _apiKeyError, value);
    }

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Step 1 «Далее»: validate + persist the nickname, then advance to
    /// step 2. On validation failure, sets <see cref="NicknameError"/>
    /// and stays on step 1.
    /// </summary>
    [RelayCommand]
    private void NextFromStep1()
    {
        var trimmed = (_nickname ?? string.Empty).Trim();
        if (!Profile.ValidateNickname(trimmed, out var error))
        {
            NicknameError = error;
            return;
        }
        try
        {
            // GetOrCreate creates profile.json on first call (the file
            // didn't exist before this — that's the whole point of the
            // first-run wizard). Rename then validates + applies the
            // user's chosen nickname. After this call, profile.json
            // exists on disk, so the next NavigateToMenu call will
            // skip the wizard.
            _profileStore.GetOrCreate();
            _profileStore.Rename(trimmed);
            Nickname = trimmed; // normalize (trim)
            NicknameError = null;
            Step = 2;
        }
        catch (Exception ex)
        {
            NicknameError = ex.Message;
        }
    }

    /// <summary>
    /// Step 2 «Пропустить»: skip API key entry (the user can add it
    /// later in Settings). Advance to step 3 without persisting.
    /// </summary>
    [RelayCommand]
    private void SkipStep2()
    {
        ApiKeyError = null;
        Step = 3;
    }

    /// <summary>
    /// Step 2 «Далее»: persist the API key (when non-empty) to
    /// <see cref="Settings.Ai.ApiKey"/> via <see cref="SettingsStore.Update"/>,
    /// then advance to step 3. An empty key is treated the same as
    /// «Пропустить» — the user can add it later.
    /// </summary>
    [RelayCommand]
    private void NextFromStep2()
    {
        try
        {
            var key = string.IsNullOrWhiteSpace(_apiKey) ? null : _apiKey.Trim();
            _settingsStore.Update(s => s with
            {
                Ai = s.Ai with { ApiKey = key },
            });
            ApiKey = key;
            ApiKeyError = null;
            Step = 3;
        }
        catch (Exception ex)
        {
            ApiKeyError = ex.Message;
        }
    }

    /// <summary>
    /// Step 2 «Расширенные настройки»: open the Settings screen so the
    /// user can configure BaseUrl, model, per-role overrides, etc. The
    /// wizard's state is preserved — when the user navigates back to the
    /// menu (via Settings → Сохранить or Отмена), NavigateToMenu sees
    /// the profile already exists and shows the menu (skipping the
    /// wizard). The user can re-enter the wizard only by deleting
    /// profile.json manually.
    /// </summary>
    /// <remarks>
    /// Practically, clicking this link abandons the wizard — the user
    /// ends up on the Settings screen, then on the menu. The wizard's
    /// step 3 (Ready) is a one-tap gateway from there. Acceptable UX
    /// for a first-run flow.
    /// </remarks>
    [RelayCommand]
    private void OpenSettings() => _shell.NavigateToSettings();

    /// <summary>
    /// Step 3 «Начать»: navigate to the main menu. By this point the
    /// profile has been created (step 1) and the API key optionally
    /// persisted (step 2).
    /// </summary>
    [RelayCommand]
    private void Finish() => _shell.NavigateToMenu();
}
