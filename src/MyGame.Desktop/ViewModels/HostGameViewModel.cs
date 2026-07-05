using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Multiplayer;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Host setup screen. Lets the host pick a game name and the max-players
/// cap, then creates a HostSession bound to a fresh DefaultWorld save
/// and starts the in-process WebSocket server.
///
/// <para>
/// On success, the screen navigates to the game view as host. The
/// HostSession is stashed on the GameViewModel — the HostGame screen
/// itself doesn't keep a reference after navigation.
/// </para>
/// </summary>
public partial class HostGameViewModel : ViewModelBase
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly SaveManager _saveManager;
    private readonly MainViewModel _shell;

    private string _gameName = "Новая партия";
    private int _maxPlayers = 4;
    private string? _shareAddress;
    private bool _useAiWorldBuild;
    private string _worldBrief = string.Empty;

    // Holds the started session between the StartCommand's completion
    // and the shell's navigation. Read by the GameViewModel ctor via
    // the <see cref="StartedSession"/> property.
    internal HostSession? StartedSession { get; private set; }

    public HostGameViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        SaveManager saveManager,
        MainViewModel shell)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    // ─── Form fields ─────────────────────────────────────────────────

    public string GameName
    {
        get => _gameName;
        set => SetProperty(ref _gameName, value);
    }

    public int MaxPlayers
    {
        get => _maxPlayers;
        set => SetProperty(ref _maxPlayers, Math.Max(1, Math.Min(8, value)));
    }

    /// <summary>
    /// After the host starts, shows the IP:port string the host can
    /// share with friends. Null before StartAsync.
    /// </summary>
    public string? ShareAddress
    {
        get => _shareAddress;
        private set => SetProperty(ref _shareAddress, value);
    }

    /// <summary>
    /// If true, the host wants the world generated via AI (world-builder
    /// orchestrator) instead of the stock DefaultWorld. Drives the
    /// visibility of the brief TextBox and routes the start flow to the
    /// WorldBrief screen.
    /// </summary>
    public bool UseAiWorldBuild
    {
        get => _useAiWorldBuild;
        set => SetProperty(ref _useAiWorldBuild, value);
    }

    /// <summary>
    /// Free-form world brief shown only when <see cref="UseAiWorldBuild"/>
    /// is on. If left empty, the planner creates a default dark-fantasy
    /// world.
    /// </summary>
    public string WorldBrief
    {
        get => _worldBrief;
        set => SetProperty(ref _worldBrief, value);
    }

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Start hosting: build a fresh world, save it, create a GameMaster
    /// bound to that world, start a HostSession on an OS-assigned port,
    /// then navigate to the game view as host.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            // If the host opted into AI world-build, route to the world-build
            // screen with the current brief (or the brief entry screen if the
            // brief is empty). The world-build screen will create the save
            // itself; the host flow here is abandoned in favour of the
            // single-player-style "create world then play" path. The host can
            // start the actual server after the build completes via the game
            // screen's host controls (TBD). For now, this keeps the flow
            // simple: build world → play single-player → optionally host.
            if (UseAiWorldBuild)
            {
                if (string.IsNullOrWhiteSpace(WorldBrief))
                {
                    _shell.NavigateToWorldBrief();
                }
                else
                {
                    _shell.NavigateToWorldBuild(WorldBrief);
                }
                return;
            }

            var profile = _profileStore.GetOrCreate();
            var settings = _settingsStore.Load();

            // 1) Create the starting world + persist it as a save. The
            //    world ships WITHOUT a player (issue #106) — the host
            //    creates their character on the CharacterCreation screen
            //    before the HostSession starts, so the GM's opening turn
            //    has a player to place (issue #105).
            var world = DefaultWorld.Create();
            var meta = _saveManager.CreateSave(
                string.IsNullOrWhiteSpace(_gameName) ? "Мультиплеер" : _gameName.Trim(),
                world, profile.Id);

            // 2) Stash the host setup (profile + settings) for the
            //    deferred host-start. CharacterCreation will trigger
            //    CompleteHostStartAsync after the player is created,
            //    which builds the GM + HostSession and navigates to the
            //    lobby. This defers the HostSession.StartAsync (and its
            //    UPnP / firewall dialog) until AFTER character creation,
            //    so the user isn't holding a server open while typing a
            //    character name.
            PendingHostStartTransfer.Set(profile, settings);
            _shell.NavigateToCharacterCreation(meta.Id, forHost: true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось запустить хост: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Back to the main menu.</summary>
    [RelayCommand]
    private void Back() => _shell.NavigateToMenu();

    private bool CanStart() => !IsBusy && !string.IsNullOrWhiteSpace(_gameName);
}

