using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI.Agents;
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
        //
        // First-run note (issue #73): if no profile.json exists yet, we
        // DON'T call GetOrCreate here — that would create the profile as
        // a side effect and short-circuit the onboarding flow. The
        // NavigateToMenu() method checks ProfileExists() and routes to
        // the onboarding wizard instead of the menu when this is the
        // first launch. Onboarding will create the profile (via
        // ProfileStore.Rename) and then navigate back to NavigateToMenu,
        // which on the second call sees the profile exists and shows
        // the menu.
        if (_profileStore.ProfileExists())
        {
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
    ///
    /// <para>
    /// <b>First-run onboarding (issue #73):</b> if no
    /// <c>profile.json</c> exists yet, this routes to
    /// <see cref="NavigateToOnboarding"/> instead of the menu. The
    /// wizard creates the profile (via <see cref="ProfileStore.Rename"/>
    /// on step 1), then calls <see cref="NavigateToMenu"/> again — at
    /// which point the profile exists and the menu is shown normally.
    /// </para>
    /// </summary>
    public void NavigateToMenu()
    {
        if (!_profileStore.ProfileExists())
        {
            NavigateToOnboarding();
            return;
        }

        try
        {
            CurrentNickname = _profileStore.GetOrCreate().Nickname;
        }
        catch { /* fall back to whatever we had */ }

        var vm = new MainMenuViewModel(this, _saveManager, _profileStore, _settingsStore);
        vm.Title = "MyGame";
        CurrentView = vm;
    }

    /// <summary>
    /// Show the first-run onboarding wizard (issue #73). Constructs an
    /// <see cref="OnboardingViewModel"/> with the shell + profile +
    /// settings stores; the wizard navigates back to
    /// <see cref="NavigateToMenu"/> on completion (by which point the
    /// profile exists and the menu shows normally).
    /// </summary>
    public void NavigateToOnboarding()
    {
        var vm = new OnboardingViewModel(this, _profileStore, _settingsStore);
        vm.Title = "Добро пожаловать";
        CurrentView = vm;
    }

    /// <summary>
    /// Navigate to the settings screen (AI connection only). The nickname
    /// is edited inline on the main menu — no separate profile screen.
    /// </summary>
    public void NavigateToSettings()
    {
        var vm = new SettingsViewModel(_settingsStore, this);
        vm.Title = "Настройки";
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
    /// <param name="generationMode">Optional generation mode override
    /// (issue #20): "full" (default) or "chunked". When "chunked", the
    /// planner only designs the start region; others are generated
    /// on-demand as the player travels toward them.</param>
    public void NavigateToWorldBuild(
        string brief,
        IReadOnlyCollection<MyGame.Core.AI.Agents.PetDelegation>? petDelegations = null,
        string? generationMode = null)
    {
        var vm = new WorldBuildViewModel(_profileStore, _settingsStore, _saveManager, this, brief, petDelegations, generationMode);
        vm.Title = "Создание мира";
        CurrentView = vm;
        // Auto-start the build on navigation.
        vm.StartCommand.Execute(null);
    }

    /// <summary>
    /// Navigate to the world-build progress screen with a saved
    /// <see cref="WorldBuilderState"/> to resume from (issue #19).
    /// Constructs the VM with the original brief, stages the state for
    /// loading on the orchestrator (which happens inside
    /// <see cref="WorldBuildViewModel.StartAsync"/> once the orchestrator
    /// is constructed), and auto-starts the build. The resumed run skips
    /// already-completed stages (planning → committer → pets → narration)
    /// and continues from where the previous run left off.
    /// </summary>
    /// <param name="state">Saved orchestrator state (must not be null).</param>
    /// <param name="brief">The original world brief (read from the state
    /// file by the caller — typically <see cref="MainMenuViewModel"/>).</param>
    /// <param name="petDelegations">Optional pet delegations (resumed
    /// runs that completed some delegations will skip them via
    /// <see cref="WorldBuilderState.Iteration"/>).</param>
    public void NavigateToWorldBuildForResume(
        WorldBuilderState state,
        string brief,
        IReadOnlyCollection<MyGame.Core.AI.Agents.PetDelegation>? petDelegations = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        var vm = new WorldBuildViewModel(_profileStore, _settingsStore, _saveManager, this, brief ?? string.Empty, petDelegations);
        vm.Title = "Возобновление генерации мира";
        vm.LoadStateForResume(state, brief ?? string.Empty);
        CurrentView = vm;
        // Auto-start the build on navigation. StartAsync will see the
        // staged state and call orchestrator.LoadState() before RunAsync.
        vm.StartCommand.Execute(null);
    }

    /// <summary>
    /// Navigate to the character-creation screen for a freshly-built world.
    /// The save was just created by <see cref="WorldBuildViewModel"/>;
    /// the CC screen will load it, swap the auto-created «Странник» for
    /// the player's chosen character (name/race/class/background +
    /// class-appropriate starter gear + attributes), persist, and then
    /// navigate into the game via <see cref="NavigateToGame"/>.
    /// </summary>
    public void NavigateToCharacterCreation(string saveId)
    {
        var vm = new CharacterCreationViewModel(_saveManager, this, saveId);
        vm.Title = "Создание персонажа";
        CurrentView = vm;
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

    /// <summary>
    /// Navigate to the rebuild dialog (issue #23) for the given save.
    /// The dialog lets the user pick which categories to regenerate
    /// (locations / NPC / items / narration / full rebuild) and an
    /// optional rebuild brief; on confirm it runs
    /// <see cref="WorldBuilderOrchestrator.RebuildAsync"/> and then
    /// navigates into the game with the rebuilt save.
    /// </summary>
    public void NavigateToRebuild(string saveId)
    {
        var vm = new RebuildViewModel(_profileStore, _settingsStore, _saveManager, this, saveId);
        vm.Title = "Перестроить мир";
        CurrentView = vm;
    }

    // ─── Commands bound to menu buttons ──────────────────────────────

    [RelayCommand]
    private void GoToMenu() => NavigateToMenu();

    [RelayCommand]
    private void GoToSettings() => NavigateToSettings();

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
