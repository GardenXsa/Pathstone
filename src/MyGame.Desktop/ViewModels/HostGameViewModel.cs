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

            // 1) Create the starting world + persist it as a save so
            //    the HostSession has something to mutate + save.
            var world = DefaultWorld.Create();
            var meta = _saveManager.CreateSave(
                string.IsNullOrWhiteSpace(_gameName) ? "Мультиплеер" : _gameName.Trim(),
                world, profile.Id);

            // 2) Wire the AI client + GM + tool registry onto the
            //    world. The GM mutates the world in-place via tool
            //    calls during ProcessNextTurnAsync.
            var ai = new AiClient(settings.Ai);
            var prompts = ServiceHost.Resolve<PromptLoader>();
            var tools = new ToolRegistry(world);
            var gm = new GameMaster(ai, world, prompts, tools, settings.MaxToolIterations);

            // 3) Build the session + start the WebSocket server.
            var cts = new CancellationTokenSource();
            var session = new HostSession(
                profile, world, gm,
                saveManager: _saveManager,
                saveId: meta.Id,
                requestedPort: 0,
                shutdownToken: cts.Token,
                bindHost: "+");
            var port = await session.StartAsync();
            StartedSession = session;

            // 4) Transition the party to Playing (the DefaultWorld is
            //    ready immediately — no world-builder stage here).
            await session.SetStatusAsync(PartyStatus.Playing, CancellationToken.None);

            ShareAddress = $"localhost:{port}  (порт {port})";

            // 5) Hand off to the GameViewModel. The shell constructs
            //    the VM with the save id; the VM pulls the session via
            //    a transient channel — we use a static slot here for
            //    simplicity (only one host session can exist per
            //    process at a time).
            PendingHostSessionTransfer.Set(session, cts);
            _shell.NavigateToGameAsHost(meta.Id).FireAndForget();
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
