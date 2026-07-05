using System.Diagnostics;
using System.Text;
using MyGame.Core.AI.Agents;
using MyGame.Core.Multiplayer.Protocol;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;

namespace MyGame.Core.Multiplayer;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). Same alias trick as SaveManager.cs — alias
// to a different name to sidestep the namespace/type collision.
using GameWorld = MyGame.Core.World.World;

/// <summary>
/// High-level orchestrator that glues together the host's moving parts:
/// <see cref="HostServer"/> (transport) + <see cref="GameMaster"/> (AI) +
/// <see cref="SaveManager"/> (persistence) + <see cref="ActionQueue"/>
/// (player inputs).
///
/// <para>
/// The UI layer talks to <see cref="HostSession"/> exclusively — it never
/// touches <see cref="HostServer"/> or <see cref="GameMaster"/> directly.
/// The session exposes a clean event-driven API:
/// </para>
/// <list type="bullet">
///   <item><see cref="SubmitActionAsync"/> — local host player submits an action.</item>
///   <item><see cref="CancelActionAsync"/> — cancel a pending action.</item>
///   <item><see cref="ProcessNextTurnAsync"/> — drain the queue + run the GM +
///     broadcast results + save state. Called by the UI's "Next Turn" button
///     or by an idle-loop timer.</item>
///   <item><see cref="SetStatusAsync"/> — transition the party lifecycle
///     (Lobby → Worldbuilding → …).</item>
///   <item>Events for member join/leave, chat, action queued, narrative
///     delta/final, state update, turn end — all raised on the host's
///     thread (UI subscribers must marshal to the UI thread).</item>
/// </list>
///
/// <para><b>Single turn at a time:</b> the session uses a
/// <see cref="SemaphoreSlim"/> to ensure only one GM turn runs at a time.
/// Concurrent <see cref="ProcessNextTurnAsync"/> calls (e.g. UI button
/// spam) are serialised — the second call waits for the first to finish,
/// then drains whatever's in the queue at that point (which may be empty,
/// in which case it returns -1 immediately).</para>
/// </summary>
public sealed class HostSession
{
    // ─── Config ──────────────────────────────────────────────────────

    /// <summary>
    /// Default batching window in seconds (issue #12). When the first
    /// player action lands in the queue, the host waits up to this many
    /// seconds for other ready non-spectator members to submit their
    /// actions before draining the queue and running the GM. When all
    /// ready members have submitted (or there's only one ready member),
    /// the window fires immediately. Set to 0 to disable batching
    /// entirely (drain + run on every action).
    /// </summary>
    public const int DefaultBatchWindowSeconds = 5;

    private readonly Profile.Profile _hostProfile;
    private readonly GameWorld _world;
    private readonly GameMaster _gm;
    private readonly SaveManager? _saveManager;
    private readonly string? _saveId;
    private readonly int _requestedPort;
    private readonly string _bindHost;
    private readonly CancellationToken _shutdownToken;

    // ─── Runtime state ───────────────────────────────────────────────

    private readonly HostServer _server;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    /// <summary>
    /// In-memory log. Appended to on every action + narrative result.
    /// Persisted to disk via <see cref="SaveManager.SaveAll"/> after each
    /// turn (when <see cref="_saveManager"/> + <see cref="_saveId"/> are
    /// both set).
    /// </summary>
    private readonly List<LogEntry> _log = new();

    /// <summary>
    /// Current save meta. Loaded at startup if <see cref="_saveId"/> is
    /// set + the save exists; otherwise created lazily on the first
    /// <see cref="ProcessNextTurnAsync"/> call (so a brand-new game
    /// doesn't write to disk until something actually happens).
    /// </summary>
    private SaveMeta? _meta;

    /// <summary>
    /// Per-save cumulative prompt (input) tokens billed across all
    /// sessions on this save. Restored from <see cref="_meta"/> on
    /// <see cref="StartAsync"/>; accumulated after each GM turn; written
    /// back to <see cref="_meta"/> before each save. The host UI reads
    /// its display counter from the <see cref="NarrativeFinalMsg"/> event
    /// (which carries per-turn counts), so this field is purely for
    /// persistence.
    /// </summary>
    private int _sessionPromptTokens;

    /// <summary>
    /// Per-save cumulative completion (output) tokens. See
    /// <see cref="_sessionPromptTokens"/>.
    /// </summary>
    private int _sessionCompletionTokens;

