using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using MyGame.Core.Multiplayer.Protocol;
using MyGame.Core.Profile;

namespace MyGame.Core.Multiplayer;

/// <summary>
/// In-process WebSocket host server. Replaces the TS source's
/// <c>mini-services/ws-server/index.ts</c> (socket.io + Next.js) with a
/// raw <see cref="HttpListener"/>-based WebSocket server running inside
/// the desktop app itself.
///
/// <para>
/// When the user clicks "Host Game", the UI constructs a
/// <see cref="HostServer"/> with the local <c>Profile</c> and the
/// (already-loaded or freshly-built) <c>World</c>, calls
/// <see cref="StartAsync"/>, and the host is live on
/// <c>ws://&lt;lan-ip&gt;:&lt;Port&gt;/</c>. Other players (running their
/// own desktop app) connect via <see cref="GameClient"/>.
/// </para>
///
/// <para><b>Architecture notes (deviations from the TS source):</b>
/// <list type="bullet">
///   <item>NO socket.io. Raw <see cref="WebSocket"/> frames carrying
///     JSON-serialised <see cref="NetMessage"/>s. The wire protocol is
///     smaller and easier to debug.</item>
///   <item>NO JWT / cookies / auth middleware. The client sends a
///     <see cref="HelloMsg"/> with their local <c>Profile</c> identity;
///     the host validates nickname uniqueness and assigns a connection
///     id. That's it.</item>
///   <item>NO separate "broadcast API server" on another port. The host
///     process owns the <see cref="GameMaster"/> + <see cref="World"/> +
///     <see cref="ActionQueue"/>, so broadcasts are direct method calls
///     (<see cref="BroadcastAsync"/>), not localhost HTTP hops.</item>
///   <item>The host is also a player. It's added as the first
///     <see cref="MemberInfo"/> (with <see cref="MemberRole.Host"/>) at
///     <see cref="StartAsync"/> time. The host's own actions go through
///     the same <see cref="ActionQueue"/> as remote clients' actions.</item>
/// </list>
/// </para>
///
/// <para><b>Port assignment:</b> the ctor takes a port (0 = OS-assigned).
/// Because <see cref="HttpListener"/> doesn't expose the bound port back
/// when port=0 (unlike <see cref="System.Net.Sockets.TcpListener"/>), we
/// pre-resolve a free port via a temporary <see cref="System.Net.Sockets.TcpListener"/>
/// before starting the <see cref="HttpListener"/>. There's a small
/// TOCTOU race (the port could be taken between the TcpListener.Close
/// and the HttpListener.Start), but for a desktop app it's acceptable
/// — if StartAsync fails, the caller can retry.</para>
///
/// <para><b>Thread safety:</b> the connections dictionary is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Each connection's
/// read loop runs on its own (ThreadPool) task; writes are guarded by
/// a per-connection <see cref="SemaphoreSlim"/> (because
/// <see cref="WebSocket.SendAsync"/> is NOT safe for concurrent callers
/// on the same instance). All event invocations are exception-safe
/// (try/catch around each subscriber).</para>
/// </summary>
public sealed class HostServer
{
    // ─── Config ──────────────────────────────────────────────────────

    private readonly Profile.Profile _hostProfile;
    private readonly int _requestedPort;
    private readonly string _bindHost;
    private readonly CancellationToken _shutdownToken;

    // ─── Runtime state ───────────────────────────────────────────────

    private HttpListener? _listener;
    private Task? _acceptLoopTask;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, Guid> _nicknamesByLower = new();

    private readonly object _statusLock = new();
    private PartyStatus _status = PartyStatus.Lobby;
    private string? _saveId;
    private int _turn;

    /// <summary>
    /// The host's own connection id (assigned at <see cref="StartAsync"/>
    /// time). Used as the <c>fromId</c> on host-originated chat / actions.
    /// </summary>
    public Guid HostConnectionId { get; private set; }

    /// <summary>
    /// The action queue. The HostSession drains this when processing a
    /// GM turn. Host actions and remote client actions both land here.
    /// </summary>
    public ActionQueue ActionQueue { get; } = new();

