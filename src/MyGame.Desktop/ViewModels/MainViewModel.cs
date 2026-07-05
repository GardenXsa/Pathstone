using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// The shell view model. Holds the current screen (<see cref="CurrentView"/>)
/// and exposes navigation commands that the main menu and each screen
/// use to move between screens. The shell is the only object the
/// <c>MainWindow</c> binds to; each screen is a <see cref="ViewModelBase"/>
/// that the ViewLocator turns into the matching <c>*View</c>.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly SaveManager _saveManager;

    private ViewModelBase? _currentView;
    private string _currentNickname = string.Empty;

    public MainViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        SaveManager saveManager)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));

        // The nickname is shown at the top of every screen — keep a
        // snapshot on the shell and refresh whenever we navigate.
        try
        {
            var p = _profileStore.GetOrCreate();
            _currentNickname = p.Nickname;
        }
        catch
        {
            _currentNickname = "Игрок";
        }
    }

    /// <summary>
    /// Current screen. The MainWindow binds its content to this —
    /// the ViewLocator DataTemplate resolves it to the matching *View.
    /// </summary>
    public ViewModelBase? CurrentView
    {
        get => _currentView;
        private set => SetProperty(ref _currentView, value);
    }

    /// <summary>
    /// The local player's nickname (snapshot at shell construction).
    /// Shown in the window header / menu chrome. Refreshed whenever we
    /// navigate back to the main menu.
    /// </summary>
    public string CurrentNickname
    {
        get => _currentNickname;
        private set => SetProperty(ref _currentNickname, value);
    }

    // ─── Bootstrapping ───────────────────────────────────────────────

    /// <summary>
    /// Show the main menu. Called once on app startup (after
    /// ServiceHost is initialized) and whenever we return to the menu.
    /// </summary>
    public void NavigateToMenu()
    {
        try
        {
            CurrentNickname = _profileStore.GetOrCreate().Nickname;
        }
        catch { /* fall back to whatever we had */ }

        var vm = new MainMenuViewModel(this, _saveManager, _profileStore);
        vm.Title = "MyGame";
        CurrentView = vm;
    }

    /// <summary>Navigate to the profile editor.</summary>
    public void NavigateToProfile()
    {
        var vm = new ProfileViewModel(_profileStore, _settingsStore, this);
        vm.Title = "Профиль";
        CurrentView = vm;
    }

    /// <summary>Navigate to the multiplayer host setup screen.</summary>
    public void NavigateToHost()
    {
        var vm = new HostGameViewModel(_profileStore, _settingsStore, _saveManager, this);
        vm.Title = "Хост игры";
        CurrentView = vm;
    }

    /// <summary>Navigate to the multiplayer join screen.</summary>
    public void NavigateToJoin()
    {
        var vm = new JoinGameViewModel(_profileStore, _settingsStore, this);
        vm.Title = "Подключиться";
        CurrentView = vm;
    }

    /// <summary>
    /// Navigate to the world-brief entry screen (the AI world-build flow).
    /// The user types a free-form brief, then navigates to the
    /// WorldBuildViewModel which runs the orchestrator.
    /// </summary>
    public void NavigateToWorldBrief()
    {
        var vm = new WorldBriefViewModel(_profileStore, _settingsStore, _saveManager, this);
        vm.Title = "Создать мир";
        CurrentView = vm;
    }

    /// <summary>
    /// Navigate directly to the world-build progress screen with a
    /// already-provided brief. Used by WorldBriefViewModel after the user
    /// confirms the brief. Optional <paramref name="petDelegations"/> add
    /// extra AI sub-tasks (mass NPC spawn, batch item creation) that run
    /// after the deterministic committer stage.
    /// </summary>
    public void NavigateToWorldBuild(string brief, IReadOnlyCollection<MyGame.Core.AI.Agents.PetDelegation>? petDelegations = null)
    {
        var vm = new WorldBuildViewModel(_profileStore, _settingsStore, _saveManager, this, brief, petDelegations);
        vm.Title = "Создание мира";
        CurrentView = vm;
        // Auto-start the build on navigation.
        vm.StartCommand.Execute(null);
    }

    /// <summary>
    /// Navigate to a single-player game loaded from an existing save.
    /// </summary>
    public Task NavigateToGame(string saveId)
        => NavigateToGameInternal(saveId, isHost: false, standaloneSinglePlayer: true,
            host: null, port: 0);

    /// <summary>
    /// Navigate to a multiplayer game as a client connected to a host.
    /// </summary>
    public Task NavigateToGameClient(string host, int port)
        => NavigateToGameInternal(saveId: null, isHost: false, standaloneSinglePlayer: false,
            host: host, port: port);

    /// <summary>
    /// Navigate to a multiplayer game as the host. The HostGame screen
    /// has already started the HostSession; the GameViewModel just needs
    /// to subscribe to its events.
    /// </summary>
    public Task NavigateToGameAsHost(string saveId)
        => NavigateToGameInternal(saveId, isHost: true, standaloneSinglePlayer: false,
            host: null, port: 0);

    // ─── Commands bound to menu buttons ──────────────────────────────

    [RelayCommand]
    private void GoToMenu() => NavigateToMenu();

    [RelayCommand]
    private void GoToProfile() => NavigateToProfile();

    [RelayCommand]
    private void GoToHost() => NavigateToHost();

    [RelayCommand]
    private void GoToJoin() => NavigateToJoin();

    // ─── Internal: build the GameViewModel ───────────────────────────

    private async Task NavigateToGameInternal(
        string? saveId,
        bool isHost,
        bool standaloneSinglePlayer,
        string? host,
        int port)
    {
        var vm = new GameViewModel(
            _profileStore,
            _settingsStore,
            _saveManager,
            this,
            isHost: isHost,
            standaloneSinglePlayer: standaloneSinglePlayer,
            saveId: saveId,
            host: host,
            port: port);
        vm.Title = "Игра";
        CurrentView = vm;
        try
        {
            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            vm.SetError($"Не удалось запустить игру: {ex.Message}");
        }
    }
}