    // ─── Batching window state (issue #12) ───────────────────────────
    //
    // When the first player action lands in the queue, the host starts a
    // batching window (DefaultBatchWindowSeconds, default 5). The window
    // lets other ready non-spectator members submit their actions before
    // the GM drains the queue. The window fires ProcessNextTurnAsync
    // immediately when all ready members have submitted, OR after the
    // timeout expires. Single-player-host (1 ready member) fires
    // immediately — no countdown.
    //
    // _batchLock guards _batchWindowCts / _batchSubmitted /
    // _batchReadyCount. The lock is held only briefly (no I/O, no event
    // raises) — the actual ProcessNextTurnAsync call happens outside the
    // lock to avoid re-entrancy with the turn lock.
    private readonly object _batchLock = new();
    private CancellationTokenSource? _batchWindowCts;
    private readonly HashSet<Guid> _batchSubmitted = new();
    private int _batchReadyCount;

    /// <summary>
    /// True after <see cref="StartAsync"/> has been called.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Create a host session. Does NOT start the server — call
    /// <see cref="StartAsync"/> to bind the WebSocket.
    /// </summary>
    /// <param name="hostProfile">The local host player's profile.</param>
    /// <param name="world">The world to play in. Mutated in-place by the
    ///     GameMaster's tool calls.</param>
    /// <param name="gm">The GameMaster bound to <paramref name="world"/>.
    ///     Must have been constructed with the same World instance.</param>
    /// <param name="saveManager">Optional save manager. When null,
    ///     turn-end state is broadcast to clients but NOT persisted to
    ///     disk (useful for ephemeral games / tests).</param>
    /// <param name="saveId">Optional save id. When null, a new save is
    ///     created lazily on the first turn (only if
    ///     <paramref name="saveManager"/> is provided).</param>
    /// <param name="requestedPort">TCP port for the WebSocket server.
    ///     0 = OS-assigned.</param>
    /// <param name="shutdownToken">Cancellation token that triggers
    ///     graceful shutdown of the server + any in-flight GM turn.</param>
    /// <param name="bindHost">HttpListener bind host. "+" = all
    ///     interfaces (LAN-playable); "localhost" = single-machine.</param>
    public HostSession(
        Profile.Profile hostProfile,
        GameWorld world,
        GameMaster gm,
        SaveManager? saveManager = null,
        string? saveId = null,
        int requestedPort = 0,
        CancellationToken shutdownToken = default,
        string bindHost = "+")
    {
        _hostProfile = hostProfile ?? throw new ArgumentNullException(nameof(hostProfile));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gm = gm ?? throw new ArgumentNullException(nameof(gm));
        _saveManager = saveManager;
        _saveId = saveId;
        _requestedPort = requestedPort;
        _bindHost = string.IsNullOrWhiteSpace(bindHost) ? "+" : bindHost;
        _shutdownToken = shutdownToken;

        _server = new HostServer(hostProfile, requestedPort, shutdownToken, bindHost);

        // Bubble up HostServer events so the UI can subscribe to a
        // single object. Each subscriber is exception-safe (the
        // HostServer already wraps dispatch in try/catch, so this is
        // belt-and-suspenders).
        _server.MemberJoined += m => RaiseEvent(MemberJoined, m);
        // Issue #32: send last 20 log entries to the new joiner so they
        // have context. Fire-and-forget (best-effort — if the send fails,
        // the client just gets a shorter history).
        _server.MemberJoined += async m =>
        {
            try
            {
                var tail = _log.Skip(Math.Max(0, _log.Count - 20))
                    .Select(e => e.Text)
                    .ToList();
                if (tail.Count > 0)
                {
                    await _server.SendAsync(m.ConnectionId, new LogSyncMsg { Entries = tail }, default);
                }
            }
            catch { /* best-effort */ }
        };
        _server.MemberLeft += m => RaiseEvent(MemberLeft, m);
        _server.ChatReceived += c => RaiseEvent(ChatReceived, c);
        _server.ActionQueued += a => RaiseEvent(ActionQueued, a);
        _server.ActionCancelled += id => RaiseEventNullable(ActionCancelled, id);
        // Issue #77 — bubble up the lobby-ready + status-changed events
        // so the host UI can react to lobby state changes (members
        // toggling ready, host starting the game). Without these events,
        // the host UI would have to poll the Members list + Status
        // property — wasteful and prone to races.
        _server.MemberReady += m => RaiseEvent(MemberReady, m);
        _server.StatusChanged += s => RaiseEvent(StatusChanged, s);
        // Batching window (issue #12): every queued action starts (or
        // advances) a batching window. The window fires ProcessNextTurnAsync
        // after DefaultBatchWindowSeconds OR when all ready non-spectator
        // members have submitted. See OnActionQueuedBatching.
        _server.ActionQueued += OnActionQueuedBatching;
    }