    /// <summary>
    /// Create a new host server. Does NOT start listening — call
    /// <see cref="StartAsync"/> to bind the socket.
    /// </summary>
    /// <param name="hostProfile">
    /// The local player's profile. The host is added to the roster as
    /// the first <see cref="MemberInfo"/> with role
    /// <see cref="MemberRole.Host"/> and nickname
    /// <see cref="Profile.Profile.Nickname"/>.
    /// </param>
    /// <param name="requestedPort">
    /// TCP port to listen on. 0 = OS-assigned (the actual port is read
    /// back from <see cref="Port"/> after <see cref="StartAsync"/>
    /// completes).
    /// </param>
    /// <param name="shutdownToken">
    /// Cancellation token that triggers graceful shutdown (the accept
    /// loop stops, all connections are closed, the listener is disposed).
    /// Typically the application shutdown token.
    /// </param>
    /// <param name="bindHost">
    /// Hostname to bind the <see cref="HttpListener"/> to. Default
    /// <c>"+"</c> (all interfaces — lets remote players on the LAN
    /// connect). Use <c>"localhost"</c> for single-machine testing.
    /// </param>
    public HostServer(
        Profile.Profile hostProfile,
        int requestedPort = 0,
        CancellationToken shutdownToken = default,
        string bindHost = "+")
    {
        _hostProfile = hostProfile ?? throw new ArgumentNullException(nameof(hostProfile));
        _requestedPort = requestedPort;
        _shutdownToken = shutdownToken;
        _bindHost = string.IsNullOrWhiteSpace(bindHost) ? "+" : bindHost;
    }

    // ─── Properties ──────────────────────────────────────────────────

    /// <summary>
    /// The actual port the server is listening on. Only valid after
    /// <see cref="StartAsync"/> completes. Returns 0 if not yet started.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Current party lifecycle status. Set via <see cref="SetStatusAsync"/>.
    /// </summary>
    public PartyStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <summary>
    /// Save id the party is playing (or null if still in the lobby).
    /// Set via <see cref="SetStatusAsync"/> when transitioning to Playing.
    /// </summary>
    public string? SaveId
    {
        get { lock (_statusLock) return _saveId; }
    }

    /// <summary>
    /// Current turn number. Incremented by the HostSession after each GM
    /// turn; broadcast in <see cref="TurnEndMsg"/>.
    /// </summary>
    public int Turn
    {
        get { lock (_statusLock) return _turn; }
    }

    /// <summary>
    /// Current lobby roster (host + every connected client). Returns a
    /// snapshot — safe to enumerate outside the lock.
    /// </summary>
    public IReadOnlyList<MemberInfo> Members =>
        _connections.Values
            .Where(c => c.Member is not null)
            .Select(c => c.Member!)
            .OrderByDescending(m => m.Role == MemberRole.Host)
            .ThenBy(m => m.JoinedAt)
            .ToList();

    /// <summary>True if <see cref="StartAsync"/> has been called and the
    /// accept loop is running.</summary>
    public bool IsRunning => _listener is not null && _listener.IsListening;

    // ─── Events ──────────────────────────────────────────────────────

    /// <summary>Raised when a new client completes the handshake and
    /// joins the party. The host is NOT announced here (the host is
    /// added at StartAsync time before any client can connect).</summary>
    public event Action<MemberInfo>? MemberJoined;

    /// <summary>Raised when a client disconnects (graceful close or
    /// network drop). Passes the member that left (with status
    /// <see cref="MemberStatus.Disconnected"/>).</summary>
    public event Action<MemberInfo>? MemberLeft;

    /// <summary>Raised when a chat message arrives from a client. The
    /// host has already relayed it to all clients by the time this event
    /// fires — the HostSession can use it to display the chat locally
    /// (the host is also a player and sees the lobby chat in its UI).</summary>
    public event Action<ChatMsg>? ChatReceived;

    /// <summary>Raised when a player action is queued (either from a
    /// remote client or from the host player via
    /// <see cref="EnqueueActionAsync"/>). The action is already in
    /// <see cref="ActionQueue"/> and already broadcast to all clients.
    /// The HostSession can use this to wake up an idle GM turn loop.</summary>
    public event Action<PlayerAction>? ActionQueued;

    /// <summary>Raised when an action is cancelled. Passes the action id
    /// (or null if the id wasn't in the queue).</summary>
    public event Action<string?>? ActionCancelled;

    /// <summary>
    /// Raised when a client toggles their ready state in the lobby
    /// (issue #77). Carries the updated <see cref="MemberInfo"/> (with
    /// <see cref="MemberInfo.Status"/> set to Ready or Pending). The
    /// host UI uses this to refresh its lobby members list. Raised
    /// AFTER the new MemberInfo has been stored on the connection +
    /// AFTER the MemberReadyMsg has been broadcast to all clients — so
    /// by the time this event fires, the host UI can re-read
    /// <see cref="Members"/> and get the current snapshot.
    /// </summary>
    public event Action<MemberInfo>? MemberReady;