/// <summary>
/// Builds and starts a <see cref="HostSession"/> from a save + stashed
/// host setup. Shared between the HostGame screen's DefaultWorld path
/// and the AI world-build path (issue #107) — both go through character
/// creation first, then call <see cref="Start"/> to bring up the server.
/// </summary>
internal static class HostSessionStarter
{
    /// <summary>
    /// Load the world for <paramref name="saveId"/>, build the AI client
    /// + GM + tool registry, construct a HostSession bound to all
    /// interfaces (LAN play) on an OS-assigned port, start it, and
    /// return the session + its shutdown token source + the bound port.
    /// </summary>
    public static async Task<(HostSession session, CancellationTokenSource cts, int port)> StartAsync(
        SaveManager saveManager,
        string saveId,
        MyGame.Core.Profile.Profile profile,
        MyGame.Core.Profile.Settings settings)
    {
        var loaded = saveManager.LoadAll(saveId)
            ?? throw new InvalidOperationException($"Сохранение {saveId} не найдено.");
        var world = loaded.world;

        var ai = new AiClient(settings.Ai);
        var prompts = ServiceHost.Resolve<PromptLoader>();
        var tools = new ToolRegistry(world);
        var gm = new GameMaster(
            ai, world, prompts, tools, settings.MaxToolIterations,
            aiSettings: settings.Ai,
            maxContextTokens: settings.MaxContextTokens);

        var cts = new CancellationTokenSource();
        var session = new HostSession(
            profile, world, gm,
            saveManager: saveManager,
            saveId: saveId,
            requestedPort: 0,
            shutdownToken: cts.Token,
            bindHost: "+");
        var port = await session.StartAsync().ConfigureAwait(false);
        return (session, cts, port);
    }
}

/// <summary>
/// Stashed host setup (profile + settings) passed from HostGameViewModel
/// to MainViewModel.CompleteHostStartAsync after character creation.
/// Mirrors <see cref="PendingHostSessionTransfer"/> but for the pre-start
/// config (the HostSession is built AFTER character creation, so the
/// server isn't held open while the user picks a name).
/// </summary>
internal static class PendingHostStartTransfer
{
    private static MyGame.Core.Profile.Profile? _profile;
    private static MyGame.Core.Profile.Settings? _settings;
    private static readonly object _lock = new();

    public static void Set(MyGame.Core.Profile.Profile profile, MyGame.Core.Profile.Settings settings)
    {
        lock (_lock) { _profile = profile; _settings = settings; }
    }

    public static (MyGame.Core.Profile.Profile? profile, MyGame.Core.Profile.Settings? settings) Take()
    {
        lock (_lock)
        {
            var p = _profile; var s = _settings;
            _profile = null; _settings = null;
            return (p, s);
        }
    }
}


/// <summary>
/// Tiny channel for transferring a freshly-started HostSession from
/// the HostGame screen to the Game screen without exposing it via the
/// shell. There's only ever one host session per process at a time —
/// the Game screen pulls it from here on construction and clears the
/// slot. (A cleaner DI scope would also work; this keeps the shell's
/// API surface lean.)
/// </summary>
internal static class PendingHostSessionTransfer
{
    private static HostSession? _session;
    private static CancellationTokenSource? _cts;
    private static readonly object _lock = new();

    public static void Set(HostSession session, CancellationTokenSource cts)
    {
        lock (_lock) { _session = session; _cts = cts; }
    }

    public static (HostSession? session, CancellationTokenSource? cts) Take()
    {
        lock (_lock)
        {
            var s = _session; var c = _cts;
            _session = null; _cts = null;
            return (s, c);
        }
    }
}

/// <summary>
/// Tiny helper extension — fire-and-forget an async task while
/// surfacing any exception via Trace (avoids unobserved-task crashes).
/// </summary>
internal static class TaskExtensions
{
    public static void FireAndForget(this Task task)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
                System.Diagnostics.Trace.WriteLine($"[FireAndForget] {t.Exception}");
        }, TaskScheduler.Default);
    }
}