    // ─── Properties ──────────────────────────────────────────────────

    /// <summary>The underlying host server. Exposed for advanced
    /// scenarios (e.g. direct send to a specific client). The UI layer
    /// should normally use the HostSession API instead.</summary>
    public HostServer Server => _server;

    /// <summary>The world being played. Mutated by the GameMaster's tool
    /// calls during <see cref="ProcessNextTurnAsync"/>.</summary>
    public GameWorld World => _world;

    /// <summary>The GameMaster. Exposed for advanced scenarios (e.g.
    /// direct history inspection).</summary>
    public GameMaster GameMaster => _gm;

    /// <summary>The action queue. Same as <see cref="HostServer.ActionQueue"/>.</summary>
    public ActionQueue ActionQueue => _server.ActionQueue;

    /// <summary>The TCP port the server is listening on. Valid only after
    /// <see cref="StartAsync"/> completes.</summary>
    public int Port => _server.Port;

    /// <summary>Current lobby roster.</summary>
    public IReadOnlyList<MemberInfo> Members => _server.Members;

    /// <summary>Current party status.</summary>
    public PartyStatus Status => _server.Status;

    /// <summary>Host's own connection id.</summary>
    public Guid HostConnectionId => _server.HostConnectionId;

    /// <summary>Current in-memory log (read-only snapshot).</summary>
    public IReadOnlyList<LogEntry> Log
    {
        get { lock (_log) return _log.ToArray(); }
    }

    /// <summary>
    /// Cumulative prompt (input) tokens billed across all sessions on
    /// this save. Restored from <see cref="SaveMeta.SessionPromptTokens"/>
    /// on <see cref="StartAsync"/>; accumulated after each GM turn; used
    /// by the host UI to seed its top-bar token counter so it survives
    /// reload. Persisted into meta on every save.
    /// </summary>
    public int SessionPromptTokens => _sessionPromptTokens;

    /// <summary>
    /// Cumulative completion (output) tokens. See
    /// <see cref="SessionPromptTokens"/>.
    /// </summary>
    public int SessionCompletionTokens => _sessionCompletionTokens;

    /// <summary>
    /// Convenience: sum of <see cref="SessionPromptTokens"/> +
    /// <see cref="SessionCompletionTokens"/>. Used by the host UI's
    /// "Сессия: Nk токенов" display.
    /// </summary>
    public int SessionTotalTokens => _sessionPromptTokens + _sessionCompletionTokens;

    // ─── Events ──────────────────────────────────────────────────────

    /// <summary>A new member joined the party.</summary>
    public event Action<MemberInfo>? MemberJoined;

    /// <summary>A member left the party.</summary>
    public event Action<MemberInfo>? MemberLeft;

    /// <summary>A chat message was received.</summary>
    public event Action<ChatMsg>? ChatReceived;

    /// <summary>An action was queued (by anyone — host or client).</summary>
    public event Action<PlayerAction>? ActionQueued;

    /// <summary>An action was cancelled. Passes the action id (or null
    /// if the id wasn't in the queue).</summary>
    public event Action<string?>? ActionCancelled;

    /// <summary>Raised at the start of a GM turn — the host is about to
    /// call the GM with the batched actions. Carries the action ids being
    /// resolved. UI can use this to disable the "Next Turn" button.</summary>
    public event Action<IReadOnlyList<string>>? TurnStarted;

    /// <summary>Raised when a narrative delta is broadcast to clients.
    /// The host UI can also append to its own narration buffer here
    /// (it doesn't get this via the WebSocket because it has no client).</summary>
    public event Action<string, int>? NarrativeDelta;

    /// <summary>Raised at the end of a GM turn with the full narration +
    /// tool events. Same payload as the <see cref="NarrativeFinalMsg"/>
    /// broadcast to clients.</summary>
    public event Action<NarrativeFinalMsg>? NarrativeFinal;

    /// <summary>Raised when a state update is broadcast to clients.
    /// Carries the world JSON + turn number.</summary>
    public event Action<StateUpdateMsg>? StateUpdate;

    /// <summary>Raised when a GM turn ends. Carries the turn number.</summary>
    public event Action<int>? TurnEnded;