    /// <summary>
    /// Raised when the party lifecycle status changes (issue #77).
    /// Carries the new <see cref="PartyStatus"/>. The host UI subscribes
    /// to refresh its <c>IsLobby</c> flag (so the lobby layout
    /// disappears when the host starts the game via
    /// <see cref="HostSession.SetStatusAsync"/>). Raised AFTER the
    /// StatusChangedMsg has been broadcast to all clients.
    /// </summary>
    public event Action<PartyStatus>? StatusChanged;

    // ─── Lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Bind the <see cref="HttpListener"/>, register the host as the
    /// first member, and start the accept loop. Returns the actual port
    /// the server is listening on (also available via <see cref="Port"/>).
    /// </summary>
    public Task<int> StartAsync()
    {
        if (_listener is not null)
            throw new InvalidOperationException("HostServer is already running.");

        // Pre-resolve a free port if the caller asked for OS-assigned.
        Port = _requestedPort == 0 ? GetFreePort() : _requestedPort;

        // The host's own connection id is a fresh Guid.
        HostConnectionId = Guid.NewGuid();

        _listener = new HttpListener();
        // HttpListener prefixes MUST end with '/'. The "+" binds to all
        // interfaces (use "localhost" for single-machine testing).
        _listener.Prefixes.Add($"http://{_bindHost}:{Port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _listener = null;
            throw new InvalidOperationException(
                $"Failed to start HttpListener on {_bindHost}:{Port}. " +
                $"On Windows, run `netsh http add urlacl url=http://{_bindHost}:{Port}/ user=Everyone` " +
                $"or run as administrator. On Linux, ensure no other process is using the port. " +
                $"Error: {ex.Message}", ex);
        }

        // Register the host as the first member. The host has no
        // WebSocket (it talks to itself directly via the HostSession),
        // so we mark its Connection with a null WebSocket.
        var hostMember = new MemberInfo
        {
            ConnectionId = HostConnectionId,
            ProfileId = _hostProfile.Id,
            Nickname = _hostProfile.Nickname,
            Role = MemberRole.Host,
            Status = MemberStatus.Ready,
            JoinedAt = DateTimeOffset.UtcNow,
        };
        var hostConn = new Connection(null!, hostMember);
        _connections[HostConnectionId] = hostConn;
        _nicknamesByLower[_hostProfile.Nickname.ToLowerInvariant()] = HostConnectionId;

        // Start the accept loop on the ThreadPool.
        _acceptLoopTask = Task.Run(AcceptLoopAsync);

        return Task.FromResult(Port);
    }

