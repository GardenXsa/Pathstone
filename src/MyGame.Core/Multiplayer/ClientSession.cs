using System.Diagnostics;
using System.Text;
using MyGame.Core.Multiplayer.Protocol;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;

namespace MyGame.Core.Multiplayer;

// Same World namespace/type alias trick as HostSession.cs.
using GameWorld = MyGame.Core.World.World;

/// <summary>
/// High-level orchestrator for the JOIN-side of multiplayer. Glues
/// together a <see cref="GameClient"/> (transport) with a local state
/// cache (world snapshot, narrative buffer, log) so the UI layer has a
/// single object to subscribe to.
///
/// <para>
/// The client session is the join-side mirror of <see cref="HostSession"/>:
/// the same event surface (member join/leave, chat, action queued,
/// narrative delta/final, state update, turn end) but with the data
/// flowing FROM the host instead of being generated locally. Outgoing
/// player actions go through <see cref="SubmitActionAsync"/> which
/// generates an action id locally + sends <see cref="ActionQueuedMsg"/>
/// to the host (the host re-broadcasts to everyone, including this
/// client, so the local UI sees the action appear in the queue via the
/// echoed <see cref="GameClient.ActionQueued"/> event).
/// </para>
///
/// <para><b>Local state cache:</b> the session keeps:
/// <list type="bullet">
///   <item><see cref="LocalWorld"/> — the latest World received via
///     <see cref="StateUpdateMsg"/>. Deserialised once per update (the
///     host sends a full snapshot; diff support is a future feature).</item>
///   <item><see cref="NarrativeBuffer"/> — the accumulated narrative
///     text from <see cref="NarrativeDeltaMsg"/>s. Replaced wholesale
///     when a <see cref="NarrativeFinalMsg"/> arrives (the final is the
///     canonical full text — any deltas before it were streaming).</item>
///   <item><see cref="Log"/> — a list of <see cref="LogEntry"/> records
///     mirroring what the host is persisting. Built locally from
///     narrative-final + action-queued + tool events. Lets the UI render
///     the log without needing the save file.</item>
///   <item><see cref="PendingActions"/> — actions submitted by this
///     client that haven't been resolved yet. Tracked locally so the UI
///     can show "your action is pending" + support cancel.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ClientSession
{
    // ─── Config ──────────────────────────────────────────────────────

    private readonly Profile.Profile _profile;
    private readonly GameClient _client;

    // ─── Runtime state ───────────────────────────────────────────────

    private readonly object _stateLock = new();
    private GameWorld? _localWorld;
    private string _narrativeBuffer = string.Empty;
    private readonly List<LogEntry> _log = new();
    private readonly Dictionary<string, PlayerAction> _pendingActions = new();
    private int _turn;
    private PartyStatus _status = PartyStatus.Lobby;
    private string? _saveId;

    /// <summary>True after <see cref="ConnectAsync"/> succeeds.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>The connection id assigned by the host.</summary>
    public Guid ConnectionId => _client.ConnectionId;

    /// <summary>The role assigned by the host.</summary>
    public MemberRole Role => _client.Role;

    /// <summary>Create a client session for the given local profile.</summary>
    public ClientSession(Profile.Profile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _client = new GameClient(profile);

        // Wire GameClient events to our internal handlers. Each handler
        // updates the local state cache and raises the corresponding
        // session event (so the UI subscribes to one object).
        _client.Welcomed += OnWelcomed;
        _client.MemberJoined += m => RaiseEvent(MemberJoined, m);
        _client.MemberLeft += m => RaiseEvent(MemberLeft, m);
        _client.MemberReady += m => RaiseEvent(MemberReady, m);
        _client.StatusChanged += OnStatusChanged;
        _client.ChatReceived += c => RaiseEvent(ChatReceived, c);
        _client.ActionQueued += OnActionQueued;
        _client.ActionCancelled += OnActionCancelled;
        _client.ActionResolving += OnActionResolving;
        _client.NarrativeDelta += OnNarrativeDelta;
        _client.NarrativeFinal += OnNarrativeFinal;
        _client.StateUpdate += OnStateUpdate;
        _client.TurnEnd += OnTurnEnd;
        _client.Error += e => RaiseEvent(Error, e);
        _client.Kicked += k => RaiseEvent(Kicked, k);
        _client.LogSynced += log => RaiseEvent(LogSynced, log);
        _client.Disconnected += info => RaiseEvent(Disconnected, info);
    }

    // ─── Properties (local state cache) ──────────────────────────────

    /// <summary>
    /// The latest World received from the host. Null until the first
    /// <see cref="StateUpdateMsg"/> arrives (typically after the first
    /// GM turn). The UI should null-check before rendering.
    /// </summary>
    public GameWorld? LocalWorld
    {
        get { lock (_stateLock) return _localWorld; }
    }

    /// <summary>
    /// Accumulated narrative text from streaming deltas. Replaced
    /// wholesale when a <see cref="NarrativeFinalMsg"/> arrives.
    /// </summary>
    public string NarrativeBuffer
    {
        get { lock (_stateLock) return _narrativeBuffer; }
    }

    /// <summary>
    /// Local narrative + action + tool log. Built from incoming
    /// messages — NOT loaded from a save file (the joining client
    /// doesn't have the host's save). Lets the UI render the log from
    /// session start; for the full history, the client would need to
    /// request the save file separately (future feature).
    /// </summary>
    public IReadOnlyList<LogEntry> Log
    {
        get { lock (_stateLock) return _log.ToArray(); }
    }

    /// <summary>
    /// Actions submitted by this client that haven't been resolved yet
    /// (i.e. are still in the host's action queue). Tracked locally so
    /// the UI can render a "your pending actions" list and support
    /// cancel.
    /// </summary>
    public IReadOnlyList<PlayerAction> PendingActions
    {
        get { lock (_stateLock) return _pendingActions.Values.ToList(); }
    }

    /// <summary>Current turn number (from the last
    /// <see cref="TurnEndMsg"/> or <see cref="StateUpdateMsg"/>).</summary>
    public int Turn
    {
        get { lock (_stateLock) return _turn; }
    }

    /// <summary>Current party status (from the last
    /// <see cref="StatusChangedMsg"/> or <see cref="WelcomeMsg"/>).</summary>
    public PartyStatus Status
    {
        get { lock (_stateLock) return _status; }
    }

    /// <summary>Save id the party is playing (from the
    /// <see cref="WelcomeMsg"/> or <see cref="StatusChangedMsg"/>).</summary>
    public string? SaveId
    {
        get { lock (_stateLock) return _saveId; }
    }

    // ─── Events (UI surface) ─────────────────────────────────────────

    /// <summary>Raised when the handshake completes. Carries the
    /// WelcomeMsg (which contains the party snapshot + assigned role).</summary>
    public event Action<WelcomeMsg>? Welcomed;

    /// <summary>Another member joined.</summary>
    public event Action<MemberJoinedMsg>? MemberJoined;

    /// <summary>A member left.</summary>
    public event Action<MemberLeftMsg>? MemberLeft;

    /// <summary>A member toggled their ready state.</summary>
    public event Action<MemberReadyMsg>? MemberReady;

    /// <summary>Party status changed.</summary>
    public event Action<StatusChangedMsg>? StatusChanged;

    /// <summary>Chat message received.</summary>
    public event Action<ChatMsg>? ChatReceived;

    /// <summary>An action was queued (by anyone — including this
    /// client, echoed back by the host).</summary>
    public event Action<ActionQueuedMsg>? ActionQueued;

    /// <summary>An action was cancelled.</summary>
    public event Action<ActionCancelledMsg>? ActionCancelled;

    /// <summary>The host started resolving a batch of actions.</summary>
    public event Action<ActionResolvingMsg>? ActionResolving;

    /// <summary>Streaming narrative delta. Append to UI narration.</summary>
    public event Action<NarrativeDeltaMsg>? NarrativeDelta;

    /// <summary>Final narration for the turn (full text + tool events).</summary>
    public event Action<NarrativeFinalMsg>? NarrativeFinal;

    /// <summary>World state update — full snapshot from the host.</summary>
    public event Action<StateUpdateMsg>? StateUpdate;

    /// <summary>GM turn ended.</summary>
    public event Action<TurnEndMsg>? TurnEnd;

    /// <summary>Host sent an error.</summary>
    public event Action<ErrorMsg>? Error;

    /// <summary>Host kicked this client.</summary>
    public event Action<KickedMsg>? Kicked;

    /// <summary>Issue #32: log history from host (late joiner sync).</summary>
    public event Action<LogSyncMsg>? LogSynced;

    /// <summary>WebSocket connection dropped. Carries the close reason
    /// (may be empty) and an <see cref="DisconnectedInfo.Intentional"/>
    /// flag distinguishing user/host-initiated disconnects (Leave button,
    /// kick) from network drops. The UI uses the flag to decide whether
    /// to show a reconnect overlay.</summary>
    public event Action<DisconnectedInfo>? Disconnected;

    // ─── Connect / disconnect ────────────────────────────────────────

    /// <summary>
    /// Connect to a host. Performs the HelloMsg → WelcomeMsg handshake.
    /// Returns the WelcomeMsg on success; throws on reject/timeout.
    /// </summary>
    public async Task<WelcomeMsg> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var welcome = await _client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        // OnWelcomed (the event handler) runs the state-cache update.
        return welcome;
    }

    /// <summary>Graceful disconnect. Marks the disconnect as intentional
    /// (the user clicked Leave) so the <see cref="Disconnected"/> event
    /// suppresses the reconnect overlay.</summary>
    public Task DisconnectAsync() => _client.DisconnectAsync();

    /// <summary>
    /// Re-connect to the same host/port as the last successful
    /// <see cref="ConnectAsync"/>. Performs the HelloMsg → WelcomeMsg
    /// handshake again. On success, raises <see cref="Welcomed"/> with
    /// the fresh WelcomeMsg (which carries a fresh party snapshot —
    /// the host re-sends members, save id, turn, etc.).
    ///
    /// <para>
    /// On failure (host still unreachable, handshake timeout, reject),
    /// the method throws — the caller (GameViewModel) catches it and
    /// retries up to 3 times with 2-second backoff before giving up and
    /// showing the "Не удалось переподключиться" exit-only overlay.
    /// </para>
    /// </summary>
    public async Task<WelcomeMsg> ReconnectAsync(CancellationToken ct = default)
    {
        var welcome = await _client.ReconnectAsync(ct).ConfigureAwait(false);
        // OnWelcomed (the GameClient event handler) runs the state-cache
        // update + raises the session-level Welcomed event, so the UI
        // refreshes its members list + party status from the fresh
        // snapshot.
        return welcome;
    }

    // ─── Outgoing player actions / chat ──────────────────────────────

    /// <summary>
    /// Submit an action on behalf of the local player. Generates an
    /// action id locally, sends <see cref="ActionQueuedMsg"/> to the
    /// host (which re-broadcasts to everyone including this client),
    /// and tracks the action in <see cref="PendingActions"/> for
    /// cancel support.
    /// </summary>
    public async Task SubmitActionAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Action text cannot be empty.", nameof(text));

        var actionId = NewActionId();
        var trimmed = text.Trim();
        if (trimmed.Length > 4000) trimmed = trimmed[..4000];

        var ts = DateTimeOffset.UtcNow;
        var action = new PlayerAction
        {
            Id = actionId,
            PlayerId = _client.ConnectionId,
            PlayerNickname = _profile.Nickname,
            Text = trimmed,
            SubmittedAt = ts,
        };

        // Track locally so we can cancel later + show as "pending" in UI.
        lock (_stateLock) _pendingActions[actionId] = action;

        var msg = new ActionQueuedMsg
        {
            ActionId = actionId,
            FromId = _client.ConnectionId,
            FromNickname = _profile.Nickname,
            Text = trimmed,
            Ts = ts,
        };
        await _client.SendAsync(msg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancel a pending action by id. Sends
    /// <see cref="ActionCancelMsg"/> to the host. Returns true if the
    /// action was in the local pending list (the host's actual cancel
    /// confirmation comes back via <see cref="ActionCancelledMsg"/>
    /// and is handled in <see cref="OnActionCancelled"/>).
    /// </summary>
    public async Task<bool> CancelActionAsync(string actionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actionId)) return false;

        bool wasPending;
        lock (_stateLock) wasPending = _pendingActions.ContainsKey(actionId);
        if (!wasPending) return false;

        await _client.SendAsync(new ActionCancelMsg { ActionId = actionId }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Send a lobby chat message.</summary>
    public async Task SendChatAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var trimmed = text.Trim();
        if (trimmed.Length > 1000) trimmed = trimmed[..1000];

        var msg = new ChatMsg
        {
            // The host rewrites fromId + fromNickname from the socket
            // identity, so these fields are technically ignored — but we
            // send them anyway for symmetry (and in case a future host
            // trusts the client).
            FromId = _client.ConnectionId,
            FromNickname = _profile.Nickname,
            Text = trimmed,
            Ts = DateTimeOffset.UtcNow,
        };
        await _client.SendAsync(msg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Toggle this client's ready state in the lobby. Sends
    /// <see cref="MemberReadyMsg"/> to the host (which re-broadcasts).
    /// </summary>
    public Task SetReadyAsync(bool ready, CancellationToken ct = default) =>
        _client.SendAsync(new MemberReadyMsg
        {
            ConnectionId = _client.ConnectionId,
            Ready = ready,
        }, ct);

    // ─── Event handlers (update local cache + raise session events) ──

    private void OnWelcomed(WelcomeMsg w)
    {
        lock (_stateLock)
        {
            _status = w.Party.Status;
            _saveId = w.Party.SaveId;
            _turn = w.Party.Turn;
        }
        RaiseEvent(Welcomed, w);
    }

    private void OnStatusChanged(StatusChangedMsg s)
    {
        lock (_stateLock)
        {
            _status = s.Status;
            if (s.SaveId is not null) _saveId = s.SaveId;
            if (s.Turn > 0) _turn = s.Turn;
        }
        RaiseEvent(StatusChanged, s);
    }

    private void OnActionQueued(ActionQueuedMsg msg)
    {
        // Append to local log (so the UI's log view shows actions from
        // other players too).
        lock (_stateLog)
        {
            _log.Add(LogEntry.Action(
                $"{msg.FromNickname}: {msg.Text}",
                authorId: msg.FromId.ToString()));
        }

        // If this is our own action echoed back, it's already in
        // PendingActions — no need to re-add. (We could double-check
        // the actionId matches and update the SubmittedAt, but it's
        // already correct from the local SubmitActionAsync.)

        RaiseEvent(ActionQueued, msg);
    }

    private void OnActionCancelled(ActionCancelledMsg msg)
    {
        lock (_stateLock)
        {
            _pendingActions.Remove(msg.ActionId);
        }
        RaiseEvent(ActionCancelled, msg);
    }

    private void OnActionResolving(ActionResolvingMsg msg)
    {
        // The host has picked up these actions for resolution — remove
        // them from our local pending list (they're no longer
        // cancellable).
        lock (_stateLock)
        {
            foreach (var id in msg.ActionIds)
            {
                _pendingActions.Remove(id);
            }
        }
        RaiseEvent(ActionResolving, msg);
    }

    private void OnNarrativeDelta(NarrativeDeltaMsg msg)
    {
        lock (_stateLock)
        {
            _narrativeBuffer += msg.TextDelta;
        }
        RaiseEvent(NarrativeDelta, msg);
    }

    private void OnNarrativeFinal(NarrativeFinalMsg msg)
    {
        lock (_stateLock)
        {
            // The final message is the canonical full text — replace
            // the buffer (in case any deltas were lost or out of order).
            _narrativeBuffer = msg.FullText;
            // Append to log.
            _log.Add(LogEntry.Narrative(msg.FullText));
            // Append tool events.
            foreach (var tc in msg.ToolEvents)
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
        RaiseEvent(NarrativeFinal, msg);
    }

    private void OnStateUpdate(StateUpdateMsg msg)
    {
        // Deserialise the world snapshot. Use the default content
        // registry (same as SaveManager.LoadWorld does when no
        // registry is injected).
        GameWorld? newWorld = null;
        try
        {
            if (!string.IsNullOrEmpty(msg.WorldJson))
            {
                newWorld = GameWorld.FromJson(msg.WorldJson, ContentRegistry.LoadDefault());
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ClientSession] Failed to deserialise world snapshot: {ex}");
        }

        lock (_stateLock)
        {
            if (newWorld is not null) _localWorld = newWorld;
            _turn = msg.Turn;
        }
        RaiseEvent(StateUpdate, msg);
    }

    private void OnTurnEnd(TurnEndMsg msg)
    {
        lock (_stateLock)
        {
            _turn = msg.TurnNumber;
        }
        RaiseEvent(TurnEnd, msg);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string NewActionId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Alias used inside OnActionQueued to take the same lock as the
    /// other state mutations. (C# doesn't let us refer to <c>_stateLock</c>
    /// from a field initializer, so we use a property that returns it.)
    /// </summary>
    private object _stateLog => _stateLock;

    /// <summary>Exception-safe event raise.</summary>
    private static void RaiseEvent<T>(Action<T>? handler, T args)
    {
        if (handler is null) return;
        var delegates = handler.GetInvocationList();
        foreach (var d in delegates)
        {
            try { ((Action<T>)d)(args); }
            catch (Exception ex) { Trace.WriteLine($"[ClientSession] event subscriber threw: {ex}"); }
        }
    }
}