    /// <summary>Raised if a GM turn fails (AI exception, etc.). Carries
    /// the error message. The turn is aborted; the action queue is left
    /// intact (the actions are NOT re-drained — caller can retry or
    /// cancel them).</summary>
    public event Action<string>? TurnFailed;

    /// <summary>
    /// Raised every second while the batching window (issue #12) is
    /// counting down. Carries the seconds remaining (5 → 4 → 3 → 2 → 1).
    /// A final 0 is raised when the window fires (either by timeout, by
    /// all-ready-submitted, or by shutdown). Not raised at all when the
    /// window fires immediately (single-player-host or
    /// <see cref="DefaultBatchWindowSeconds"/> = 0). UI subscribers should
    /// marshal to the UI thread — the event fires on a ThreadPool thread.
    /// </summary>
    public event Action<int>? BatchCountdownChanged;

    /// <summary>
    /// Raised when a client toggles their ready state in the lobby
    /// (issue #77). Carries the updated <see cref="MemberInfo"/>.
    /// Bubbled up from <see cref="HostServer.MemberReady"/>. The host
    /// UI subscribes to refresh its lobby members list.
    /// </summary>
    public event Action<MemberInfo>? MemberReady;

    /// <summary>
    /// Raised when the party lifecycle status changes (issue #77).
    /// Carries the new <see cref="PartyStatus"/>. Bubbled up from
    /// <see cref="HostServer.StatusChanged"/>. The host UI subscribes
    /// to refresh its <c>IsLobby</c> flag.
    /// </summary>
    public event Action<PartyStatus>? StatusChanged;

    // ─── Lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Start the host server + load the save's existing log/meta (if
    /// applicable). Returns the actual port the server is listening on.
    /// </summary>
    public async Task<int> StartAsync()
    {
        if (IsRunning) throw new InvalidOperationException("HostSession is already running.");

        // If a save id was given, load the existing meta + log so the
        // session continues from where the save left off.
        if (_saveManager is not null && !string.IsNullOrEmpty(_saveId))
        {
            var loaded = _saveManager.LoadAll(_saveId);
            if (loaded is { } tuple)
            {
                _meta = tuple.meta;
                lock (_log) _log.AddRange(tuple.log);
                // Restore session-tokens from the save so the counter
                // survives reload.
                _sessionPromptTokens = tuple.meta.SessionPromptTokens;
                _sessionCompletionTokens = tuple.meta.SessionCompletionTokens;
            }
        }

        var port = await _server.StartAsync().ConfigureAwait(false);
        IsRunning = true;

        // Issue #29: attempt UPnP port forwarding (best-effort). If
        // successful, UpnpPublicAddress is set and the host UI can show
        // the public IP:port for internet play. If it fails (no router,
        // router doesn't support UPnP), UpnpPublicAddress stays null and
        // the host shows the local address (manual forwarding required).
        try
        {
            var upnp = await UpnpForwarder.TryForwardAsync(port, port, "Pathstone", default).ConfigureAwait(false);
            if (upnp is not null)
            {
                UpnpPublicAddress = upnp.PublicAddress;
            }
        }
        catch { /* best-effort — silent */ }

        return port;
    }

    /// <summary>
    /// Public IP:port from UPnP forwarding (issue #29). Null if UPnP
    /// failed (host shows local address; user must forward manually).
    /// </summary>
    public string? UpnpPublicAddress { get; private set; }

