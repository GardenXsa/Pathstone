using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.World;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Landing screen after app startup. Shows an inline nickname editor in
/// the top-right corner (no separate profile screen — nickname is the
/// only profile field, no auth, no settings mixed in) and the primary
/// buttons:
/// <list type="bullet">
///   <item>«Новая игра» — single-player, creates a save with DefaultWorld.</item>
///   <item>«Создать мир (AI)» — open the AI world-build flow.</item>
///   <item>«Хост игры» — start a multiplayer host.</item>
///   <item>«Подключиться» — join a multiplayer host.</item>
///   <item>«Загрузить» — pick from existing saves.</item>
///   <item>«Настройки» — edit AI settings only.</item>
/// </list>
/// </summary>
public partial class MainMenuViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;
    private readonly SaveManager _saveManager;
    private readonly ProfileStore _profileStore;

    // The nickname TextBox binds two-way to this. Editing + Enter (or
    // focus loss) calls SaveNicknameCommand, which validates + persists
    // via ProfileStore. No separate profile screen — the nickname is the
    // only profile field, and there's no auth to wrap it in.
    private string _nickname = string.Empty;
    private string? _nicknameError;

    public MainMenuViewModel(MainViewModel shell, SaveManager saveManager, ProfileStore profileStore)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));

        try { _nickname = _profileStore.GetOrCreate().Nickname; }
        catch { _nickname = "Игрок"; }
    }

    /// <summary>
    /// Disables buttons while a navigation action is in flight (e.g.
    /// creating a new save takes a moment). Hides the inherited
    /// ViewModelBase.IsBusy setter — we want this one to be public-set
    /// from within this VM only.
    /// </summary>
    public new bool IsBusy
    {
        get => base.IsBusy;
        private set => base.IsBusy = value;
    }

    /// <summary>
    /// Editable nickname. Bound two-way to the top-right TextBox. The
    /// user can type freely; pressing Enter or losing focus triggers
    /// <see cref="SaveNicknameCommand"/> which validates + persists.
    /// </summary>
    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value);
    }

    /// <summary>
    /// Inline validation error for the nickname (shown under the TextBox).
    /// Null when the current value is valid or unchanged.
    /// </summary>
    public string? NicknameError
    {
        get => _nicknameError;
        private set => SetProperty(ref _nicknameError, value);
    }

    /// <summary>
    /// List of existing saves, populated when the user expands
    /// «Загрузить». Each item is wrapped in a small VM that exposes
    /// a Load command + display fields.
    /// </summary>
    public ObservableCollection<SaveSlotViewModel> Saves { get; } = new();

    private bool _savesLoaded;
    /// <summary>
    /// True after the first <see cref="RefreshSavesCommand"/> call —
    /// drives whether the saves list panel is visible.
    /// </summary>
    public bool SavesLoaded
    {
        get => _savesLoaded;
        private set => SetProperty(ref _savesLoaded, value);
    }

    // ─── Commands ───────────────────────────────────────────────────

    /// <summary>
    /// «Новая игра» — create a DefaultWorld save and navigate to the
    /// game screen in single-player mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private async Task NewGameAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var world = DefaultWorld.Create();
            var profile = _profileStore.GetOrCreate();
            var meta = _saveManager.CreateSave("Новая игра", world, profile.Id);
            await _shell.NavigateToGame(meta.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось создать игру: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// «Создать мир» — open the AI world-build flow. The user types a
    /// brief, then the orchestrator plans + commits + narrates a custom
    /// world. Requires an AI API key (set in Настройки → AI settings).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void CreateWorld() => _shell.NavigateToWorldBrief();

    /// <summary>«Хост игры» — open the multiplayer host setup screen.</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void HostGame() => _shell.NavigateToHost();

    /// <summary>«Подключиться» — open the multiplayer join screen.</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void JoinGame() => _shell.NavigateToJoin();

    /// <summary>
    /// «Настройки» — open the settings screen (AI settings only; the
    /// nickname is edited inline on the main menu).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void OpenSettings() => _shell.NavigateToSettings();

    /// <summary>
    /// Validate + persist the current nickname. Called from the TextBox's
    /// KeyDown (Enter) or LostFocus. On validation failure, sets
    /// <see cref="NicknameError"/> and leaves the TextBox content intact
    /// so the user can fix it. On success, clears the error.
    /// </summary>
    [RelayCommand]
    private void SaveNickname()
    {
        var trimmed = (_nickname ?? string.Empty).Trim();
        if (!MyGame.Core.Profile.Profile.ValidateNickname(trimmed, out var error))
        {
            NicknameError = error;
            return;
        }
        try
        {
            _profileStore.Rename(trimmed);
            Nickname = trimmed; // normalize (trim)
            NicknameError = null;
        }
        catch (Exception ex)
        {
            NicknameError = ex.Message;
        }
    }

    /// <summary>
    /// «Загрузить» — toggle the saves-list panel. The list is refreshed
    /// every time the panel is opened so newly-created saves appear.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void ToggleLoad()
    {
        if (SavesLoaded)
        {
            SavesLoaded = false;
            return;
        }
        RefreshSaves();
    }

    /// <summary>Reload the saves list from disk.</summary>
    [RelayCommand]
    private void RefreshSaves()
    {
        Saves.Clear();
        try
        {
            foreach (var meta in _saveManager.ListSaves())
            {
                Saves.Add(new SaveSlotViewModel(meta, id => _shell.NavigateToGame(id)));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось прочитать список сохранений: {ex.Message}";
        }
        SavesLoaded = true;
    }

    private bool CanNavigate() => !IsBusy;
}

/// <summary>
/// One row in the «Загрузить» list. Wraps a <see cref="SaveMeta"/> with
/// display fields (name, character, time, turn) and a Load command.
/// </summary>
public sealed class SaveSlotViewModel : ObservableObject
{
    private readonly SaveMeta _meta;
    private readonly Action<string> _load;

    public SaveSlotViewModel(SaveMeta meta, Action<string> load)
    {
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        LoadCommand = new RelayCommand(() => _load(_meta.Id));
    }

    public string Name => string.IsNullOrEmpty(_meta.Name) ? "(без названия)" : _meta.Name;
    public string Character => string.IsNullOrEmpty(_meta.CharacterName)
        ? "—"
        : $"{_meta.CharacterName} (ур. {_meta.CharacterLevel?.ToString() ?? "?"})";
    public string World => _meta.WorldTitle ?? _meta.LocationName ?? "—";
    public string UpdatedAt => _meta.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string TurnInfo => $"Ход {_meta.Turn}";
    public ICommand LoadCommand { get; }
}