    /// <summary>
    /// Graceful shutdown: stop the accept loop, close all client
    /// connections with a "server shutting down" close code, dispose
    /// the listener. Safe to call multiple times.
    /// </summary>
    public async Task StopAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null) return;

        // Stop accepting new connections first.
        try { listener.Stop(); } catch { /* ignore */ }

        // Close all client WebSockets. The host's own Connection has a
        // null WebSocket and is skipped.
        var closeTasks = new List<Task>();
        foreach (var kvp in _connections)
        {
            var ws = kvp.Value.WebSocket;
            if (ws is null) continue;
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    closeTasks.Add(ws.CloseAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "server shutting down",
                        CancellationToken.None));
                }
            }
            catch { /* ignore */ }
        }
        if (closeTasks.Count > 0)
        {
            try { await Task.WhenAll(closeTasks).ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        // Wait for the accept loop to finish.
        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _acceptLoopTask = null;

                        foreach (var kvp in _connections) 
                {
                    try { kvp.Value.DisposeSemaphore(); } catch { /* ignore */ }
                }
                _connections.Clear();
                _nicknamesByLower.Clear();
                try { listener.Close(); } catch { /* ignore */ }
            }

    // ─── Accept loop ─────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        if (listener is null) return;

        while (!_shutdownToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (_shutdownToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_shutdownToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HostServer] GetContextAsync threw: {ex}");
                if (!listener.IsListening) break;
                continue;
            }

            // Hand off to a background task so the accept loop keeps moving.
            _ = Task.Run(() => HandleConnectionAsync(ctx));
        }
    }

    private async Task HandleConnectionAsync(HttpListenerContext ctx)
    {
        // Reject non-WebSocket requests with a 400.
        if (!ctx.Request.IsWebSocketRequest)
        {
            try { ctx.Response.StatusCode = 400; ctx.Response.Close(); } catch { /* ignore */ }
            return;
        }

        HttpListenerWebSocketContext wsCtx;
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HostServer] AcceptWebSocketAsync threw: {ex}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* ignore */ }
            return;
        }

        var ws = wsCtx.WebSocket;

        // Pre-handshake state: the connection exists but no MemberInfo yet.
        // We use a temporary Guid so we can track the WebSocket before the
        // HelloMsg arrives. The ConnectionReadLoopAsync will swap this id
        // for a real one if/when the handshake completes.
        var pendingConnectionId = Guid.NewGuid();
        var conn = new Connection(ws, null);
        _connections[pendingConnectionId] = conn;

        // The read loop tracks the CURRENT connection id (which may change
        // after the handshake). We remove by that id in the finally block.
        var currentConnectionId = pendingConnectionId;
        try
        {
            currentConnectionId = await ConnectionReadLoopAsync(conn, pendingConnectionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HostServer] Read loop threw: {ex}");
        }
        finally
        {
            // Remove by the current id. If the handshake completed, this
            // is the real connection id; if it didn't (or the loop threw
            // before the swap), it's the pending id. Both are no-ops if
            // the id is no longer in the dict.
            await RemoveConnectionAsync(currentConnectionId).ConfigureAwait(false);
        }
    }

    // ─── Per-connection read loop ────────────────────────────────────

    private async Task<Guid> ConnectionReadLoopAsync(Connection conn, Guid pendingConnectionId)
    {
        var ws = conn.WebSocket;
        if (ws is null) return pendingConnectionId;

        // The first message MUST be HelloMsg. Read it (with a timeout
        // so a dead connection doesn't leak).
        using var firstReadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HelloMsg? hello;
        try
        {
            var (firstJson, firstClosed) = await ReceiveMessageAsync(ws, firstReadCts.Token).ConfigureAwait(false);
            if (firstClosed)
            {
                // Client closed before sending Hello — drop silently.
                return pendingConnectionId;
            }
            hello = MultiplayerJson.Deserialize(firstJson) as HelloMsg;
        }
        catch (OperationCanceledException)
        {
            await SendAndCloseAsync(ws, new RejectMsg { Reason = "Handshake timeout" }).ConfigureAwait(false);
            return pendingConnectionId;
        }

        if (hello is null)
        {
            await SendAndCloseAsync(ws, new RejectMsg { Reason = "Expected hello as first message" }).ConfigureAwait(false);
            return pendingConnectionId;
        }

        // Validate protocol version.
        if (!Common.Version.IsCompatible(hello.ProtocolVersion))
        {
            await SendAndCloseAsync(ws, new RejectMsg
            {
                Reason = $"Protocol version mismatch: server={Common.Version.Current}, client={hello.ProtocolVersion}"
            }).ConfigureAwait(false);
            return pendingConnectionId;
        }

        // Validate nickname uniqueness (case-insensitive).
        if (string.IsNullOrWhiteSpace(hello.Nickname))
        {
            await SendAndCloseAsync(ws, new RejectMsg { Reason = "Ник не может быть пустым" }).ConfigureAwait(false);
            return pendingConnectionId;
        }
        var nickLower = hello.Nickname.ToLowerInvariant();
        if (!_nicknamesByLower.TryAdd(nickLower, pendingConnectionId))
        {
            await SendAndCloseAsync(ws, new RejectMsg { Reason = "Ник уже занят в этой партии" }).ConfigureAwait(false);
            return pendingConnectionId;
        }

        // Handshake succeeded — promote the connection to a full member.
        var connectionId = Guid.NewGuid();
        var member = new MemberInfo
        {
            ConnectionId = connectionId,
            ProfileId = hello.ProfileId,
            Nickname = hello.Nickname,
            Role = MemberRole.Player,
            Status = MemberStatus.Pending,
            JoinedAt = DateTimeOffset.UtcNow,
        };

        // Swap the pending connection id for the real one. The pending
        // entry has a null Member; the new entry has the full MemberInfo.
        _connections.TryRemove(pendingConnectionId, out _);
        _connections[connectionId] = conn;
        conn.Member = member;
        _nicknamesByLower[nickLower] = connectionId;

        // Send WelcomeMsg to the new client with the current party snapshot.
        var snapshot = BuildPartySnapshot();
        await SendAsync(conn, new WelcomeMsg
        {
            ConnectionId = connectionId,
            Party = snapshot,
            Role = MemberRole.Player,
        }, _shutdownToken).ConfigureAwait(false);

        // Broadcast MemberJoinedMsg to everyone (including the new client
        // — so they see themselves in the roster).
        await BroadcastAsync(new MemberJoinedMsg { Member = member }, _shutdownToken).ConfigureAwait(false);

        // Raise the event locally (for the HostSession).
        RaiseEvent(MemberJoined, member);

        // Main read loop.
        while (!_shutdownToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            string json;
            bool closed;
            try
            {
                (json, closed) = await ReceiveMessageAsync(ws, _shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                Trace.WriteLine($"[HostServer] WebSocket read failed for {member.Nickname}: {ex.Message}");
                break;
            }

            if (closed) break;

            NetMessage? msg;
            try
            {
                msg = MultiplayerJson.Deserialize(json);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HostServer] Failed to parse message from {member.Nickname}: {ex.Message}");
                await SendAsync(conn, new ErrorMsg { Message = "Invalid JSON" }, _shutdownToken).ConfigureAwait(false);
                continue;
            }

            if (msg is null) continue;

            try
            {
                await DispatchIncomingAsync(conn, member, msg).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HostServer] Dispatch failed for {msg.GetType().Name}: {ex}");
            }
        }

        // Return the real connection id so the caller (HandleConnectionAsync)
        // can remove it from _connections on cleanup. (If the handshake
        // never completed, we returned pendingConnectionId earlier.)
        return connectionId;
    }

    /// <summary>
    /// Dispatch one incoming client message. Handles relay-style messages
    /// (chat, actions) by re-broadcasting, and lifecycle messages (ready,
    /// cancel) by updating state + broadcasting.
    /// </summary>
    private async Task DispatchIncomingAsync(Connection conn, MemberInfo member, NetMessage msg)
    {
        switch (msg)
        {
            case ChatMsg chat:
                {
                    // Rewrite fromId + fromNickname from the socket identity
                    // (clients cannot spoof another member's id).
                    var sanitized = SanitizeChatText(chat.Text);
                    if (sanitized is null) return; // empty after trim
                    var clean = chat with
                    {
                        FromId = member.ConnectionId,
                        FromNickname = member.Nickname,
                        Text = sanitized,
                        Ts = DateTimeOffset.UtcNow,
                    };
                    await BroadcastAsync(clean, _shutdownToken).ConfigureAwait(false);
                    RaiseEvent(ChatReceived, clean);
                    break;
                }

            case ActionQueuedMsg action:
                {
                    var sanitized = SanitizeActionText(action.Text);
                    if (sanitized is null) return;
                    var clean = action with
                    {
                        FromId = member.ConnectionId,
                        FromNickname = member.Nickname,
                        Text = sanitized,
                    };
                    var playerAction = new PlayerAction
                    {
                        Id = clean.ActionId,
                        PlayerId = member.ConnectionId,
                        PlayerNickname = member.Nickname,
                        Text = sanitized,
                        SubmittedAt = clean.Ts,
                    };
                    ActionQueue.Enqueue(playerAction);
                    await BroadcastAsync(clean, _shutdownToken).ConfigureAwait(false);
                    RaiseEvent(ActionQueued, playerAction);
                    break;
                }

            case ActionCancelMsg cancel:
                {
                    var ok = ActionQueue.Cancel(cancel.ActionId);
                    if (ok)
                    {
                        await BroadcastAsync(new ActionCancelledMsg { ActionId = cancel.ActionId }, _shutdownToken).ConfigureAwait(false);
                    }
                    RaiseEvent(ActionCancelled, ok ? cancel.ActionId : null);
                    break;
                }

            case MemberReadyMsg ready:
                {
                    // Update the member's status.
                    MemberInfo? updatedMember = null;
                    if (_connections.TryGetValue(member.ConnectionId, out var c) && c.Member is not null)
                    {
                        c.Member = c.Member with { Status = ready.Ready ? MemberStatus.Ready : MemberStatus.Pending };
                        updatedMember = c.Member;
                    }
                    var clean = ready with { ConnectionId = member.ConnectionId };
                    await BroadcastAsync(clean, _shutdownToken).ConfigureAwait(false);
                    // Raise the local event AFTER the broadcast so the host
                    // UI sees the post-update snapshot when it re-reads
                    // Members. Null when the member wasn't found (defensive
                    // — shouldn't happen because the handshake already
                    // registered them, but guards against a race with
                    // RemoveConnectionAsync).
                    if (updatedMember is not null)
                        RaiseEvent(MemberReady, updatedMember);
                    break;
                }

            case StatusChangedMsg status:
                {
                    // Only the host can change party status. Clients
                    // sending this are ignored (host drives transitions
                    // via SetStatusAsync).
                    if (member.Role != MemberRole.Host) return;
                    lock (_statusLock)
                    {
                        _status = status.Status;
                        if (status.SaveId is not null) _saveId = status.SaveId;
                        if (status.Turn > 0) _turn = status.Turn;
                    }
                    await BroadcastAsync(status, _shutdownToken).ConfigureAwait(false);
                    // Raise the local event AFTER the broadcast so the
                    // host UI can refresh its IsLobby flag.
                    RaiseEvent(StatusChanged, status.Status);
                    break;
                }

            case PingMsg ping:
                {
                    // Reply directly to the sender with a PongMsg echoing
                    // the ping's Ts.
                    await SendAsync(conn, new PongMsg { Ts = ping.Ts }, _shutdownToken).ConfigureAwait(false);
                    break;
                }

            case HelloMsg:
            case WelcomeMsg:
            case RejectMsg:
            case MemberJoinedMsg:
            case MemberLeftMsg:
            case ActionCancelledMsg:
            case ActionResolvingMsg:
            case NarrativeDeltaMsg:
            case NarrativeFinalMsg:
            case StateUpdateMsg:
            case TurnEndMsg:
            case ErrorMsg:
            case KickedMsg:
            case PongMsg:
                // Server-only messages — ignore if received from a client.
                // (Future: log a warning.)
                break;

            default:
                Trace.WriteLine($"[HostServer] Unhandled message kind: {msg.GetType().Name}");
                break;
        }
    }

    // ─── Connection removal ──────────────────────────────────────────

    private async Task RemoveConnectionAsync(Guid connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var conn)) return;

        // Only announce MemberLeft for connections that completed the
        // handshake (i.e. have a MemberInfo). Pending connections (no
        // HelloMsg yet) are dropped silently.
        if (conn.Member is not null)
        {
            // Free the nickname.
            _nicknamesByLower.TryRemove(conn.Member.Nickname.ToLowerInvariant(), out _);

            // Broadcast MemberLeftMsg.
            try
            {
                await BroadcastAsync(
                    new MemberLeftMsg { ConnectionId = connectionId, Reason = "disconnected" },
                    _shutdownToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HostServer] Failed to broadcast MemberLeft: {ex}");
            }

            // Raise the event locally.
            RaiseEvent(MemberLeft, conn.Member);
        }

        // Dispose the WebSocket.
        var ws = conn.WebSocket;
        if (ws is not null)
        {
            try { ws.Dispose(); } catch { /* ignore */ }
        }
    }

    // ─── Broadcast / send ────────────────────────────────────────────

    /// <summary>
    /// Send a message to ALL connected clients (host is skipped because
    /// it has no WebSocket — the HostSession raises the event locally).
    /// Each client send is best-effort: a single failed send doesn't
    /// stop the broadcast to other clients.
    /// </summary>
    public async Task BroadcastAsync(NetMessage msg, CancellationToken ct)
    {
        var bytes = MultiplayerJson.SerializeToUtf8Bytes(msg);
        var segment = new ArraySegment<byte>(bytes);

        // Snapshot the connections so we don't hold the dict during sends.
        var targets = _connections.Values
            .Where(c => c.WebSocket is not null && c.WebSocket.State == WebSocketState.Open)
            .ToList();

        foreach (var c in targets)
        {
            try
            {
                await SendBytesAsync(c, segment, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HostServer] Broadcast send failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Send a message to ONE client (identified by connection id). No-op
    /// if the connection doesn't exist or isn't open. The host's own
    /// connection id is a valid argument but is a no-op (no WebSocket).
    /// </summary>
    public async Task SendAsync(Guid connectionId, NetMessage msg, CancellationToken ct)
    {
        if (!_connections.TryGetValue(connectionId, out var conn)) return;
        var ws = conn.WebSocket;
        if (ws is null || ws.State != WebSocketState.Open) return;

        var bytes = MultiplayerJson.SerializeToUtf8Bytes(msg);
        try
        {
            await SendBytesAsync(conn, new ArraySegment<byte>(bytes), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[HostServer] Send to {connectionId} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Convenience: enqueue an action on behalf of the host player. The
    /// host has no WebSocket, so this method directly:
    /// 1. Constructs a PlayerAction + ActionQueuedMsg,
    /// 2. Enqueues into <see cref="ActionQueue"/>,
    /// 3. Broadcasts the ActionQueuedMsg to all remote clients,
    /// 4. Raises <see cref="ActionQueued"/> locally.
    /// </summary>
    public async Task EnqueueHostActionAsync(string actionId, string text, DateTimeOffset ts, CancellationToken ct)
    {
        var sanitized = SanitizeActionText(text);
        if (sanitized is null) return;

        var action = new PlayerAction
        {
            Id = actionId,
            PlayerId = HostConnectionId,
            PlayerNickname = _hostProfile.Nickname,
            Text = sanitized,
            SubmittedAt = ts,
        };
        ActionQueue.Enqueue(action);

        var msg = new ActionQueuedMsg
        {
            ActionId = actionId,
            FromId = HostConnectionId,
            FromNickname = _hostProfile.Nickname,
            Text = sanitized,
            Ts = ts,
        };
        await BroadcastAsync(msg, ct).ConfigureAwait(false);
        RaiseEvent(ActionQueued, action);
    }

    /// <summary>
    /// Cancel an action by id (host or any client). Cancels in the local
    /// <see cref="ActionQueue"/> and broadcasts
    /// <see cref="ActionCancelledMsg"/> to all clients.
    /// </summary>
    public async Task CancelActionAsync(string actionId, CancellationToken ct)
    {
        var ok = ActionQueue.Cancel(actionId);
        if (ok)
        {
            await BroadcastAsync(new ActionCancelledMsg { ActionId = actionId }, ct).ConfigureAwait(false);
        }
        RaiseEvent(ActionCancelled, ok ? actionId : null);
    }

    /// <summary>
    /// Update the party lifecycle status and broadcast
    /// <see cref="StatusChangedMsg"/> to all clients. Typically called by
    /// the HostSession when transitioning Lobby → Worldbuilding →
    /// CharacterCreation → Playing. Also raises the local
    /// <see cref="StatusChanged"/> event so the host UI can refresh
    /// (e.g. hide the lobby layout when transitioning to Playing —
    /// issue #77).
    /// </summary>
    public async Task SetStatusAsync(PartyStatus status, string? saveId = null, int turn = 0, CancellationToken ct = default)
    {
        lock (_statusLock)
        {
            _status = status;
            if (saveId is not null) _saveId = saveId;
            if (turn > 0) _turn = turn;
        }
        await BroadcastAsync(new StatusChangedMsg
        {
            Status = status,
            Turn = turn > 0 ? turn : _turn,
            SaveId = saveId ?? _saveId,
        }, ct).ConfigureAwait(false);
        // Raise the local event AFTER the broadcast so the host UI
        // sees the post-update Status when it re-reads. This is what
        // drives the IsLobby → in-game UI transition when the host
        // clicks «Начать игру» in the lobby (issue #77).
        RaiseEvent(StatusChanged, status);
    }

    /// <summary>
    /// Set the current turn number (called by HostSession after a GM turn
    /// completes). Does NOT broadcast — the HostSession is expected to
    /// send a <see cref="TurnEndMsg"/> via <see cref="BroadcastAsync"/>
    /// which carries the turn number.
    /// </summary>
    public void SetTurn(int turn)
    {
        lock (_statusLock) _turn = turn;
    }

    /// <summary>
    /// Kick a client. Sends <see cref="KickedMsg"/>, then closes the
    /// connection. No-op if the connection id is unknown (or belongs to
    /// the host, which cannot be kicked).
    /// </summary>
    public async Task KickAsync(Guid connectionId, string reason, CancellationToken ct)
    {
        if (connectionId == HostConnectionId) return;
        if (!_connections.TryGetValue(connectionId, out var conn)) return;
        var ws = conn.WebSocket;
        if (ws is null) return;

        try
        {
            await SendAsync(conn, new KickedMsg { Reason = reason }, ct).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "kicked", ct).ConfigureAwait(false);
            }
        }
        catch { /* ignore */ }
    }

    // ─── Internal helpers ────────────────────────────────────────────

    private PartySnapshot BuildPartySnapshot()
    {
        var members = Members;
        PartyStatus status; string? saveId; int turn;
        lock (_statusLock) { status = _status; saveId = _saveId; turn = _turn; }
        return new PartySnapshot
        {
            HostConnectionId = HostConnectionId,
            Members = members,
            Status = status,
            SaveId = saveId,
            Turn = turn,
        };
    }

    /// <summary>
    /// Send raw bytes to a connection's WebSocket, guarded by the
    /// per-connection <see cref="Connection.SendSemaphore"/>
    /// (<see cref="WebSocket.SendAsync"/> is NOT safe for concurrent
    /// callers on the same instance — multiple broadcasts in flight
    /// would corrupt the wire).
    /// </summary>
    private static async Task SendBytesAsync(Connection conn, ArraySegment<byte> bytes, CancellationToken ct)
    {
        var ws = conn.WebSocket;
        if (ws is null) return;
        await conn.SendSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            conn.SendSemaphore.Release();
        }
    }

    /// <summary>
    /// Receive one full WebSocket text message. Returns the decoded
    /// UTF-8 string. If the client sends a Close frame, returns
    /// (empty, closed: true). Fragments are accumulated into a
    /// MemoryStream until <c>EndOfMessage</c>.
    /// </summary>
    private static async Task<(string text, bool closed)> ReceiveMessageAsync(WebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (string.Empty, true);
            }

            if (result.Count > 0)
            {
                ms.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage) break;
        }

        var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        return (text, false);
    }

    private static async Task SendAndCloseAsync(WebSocket ws, NetMessage msg)
    {
        try
        {
            var bytes = MultiplayerJson.SerializeToUtf8Bytes(msg);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* ignore */ }
        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "rejected", CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Send a message to a connection (used by the per-connection read
    /// loop for WelcomeMsg / PongMsg replies). Guarded by the
    /// per-connection semaphore so concurrent broadcasts can't corrupt
    /// the wire.
    /// </summary>
    private static async Task SendAsync(Connection conn, NetMessage msg, CancellationToken ct)
    {
        var ws = conn.WebSocket;
        if (ws is null || ws.State != WebSocketState.Open) return;
        var bytes = MultiplayerJson.SerializeToUtf8Bytes(msg);
        await SendBytesAsync(conn, new ArraySegment<byte>(bytes), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sanitise chat text: trim, cap at 1000 chars. Returns null if the
    /// result is empty (the message is dropped).
    /// </summary>
    private static string? SanitizeChatText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 1000 ? trimmed[..1000] : trimmed;
    }

    /// <summary>
    /// Sanitise action text: trim, cap at 4000 chars. Returns null if
    /// the result is empty.
    /// </summary>
    private static string? SanitizeActionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 4000 ? trimmed[..4000] : trimmed;
    }

    /// <summary>Find a free TCP port via a temporary TcpListener.</summary>
    private static int GetFreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    /// <summary>
    /// Raise an event with exception-safe dispatch (each subscriber is
    /// wrapped in try/catch; one bad subscriber doesn't break the others).
    /// </summary>
    private static void RaiseEvent<T>(Action<T>? handler, T args)
    {
        if (handler is null) return;
        var delegates = handler.GetInvocationList();
        foreach (var d in delegates)
        {
            try { ((Action<T>)d)(args); }
            catch (Exception ex) { Trace.WriteLine($"[HostServer] event subscriber threw: {ex}"); }
        }
    }

    // ─── Per-connection state ────────────────────────────────────────

    /// <summary>
    /// One connection's state. The host's own Connection has a null
    /// <see cref="WebSocket"/> (it talks to itself directly).
    /// </summary>
    private sealed class Connection
    {
        public WebSocket? WebSocket { get; }
        public MemberInfo? Member { get; set; }
        public SemaphoreSlim SendSemaphore { get; } = new(1, 1);

        public Connection(WebSocket? ws, MemberInfo? member)
        {
            WebSocket = ws;
            Member = member;
        }

        // Note: we don't implement IDisposable here because the
        // HostServer manages disposal explicitly via RemoveConnectionAsync.
        // The SendSemaphore is disposed when the HostServer shuts down
        // (semaphore disposal is best-effort — abandoned waits just throw).
        public void DisposeSemaphore() => SendSemaphore.Dispose();
    }
}
