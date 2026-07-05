using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Common;
using MyGame.Core.Engine;
using MyGame.Core.Multiplayer;
using MyGame.Core.Multiplayer.Protocol;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;
using MyGame.Desktop.Services;
using MyGame.Desktop.ViewModels.Panels;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// The main play screen. Three modes (mutually exclusive):
///
/// <list type="bullet">
///   <item><b>Single-player</b> (<see cref="StandaloneSinglePlayer"/>=true):
///     a save was loaded; the GM runs locally; no HostSession/ClientSession.
///     SubmitAction runs the GM directly and saves state.</item>
///   <item><b>Host</b> (<see cref="IsHost"/>=true): a HostSession was
///     started by the HostGame screen. SubmitAction enqueues into the
///     session's ActionQueue and runs ProcessNextTurnAsync. State saves
///     via the session's SaveManager.</item>
///   <item><b>Client</b> (<see cref="IsHost"/>=false AND not standalone):
///     a ClientSession was connected by the JoinGame screen.
///     SubmitAction sends ActionQueuedMsg to the host. State updates +
///     narrative arrive as events.</item>
/// </list>
///
/// <para>
/// All Core event handlers marshal to the UI thread via
/// <see cref="Dispatcher.UIThread.Post"/> — the HostSession/ClientSession
/// raise events on ThreadPool threads (network read loops, GM task
/// continuation), but UI bindings only refresh safely on the UI thread.
/// </para>
/// </summary>
public partial class GameViewModel : ViewModelBase
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly SaveManager _saveManager;
    private readonly MainViewModel _shell;

    // ─── Mode flags ──────────────────────────────────────────────────
    public bool IsHost { get; }
    public bool StandaloneSinglePlayer { get; }
    public bool IsMultiplayer => !StandaloneSinglePlayer;
    public bool IsClient => !StandaloneSinglePlayer && !IsHost;

    /// <summary>
    /// True when this screen is in the multiplayer lobby phase (issue #77):
    /// the host has started a session but hasn't transitioned to Playing
    /// yet, OR the client has connected but the host hasn't started the
    /// game yet. Drives the lobby layout in the center column (lobby
    /// panel with members list + ready toggle + start-game button) vs
    /// the normal game layout (narrative log + action input).
    /// </summary>
    /// <remarks>
    /// Refreshed on every relevant lifecycle event:
    /// <list type="bullet">
    ///   <item>Host: <see cref="InitHost"/> (initial status from
    ///     HostSession.Status) + HostSession.StatusChanged (host
    ///     transitioned Lobby → Playing via StartGameCommand).</item>
    ///   <item>Client: <see cref="InitClientAsync"/> (initial status
    ///     from ClientSession.Status) + ClientSession.StatusChanged
    ///     (host transitioned).</item>
    /// </list>
    /// Always false in single-player mode (no lobby phase).
    /// </remarks>
    [ObservableProperty] private bool _isLobby;

    // ─── Sessions (one of these is non-null depending on mode) ───────
    public HostSession? HostSession { get; private set; }
    public ClientSession? ClientSession { get; private set; }
    private CancellationTokenSource? _hostShutdownCts;

    // ─── Single-player runtime state ─────────────────────────────────
    private GameMaster? _gm;
    private MyGame.Core.Tooling.ReplayRecorder? _replayRecorder;
    private World? _world;
    private SaveMeta? _meta;
    private string? _saveId;
    private readonly List<LogEntry> _log = new();
    private readonly object _logLock = new();

    /// <summary>
    /// Player level as of the previous turn. Used to detect level-ups after
    /// a GM turn resolves (single-player + host modes). Null until the first
    /// refresh, at which point it's seeded with the current player level
    /// (so the first turn never reports a false level-up).
    /// </summary>
    private int? _previousLevel;

    // ─── Chat panel state ────────────────────────────────────────────
    private readonly ObservableCollection<ChatLine> _chat = new();

    public GameViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        SaveManager saveManager,
        MainViewModel shell,
        bool isHost,
        bool standaloneSinglePlayer,
        string? saveId,
        string? host,
        int port)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        IsHost = isHost;
        StandaloneSinglePlayer = standaloneSinglePlayer;
        _saveId = saveId;

        // Title shows the mode so the user knows what they're playing.
        Title = standaloneSinglePlayer
            ? "Одиночная игра"
            : (isHost ? "Игра (хост)" : "Игра (клиент)");

        // Wire inventory panel actions → world mutations. The panel raises
        // an event; this VM decides how to apply it (single-player mutates
        // the world directly; multiplayer routes through the GM tool flow).
        InventoryPanel.ItemActionRequested += OnInventoryAction;

        // Wire world-panel travel → world mutations. Each exit row is a
        // Button bound to WorldPanel.TravelCommand; the panel forwards the
        // clicked ExitRow here, and we move the player, mark the
        // destination visited/discovered, log the trip, persist, and
        // (if the destination is dangerous) roll a random encounter.
        WorldPanel.TravelRequested += OnTravel;

        // Wire character-panel export → CharacterSheetStore.Export (issue
        // #62). The panel raises ExportRequested (no payload — the panel
        // doesn't know the save id); this VM resolves the save id + active
        // player id, calls Export, and writes the result path / error back
        // to the panel's ExportStatus property so the user sees inline
        // feedback under the «Экспорт» button.
        CharacterPanel.ExportRequested += OnCharacterExportRequested;

        // Wire quest-panel reward claiming → world mutations (issue #70).
        // The QuestPanel raises ClaimRewardsRequested(questId) when the
        // user clicks «Получить награду» on a completed quest whose
        // rewards are staged in Quest.UnclaimedRewards. We grant the
        // currency / XP / items, clear the field, log a summary entry,
        // refresh the panels, and persist.
        QuestPanel.ClaimRewardsRequested += OnQuestClaimRewards;
    }

    // ─── Observable props ────────────────────────────────────────────

    [ObservableProperty] private string _currentAction = string.Empty;
    [ObservableProperty] private string? _chatInput;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string _clockDisplay = "День 1, 08:00";
    [ObservableProperty] private string _characterSummary = string.Empty;
    [ObservableProperty] private string _worldInfo = string.Empty;
    [ObservableProperty] private string _gameName = "Игра";
    [ObservableProperty] private string _shareAddress = string.Empty;
    [ObservableProperty] private bool _canSave;
    [ObservableProperty] private bool _canSubmit = true;

    // ─── Combat + death UI state (issues #88, #63) ──────────────────────
    //
    // CombatDisplay: a one-line combat indicator for the top bar — null
    // when no combat is active. Refreshed in RefreshFromWorld.
    // IsPlayerDead: drives the death overlay. True when the active
    // player's IsAlive flag is false; set in RefreshFromWorld. The
    // overlay's "reload last save" and "leave to menu" buttons are
    // bound to the dedicated relay commands below.
    [ObservableProperty] private string? _combatDisplay;
    [ObservableProperty] private bool _isPlayerDead;

    // ─── Reconnect overlay state (issue #7) ────────────────────────────
    //
    // IsDisconnected: drives the reconnect overlay. True when the
    // ClientSession's Disconnected event fires with Intentional=false
    // (network drop). The overlay offers "Переподключиться" (calls
    // ReconnectCommand, retries 3x with 2s backoff) and "Выйти в меню"
    // (calls LeaveToMenuCommand). On reconnect success the Welcomed
    // event fires and refreshes the members list + party status.
    //
    // ReconnectFailed: true after 3 reconnect attempts have all failed.
    // Switches the overlay to a simpler "Не удалось переподключиться"
    // message with only the "Выйти в меню" button. Reset to false on a
    // successful reconnect (so a subsequent drop starts fresh).
    //
    // DisconnectReason: the close reason from the Disconnected event,
    // shown in the overlay body. Empty string when no disconnect has
    // occurred.
    [ObservableProperty] private bool _isDisconnected;
    [ObservableProperty] private bool _reconnectFailed;
    [ObservableProperty] private string _disconnectReason = string.Empty;

    // ─── Token billing (issue #6) ─────────────────────────────────────
    //
    // Per-turn + per-session token counters. Accumulate after each GM
    // turn (single-player + host). Persist on SaveMeta so the session
    // total survives reload. The "last turn" counter resets each turn;
    // the "session" counter accumulates until the save is reset.
    [ObservableProperty] private int _sessionPromptTokens;
    [ObservableProperty] private int _sessionCompletionTokens;
    [ObservableProperty] private int _sessionTotalTokens;
    [ObservableProperty] private int _lastTurnTokens;

    // ─── Action-queue batching (issue #12) ──────────────────────────
    //
    // BatchCountdown: seconds remaining in the host's batching window
    // (5 → 4 → 3 → 2 → 1 → 0). 0 means no batching in progress. The
    // host sets this via the HostSession.BatchCountdownChanged event
    // when the batching window starts ticking. The pending-actions
    // list (right column) already shows queued actions; this countdown
    // gives the user a sense of when the next turn will fire.
    //
    // For single-player-host (1 ready member), the batching window
    // fires immediately — no countdown is shown.
    [ObservableProperty] private int _batchCountdown;

    /// <summary>
    /// Live-streaming narrative buffer. Bound to a TextBlock at the end
    /// of the log so the user sees the GM's narration appear word-by-word
    /// as the SSE stream delivers content deltas. Cleared on turn start
    /// and after the final NarrativeResult is appended to the Log.
    /// </summary>
    [ObservableProperty] private string _streamingNarrativeText = string.Empty;

    /// <summary>
    /// Throttled streaming-narrative buffer. Deltas are appended here as
    /// they arrive; the property <see cref="StreamingNarrativeText"/> is
    /// only refreshed when at least 50ms have passed since the last
    /// flush, to avoid flooding the UI with PropertyChanged events.
    /// </summary>
    private readonly System.Text.StringBuilder _streamBuffer = new();
    private long _lastStreamFlushTicks;

    /// <summary>
    /// Session-tokens display string for the top-bar widget. Format:
    /// "Сессия: 12.3k токенов" (k-suffix for ≥1000). Empty when 0.
    /// </summary>
    public string SessionTokensDisplay => FormatTokenCount("Сессия", SessionTotalTokens);

    /// <summary>
    /// Last-turn tokens display string for the top-bar widget. Format:
    /// "Ход: 456" (plain int, no k-suffix — single turns are small).
    /// </summary>
    public string LastTurnTokensDisplay => LastTurnTokens > 0
        ? $"Ход: {LastTurnTokens}"
        : string.Empty;

    /// <summary>
    /// Format a token count as "Label: N токенов" (or "Label: N.Nk токенов"
    /// for ≥1000). Returns empty string when count is 0 (so the TextBlock
    /// can collapse cleanly when no tokens have been billed yet).
    /// </summary>
    private static string FormatTokenCount(string label, int count)
    {
        if (count <= 0) return string.Empty;
        if (count < 1000) return $"{label}: {count} токенов";
        // 1234 → "1.2k", 12345 → "12.3k", 123456 → "123k"
        double k = count / 1000.0;
        return $"{label}: {k:F1}k токенов";
    }

    /// <summary>
    /// Refresh the token-display properties after a turn. Called on the
    /// UI thread (the OnPropertyChanged notifications fire here).
    /// </summary>
    private void RefreshTokenDisplays()
    {
        // Touch each property to fire PropertyChanged so the bound
        // TextBlocks re-read the computed display strings.
        OnPropertyChanged(nameof(SessionTokensDisplay));
        OnPropertyChanged(nameof(LastTurnTokensDisplay));
    }

    /// <summary>
    /// Accumulate token usage from one GM turn into the session totals
    /// and the last-turn counter. Also refreshes the display properties.
    /// Safe to call from any thread — but PropertyChanged fires here, so
    /// callers should be on the UI thread (or marshal).
    /// </summary>
    private void AccumulateTokens(int promptTokens, int completionTokens, int totalTokens)
    {
        SessionPromptTokens += promptTokens;
        SessionCompletionTokens += completionTokens;
        SessionTotalTokens += totalTokens;
        LastTurnTokens = totalTokens;
        RefreshTokenDisplays();
    }

    /// <summary>
    /// Write the current session-tokens into <see cref="_meta"/>. Called
    /// before every save so the totals persist across reloads. Also copies
    /// the GM's conversation-history summary (issue #25) so the GM doesn't
    /// lose context across save/load. No-op when <see cref="_meta"/> is null.
    /// </summary>
    private void PersistTokensToMeta()
    {
        if (_meta is null) return;
        _meta = _meta with
        {
            SessionPromptTokens = SessionPromptTokens,
            SessionCompletionTokens = SessionCompletionTokens,
            // Persist the conversation-history summary (issue #25). Null
            // when no summarization has happened yet — saves load with
            // null and the GM will start fresh summarization when needed.
            HistorySummary = _gm?.HistorySummary ?? _meta.HistorySummary,
        };
    }

    /// <summary>
    /// Restore session-tokens from <see cref="_meta"/> into the VM's
    /// observable properties. Called after a save load so the counter
    /// survives reload. No-op when <see cref="_meta"/> is null.
    /// </summary>
    private void RestoreTokensFromMeta()
    {
        if (_meta is null) return;
        SessionPromptTokens = _meta.SessionPromptTokens;
        SessionCompletionTokens = _meta.SessionCompletionTokens;
        SessionTotalTokens = _meta.SessionPromptTokens + _meta.SessionCompletionTokens;
        LastTurnTokens = 0; // last-turn counter resets on reload
        RefreshTokenDisplays();
    }

    /// <summary>Observable narrative + action + tool log.</summary>
    public ObservableCollection<LogEntry> Log { get; } = new();

    /// <summary>Observable lobby/chat roster (multiplayer).</summary>
    public ObservableCollection<MemberInfo> Members { get; } = new();

    /// <summary>Observable chat panel (lobby + in-game chat).</summary>
    public ObservableCollection<ChatLine> Chat => _chat;

    /// <summary>Observable pending-action queue (multiplayer only).</summary>
    public ObservableCollection<PlayerAction> PendingActions { get; } = new();

    // ─── Side-panel VMs (character / inventory / quests / world) ─────
    // Each panel VM is owned by the GameViewModel. RefreshFromWorld()
    // pushes the live World into all four so they stay in sync after
    // every GM turn / state update. The View binds a TabControl to these.
    public CharacterPanelViewModel CharacterPanel { get; } = new();
    public InventoryPanelViewModel InventoryPanel { get; } = new();
    public QuestPanelViewModel QuestPanel { get; } = new();
    public WorldPanelViewModel WorldPanel { get; } = new();

    // ─── Initialization ──────────────────────────────────────────────

    /// <summary>
    /// Async init: pulls the started session from the transfer channel
    /// (multiplayer modes), wires event subscriptions, and loads the
    /// save's world (single-player/host modes).
    /// </summary>
    public async Task InitializeAsync()
    {
        if (StandaloneSinglePlayer)
        {
            await InitSinglePlayerAsync();
        }
        else if (IsHost)
        {
            InitHost();
            RefreshFromHostWorld();
        }
        else
        {
            await InitClientAsync();
        }
    }

    private async Task InitSinglePlayerAsync()
    {
        if (string.IsNullOrEmpty(_saveId))
        {
            ErrorMessage = "Save id is missing.";
            return;
        }

        var loaded = _saveManager.LoadAll(_saveId);
        if (loaded is null)
        {
            ErrorMessage = $"Сохранение {_saveId} не найдено.";
            return;
        }

        ApplyLoadedSave(loaded.Value.world, loaded.Value.meta, loaded.Value.log, resetHistory: true);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Apply a freshly-loaded (world, meta, log) tuple to this VM:
    /// replace <c>_world</c>/<c>_meta</c>, swap the in-memory log,
    /// build a fresh GameMaster (history reset when
    /// <paramref name="resetHistory"/> is true), restore session-tokens
    /// from meta, and refresh the UI. Shared between initial load
    /// (<see cref="InitSinglePlayerAsync"/>) and the death-overlay
    /// reload (<see cref="ReloadLastSaveAsync"/>).
    /// </summary>
    private void ApplyLoadedSave(World world, SaveMeta meta, LogEntry[] log, bool resetHistory)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));

        // Reset the in-memory log to the loaded snapshot. The lock guards
        // against the AppendLog thread (which can fire from a background
        // GM continuation) racing the clear.
        lock (_logLock)
        {
            _log.Clear();
            _log.AddRange(log ?? Array.Empty<LogEntry>());
        }
        Dispatcher.UIThread.Post(() =>
        {
            Log.Clear();
            foreach (var e in log ?? Array.Empty<LogEntry>()) Log.Add(e);
        });

        var settings = _settingsStore.Load();
        var ai = new AiClient(settings.Ai);
        var prompts = ServiceHost.Resolve<PromptLoader>();
        var tools = new ToolRegistry(_world);
        _gm = new GameMaster(
            ai, _world, prompts, tools, settings.MaxToolIterations,
            aiSettings: settings.Ai,
            maxContextTokens: settings.MaxContextTokens);
        if (resetHistory)
            _gm.ResetHistory();
        // Restore the conversation-history summary from the save so the
        // GM doesn't lose context across reload (issue #25). The summary
        // was persisted on the previous SaveAll; null on a fresh save.
        _gm.HistorySummary = _meta?.HistorySummary;

        GameName = _meta?.Name ?? "Игра";
        CanSave = true;
        // Issue #85: initialize replay recorder for this save session.
        var replayDir = System.IO.Path.Combine(
            MyGame.Core.Profile.ProfileStore.DefaultProfileDirectory, "replays");
        _replayRecorder = new MyGame.Core.Tooling.ReplayRecorder(replayDir, _saveId ?? "unknown");
        RestoreTokensFromMeta();
        // Reset the level-up baseline so a reloaded game doesn't toast a
        // false level-up on the next turn.
        _previousLevel = null;
        RefreshFromWorld();
    }

    private void InitHost()
    {
        var (session, cts) = PendingHostSessionTransfer.Take();
        if (session is null)
        {
            ErrorMessage = "Host session is missing.";
            return;
        }
        HostSession = session;
        _hostShutdownCts = cts;
        _world = session.World;
        // _saveId was already set via the GameViewModel ctor (the shell
        // passes it down from NavigateToGameAsHost).
        GameName = session.World?.Locations.FirstOrDefault()?.Name ?? "Мультиплеер";
        ShareAddress = $"localhost:{session.Port}";
        CanSave = true;

        // Wire session events → UI thread.
        session.MemberJoined += OnMemberJoined;
        session.MemberLeft += OnMemberLeft;
        session.ChatReceived += OnChatReceived;
        session.ActionQueued += OnActionQueued;
        session.ActionCancelled += OnActionCancelledHost;
        session.TurnStarted += OnTurnStarted;
        session.NarrativeDelta += OnNarrativeDelta;
        session.NarrativeFinal += OnNarrativeFinal;
        session.StateUpdate += OnHostStateUpdate;
        session.TurnEnded += OnTurnEnded;
        session.TurnFailed += OnTurnFailed;
        session.BatchCountdownChanged += OnHostBatchCountdownChanged;
        // Issue #77 — lobby events: member ready toggles + party status
        // changes (host → Playing). Used to refresh the lobby UI.
        session.MemberReady += OnMemberReady;
        session.StatusChanged += OnHostStatusChanged;

        // Pre-populate the members list with the host + anyone already
        // connected (no one yet at this point, but defensive).
        Members.Clear();
        foreach (var m in session.Members) Members.Add(m);

        // Seed the host's token counters from the session's cumulative
        // totals (which were restored from the save on StartAsync). The
        // per-turn counter stays at 0 — a new host session has no "last
        // turn" yet.
        SessionPromptTokens = session.SessionPromptTokens;
        SessionCompletionTokens = session.SessionCompletionTokens;
        SessionTotalTokens = session.SessionTotalTokens;
        LastTurnTokens = 0;
        RefreshTokenDisplays();

        // Append a system entry to the log so the user sees something.
        AppendLog(LogEntry.System($"Хост запущен на порту {session.Port}."));

        // Issue #77 — initial lobby detection. The HostServer defaults
        // to Lobby status; the host will transition to Playing via the
        // StartGameCommand once everyone's ready.
        RefreshIsLobby();
        // Notify command CanExecute so the lobby's Start button picks
        // up the initial ready-state check.
        StartGameCommand.NotifyCanExecuteChanged();
        ToggleReadyCommand.NotifyCanExecuteChanged();
    }

    private async Task InitClientAsync()
    {
        var session = PendingClientSessionTransfer.Take();
        if (session is null)
        {
            ErrorMessage = "Client session is missing.";
            return;
        }
        ClientSession = session;
        CanSave = false;
        GameName = "Подключено";

        // Wire session events → UI thread.
        session.Welcomed += OnClientWelcomed;
        session.MemberJoined += OnClientMemberJoined;
        session.MemberLeft += OnClientMemberLeft;
        session.ChatReceived += OnClientChatReceived;
        session.ActionQueued += OnClientActionQueued;
        session.ActionCancelled += OnClientActionCancelled;
        session.ActionResolving += OnClientActionResolving;
        session.NarrativeDelta += OnClientNarrativeDelta;
        session.NarrativeFinal += OnClientNarrativeFinal;
        session.StateUpdate += OnClientStateUpdate;
        session.TurnEnd += OnClientTurnEnd;
        session.Error += OnClientError;
        session.Kicked += OnClientKicked;
        session.LogSynced += OnClientLogSynced;
        session.Disconnected += OnClientDisconnected;
        // Issue #77 — lobby events: another member toggled ready, or
        // the host transitioned the party to Playing. Used to refresh
        // the lobby UI + IsLobby flag.
        session.MemberReady += OnClientMemberReady;
        session.StatusChanged += OnClientStatusChanged;

        AppendLog(LogEntry.System("Подключено к хосту."));

        // Issue #77 — initial lobby detection from the WelcomeMsg's
        // party snapshot. OnClientWelcomed also calls RefreshIsLobby,
        // but that event may have already fired (race between here and
        // the GameClient raising Welcomed during ConnectAsync, which
        // happened in JoinGameViewModel before we got here). Refresh
        // defensively from the cached Status.
        RefreshIsLobby();
        ToggleReadyCommand.NotifyCanExecuteChanged();
        await Task.CompletedTask;
    }

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Submit the player's action.
    /// <list type="bullet">
    ///   <item>Single-player: run the GM directly, append the narration
    ///     to the log, save state.</item>
    ///   <item>Host: enqueue + immediately run ProcessNextTurnAsync so
    ///     the host sees the result without waiting for an external
    ///     "Next Turn" button.</item>
    ///   <item>Client: send ActionQueuedMsg to the host.</item>
    /// </list>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSubmitAction))]
    private async Task SubmitActionAsync()
    {
        var text = (CurrentAction ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return;
        CurrentAction = string.Empty;
        CanSubmit = false;
        IsWaiting = true;
        StatusText = "Обработка действия…";
        ErrorMessage = null;
        try
        {
            if (StandaloneSinglePlayer)
            {
                await RunSinglePlayerTurnAsync(text);
            }
            else if (IsHost && HostSession is not null)
            {
                await HostSession.SubmitActionAsync(text);
                // The HostSession's batching window (issue #12) drives the
                // GM turn: for single-player-host it fires immediately;
                // for multi-player-host it waits up to 5s for other
                // ready members to submit their actions, then drains the
                // queue and runs the GM once on the batch. We do NOT
                // call ProcessNextTurnAsync directly here — that would
                // defeat the batching window by draining the queue
                // before other players' actions could join the batch.
            }
            else if (ClientSession is not null)
            {
                await ClientSession.SubmitActionAsync(text);
                StatusText = "Действие отправлено хосту.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Действие не удалось: {ex.Message}";
        }
        finally
        {
            IsWaiting = false;
            CanSubmit = true;
            StatusText = null;
        }
    }

    private bool CanSubmitAction() => CanSubmit && !IsWaiting && !IsLobby && !IsLocalSpectator;

    /// <summary>
    /// Send a chat message (lobby chat in multiplayer; in single-player
    /// this is a no-op since there's nobody to chat with).
    /// </summary>
    [RelayCommand]
    private async Task SendChatAsync()
    {
        var text = (ChatInput ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return;
        ChatInput = string.Empty;
        try
        {
            if (IsHost && HostSession is not null)
            {
                await HostSession.SendChatAsync(text);
            }
            else if (ClientSession is not null)
            {
                await ClientSession.SendChatAsync(text);
            }
            // Single-player: no chat — just clear the input.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось отправить сообщение: {ex.Message}";
        }
    }

    /// <summary>
    /// Host-only: explicitly save state. The session also saves after
    /// every turn; this is a manual flush.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveState))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        IsBusy = true;
        try
        {
            if (StandaloneSinglePlayer && _world is not null && _meta is not null && _saveId is not null)
            {
                // Persist session-tokens before the save so the counter
                // survives reload.
                PersistTokensToMeta();
                LogEntry[] snapshot;
                lock (_logLock) snapshot = _log.ToArray();
                _saveManager.SaveAll(_saveId, _world, _meta, snapshot);
                StatusText = "Сохранено.";
            }
            else if (IsHost && HostSession is not null)
            {
                await HostSession.StopAsync(); // stops + saves final state
                // Restart the session? No — the user can navigate back.
                StatusText = "Сохранено.";
            }
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

    private bool CanSaveState() => CanSave && !IsBusy;

    // ─── Lobby commands (issue #77) ──────────────────────────────────

    /// <summary>
    /// Toggle the local player's ready state in the lobby. Client-only
    /// — the host is always Ready (set by HostServer.StartAsync) so
    /// the command is a no-op for the host (and the lobby UI hides the
    /// button). For clients, sends a <see cref="MemberReadyMsg"/> to
    /// the host with the new ready state. The host re-broadcasts to
    /// everyone (including this client); the echoed msg arrives via
    /// <see cref="OnClientMemberReady"/> and updates the Members list
    /// authoritatively. The local Members list is also updated
    /// optimistically so the UI feels responsive.
    /// </summary>
    /// <remarks>
    /// The "new ready state" is the OPPOSITE of the local member's
    /// current <see cref="MemberInfo.Status"/> (Ready → Pending →
    /// Ready). When the local member isn't found in the Members list
    /// (race during initial connect), defaults to toggling to Ready
    /// (i.e. assumes the user is currently Pending).
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanToggleReady))]
    private async Task ToggleReadyAsync()
    {
        if (ClientSession is null) return;
        var local = LocalMemberInfo;
        bool newReady = local?.Status != MemberStatus.Ready;
        try
        {
            // Optimistic local update so the UI feels responsive.
            if (local is not null)
            {
                var updated = local with
                {
                    Status = newReady ? MemberStatus.Ready : MemberStatus.Pending,
                };
                ReplaceMember(updated);
            }
            await ClientSession.SetReadyAsync(newReady);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось изменить статус готовности: {ex.Message}";
        }
    }

    /// <summary>
    /// CanExecute for <see cref="ToggleReadyCommand"/>. Enabled only
    /// for clients in the lobby (the host is always Ready and doesn't
    /// get a toggle button). Disabled outside the lobby.
    /// </summary>
    private bool CanToggleReady() => IsClient && IsLobby && ClientSession is not null;

    /// <summary>
    /// Host-only: transition the party from Lobby to Playing (issue
    /// #77). Calls <see cref="HostSession.SetStatusAsync"/> which
    /// broadcasts a StatusChangedMsg to all clients; the resulting
    /// <see cref="OnHostStatusChanged"/> handler refreshes
    /// <see cref="IsLobby"/> (the lobby layout disappears, the normal
    /// game layout appears).
    /// </summary>
    /// <remarks>
    /// Disabled until all non-spectator members are Ready (the host
    /// is always Ready; clients must have toggled ready). Spectators
    /// are excluded from the ready-check since they don't submit
    /// actions.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStartGame))]
    private async Task StartGameAsync()
    {
        if (HostSession is null) return;
        try
        {
            await HostSession.SetStatusAsync(PartyStatus.Playing);
            AppendLog(LogEntry.System("Игра началась!"));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось начать игру: {ex.Message}";
        }
    }

    /// <summary>
    /// CanExecute for <see cref="StartGameCommand"/>. Host-only +
    /// lobby-only + all non-spectator members must be Ready. Always
    /// false outside the lobby.
    /// </summary>
    private bool CanStartGame()
    {
        if (!IsHost || !IsLobby || HostSession is null) return false;
        return Members
            .Where(m => m.Role != MemberRole.Spectator)
            .All(m => m.Status == MemberStatus.Ready || m.Status == MemberStatus.Playing);
    }

    /// <summary>
    /// Issue #30: Kick a member from the party (host only). Sends KickedMsg
    /// + closes the connection. The kicked client receives the Disconnected
    /// event and sees the reconnect overlay (but with a "kicked" reason).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanKick))]
    private async Task KickAsync(MemberInfo member)
    {
        if (HostSession is null || member is null) return;
        try
        {
            await HostSession.KickAsync(member.ConnectionId, "Исключён хостом");
            AppendLog(LogEntry.System($"{member.Nickname} исключён из партии."));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось исключить: {ex.Message}";
        }
    }

    private bool CanKick(MemberInfo member)
    {
        if (!IsHost || member is null) return false;
        if (HostSession is null) return false;
        // Can't kick self or the host.
        if (member.ConnectionId == HostSession.HostConnectionId) return false;
        return true;
    }

    /// <summary>
    /// Issue #31: True when the local player is a spectator (can see
    /// everything but cannot submit actions). Drives the action-input
    /// placeholder + a spectator badge in the UI.
    /// </summary>
    public bool IsLocalSpectator
    {
        get
        {
            if (!IsMultiplayer) return false;
            var local = LocalMemberInfo;
            return local?.Role == MemberRole.Spectator;
        }
    }

    /// <summary>
    /// The local player's <see cref="MemberInfo"/> (looked up by
    /// connection id). Null when the local member isn't in the roster
    /// yet (race during initial connect). Used by the lobby UI to show
    /// the local player's ready state + by <see cref="ToggleReadyAsync"/>
    /// to decide the new ready state.
    /// </summary>
    public MemberInfo? LocalMemberInfo
    {
        get
        {
            var localId = IsHost
                ? HostSession?.HostConnectionId
                : ClientSession?.ConnectionId;
            if (localId is null || localId == Guid.Empty) return null;
            return Members.FirstOrDefault(m => m.ConnectionId == localId);
        }
    }

    /// <summary>
    /// Convenience for the lobby UI: the local player's ready state.
    /// Mirrors <see cref="LocalMemberInfo"/>?.<see cref="MemberInfo.Status"/>
    /// == <see cref="MemberStatus.Ready"/>. Returns true when no local
    /// member is found (defensive — defaults to "ready" so the lobby
    /// doesn't show a misleading "pending" indicator during the
    /// initial-connect race).
    /// </summary>
    public bool IsLocalReady => LocalMemberInfo?.Status == MemberStatus.Ready;

    /// <summary>
    /// True when all non-spectator members in the lobby are Ready
    /// (host is always Ready; clients must toggle). Drives the enabled
    /// state of the host's «Начать игру» button — recomputed on every
    /// member ready / join / leave event.
    /// </summary>
    public bool AllReady =>
        Members.Count > 0
        && Members.Where(m => m.Role != MemberRole.Spectator)
                  .All(m => m.Status == MemberStatus.Ready || m.Status == MemberStatus.Playing);

    /// <summary>
    /// Refresh <see cref="IsLobby"/> from the live session status.
    /// Called on init + on every status-change event. Single-player
    /// mode is always false (no lobby phase). Host reads
    /// <see cref="HostSession.Status"/>, client reads
    /// <see cref="ClientSession.Status"/>.
    /// </summary>
    private void RefreshIsLobby()
    {
        bool newIsLobby;
        if (StandaloneSinglePlayer)
        {
            newIsLobby = false;
        }
        else if (IsHost)
        {
            newIsLobby = HostSession?.Status == PartyStatus.Lobby;
        }
        else
        {
            newIsLobby = ClientSession?.Status == PartyStatus.Lobby;
        }
        if (IsLobby != newIsLobby)
        {
            IsLobby = newIsLobby;
            // Re-evaluate CanExecute for lobby-only commands + the
            // SubmitAction command (disabled in lobby) when the lobby
            // state flips.
            StartGameCommand.NotifyCanExecuteChanged();
            ToggleReadyCommand.NotifyCanExecuteChanged();
            SubmitActionCommand.NotifyCanExecuteChanged();
        }
        OnPropertyChanged(nameof(LocalMemberInfo));
        OnPropertyChanged(nameof(IsLocalReady));
        OnPropertyChanged(nameof(AllReady));
    }

    /// <summary>
    /// Replace a member in the <see cref="Members"/> collection (matched
    /// by <see cref="MemberInfo.ConnectionId"/>) with a new instance.
    /// No-op when the member isn't found. Used by the lobby ready-toggle
    /// flow to update a member's Status in place.
    /// </summary>
    private void ReplaceMember(MemberInfo updated)
    {
        for (int i = 0; i < Members.Count; i++)
        {
            if (Members[i].ConnectionId == updated.ConnectionId)
            {
                Members[i] = updated;
                break;
            }
        }
        // Fire change notifications for the derived lobby properties so
        // the lobby UI re-evaluates the local-ready indicator + the
        // StartGame button's enabled state.
        OnPropertyChanged(nameof(LocalMemberInfo));
        OnPropertyChanged(nameof(IsLocalReady));
        OnPropertyChanged(nameof(AllReady));
        StartGameCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Leave the game and return to the main menu. Host: stop the
    /// server (saves final state). Client: disconnect. Single-player:
    /// save final state if a save id is available.
    /// </summary>
    [RelayCommand]
    private async Task LeaveGameAsync()
    {
        IsBusy = true;
        try
        {
            if (StandaloneSinglePlayer && _world is not null && _meta is not null && _saveId is not null)
            {
                // Persist session-tokens before the final save.
                PersistTokensToMeta();
                LogEntry[] snapshot;
                lock (_logLock) snapshot = _log.ToArray();
                try { _saveManager.SaveAll(_saveId, _world, _meta, snapshot); }
                catch { /* best-effort */ }
            }
            else if (IsHost && HostSession is not null)
            {
                try { await HostSession.StopAsync(); } catch { /* best-effort */ }
                _hostShutdownCts?.Cancel();
            }
            else if (ClientSession is not null)
            {
                try { await ClientSession.DisconnectAsync(); } catch { /* best-effort */ }
            }
        }
        finally
        {
            IsBusy = false;
            _shell.NavigateToMenu();
        }
    }

    /// <summary>
    /// COMBAT-DEATH (issue #63): reload the current save from disk,
    /// discarding the in-memory state (which has the dead player). The
    /// GM is rebuilt from scratch — the conversation history from the
    /// dead-state session is dropped. The save on disk reflects the
    /// pre-death snapshot because RunSinglePlayerTurnAsync skips the
    /// post-turn auto-save when the player just died.
    ///
    /// <para>
    /// Single-player only. Host: no-op (the host owns the save, but
    /// mid-session reload isn't supported in MVP — the user can leave
    /// and re-enter instead). Client: no-op (the host owns the world).
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task ReloadLastSaveAsync()
    {
        if (!StandaloneSinglePlayer)
        {
            // Multiplayer modes don't support mid-session reload from
            // the death overlay in MVP. Just go to the menu — the user
            // can re-host / re-join.
            _shell.NavigateToMenu();
            return;
        }
        if (string.IsNullOrEmpty(_saveId))
        {
            ErrorMessage = "Сохранение не найдено — не из чего загружать.";
            return;
        }

        IsBusy = true;
        try
        {
            var loaded = _saveManager.LoadAll(_saveId);
            if (loaded is null)
            {
                ErrorMessage = $"Сохранение {_saveId} не найдено.";
                return;
            }
            AppendLog(LogEntry.System("Перезагрузка последнего сохранения…"));
            ApplyLoadedSave(loaded.Value.world, loaded.Value.meta, loaded.Value.log, resetHistory: true);
            AppendLog(LogEntry.System("Сохранение загружено. Удачи!"));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось загрузить сохранение: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// COMBAT-DEATH (issue #63): leave to the main menu WITHOUT saving
    /// the current (dead) state. The save on disk is left as-is, so the
    /// player can pick it up later from the «Загрузить» menu. Differs
    /// from <see cref="LeaveGameAsync"/> in that the latter saves before
    /// leaving (we don't want that here — we'd overwrite the pre-death
    /// save with the dead state).
    /// </summary>
    [RelayCommand]
    private void LeaveToMenu()
    {
        // Best-effort: stop the host session if any (no save — we don't
        // want to persist the dead state).
        if (IsHost && HostSession is not null)
        {
            try { HostSession.StopAsync().FireAndForget(); } catch { /* best-effort */ }
            _hostShutdownCts?.Cancel();
        }
        else if (ClientSession is not null)
        {
            try { ClientSession.DisconnectAsync().FireAndForget(); } catch { /* best-effort */ }
        }
        _shell.NavigateToMenu();
    }

    /// <summary>
    /// RECONNECT (issue #7): attempt to re-establish the client WebSocket
    /// to the same host/port. Tries up to 3 times with a 2-second backoff
    /// between attempts. On success: clears IsDisconnected (the overlay
    /// hides); the Welcomed event (raised by ClientSession.ReconnectAsync
    /// via GameClient.ConnectAsync) refreshes the members list + party
    /// status from the fresh WelcomeMsg. On all-fail: sets ReconnectFailed
    /// = true so the overlay switches to the "Не удалось переподключиться"
    /// exit-only state.
    ///
    /// <para>
    /// Client-mode only. No-op in single-player / host modes (those don't
    /// have a remote peer to reconnect to — the Leave command handles
    /// their teardown).
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private async Task ReconnectAsync()
    {
        if (ClientSession is null) return;

        CanSubmit = false;
        IsBusy = true;
        StatusText = "Переподключение…";
        try
        {
            const int maxAttempts = 3;
            const int backoffMs = 2000;

            Exception? lastError = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                StatusText = $"Переподключение (попытка {attempt}/{maxAttempts})…";
                try
                {
                    // ReconnectAsync re-runs the HelloMsg→WelcomeMsg
                    // handshake. On success, GameClient raises Welcomed,
                    // which fires ClientSession.Welcomed, which our
                    // OnClientWelcomed handler picks up to refresh the
                    // Members list. We just need to clear the overlay
                    // state here.
                    await ClientSession.ReconnectAsync(CancellationToken.None);
                    IsDisconnected = false;
                    ReconnectFailed = false;
                    DisconnectReason = string.Empty;
                    ErrorMessage = null;
                    StatusText = "Переподключение выполнено.";
                    AppendLog(LogEntry.System("Переподключение выполнено."));
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    System.Diagnostics.Trace.WriteLine(
                        $"[GameViewModel] Reconnect attempt {attempt} failed: {ex.Message}");
                    // Backoff before the next attempt (skip on the last
                    // attempt — no point waiting before giving up).
                    if (attempt < maxAttempts)
                    {
                        try { await Task.Delay(backoffMs); } catch { /* ignore */ }
                    }
                }
            }

            // All attempts failed — switch the overlay to exit-only mode.
            ReconnectFailed = true;
            StatusText = null;
            ErrorMessage = $"Не удалось переподключиться: {lastError?.Message ?? "неизвестная ошибка"}";
            AppendLog(LogEntry.System(
                $"Переподключение не удалось после {maxAttempts} попыток."));
        }
        finally
        {
            IsBusy = false;
            CanSubmit = true;
            // Clear the transient status text — the overlay's own
            // message takes over the user-facing surface now.
            if (IsDisconnected) StatusText = null;
        }
    }

    /// <summary>
    /// CanExecute for ReconnectCommand. The button is only enabled in
    /// client mode (where the overlay is shown), when a reconnect isn't
    /// already in flight (IsBusy), and when the previous reconnect
    /// attempt didn't already exhaust the retry budget (ReconnectFailed).
    /// </summary>
    private bool CanReconnect() =>
        ClientSession is not null
        && !IsBusy
        && IsDisconnected
        && !ReconnectFailed;

    // ─── Single-player GM loop ───────────────────────────────────────

    private async Task RunSinglePlayerTurnAsync(string text)
    {
        if (_gm is null || _world is null)
        {
            ErrorMessage = "Игра не инициализирована.";
            return;
        }

        var profile = _profileStore.GetOrCreate();
        AppendLog(LogEntry.Action($"{profile.Nickname}: {text}", authorId: profile.Id.ToString()));

        // Reset the streaming buffer + clear the streaming text so the
        // user sees only the new turn's live narration. The Progress<T>
        // callback runs on the captured SynchronizationContext — since
        // this method is invoked from the SubmitAction relay command on
        // the UI thread, that's the UI thread. So we can safely mutate
        // UI-bound state directly in the handler.
        StartStreamingNarrative();
        var progress = new Progress<string>(OnNarrativeDelta);

        NarrativeResult result;
        try
        {
            result = await _gm.ProcessActionAsync(text, progress);
        }
        finally
        {
            // Final flush: push any remaining buffered content to the UI,
            // then clear so the streaming TextBlock disappears before the
            // final narrative LogEntry is appended below. (If we cleared
            // before the flush, the user would see a brief empty flash.)
            FlushStreamingNarrative();
            ClearStreamingNarrative();
        }

        if (result.Failed)
        {
            AppendLog(LogEntry.System($"Ошибка ИИ: {result.Error ?? "неизвестная ошибка"}"));
            ErrorMessage = result.Error ?? "Ошибка ИИ.";
            return;
        }

        if (!string.IsNullOrEmpty(result.NarrativeText))
            AppendLog(LogEntry.Narrative(result.NarrativeText));
        foreach (var tc in result.ToolCalls)
        {
            AppendLog(LogEntry.Tool($"{tc.Name}: {tc.Result}",
                metadata: new System.Collections.Generic.Dictionary<string, object>
                {
                    ["name"] = tc.Name,
                    ["args"] = tc.ArgsJson,
                    ["isError"] = tc.IsError,
                }));

            // Issue #70 — when the GM completes a quest (update_quest
            // with action=complete), the tool result text is
            // "Квест «{name}» выполнен. Награда ожидает получения во
            // вкладке Квесты." Detect this and emit a SYSTEM log entry
            // telling the player to go claim the reward (the Tool
            // entry above is somewhat terse + the system entry is
            // player-facing guidance). Parse the quest name from the
            // angle-bracketed «…» segment so we can name it in the
            // notification.
            if (tc.Name == "update_quest" && !tc.IsError)
            {
                var questName = TryExtractQuestCompletedName(tc.Result);
                if (questName is not null)
                {
                    AppendLog(LogEntry.System(
                        $"Квест «{questName}» завершён! Награда доступна во вкладке Квесты."));
                }
            }
        }

        // Accumulate token billing for this turn into the session totals
        // + last-turn counter, and refresh the top-bar display.
        AccumulateTokens(result.PromptTokens, result.CompletionTokens, result.TotalTokens);

        // Increment turn + save.
        _world.Turn++;

        // COMBAT-DEATH: when the player just died this turn, do NOT
        // auto-save. Otherwise the on-disk save would lock the player
        // into a dead state, and "Загрузить последнее сохранение" from
        // the death overlay would reload the dead state — defeating the
        // point. Skipping the save preserves the pre-death snapshot.
        var playerAfterTurn = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        bool playerJustDied = playerAfterTurn is { IsAlive: false };
        if (_meta is not null && _saveId is not null && !playerJustDied)
        {
            // Persist session-tokens into meta before the save so the
            // counter survives reload.
            PersistTokensToMeta();
            try
            {
                LogEntry[] snapshot;
                lock (_logLock) snapshot = _log.ToArray();
                _saveManager.SaveAll(_saveId, _world, _meta, snapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[GameViewModel] save failed: {ex}");
            }
        }
        RefreshFromWorld();
        // Detect a level-up that occurred during this turn and toast it.
        // Runs after RefreshFromWorld so the character panel already shows
        // the new level.
        CheckLevelUpAndToast();

        // Issue #85: record this turn for replay. Appended after
        // RefreshFromWorld so the world state snapshot includes all
        // mutations from tool calls. Persist immediately.
        if (_replayRecorder is not null && !string.IsNullOrEmpty(result.NarrativeText))
        {
            try
            {
                _replayRecorder.RecordTurn(_world.Turn, text, result.NarrativeText, _world);
                _replayRecorder.Save();
            }
            catch { /* best-effort — don't crash on replay failure */ }
        }
    }

    // ─── Streaming-narrative helpers ─────────────────────────────────
    //
    // The streaming-narrative TextBlock at the bottom of the log shows
    // the GM's narration appear word-by-word as the SSE stream delivers
    // content deltas. To avoid flooding the UI with PropertyChanged
    // events (one per delta, potentially dozens per second), we batch
    // deltas into a StringBuilder and only flush to the bound property
    // when at least ~50ms have passed since the last flush.

    /// <summary>
    /// Reset the streaming buffer + clear the streaming text property.
    /// Called at the start of each turn.
    /// </summary>
    private void StartStreamingNarrative()
    {
        _streamBuffer.Clear();
        _lastStreamFlushTicks = 0;
        StreamingNarrativeText = string.Empty;
    }

    /// <summary>
    /// Progress callback for streaming narrative deltas. Appends to the
    /// buffer and throttles property updates to ~50ms intervals.
    /// </summary>
    private void OnNarrativeDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _streamBuffer.Append(delta);

        var now = Environment.TickCount64;
        if (now - _lastStreamFlushTicks >= 50)
        {
            _lastStreamFlushTicks = now;
            StreamingNarrativeText = _streamBuffer.ToString();
        }
    }

    /// <summary>
    /// Push any remaining buffered content to the streaming text
    /// property. Called at the end of the turn (before clearing) so the
    /// user sees the final streamed content even if the last delta
    /// arrived within the 50ms throttle window.
    /// </summary>
    private void FlushStreamingNarrative()
    {
        if (_streamBuffer.Length > 0)
            StreamingNarrativeText = _streamBuffer.ToString();
    }

    /// <summary>
    /// Clear the streaming text property (called after the final
    /// NarrativeResult has been appended to the Log so the streaming
    /// TextBlock doesn't duplicate the final narrative entry).
    /// </summary>
    private void ClearStreamingNarrative()
    {
        _streamBuffer.Clear();
        StreamingNarrativeText = string.Empty;
    }

    /// <summary>
    /// Compare the player's current level against the previously recorded
    /// baseline; if it went up, emit a system log entry celebrating the
    /// new level. Then update the baseline. No-op when the player or world
    /// is missing, or on the very first refresh (where the baseline is
    /// seeded to the current level rather than a stale value).
    /// </summary>
    private void CheckLevelUpAndToast()
    {
        if (_world is null) return;
        var p = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        if (p is null) return;
        int current = p.Level ?? 1;
        if (_previousLevel is int prev && current > prev)
        {
            AppendLog(LogEntry.System($"Новый уровень! Теперь вы {current}-го уровня."));
        }
        _previousLevel = current;
    }

    private void AppendLog(LogEntry entry)
    {
        lock (_logLock) _log.Add(entry);
        Dispatcher.UIThread.Post(() => Log.Add(entry));
    }

    // ─── Refresh displays from the live World ────────────────────────

    private void RefreshFromWorld()
    {
        if (_world is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            // Issue #35: top-bar clock shows the time-of-day label so the
            // player sees at a glance whether it's day or night (drives
            // Perception disadvantage + encounter rate). Rendered as
            // "День 3, 14:00 — день" / "День 3, 22:00 — ночь".
            ClockDisplay = _world.Clock.ToDisplayWithTimeOfDay();
            var p = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
            CharacterSummary = BuildCharacterSummary(p);
            WorldInfo = BuildWorldInfo(_world, p);

            // COMBAT-DEATH: refresh the top-bar combat indicator + the
            // death-overlay flag. CombatDisplay is null when no combat
            // is active (so the bound TextBlock collapses); otherwise
            // "⚔ Бой: раунд {round}, ход: {actor}". IsPlayerDead drives
            // the death overlay (covers the game screen with reload/
            // leave buttons).
            if (_world.Combat is { Active: true } combat && combat.TurnOrder.Count > 0)
            {
                var idx = Math.Clamp(combat.CurrentActorIndex, 0, combat.TurnOrder.Count - 1);
                var actor = combat.TurnOrder[idx].Name;
                CombatDisplay = $"⚔ Бой: раунд {combat.Round}, ход: {actor}";
            }
            else
            {
                CombatDisplay = null;
            }
            IsPlayerDead = p is { IsAlive: false };

            // Seed the level-tracking baseline on the first refresh (covers
            // InitializeAsync + post-load). Actual level-up detection runs
            // in the single-player / host turn-end handlers, comparing
            // against this baseline.
            if (_previousLevel is null && p is not null)
                _previousLevel = p.Level ?? 1;

            // Push the live world into every side panel so the UI stays
            // in sync after each GM turn / state update.
            CharacterPanel.RefreshFromWorld(_world);
            InventoryPanel.RefreshFromWorld(_world);
            QuestPanel.RefreshFromWorld(_world);
            WorldPanel.RefreshFromWorld(_world);

            // Issue #86: check achievement milestones after each refresh.
            // Newly unlocked achievements get a system log entry (toast).
            if (StandaloneSinglePlayer || IsHost)
            {
                var newAchievements = AchievementTracker.CheckMilestones(_world);
                foreach (var ach in newAchievements)
                {
                    AppendLog(LogEntry.System($"🏆 Достижение: «{ach.Name}» — {ach.Description}"));
                }
            }

            // Issue #31: refresh IsLocalSpectator so CanSubmitAction
            // re-evaluates (spectators can't submit actions).
            OnPropertyChanged(nameof(IsLocalSpectator));
            SubmitActionCommand.NotifyCanExecuteChanged();
        });
    }

    private void RefreshFromHostWorld()
    {
        if (HostSession?.World is { } w)
        {
            _world = w;
            RefreshFromWorld();
            // Seed the log from the host's in-memory log (post-load).
            Log.Clear();
            foreach (var e in HostSession.Log) Log.Add(e);
        }
    }

    private static string BuildCharacterSummary(Player? p)
    {
        if (p is null) return "Персонаж: (не создан)";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Имя: {p.Name}");
        if (!string.IsNullOrEmpty(p.Race) || !string.IsNullOrEmpty(p.Class))
            sb.AppendLine($"Раса/класс: {p.Race ?? "—"} / {p.Class ?? "—"}");
        if (p.Level is int lvl) sb.AppendLine($"Уровень: {lvl}");
        if (p.Resources.Count > 0)
        {
            var hp = p.Resources.FirstOrDefault(kv => kv.Key.Equals("hp", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(hp.Key))
                sb.AppendLine($"HP: {hp.Value}");
            else
                sb.AppendLine($"Ресурсы: {string.Join(", ", p.Resources.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
        if (p.Inventory.Items.Count > 0)
            sb.AppendLine($"Инвентарь: {string.Join(", ", p.Inventory.Items.Take(8).Select(i => $"{i.Name} ×{i.Quantity}"))}");
        return sb.ToString().TrimEnd();
    }

    private static string BuildWorldInfo(World world, Player? p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Время: {world.Clock}");
        sb.AppendLine($"Ход: {world.Turn}");
        if (p is not null)
        {
            var loc = world.GetLocation(p.LocationId);
            if (loc is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"Локация: {loc.Name}");
                if (!string.IsNullOrEmpty(loc.Description))
                    sb.AppendLine(loc.Description);
                if (loc.Exits.Count > 0)
                    sb.AppendLine($"Выходы: {string.Join(", ", loc.Exits.Select(e => e.Direction))}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Локации мира:");
        foreach (var l in world.Locations.Take(15))
            sb.AppendLine($"  • {l.Name} [{l.Terrain}]");
        return sb.ToString().TrimEnd();
    }

    // ─── Inventory action handler ────────────────────────────────────
    //
    // The inventory panel raises ItemAction events when the user clicks
    // use/equip/unequip/drop. We apply them directly to the live World
    // (single-player + host modes). Client mode is a no-op for now —
    // item mutations there should route through the host via a future
    // tool-call message; the panel buttons are hidden for clients
    // anyway (CanSave=false implies read-only).
    private void OnInventoryAction(Panels.ItemAction action)
    {
        if (_world is null) return;
        var player = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        if (player is null) return;

        // Find the item: could be in the carried inventory or in an
        // equipped slot. Unequip operates on equipped; the others on
        // carried.
        Item? item = null;
        string? equippedSlot = null;
        foreach (var kv in player.Equipped)
        {
            if (kv.Value.Id == action.ItemId) { item = kv.Value; equippedSlot = kv.Key; break; }
        }
        if (item is null)
        {
            item = player.Inventory.Items.FirstOrDefault(i => i.Id == action.ItemId);
        }
        if (item is null) return;

        var template = item.TemplateId is not null
            ? _world.Registries.Items.Get(item.TemplateId)
            : null;

        string logText;
        switch (action.Kind)
        {
            case Panels.ItemActionKind.Use:
                logText = ApplyUse(player, item, template);
                break;

            case Panels.ItemActionKind.Equip:
                logText = ApplyEquip(player, item, template);
                break;

            case Panels.ItemActionKind.Unequip:
                if (equippedSlot is null) return;
                logText = ApplyUnequip(player, equippedSlot, item);
                break;

            case Panels.ItemActionKind.Drop:
                logText = ApplyDrop(player, item);
                break;

            case Panels.ItemActionKind.Split:
                // STACK-SPLIT (issue #64): only stackable items in the
                // carried inventory can be split. The InventoryPanel VM
                // has already validated this before raising the action,
                // but we re-check defensively. The split quantity comes
                // in via ItemAction.Quantity (1..item.Quantity-1).
                if (template is null || !template.Stackable) return;
                if (item.Quantity < 2) return;
                logText = ApplySplit(player, item, template, action.Quantity);
                break;

            default:
                return;
        }

        AppendLog(LogEntry.System(logText));
        RefreshFromWorld();

        // Persist immediately in single-player/host modes so the change
        // survives a reload. Skip in client mode (no save authority).
        if (CanSave && _saveId is not null && _meta is not null)
        {
            // Persist session-tokens before the save.
            PersistTokensToMeta();
            _ = Task.Run(() =>
            {
                try { _saveManager.SaveAll(_saveId, _world, _meta, Log.ToArray()); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[save] {ex.Message}"); }
            });
        }
    }

    private static string ApplyUse(Player player, Item item, ItemTemplate? tpl)
    {
        // Consumable: apply healing/effect, decrement quantity, remove if empty.
        if (tpl?.Consumable is { } cons)
        {
            // Healing: parse "NdM" or plain int; apply to HP resource.
            if (!string.IsNullOrWhiteSpace(cons.Healing))
            {
                var healed = ParseHealing(cons.Healing);
                if (healed > 0 && player.Resources.Count > 0)
                {
                    var hpKey = player.Resources.Keys.FirstOrDefault(k =>
                        k.Equals("hp", StringComparison.OrdinalIgnoreCase) ||
                        k.Equals("health", StringComparison.OrdinalIgnoreCase));
                    if (hpKey is not null)
                        player.Resources[hpKey] = player.Resources[hpKey] + healed;
                }
            }
            // Effects: add to the character's active effects (simplified —
            // a full consumable-effect system would apply timed modifiers).
            if (cons.Effects is not null)
            {
                foreach (var eff in cons.Effects)
                    player.Effects.Add(new StatusEffect
                    {
                        Id = EntityId.NewId(),
                        Name = eff.Name,
                        Description = eff.Description,
                        Duration = eff.Duration,
                    });
            }
            // Decrement / remove.
            item.Quantity -= 1;
            if (item.Quantity <= 0)
                player.Inventory.Items.Remove(item);
            return $"Использован «{item.Name}».";
        }

        // Non-consumable "use": nothing mechanical, just log it.
        return $"Использован «{item.Name}» (без механического эффекта).";
    }

    private static string ApplyEquip(Player player, Item item, ItemTemplate? tpl)
    {
        // Determine slot from template profile.
        string slot = tpl?.Weapon is not null ? "weapon"
            : tpl?.Armor is not null ? "armor"
            : "misc";
        // If the slot is occupied, swap the old item back to the inventory.
        if (player.Equipped.TryGetValue(slot, out var old))
        {
            old.Equipped = false;
            player.Inventory.Items.Add(old);
        }
        player.Inventory.Items.Remove(item);
        item.Equipped = true;
        player.Equipped[slot] = item;
        return $"Экипирован «{item.Name}» (слот: {slot}).";
    }

    private static string ApplyUnequip(Player player, string slot, Item item)
    {
        player.Equipped.Remove(slot);
        item.Equipped = false;
        player.Inventory.Items.Add(item);
        return $"Снят «{item.Name}» (слот: {slot}).";
    }

    private string ApplyDrop(Player player, Item item)
    {
        player.Inventory.Items.Remove(item);
        // Drop on the ground at the player's current location.
        var loc = _world?.GetLocation(player.LocationId);
        if (loc is not null && _world is not null)
            _world.SpawnItemOnGround(item, loc.Id);
        return $"Брошен «{item.Name}».";
    }

    /// <summary>
    /// STACK-SPLIT (issue #64): create a new Item instance with the
    /// given split quantity, decrement the original stack, and add the
    /// new stack to the inventory. The new Item gets a fresh EntityId
    /// (it's a distinct object) but inherits the template, name, weight,
    /// and enchantments of the original. Returns a one-line log message
    /// describing the split.
    /// </summary>
    /// <param name="player">The active player (must carry <paramref name="item"/>).</param>
    /// <param name="item">The original stack to split. Must have Quantity &gt;= 2.</param>
    /// <param name="template">The item's template (used to instantiate the new stack).</param>
    /// <param name="splitQty">How many units to peel off into the new stack. Clamped to 1..item.Quantity-1.</param>
    private string ApplySplit(Player player, Item item, ItemTemplate template, int splitQty)
    {
        // Clamp the split quantity to a sane range. The InventoryPanel
        // VM has already validated this, but defensive bounds prevent a
        // bad client call from emptying the original stack or creating
        // a negative-quantity item.
        var qty = splitQty;
        if (qty < 1) qty = 1;
        if (qty > item.Quantity - 1) qty = item.Quantity - 1;

        // Instantiate the new stack from the same template. This carries
        // over Name, Weight, TemplateId, etc. via EntityFactory.
        var newStack = EntityFactory.InstantiateItem(template, qty);
        // Carry over runtime-only fields the factory doesn't set.
        newStack.Enchantments = item.Enchantments;
        newStack.CustomDamage = item.CustomDamage;

        // Decrement the original stack. If splitting leaves it at 0
        // (shouldn't happen due to the clamp above, but defensive),
        // remove the original entry entirely.
        item.Quantity -= qty;
        if (item.Quantity <= 0)
            player.Inventory.Items.Remove(item);

        player.Inventory.Items.Add(newStack);
        return $"Разделено: «{newStack.Name}» ×{qty} (осталось ×{item.Quantity}).";
    }

    /// <summary>
    /// Parse a healing expression. Accepts plain ints ("5"), NdM dice
    /// ("1d8"), and "+N"/"NdM" with a leading plus. Returns 0 on any
    /// parse failure (so a bad template doesn't crash the use action).
    /// </summary>
    private static int ParseHealing(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return 0;
        expr = expr.Trim();
        if (int.TryParse(expr, out var plain)) return Math.Max(0, plain);
        // NdM
        var m = System.Text.RegularExpressions.Regex.Match(expr,
            @"^(?<n>\d*)d(?<m>\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var n = int.TryParse(m.Groups["n"].Value, out var nn) && nn > 0 ? nn : 1;
            var sides = int.TryParse(m.Groups["m"].Value, out var ss) && ss > 0 ? ss : 1;
            if (n > 20) n = 20; // sanity cap
            int sum = 0;
            var rng = new Rng(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            for (int i = 0; i < n; i++) sum += rng.NextInt(1, sides + 1);
            return sum;
        }
        return 0;
    }

    /// <summary>
    /// Try to extract the quest name from an <c>update_quest complete</c>
    /// tool result. The result text is
    /// <c>"Квест «{name}» выполнен. Награда ожидает получения во вкладке Квесты."</c>
    /// — we look for the angle-bracketed <c>«…»</c> segment between the
    /// leading <c>Квест</c> and the <c>выполнен</c> marker. Returns null
    /// when the input doesn't match (other update_quest actions, errors,
    /// foreign-language results, etc.).
    /// </summary>
    private static string? TryExtractQuestCompletedName(string? result)
    {
        if (string.IsNullOrEmpty(result)) return null;
        // Match "Квест «{name}» выполнен" — the «…» are guillemets
        // (U+00AB / U+00BB), not regular quotes. Use a regex with the
        // actual guillemet characters so the match is locale-agnostic.
        var m = System.Text.RegularExpressions.Regex.Match(result,
            @"Квест\s+«([^»]+)»\s+выполнен",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // ─── Quest reward claiming (issue #70) ───────────────────────────
    //
    // The QuestPanel raises ClaimRewardsRequested when the user clicks
    // «Получить награду» on a completed quest whose rewards are staged
    // in Quest.UnclaimedRewards (set by the GM's update_quest complete
    // tool call). We grant the rewards (currency, XP, items), clear the
    // UnclaimedRewards field, append a summary log entry, refresh the
    // panels, and persist. Client mode is a no-op (no save authority —
    // the panel button is effectively hidden in client mode since the
    // GM tool flow runs on the host).
    private void OnQuestClaimRewards(EntityId questId)
    {
        if (_world is null) return;
        var quest = _world.GetQuest(questId);
        if (quest is null) return;
        // Defensive: only grant when there actually are unclaimed
        // rewards. A double-click could fire the command twice before
        // RefreshFromWorld hides the button; this guard prevents a
        // double-grant.
        if (quest.UnclaimedRewards is null) return;

        var player = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        if (player is null) return;

        var reward = quest.UnclaimedRewards;
        int currency = reward.Currency ?? reward.Gold ?? 0;
        int xp = reward.Experience ?? 0;
        var grantedItemNames = new List<string>();

        if (currency > 0)
            player.Inventory.Currency += currency;

        bool leveledUp = false;
        int newLevel = player.Level ?? 1;
        if (xp > 0)
        {
            var (leveled, finalLevel) = AwardXpTool.GrantXp(player, xp);
            leveledUp = leveled;
            newLevel = finalLevel;
        }

        if (reward.Items is { } itemIds)
        {
            foreach (var tplId in itemIds)
            {
                var tpl = _world.Registries.Items.Get(tplId);
                if (tpl is null) continue;
                var inst = EntityFactory.InstantiateItem(tpl, 1);
                player.Inventory.Items.Add(inst);
                grantedItemNames.Add(inst.Name);
            }
        }

        // Clear the staged rewards so the «Получить награду» button
        // disappears on the next RefreshFromWorld.
        quest.UnclaimedRewards = null;

        // Build a readable summary line. Format:
        // "Получена награда: 50 зол., 100 опыта, Меч + Кольцо."
        // Items that don't fit (long lists) get a "+N ещё" suffix.
        var sb = new System.Text.StringBuilder("Получена награда: ");
        var parts = new List<string>();
        if (currency > 0) parts.Add($"{currency} зол.");
        if (xp > 0) parts.Add($"{xp} опыта");
        if (grantedItemNames.Count > 0)
        {
            // Show up to 3 item names; collapse the rest into "+N ещё".
            const int maxItemNames = 3;
            if (grantedItemNames.Count <= maxItemNames)
                parts.Add(string.Join(", ", grantedItemNames.Select(n => $"«{n}»")));
            else
                parts.Add(string.Join(", ",
                    grantedItemNames.Take(maxItemNames).Select(n => $"«{n}»"))
                    + $" +{grantedItemNames.Count - maxItemNames} ещё");
        }
        sb.Append(string.Join(", ", parts));
        sb.Append('.');
        if (leveledUp)
            sb.Append($" Новый уровень: {newLevel}!");

        AppendLog(LogEntry.System(sb.ToString()));
        RefreshFromWorld();

        // Persist immediately so the grant survives a reload. Skip in
        // client mode (no save authority) — the host's save already
        // captured the grant on its end.
        if (CanSave && _saveId is not null && _meta is not null)
        {
            PersistTokensToMeta();
            _ = Task.Run(() =>
            {
                try { _saveManager.SaveAll(_saveId, _world, _meta, Log.ToArray()); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[save] {ex.Message}"); }
            });
        }
    }

    // ─── Travel handler ──────────────────────────────────────────────
    //
    // The world panel raises TravelRequested when the user clicks an exit
    // row. We apply it directly to the live World (single-player + host
    // modes), mirroring OnInventoryAction's pattern: mutate, log, refresh
    // panels, persist async. Client mode is a no-op for world mutations
    // (the panel buttons are effectively read-only in client mode since
    // CanSave is false — travel authority there should route through the
    // host via a future tool-call message).
    //
    // Issue #20 (chunked generation): when the exit's destination doesn't
    // exist in World.Locations (a phantom exit to a cold region), the
    // handler triggers WorldBuilderOrchestrator.GenerateRegionAsync on a
    // background task. A "Генерация региона…" overlay is shown while the
    // AI call is in flight; on success, the new region's locations are
    // committed, the phantom exit is removed (replaced by real exits in
    // CommitLocations on the next refresh), and the world is saved. The
    // player stays at their current location — they can then click the
    // new real exit to actually travel into the generated region.
    private void OnTravel(Panels.ExitRow exit)
    {
        if (_world is null) return;
        if (exit is null) return;
        if (exit.Locked)
        {
            AppendLog(LogEntry.System($"Выход «{exit.Direction}» заперт."));
            return;
        }

        // Strip the "⚠" marker the world panel appends to phantom-exit
        // destination names (issue #20). The real destination name (the
        // cold region's name, as set by the committer on the phantom
        // exit's ToName) is what we look up.
        var destName = exit.ToName;
        var phantomMarker = " \u26a0"; // "⚠"
        var isPhantom = false;
        if (destName is not null && destName.EndsWith(phantomMarker, StringComparison.Ordinal))
        {
            destName = destName[..^phantomMarker.Length];
            isPhantom = true;
        }

        // Find the destination location by display name. ExitRow stores
        // the destination's display name (resolved in WorldPanelViewModel
        // from exit.To → world.GetLocation(...).Name). Matching back by
        // name is robust to entity-id churn across save/load.
        var destLoc = _world.Locations.FirstOrDefault(l =>
            string.Equals(l.Name, destName, StringComparison.Ordinal));

        if (destLoc is null && isPhantom)
        {
            // Phantom exit to a not-yet-generated cold region — kick off
            // region generation. The player stays at the current
            // location; after generation completes, the world is
            // refreshed so the new real exits appear.
            _ = TriggerColdRegionGenerationAsync(destName ?? exit.ToName ?? string.Empty);
            return;
        }

        if (destLoc is null)
        {
            AppendLog(LogEntry.System($"Не удалось найти локацию «{exit.ToName}»."));
            return;
        }

        var player = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        if (player is null) return;

        // Mutate the world: move the player, mark the destination as
        // visited + discovered so it shows up on the map immediately.
        player.LocationId = destLoc.Id;
        destLoc.Visited = true;
        destLoc.Discovered = true;

        AppendLog(LogEntry.System($"Вы прошли в «{destLoc.Name}»."));

        // Random encounter hook: dangerous destinations may spawn hostile
        // NPCs on arrival. Simple inline implementation (the
        // random-encounters task may later expand this into a richer
        // subsystem with ambush distance, party size, terrain modifiers).
        MaybeRandomEncounter(destLoc);

        RefreshFromWorld();

        // Persist immediately in single-player/host modes so the change
        // survives a reload. Skip in client mode (no save authority).
        if (CanSave && _saveId is not null && _meta is not null)
        {
            // Persist session-tokens before the save.
            PersistTokensToMeta();
            _ = Task.Run(() =>
            {
                try { _saveManager.SaveAll(_saveId, _world, _meta, Log.ToArray()); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[save] {ex.Message}"); }
            });
        }
    }

    /// <summary>
    /// Trigger on-demand generation of a cold region (issue #20). Shows
    /// a "Генерация региона…" overlay, calls
    /// <see cref="WorldBuilderOrchestrator.GenerateRegionAsync"/> on a
    /// background task (using the save's stashed
    /// <see cref="SaveMeta.OriginalPlanJson"/>), then refreshes + saves
    /// on completion. The player stays at their current location — the
    /// new region's locations are committed with exits connecting back
    /// to the start region (so the user can click them to actually
    /// travel in).
    /// </summary>
    private async Task TriggerColdRegionGenerationAsync(string regionName)
    {
        if (_world is null || _saveId is null || _meta is null) return;
        if (string.IsNullOrWhiteSpace(regionName)) return;
        // Don't trigger if we're already generating a region (prevents
        // double-clicks from spawning parallel AI calls).
        if (IsRegionGenerating) return;

        // Reload the meta from disk to pick up the latest OriginalPlanJson
        // (in case it was patched by a rebuild or another session).
        var freshMeta = _saveManager.LoadMeta(_saveId) ?? _meta;
        if (string.IsNullOrWhiteSpace(freshMeta.OriginalPlanJson))
        {
            AppendLog(LogEntry.System(
                $"⚠ Регион «{regionName}» ещё не сгенерирован, но оригинальный план недоступен — генерация отменена."));
            return;
        }

        // Parse the original plan from the stashed JSON.
        WorldPlan? originalPlan = null;
        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            originalPlan = System.Text.Json.JsonSerializer.Deserialize<WorldPlan>(freshMeta.OriginalPlanJson, opts);
        }
        catch (Exception ex)
        {
            AppendLog(LogEntry.System(
                $"⚠ Не удалось разобрать оригинальный план мира: {ex.Message}"));
            return;
        }
        if (originalPlan is null)
        {
            AppendLog(LogEntry.System("⚠ Оригинальный план мира пуст — генерация отменена."));
            return;
        }

        IsRegionGenerating = true;
        RegionGenerationStatus = $"Генерация региона «{regionName}»…";
        AppendLog(LogEntry.System($"Генерация региона «{regionName}»…"));

        try
        {
            var settings = _settingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.Ai.ApiKey))
            {
                AppendLog(LogEntry.System("⚠ Не задан API-ключ — генерация региона невозможна."));
                return;
            }

            // Build a temporary orchestrator bound to the LIVE world
            // (not a fresh one). GenerateRegionAsync mutates the live
            // world in place via the committer.
            var ai = new AiClient(settings.Ai);
            var prompts = ServiceHost.Resolve<PromptLoader>();
            var tools = new ToolRegistry(_world);
            var orchestrator = new WorldBuilderOrchestrator(
                ai, _world, prompts, tools, aiSettings: settings.Ai);

            var progress = new Progress<WorldBuildProgress>(p =>
                Dispatcher.UIThread.Post(() => RegionGenerationStatus = p.Label));

            var result = await Task.Run(() => orchestrator.GenerateRegionAsync(
                regionName, originalPlan, progress, ct: default));

            if (result.Success)
            {
                if (result.AlreadyReady)
                {
                    AppendLog(LogEntry.System($"Регион «{regionName}» уже был готов — обновляем выходы."));
                }
                else
                {
                    AppendLog(LogEntry.System(
                        $"Регион «{regionName}» сгенерирован: +{result.LocationsAdded} лок., " +
                        $"+{result.NpcsAdded} NPC, +{result.BuildingsAdded} зд."));
                    foreach (var err in result.Errors)
                        AppendLog(LogEntry.System($"  ⚠ {err}"));
                }

                // Remove the phantom exit that triggered the generation
                // (if any) — the new real exits are now in place from
                // the committer's CommitLocations pass.
                var player = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
                if (player is not null)
                {
                    var loc = _world.GetLocation(player.LocationId);
                    if (loc is not null)
                    {
                        var phantoms = loc.Exits
                            .Where(e => e.To == EntityId.Empty
                                && string.Equals(e.ToName, regionName, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var p in phantoms) loc.Exits.Remove(p);
                    }
                }

                RefreshFromWorld();

                // Persist the updated world (with the new locations +
                // readyRegions flag).
                _meta = freshMeta; // keep the OriginalPlanJson
                PersistTokensToMeta();
                try { _saveManager.SaveAll(_saveId, _world, _meta, Log.ToArray()); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[save] {ex.Message}"); }
            }
            else
            {
                AppendLog(LogEntry.System(
                    $"⚠ Не удалось сгенерировать регион «{regionName}»: {result.Error}"));
            }
        }
        catch (Exception ex)
        {
            AppendLog(LogEntry.System($"⚠ Ошибка генерации региона: {ex.Message}"));
        }
        finally
        {
            IsRegionGenerating = false;
            RegionGenerationStatus = null;
        }
    }

    /// <summary>
    /// True while a cold-region generation is in flight (issue #20).
    /// Drives a "Генерация региона…" overlay in the game view. Set by
    /// <see cref="TriggerColdRegionGenerationAsync"/>; cleared on
    /// completion / failure.
    /// </summary>
    [ObservableProperty] private bool _isRegionGenerating;

    /// <summary>
    /// Live status text for the region-generation overlay (issue #20).
    /// Mirrors the orchestrator's progress callback label.
    /// </summary>
    [ObservableProperty] private string? _regionGenerationStatus;


    /// <summary>
    /// Handle the character panel's ExportRequested event (issue #62).
    /// Resolves the active save id + player id from this VM's runtime
    /// state, calls <see cref="CharacterSheetStore.Export"/> (which writes
    /// a portable .json sheet under <c>{ProfileDirectory}/characters/</c>),
    /// and surfaces the result path / error back to the panel via
    /// <see cref="CharacterPanelViewModel.ExportStatus"/>.
    /// </summary>
    /// <remarks>
    /// Single-player + host modes only — clients have no save authority,
    /// so the export would fail anyway. The button is still visible to
    /// clients (so they see the affordance) but a click yields a friendly
    /// «no save» error instead of a silent no-op.
    /// </remarks>
    private void OnCharacterExportRequested()
    {
        if (_world is null)
        {
            CharacterPanel.ExportStatus = "Мир не загружен — экспорт невозможен.";
            return;
        }
        if (string.IsNullOrEmpty(_saveId))
        {
            CharacterPanel.ExportStatus = "Нет активного сохранения — экспорт невозможен.";
            return;
        }
        var player = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        if (player is null)
        {
            CharacterPanel.ExportStatus = "Нет активного персонажа для экспорта.";
            return;
        }

        try
        {
            var store = ServiceHost.Resolve<CharacterSheetStore>();
            var sheet = store.Export(_saveId, player.Id);
            if (sheet is null)
            {
                CharacterPanel.ExportStatus = "Не удалось экспортировать персонажа (см. лог).";
                return;
            }
            // Compose the on-disk path the same way PathFor does, so the
            // user gets a copyable path they can find in their file
            // manager. We don't expose PathFor publicly (it's an internal
            // helper); reconstructing it here keeps the store API tight.
            var path = System.IO.Path.Combine(
                store.CharactersDirectory,
                $"{CharacterSheetStore.CharIdToString(sheet.Id)}.json");
            CharacterPanel.ExportStatus = $"Персонаж экспортирован в:\n{path}";
        }
        catch (Exception ex)
        {
            CharacterPanel.ExportStatus = $"Ошибка экспорта: {ex.Message}";
        }
    }

    /// <summary>
    /// Simple inline random-encounter roll. If the destination danger > 0
    /// and a d100 roll lands below <paramref name="destLoc"/>.Danger * 10,
    /// spawn 1–2 hostile NPCs at the destination. Terrain picks the
    /// creature template (wolves in forests, goblins in caves/underground,
    /// goblins elsewhere as a default).
    ///
    /// <para>
    /// This is the minimal hook that makes travel immediately dangerous
    /// and gives the GM something to work with. A future random-encounters
    /// task can expand this into a proper subsystem (party size scaling,
    /// ambush detection, terrain-aware creature tables, faction-based
    /// hostility).
    /// </para>
    ///
    /// <para>
    /// ENGINE-DEPTH (issue #35): at night (hour &gt;= 20 or &lt; 5) the
    /// encounter chance is bumped from Danger*10% to Danger*15% — the
    /// wild is more dangerous after dark. ENGINE-DEPTH (issue #34): rain
    /// or fog also bumps the chance to Danger*15% (low visibility makes
    /// ambushes easier).
    /// </para>
    /// </summary>
    private void MaybeRandomEncounter(Location destLoc)
    {
        if (_world is null) return;
        if (destLoc.Danger <= 0) return;

        // Danger is on a 0-10 scale; *10 gives a 0-100 percent chance.
        int chance = destLoc.Danger * 10;

        // Issue #35: night bumps the chance to Danger*15%. Issue #34:
        // rain/fog weather also bumps to Danger*15% (low visibility).
        bool isNight = GameTime.IsNight(_world.Clock.Hour);
        bool badWeather = string.Equals(_world.CurrentWeather, "rain", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(_world.CurrentWeather, "fog", StringComparison.OrdinalIgnoreCase);
        if (isNight || badWeather)
            chance = destLoc.Danger * 15;

        if (_world.Rng.NextInt(100) >= chance) return;

        // Pick the creature template based on terrain.
        string creatureTplId = destLoc.Terrain switch
        {
            "forest" => "npc_wolf",
            "swamp" => "npc_wolf",
            "cave" => "npc_goblin",
            "underground" => "npc_goblin",
            "ruin" => "npc_ghost",
            _ => "npc_goblin",
        };

        // Spawn 1-2 of them.
        int count = _world.Rng.NextInt(1, 3); // 1 or 2
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            var npc = _world.SpawnNpcFromTemplate(creatureTplId, destLoc.Id);
            if (npc is not null)
            {
                // Force hostile so the GM picks up combat cues.
                npc.Disposition = "hostile";
                spawned++;
            }
        }

        if (spawned > 0)
        {
            var label = creatureTplId == "npc_wolf" ? "волки"
                : creatureTplId == "npc_ghost" ? "призраки"
                : "гоблины";
            AppendLog(LogEntry.System(
                $"⚠ Из тени выходит угроза: {spawned} × {label}!"));
        }
    }

    // ─── HostSession event handlers (marshal to UI thread) ───────────

    private void OnMemberJoined(MemberInfo m) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Replace if present, else add.
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].ConnectionId == m.ConnectionId) { Members[i] = m; return; }
            Members.Add(m);
            AppendLog(LogEntry.System($"{m.Nickname} присоединился."));
            // Issue #77 — refresh the lobby StartGame CanExecute (a
            // newly-joined Pending member disables the button until
            // they toggle ready).
            OnPropertyChanged(nameof(AllReady));
            OnPropertyChanged(nameof(LocalMemberInfo));
            OnPropertyChanged(nameof(IsLocalReady));
            StartGameCommand.NotifyCanExecuteChanged();
        });

    private void OnMemberLeft(MemberInfo m) =>
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].ConnectionId == m.ConnectionId) { Members.RemoveAt(i); break; }
            AppendLog(LogEntry.System($"{m.Nickname} отключился."));
            // Issue #77 — refresh the lobby StartGame CanExecute (a
            // departing Pending member may have been blocking start).
            OnPropertyChanged(nameof(AllReady));
            OnPropertyChanged(nameof(LocalMemberInfo));
            OnPropertyChanged(nameof(IsLocalReady));
            StartGameCommand.NotifyCanExecuteChanged();
        });

    private void OnChatReceived(ChatMsg c) =>
        Dispatcher.UIThread.Post(() => Chat.Add(new ChatLine(c.FromNickname, c.Text, c.Ts)));

    private void OnActionQueued(PlayerAction a) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Add to the pending-actions panel (replaces nothing; we
            // don't dedupe — the host's own action is also echoed here
            // via the broadcast).
            PendingActions.Add(a);
        });

    private void OnActionCancelledHost(string? id) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (id is null) return;
            for (int i = 0; i < PendingActions.Count; i++)
                if (PendingActions[i].Id == id) { PendingActions.RemoveAt(i); break; }
        });

    private void OnTurnStarted(System.Collections.Generic.IReadOnlyList<string> ids) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsWaiting = true;
            StatusText = "ГМ обрабатывает ход…";
        });

    private void OnNarrativeDelta(string delta, int turn) =>
        Dispatcher.UIThread.Post(() =>
        {
            // In the non-streaming GM, the delta is the full text —
            // we just append it as a single narrative entry. The
            // NarrativeFinal handler below dedupes by replacing it.
            // For now: do nothing on delta; the final is authoritative.
        });

    private void OnNarrativeFinal(NarrativeFinalMsg msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(msg.FullText))
                AppendLog(LogEntry.Narrative(msg.FullText));
            foreach (var tc in msg.ToolEvents)
            {
                AppendLog(LogEntry.Tool($"{tc.Name}: {tc.Result}",
                    metadata: new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["name"] = tc.Name,
                        ["args"] = tc.ArgsJson,
                        ["isError"] = tc.IsError,
                    }));

                // Issue #70 — same quest-completion notification as in
                // the single-player turn flow. See the corresponding
                // block in RunSinglePlayerTurnAsync for details.
                if (tc.Name == "update_quest" && !tc.IsError)
                {
                    var questName = TryExtractQuestCompletedName(tc.Result);
                    if (questName is not null)
                    {
                        AppendLog(LogEntry.System(
                            $"Квест «{questName}» завершён! Награда доступна во вкладке Квесты."));
                    }
                }
            }
            // Clear the pending-actions panel (the turn is done).
            PendingActions.Clear();
            IsWaiting = false;
            StatusText = null;
            // Accumulate token billing for this turn into the VM's
            // session totals + last-turn counter. (Persistence is handled
            // by HostSession itself, which writes the cumulative totals
            // into _meta on its own save path.)
            AccumulateTokens(msg.PromptTokens, msg.CompletionTokens, msg.TotalTokens);
            RefreshFromHostWorld();
        });

    private void OnHostStateUpdate(StateUpdateMsg _) =>
        Dispatcher.UIThread.Post(RefreshFromHostWorld);

    private void OnTurnEnded(int turn) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsWaiting = false;
            StatusText = null;
            if (HostSession is not null)
                foreach (var m in HostSession.Members) { /* noop */ }
            // Detect a level-up that occurred during this host turn and
            // toast it. RefreshFromHostWorld has already pushed the new
            // state to all panels via OnNarrativeFinal.
            CheckLevelUpAndToast();
        });

    private void OnTurnFailed(string err) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsWaiting = false;
            StatusText = null;
            ErrorMessage = $"Ход не удался: {err}";
            AppendLog(LogEntry.System($"Ошибка хода: {err}"));
        });

    /// <summary>
    /// HostSession raises this every second while the batching window
    /// (issue #12) is counting down. We mirror the value into the
    /// <see cref="BatchCountdown"/> observable property so the UI can
    /// show "Следующий ход через Ns..." (0 hides the indicator). For
    /// single-player-host, the window fires immediately and this event
    /// is never raised (BatchCountdown stays at 0).
    /// </summary>
    private void OnHostBatchCountdownChanged(int secondsRemaining) =>
        Dispatcher.UIThread.Post(() => BatchCountdown = secondsRemaining);

    // ─── Lobby event handlers (issue #77) ────────────────────────────

    /// <summary>
    /// Host-side: a client toggled their ready state in the lobby.
    /// Update the local Members list with the new MemberInfo (the
    /// HostServer already replaced the entry on its side; we just mirror
    /// it here) + refresh the lobby UI's derived properties.
    /// </summary>
    private void OnMemberReady(MemberInfo m) =>
        Dispatcher.UIThread.Post(() =>
        {
            ReplaceMember(m);
            // ReplaceMember already fires the change notifications +
            // StartGameCommand.NotifyCanExecuteChanged().
        });

    /// <summary>
    /// Host-side: party status changed (host clicked «Начать игру»,
    /// transitioning to Playing). Refresh <see cref="IsLobby"/> so the
    /// lobby layout disappears + the normal game layout appears.
    /// </summary>
    private void OnHostStatusChanged(PartyStatus status) =>
        Dispatcher.UIThread.Post(RefreshIsLobby);

    /// <summary>
    /// Client-side: another member (or the host echoing back this
    /// client's own toggle) toggled ready. The MemberReadyMsg carries
    /// the connection id + new ready state; we look up the member in
    /// our local Members list and replace it with an updated copy.
    /// </summary>
    private void OnClientMemberReady(MemberReadyMsg msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Members.Count; i++)
            {
                if (Members[i].ConnectionId == msg.ConnectionId)
                {
                    Members[i] = Members[i] with
                    {
                        Status = msg.Ready ? MemberStatus.Ready : MemberStatus.Pending,
                    };
                    break;
                }
            }
            // Refresh the lobby UI's derived properties so the local
            // ready indicator + the host's StartGame button CanExecute
            // (when this client is the host — unlikely since this is
            // the client-side handler, but defensive) re-evaluate.
            OnPropertyChanged(nameof(LocalMemberInfo));
            OnPropertyChanged(nameof(IsLocalReady));
            OnPropertyChanged(nameof(AllReady));
        });

    /// <summary>
    /// Client-side: host transitioned the party status (Lobby →
    /// Playing). Refresh <see cref="IsLobby"/> so the lobby layout
    /// disappears + the normal game layout appears. Also log a system
    /// entry so the user sees a "game started" message in the narrative
    /// log when the transition happens.
    /// </summary>
    private void OnClientStatusChanged(StatusChangedMsg s) =>
        Dispatcher.UIThread.Post(() =>
        {
            RefreshIsLobby();
            if (s.Status == PartyStatus.Playing)
            {
                AppendLog(LogEntry.System("Хост начал игру!"));
            }
        });

    // ─── ClientSession event handlers ────────────────────────────────

    private void OnClientWelcomed(WelcomeMsg w) =>
        Dispatcher.UIThread.Post(() =>
        {
            Members.Clear();
            foreach (var m in w.Party.Members) Members.Add(m);
            GameName = $"Подключено (ход {w.Party.Turn})";
            AppendLog(LogEntry.System("Рукопожатие выполнено."));
            // Issue #77 — refresh IsLobby + lobby-derived properties
            // from the fresh party snapshot the host sent in the
            // WelcomeMsg. Covers the case where the host has already
            // started the game by the time this client connects (rare
            // but possible if the host clicks StartGame between this
            // client's HelloMsg send + WelcomeMsg receive).
            RefreshIsLobby();
        });

    private void OnClientMemberJoined(MemberJoinedMsg m) =>
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].ConnectionId == m.Member.ConnectionId) { Members[i] = m.Member; return; }
            Members.Add(m.Member);
            AppendLog(LogEntry.System($"{m.Member.Nickname} присоединился."));
            // Issue #77 — refresh lobby derived props (AllReady may
            // have changed).
            OnPropertyChanged(nameof(AllReady));
            OnPropertyChanged(nameof(LocalMemberInfo));
            OnPropertyChanged(nameof(IsLocalReady));
        });

    private void OnClientMemberLeft(MemberLeftMsg m) =>
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].ConnectionId == m.ConnectionId) { Members.RemoveAt(i); break; }
            // Issue #77 — refresh lobby derived props.
            OnPropertyChanged(nameof(AllReady));
            OnPropertyChanged(nameof(LocalMemberInfo));
            OnPropertyChanged(nameof(IsLocalReady));
        });

    private void OnClientChatReceived(ChatMsg c) =>
        Dispatcher.UIThread.Post(() => Chat.Add(new ChatLine(c.FromNickname, c.Text, c.Ts)));

    private void OnClientActionQueued(ActionQueuedMsg msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            PendingActions.Add(new PlayerAction
            {
                Id = msg.ActionId,
                PlayerId = msg.FromId,
                PlayerNickname = msg.FromNickname,
                Text = msg.Text,
                SubmittedAt = msg.Ts,
            });
        });

    private void OnClientActionCancelled(ActionCancelledMsg msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < PendingActions.Count; i++)
                if (PendingActions[i].Id == msg.ActionId) { PendingActions.RemoveAt(i); break; }
        });

    private void OnClientActionResolving(ActionResolvingMsg msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsWaiting = true;
            StatusText = "ГМ хоста обрабатывает ход…";
            // Remove the resolving actions from the pending list.
            var ids = new System.Collections.Generic.HashSet<string>(msg.ActionIds);
            for (int i = PendingActions.Count - 1; i >= 0; i--)
                if (ids.Contains(PendingActions[i].Id)) PendingActions.RemoveAt(i);
        });

    private void OnClientNarrativeDelta(NarrativeDeltaMsg _) =>
        Dispatcher.UIThread.Post(() => { /* see host-side: no-op until final */ });

    private void OnClientNarrativeFinal(NarrativeFinalMsg msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(msg.FullText))
                AppendLog(LogEntry.Narrative(msg.FullText));
            foreach (var tc in msg.ToolEvents)
            {
                AppendLog(LogEntry.Tool($"{tc.Name}: {tc.Result}",
                    metadata: new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["name"] = tc.Name,
                        ["args"] = tc.ArgsJson,
                        ["isError"] = tc.IsError,
                    }));

                // Issue #70 — same quest-completion notification as in
                // single-player / host flows. The client receives the
                // host's tool events; if a quest was completed, surface
                // the "go claim" notification to the local player.
                if (tc.Name == "update_quest" && !tc.IsError)
                {
                    var questName = TryExtractQuestCompletedName(tc.Result);
                    if (questName is not null)
                    {
                        AppendLog(LogEntry.System(
                            $"Квест «{questName}» завершён! Награда доступна во вкладке Квесты."));
                    }
                }
            }
            PendingActions.Clear();
            IsWaiting = false;
            StatusText = null;
            if (ClientSession?.LocalWorld is { } w)
            {
                _world = w;
                RefreshFromWorld();
            }
        });

    private void OnClientStateUpdate(StateUpdateMsg _) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ClientSession?.LocalWorld is { } w)
            {
                _world = w;
                RefreshFromWorld();
            }
        });

    private void OnClientTurnEnd(TurnEndMsg msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            IsWaiting = false;
            StatusText = null;
            GameName = $"Подключено (ход {msg.TurnNumber})";
        });

    private void OnClientError(ErrorMsg e) =>
        Dispatcher.UIThread.Post(() => ErrorMessage = $"Ошибка хоста: {e.Message}");

    private void OnClientKicked(KickedMsg k) =>
        Dispatcher.UIThread.Post(() =>
        {
            ErrorMessage = $"Вас кикнули: {k.Reason}";
            AppendLog(LogEntry.System($"Кикнут: {k.Reason}"));
        });

    /// <summary>
    /// Issue #32: late joiner log sync. Populate the local log with the
    /// history sent by the host.
    /// </summary>
    private void OnClientLogSynced(LogSyncMsg msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var entry in msg.Entries)
            {
                Log.Add(LogEntry.Narrative(entry));
            }
        });

    private void OnClientDisconnected(DisconnectedInfo info) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Intentional disconnect (user clicked Leave, or the host
            // kicked us): don't show the reconnect overlay. The Leave
            // command already navigates to menu; the Kicked event
            // already surfaced an inline error via OnClientKicked.
            if (info.Intentional)
            {
                AppendLog(LogEntry.System($"Соединение разорвано: {info.Reason}"));
                return;
            }

            // Unexpected network drop: show the reconnect overlay.
            IsDisconnected = true;
            ReconnectFailed = false;
            DisconnectReason = info.Reason ?? string.Empty;
            ErrorMessage = $"Соединение потеряно: {info.Reason}";
            AppendLog(LogEntry.System($"Разрыв соединения: {info.Reason}"));
        });
}

/// <summary>
/// One chat line. Plain immutable record for the chat panel.
/// </summary>
public sealed record ChatLine(string Author, string Text, DateTimeOffset Ts);