    /// <summary>
    /// Graceful shutdown: stop the host server, cancel any in-flight GM
    /// turn, save state (if a save manager is configured). Safe to call
    /// multiple times.
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        // Save the final state before shutting down (best-effort).
        if (_saveManager is not null && _saveId is not null && _meta is not null)
        {
            try
            {
                LogEntry[] logSnapshot;
                lock (_log) logSnapshot = _log.ToArray();
                // Persist session-tokens into meta before the final save.
                _meta = _meta with
                {
                    SessionPromptTokens = _sessionPromptTokens,
                    SessionCompletionTokens = _sessionCompletionTokens,
                };
                _saveManager.SaveAll(_saveId, _world, _meta, logSnapshot);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HostSession] Final save failed: {ex}");
            }
        }

        await _server.StopAsync().ConfigureAwait(false);
    }

    // ─── Player actions ──────────────────────────────────────────────

    /// <summary>
    /// Submit an action on behalf of the host player. Generates an
    /// action id, enqueues it in the action queue, broadcasts
    /// <see cref="ActionQueuedMsg"/> to all clients, and raises
    /// <see cref="ActionQueued"/> locally.
    /// </summary>
    public Task SubmitActionAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Action text cannot be empty.", nameof(text));

        var actionId = NewActionId();
        var ts = DateTimeOffset.UtcNow;
        return _server.EnqueueHostActionAsync(actionId, text, ts, ct);
    }

    /// <summary>
    /// Cancel a pending action by id. Returns true if the action was
    /// found and removed; false otherwise.
    /// </summary>
    public async Task<bool> CancelActionAsync(string actionId, CancellationToken ct = default)
    {
        // HostServer.CancelActionAsync doesn't return the bool — call the
        // queue directly to check, then broadcast.
        var ok = _server.ActionQueue.Cancel(actionId);
        if (ok)
        {
            await _server.BroadcastAsync(new ActionCancelledMsg { ActionId = actionId }, ct).ConfigureAwait(false);
        }
        RaiseEventNullable(ActionCancelled, ok ? actionId : null);
        return ok;
    }

    /// <summary>
    /// Send a lobby chat message on behalf of the host player.
    /// </summary>
    public async Task SendChatAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var trimmed = text.Trim();
        if (trimmed.Length > 1000) trimmed = trimmed[..1000];

        var msg = new ChatMsg
        {
            FromId = _server.HostConnectionId,
            FromNickname = _hostProfile.Nickname,
            Text = trimmed,
            Ts = DateTimeOffset.UtcNow,
        };
        await _server.BroadcastAsync(msg, ct).ConfigureAwait(false);
        RaiseEvent(ChatReceived, msg);
    }

    /// <summary>
    /// Transition the party lifecycle status. Broadcasts
    /// <see cref="StatusChangedMsg"/> to all clients.
    /// </summary>
    public Task SetStatusAsync(PartyStatus status, CancellationToken ct = default) =>
        _server.SetStatusAsync(status, _saveId, _world.Turn, ct);

    /// <summary>
    /// Kick a member from the party (issue #30). Delegates to
    /// <see cref="HostServer.KickAsync"/>.
    /// </summary>
    public Task KickAsync(Guid connectionId, string reason, CancellationToken ct = default) =>
        _server.KickAsync(connectionId, reason, ct);

    // ─── GM turn ─────────────────────────────────────────────────────

    /// <summary>
    /// Drain the action queue + run one GM turn with the batched
    /// actions + broadcast results. Returns the new turn number, or -1
    /// if the queue was empty (no work to do).
    ///
    /// <para>Concurrent calls are serialised by an async semaphore —
    /// only one GM turn runs at a time. A second call waits for the
    /// first to finish, then drains whatever's in the queue at that
    /// point (which may be empty, returning -1).</para>
    /// </summary>
    public async Task<int> ProcessNextTurnAsync(CancellationToken ct = default)
    {
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drain the queue INSIDE the lock so a racing SubmitAction
            // can't sneak into the queue after we've decided it's empty.
            var actions = _server.ActionQueue.DrainAll();
            if (actions.Count == 0) return -1;

            var actionIds = actions.Select(a => a.Id).ToArray();
            RaiseEvent(TurnStarted, actionIds);

            // Broadcast ActionResolvingMsg so clients grey out the
            // actions in their UIs.
            await _server.BroadcastAsync(
                new ActionResolvingMsg { ActionIds = actionIds },
                ct).ConfigureAwait(false);

            // Extract just the action texts. The GM's
            // ProcessActionBatchAsync builds the combined user message
            // ("## Действия игроков в этом ходу:\n1. ...\n2. ...")
            // internally and resolves all queued actions in one GM turn
            // (issue #12). The per-action log entries above still record
            // who did what (with nicknames) for the save's log.json.
            var texts = actions.Select(a => a.Text).ToList();

            // Append an action log entry per queued action (so the save's
            // log.json records who did what).
            foreach (var a in actions)
            {
                lock (_log)
                {
                    _log.Add(LogEntry.Action(
                        $"{a.PlayerNickname}: {a.Text}",
                        authorId: a.PlayerId.ToString()));
                }
            }

            // Run the GM. The GM's tool calls mutate the world in-place;
            // we then serialise the mutated world for StateUpdateMsg.
            NarrativeResult result;
            try
            {
                result = await _gm.ProcessActionBatchAsync(texts, narrativeDelta: null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation — re-queue the actions? For simplicity we
                // don't (the user can resubmit). Just rethrow.
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HostSession] GM turn failed: {ex}");
                RaiseEvent(TurnFailed, ex.Message);

                // Broadcast an error to clients so their UI shows something.
                await _server.BroadcastAsync(
                    new ErrorMsg { Message = $"GM turn failed: {ex.Message}" },
                    ct).ConfigureAwait(false);

                // Still increment the turn + end it so the UI unblocks.
                _world.Turn++;
                await _server.BroadcastAsync(
                    new TurnEndMsg { TurnNumber = _world.Turn },
                    ct).ConfigureAwait(false);
                RaiseEvent(TurnEnded, _world.Turn);
                _server.SetTurn(_world.Turn);
                return _world.Turn;
            }

            // Broadcast narrative: one delta with the full text (the GM
            // is non-streaming in this port), then a final. The wire
            // format supports streaming for a future streaming GM.
            if (!string.IsNullOrEmpty(result.NarrativeText))
            {
                var delta = new NarrativeDeltaMsg
                {
                    TextDelta = result.NarrativeText,
                    Turn = _world.Turn + 1,
                };
                await _server.BroadcastAsync(delta, ct).ConfigureAwait(false);
                RaiseEvent(NarrativeDelta, delta.TextDelta, delta.Turn);
            }

            // Accumulate token billing for this turn into the session
            // totals. The host UI reads per-turn counts from the
            // NarrativeFinalMsg event we raise just below; we persist
            // the cumulative totals into _meta on save (below).
            _sessionPromptTokens += result.PromptTokens;
            _sessionCompletionTokens += result.CompletionTokens;

            var toolEvents = result.ToolCalls
                .Select(tc => new ToolEvent
                {
                    Name = tc.Name,
                    ArgsJson = tc.ArgsJson,
                    Result = tc.Result,
                    IsError = tc.IsError,
                })
                .ToArray();

            var finalMsg = new NarrativeFinalMsg
            {
                FullText = result.NarrativeText,
                ToolEvents = toolEvents,
                Turn = _world.Turn + 1,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.TotalTokens,
            };
            await _server.BroadcastAsync(finalMsg, ct).ConfigureAwait(false);
            RaiseEvent(NarrativeFinal, finalMsg);

            // Append a narrative log entry (and per-tool entries) so
            // save's log.json records the GM's output.
            lock (_log)
            {
                _log.Add(LogEntry.Narrative(result.NarrativeText));
                foreach (var tc in result.ToolCalls)
                {
                    _log.Add(LogEntry.Tool(
                        $"{tc.Name}: {tc.Result}",
                        metadata: new Dictionary<string, object>
                        {
                            ["name"] = tc.Name,
                            ["args"] = tc.ArgsJson,
                            ["isError"] = tc.IsError,
                        }));
                }
            }

            // Increment the world's turn counter (SaveManager.SaveAll
            // picks this up to refresh meta.Turn).
            _world.Turn++;

            // Save state (best-effort).
            if (_saveManager is not null)
            {
                try
                {
                    EnsureMeta();
                    LogEntry[] logSnapshot;
                    lock (_log) logSnapshot = _log.ToArray();
                    if (_meta is not null && _saveId is not null)
                    {
                        // Persist session-tokens into meta before saving
                        // so the counter survives reload.
                        _meta = _meta with
                        {
                            SessionPromptTokens = _sessionPromptTokens,
                            SessionCompletionTokens = _sessionCompletionTokens,
                        };
                        _saveManager.SaveAll(_saveId, _world, _meta, logSnapshot);
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[HostSession] Save failed: {ex}");
                }
            }

            // Broadcast the new world state.
            var stateMsg = new StateUpdateMsg
            {
                WorldJson = _world.ToJson(),
                Turn = _world.Turn,
                Full = true,
            };
            await _server.BroadcastAsync(stateMsg, ct).ConfigureAwait(false);
            RaiseEvent(StateUpdate, stateMsg);

            // Broadcast TurnEnd + raise the event locally.
            _server.SetTurn(_world.Turn);
            await _server.BroadcastAsync(
                new TurnEndMsg { TurnNumber = _world.Turn },
                ct).ConfigureAwait(false);
            RaiseEvent(TurnEnded, _world.Turn);

            return _world.Turn;
        }
        finally
        {
            _turnLock.Release();
        }
    }

    // ─── Batching window (issue #12) ──────────────────────────────────

    /// <summary>
    /// Handler for <see cref="HostServer.ActionQueued"/>. Every queued
    /// action starts (or advances) a batching window. The window lets
    /// other ready non-spectator members submit their actions before the
    /// GM drains the queue, so multi-player turns resolve all queued
    /// actions in one GM call (see <see cref="GameMaster.ProcessActionBatchAsync"/>).
    ///
    /// <para>
    /// Window behavior:
    /// </para>
    /// <list type="bullet">
    ///   <item>If <see cref="DefaultBatchWindowSeconds"/> is 0, batching
    ///     is disabled — fire <see cref="ProcessNextTurnAsync"/> on every
    ///     action.</item>
    ///   <item>If a window is already active, record the submitter. If
    ///     all ready non-spectator members have now submitted, fire the
    ///     turn immediately (early fire).</item>
    ///   <item>If no window is active, start one. If only one ready
    ///     member exists (typical single-player-host), fire immediately
    ///     (no countdown). Otherwise, schedule a
    ///     <see cref="DefaultBatchWindowSeconds"/>-second timer that
    ///     fires the turn on expiry, and check for early fire after each
    ///     1-second tick.</item>
    /// </list>
    /// </summary>
    private void OnActionQueuedBatching(PlayerAction action)
    {
        // Batching disabled → fire immediately. (Read into a local so
        // the compiler doesn't const-fold the check away when
        // DefaultBatchWindowSeconds is a non-zero const — keeps the
        // "set to 0 to disable" path reachable for devs who change
        // the const.)
        var windowSeconds = DefaultBatchWindowSeconds;
        if (windowSeconds <= 0)
        {
            _ = ProcessNextTurnAsync(_shutdownToken);
            return;
        }
        StartOrAdvanceBatchWindow(action.PlayerId);
    }

    private void StartOrAdvanceBatchWindow(Guid submitterId)
    {
        bool fireNow = false;
        bool startTimer = false;
        CancellationTokenSource? localCts = null;

        lock (_batchLock)
        {
            if (_batchWindowCts is not null)
            {
                // Window already active — record the submitter.
                _batchSubmitted.Add(submitterId);
                if (_batchReadyCount > 0 && _batchSubmitted.Count >= _batchReadyCount)
                {
                    // All ready players have submitted — fire early.
                    localCts = _batchWindowCts;
                    _batchWindowCts = null;
                    _batchSubmitted.Clear();
                    fireNow = true;
                }
            }
            else
            {
                // No window active — start a new one.
                _batchSubmitted.Clear();
                _batchSubmitted.Add(submitterId);
                _batchReadyCount = CountReadyPlayers();

                if (_batchReadyCount <= 1)
                {
                    // Single ready player (host only) — fire immediately,
                    // no countdown.
                    fireNow = true;
                }
                else
                {
                    _batchWindowCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
                    localCts = _batchWindowCts;
                    startTimer = true;
                }
            }
        }

        // Outside the lock: schedule the timer OR fire immediately.
        // (ProcessNextTurnAsync acquires _turnLock, which we don't want
        // to nest under _batchLock — re-entrancy hazard.)
        if (startTimer)
        {
            _ = RunBatchWindowAsync(localCts!);
            return;
        }

        if (fireNow)
        {
            if (localCts is not null)
            {
                // Early-fire: cancel the running timer task. It will
                // catch OperationCanceledException and exit cleanly.
                try { localCts.Cancel(); localCts.Dispose(); } catch { /* ignore */ }
            }
            _ = ProcessNextTurnAsync(_shutdownToken);
        }
    }

    /// <summary>
    /// Run the batching window countdown. Raises
    /// <see cref="BatchCountdownChanged"/> every second (5 → 4 → 3 → 2 → 1),
    /// then fires <see cref="ProcessNextTurnAsync"/>. Exits early (and
    /// raises BatchCountdownChanged(0)) if:
    /// <list type="bullet">
    ///   <item>The CTS is cancelled (early fire from
    ///     <see cref="StartOrAdvanceBatchWindow"/> when all ready members
    ///     have submitted).</item>
    ///   <item>All ready members submit during a 1-second tick — early
    ///     fire from inside this loop.</item>
    ///   <item>The host shuts down (linked to
    ///     <see cref="_shutdownToken"/>).</item>
    /// </list>
    /// </summary>
    private async Task RunBatchWindowAsync(CancellationTokenSource cts)
    {
        try
        {
            for (int remaining = DefaultBatchWindowSeconds; remaining > 0; remaining--)
            {
                RaiseEvent(BatchCountdownChanged, remaining);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Window was fired early (or shutdown). The early-fire
                    // path raises BatchCountdownChanged(0) — but in case
                    // this is the shutdown path, raise it here too.
                    RaiseEvent(BatchCountdownChanged, 0);
                    return;
                }

                // Check if all ready players have submitted during the
                // 1-second wait. If so, fire early.
                bool fireEarly = false;
                lock (_batchLock)
                {
                    if (ReferenceEquals(_batchWindowCts, cts) &&
                        _batchReadyCount > 0 &&
                        _batchSubmitted.Count >= _batchReadyCount)
                    {
                        _batchWindowCts = null;
                        _batchSubmitted.Clear();
                        fireEarly = true;
                    }
                }
                if (fireEarly)
                {
                    try { cts.Cancel(); cts.Dispose(); } catch { /* ignore */ }
                    RaiseEvent(BatchCountdownChanged, 0);
                    _ = ProcessNextTurnAsync(_shutdownToken);
                    return;
                }
            }

            // Timeout — fire.
            lock (_batchLock)
            {
                if (ReferenceEquals(_batchWindowCts, cts))
                {
                    _batchWindowCts = null;
                    _batchSubmitted.Clear();
                }
            }
            try { cts.Cancel(); cts.Dispose(); } catch { /* ignore */ }
            RaiseEvent(BatchCountdownChanged, 0);
            _ = ProcessNextTurnAsync(_shutdownToken);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HostSession] Batch window failed: {ex}");
            try { cts.Cancel(); cts.Dispose(); } catch { /* ignore */ }
            RaiseEvent(BatchCountdownChanged, 0);
        }
    }

    /// <summary>
    /// Count the ready non-spectator members. "Ready" = MemberStatus.Ready
    /// or MemberStatus.Playing. The host is always Ready (set in
    /// HostServer.StartAsync). Spectators are excluded — they can't
    /// submit actions. Returns at least 1 (the host).
    /// </summary>
    private int CountReadyPlayers()
    {
        return _server.Members
            .Count(m => m.Role != MemberRole.Spectator &&
                        (m.Status == MemberStatus.Ready || m.Status == MemberStatus.Playing));
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Ensure <see cref="_meta"/> is populated. If a save id was given
    /// but the save doesn't exist on disk yet, create it lazily. If no
    /// save id was given, mint a new save.
    /// </summary>
    private void EnsureMeta()
    {
        if (_meta is not null) return;
        if (_saveManager is null) return;

        var saveName = $"Multiplayer game {_hostProfile.Nickname}";
        // Use the world's first location name as the world title if available.
        var firstLoc = _world.Locations.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstLoc?.Name)) saveName = firstLoc.Name;

        _meta = _saveManager.CreateSave(saveName, _world, _hostProfile.Id);
    }

    /// <summary>Generate a 32-char lowercase hex action id (matches the
    /// client-side id format used by <see cref="ClientSession"/>).</summary>
    private static string NewActionId() => Guid.NewGuid().ToString("N");

    /// <summary>Exception-safe event raise (same pattern as HostServer).</summary>
    private static void RaiseEvent<T>(Action<T>? handler, T args)
    {
        if (handler is null) return;
        var delegates = handler.GetInvocationList();
        foreach (var d in delegates)
        {
            try { ((Action<T>)d)(args); }
            catch (Exception ex) { Trace.WriteLine($"[HostSession] event subscriber threw: {ex}"); }
        }
    }

    /// <summary>Overload for two-arg events (used by NarrativeDelta).</summary>
    private static void RaiseEvent<T1, T2>(Action<T1, T2>? handler, T1 arg1, T2 arg2)
    {
        if (handler is null) return;
        var delegates = handler.GetInvocationList();
        foreach (var d in delegates)
        {
            try { ((Action<T1, T2>)d)(arg1, arg2); }
            catch (Exception ex) { Trace.WriteLine($"[HostSession] event subscriber threw: {ex}"); }
        }
    }

    /// <summary>
    /// Overload for the <see cref="ActionCancelled"/> event, which
    /// passes a possibly-null action id (null when an unknown id was
    /// cancelled). The signature uses <c>string?</c> explicitly so the
    /// compiler doesn't pick the generic overload with T=string and
    /// complain about nullability mismatches.
    /// </summary>
    private static void RaiseEventNullable(Action<string?>? handler, string? args)
    {
        if (handler is null) return;
        var delegates = handler.GetInvocationList();
        foreach (var d in delegates)
        {
            try { ((Action<string?>)d)(args); }
            catch (Exception ex) { Trace.WriteLine($"[HostSession] event subscriber threw: {ex}"); }
        }
    }
}
