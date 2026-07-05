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
/// Landing screen after app startup. Shows the local player's nickname
/// at the top and the six primary buttons:
/// <list type="bullet">
///   <item>«Новая игра» — single-player, creates a save with DefaultWorld.</item>
///   <item>«Хост игры» — start a multiplayer host.</item>
///   <item>«Подключиться» — join a multiplayer host.</item>
///   <item>«Загрузить» — pick from existing saves.</item>
///   <item>«Профиль» — edit nickname.</item>
///   <item>«Настройки» — edit AI settings.</item>
/// </list>
/// </summary>
public partial class MainMenuViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;
    private readonly SaveManager _saveManager;
    private readonly ProfileStore _profileStore;

    private string _currentNickname = string.Empty;

    public MainMenuViewModel(MainViewModel shell, SaveManager saveManager, ProfileStore profileStore)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));

        try { _currentNickname = _profileStore.GetOrCreate().Nickname; }
        catch { _currentNickname = "Игрок"; }
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
    /// Snapshot of the local profile's nickname — shown in the menu
    /// chrome. Refreshed on every navigation to the menu (via the
    /// shell's NavigateToMenu).
    /// </summary>
    public string CurrentNickname
    {
        get => _currentNickname;
        private set => SetProperty(ref _currentNickname, value);
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
    /// world. Requires an AI API key (set in Profile → AI settings).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void CreateWorld() => _shell.NavigateToWorldBrief();

    /// <summary>«Хост игры» — open the multiplayer host setup screen.</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void HostGame() => _shell.NavigateToHost();

    /// <summary>«Подключиться» — open the multiplayer join screen.</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void JoinGame() => _shell.NavigateToJoin();

    /// <summary>«Профиль» — open the profile editor.</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void OpenProfile() => _shell.NavigateToProfile();

    /// <summary>«Настройки» — same editor (the profile screen has both).</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void OpenSettings() => _shell.NavigateToProfile();

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
