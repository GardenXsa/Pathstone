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

    // ─── Sessions (one of these is non-null depending on mode) ───────
    public HostSession? HostSession { get; private set; }
    public ClientSession? ClientSession { get; private set; }
    private CancellationTokenSource? _hostShutdownCts;

    // ─── Single-player runtime state ─────────────────────────────────
    private GameMaster? _gm;
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
    /// before every save so the totals persist across reloads. No-op when
    /// <see cref="_meta"/> is null.
    /// </summary>
    private void PersistTokensToMeta()
    {
        if (_meta is null) return;
        _meta = _meta with
        {
            SessionPromptTokens = SessionPromptTokens,
            SessionCompletionTokens = SessionCompletionTokens,
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

        _world = loaded.Value.world;
        _meta = loaded.Value.meta;
        var log = loaded.Value.log;

        lock (_logLock) _log.AddRange(log);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Log.Clear();
            foreach (var e in log) Log.Add(e);
        });

        var settings = _settingsStore.Load();
        var ai = new AiClient(settings.Ai);
        var prompts = ServiceHost.Resolve<PromptLoader>();
        var tools = new ToolRegistry(_world);
        _gm = new GameMaster(ai, _world, prompts, tools, settings.MaxToolIterations);

        GameName = _meta?.Name ?? "Игра";
        CanSave = true;
        // Restore session-tokens from the save so the top-bar counter
        // survives a reload. Last-turn counter is reset (a fresh session
        // has no "last turn" yet).
        RestoreTokensFromMeta();
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
        session.Disconnected += OnClientDisconnected;

        AppendLog(LogEntry.System("Подключено к хосту."));
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
                // Drive a turn right away (the host is the only player
                // in the typical case).
                HostSession.ProcessNextTurnAsync().FireAndForget();
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

    private bool CanSubmitAction() => CanSubmit && !IsWaiting;

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

    /// <summary>
    /// Leave the game and return to the main menu. Host: stop the
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
        }

        // Accumulate token billing for this turn into the session totals
        // + last-turn counter, and refresh the top-bar display.
        AccumulateTokens(result.PromptTokens, result.CompletionTokens, result.TotalTokens);

        // Increment turn + save.
        _world.Turn++;
        if (_meta is not null && _saveId is not null)
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
            ClockDisplay = _world.Clock.ToString();
            var p = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
            CharacterSummary = BuildCharacterSummary(p);
            WorldInfo = BuildWorldInfo(_world, p);

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

    // ─── Travel handler ──────────────────────────────────────────────
    //
    // The world panel raises TravelRequested when the user clicks an exit
    // row. We apply it directly to the live World (single-player + host
    // modes), mirroring OnInventoryAction's pattern: mutate, log, refresh
    // panels, persist async. Client mode is a no-op for world mutations
    // (the panel buttons are effectively read-only in client mode since
    // CanSave is false — travel authority there should route through the
    // host via a future tool-call message).
    private void OnTravel(Panels.ExitRow exit)
    {
        if (_world is null) return;
        if (exit is null) return;
        if (exit.Locked)
        {
            AppendLog(LogEntry.System($"Выход «{exit.Direction}» заперт."));
            return;
        }

        // Find the destination location by display name. ExitRow stores
        // the destination's display name (resolved in WorldPanelViewModel
        // from exit.To → world.GetLocation(...).Name). Matching back by
        // name is robust to entity-id churn across save/load.
        var destLoc = _world.Locations.FirstOrDefault(l =>
            string.Equals(l.Name, exit.ToName, StringComparison.Ordinal));
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
    /// </summary>
    private void MaybeRandomEncounter(Location destLoc)
    {
        if (_world is null) return;
        if (destLoc.Danger <= 0) return;

        // Danger is on a 0-10 scale; *10 gives a 0-100 percent chance.
        int chance = destLoc.Danger * 10;
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
        });

    private void OnMemberLeft(MemberInfo m) =>
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].ConnectionId == m.ConnectionId) { Members.RemoveAt(i); break; }
            AppendLog(LogEntry.System($"{m.Nickname} отключился."));
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
                AppendLog(LogEntry.Tool($"{tc.Name}: {tc.Result}",
                    metadata: new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["name"] = tc.Name,
                        ["args"] = tc.ArgsJson,
                        ["isError"] = tc.IsError,
                    }));
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

    // ─── ClientSession event handlers ────────────────────────────────

    private void OnClientWelcomed(WelcomeMsg w) =>
        Dispatcher.UIThread.Post(() =>
        {
            Members.Clear();
            foreach (var m in w.Party.Members) Members.Add(m);
            GameName = $"Подключено (ход {w.Party.Turn})";
            AppendLog(LogEntry.System("Рукопожатие выполнено."));
        });

    private void OnClientMemberJoined(MemberJoinedMsg m) =>
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].ConnectionId == m.Member.ConnectionId) { Members[i] = m.Member; return; }
            Members.Add(m.Member);
            AppendLog(LogEntry.System($"{m.Member.Nickname} присоединился."));
        });

    private void OnClientMemberLeft(MemberLeftMsg m) =>
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].ConnectionId == m.ConnectionId) { Members.RemoveAt(i); break; }
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
                AppendLog(LogEntry.Tool($"{tc.Name}: {tc.Result}",
                    metadata: new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["name"] = tc.Name,
                        ["args"] = tc.ArgsJson,
                        ["isError"] = tc.IsError,
                    }));
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

    private void OnClientDisconnected(string reason) =>
        Dispatcher.UIThread.Post(() =>
        {
            ErrorMessage = $"Соединение разорвано: {reason}";
            AppendLog(LogEntry.System($"Разрыв соединения: {reason}"));
        });
}

/// <summary>
/// One chat line. Plain immutable record for the chat panel.
/// </summary>
public sealed record ChatLine(string Author, string Text, DateTimeOffset Ts);
