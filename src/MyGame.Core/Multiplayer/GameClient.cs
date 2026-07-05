using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using MyGame.Core.Multiplayer.Protocol;
using MyGame.Core.Profile;

namespace MyGame.Core.Multiplayer;

/// <summary>
/// Outgoing WebSocket client. Replaces the TS source's
/// <c>use-websocket.ts</c> (socket.io-client React hook) with a plain
/// <see cref="ClientWebSocket"/> connection to a host's
/// <c>ws://&lt;host&gt;:&lt;port&gt;/</c> endpoint.
///
/// <para>
/// When the user clicks "Join Game", the UI constructs a
/// <see cref="GameClient"/> with the local <c>Profile</c>, calls
/// <see cref="ConnectAsync"/> with the host's IP + port, and the client
/// performs the HelloMsg → WelcomeMsg handshake. Subsequent incoming
/// messages are dispatched to typed events the UI can subscribe to.
/// </para>
///
/// <para><b>Architecture notes (deviations from the TS source):</b>
/// <list type="bullet">
///   <item>NO socket.io-client. Raw <see cref="ClientWebSocket"/> carrying
///     JSON-serialised <see cref="NetMessage"/>s.</item>
///   <item>NO React hook. The client is a plain class with C# events. The
///     Avalonia UI layer wires up an <c>IObservable</c> adapter or calls
///     <c>Dispatcher.UIThread.Post</c> from the event handlers.</item>
///   <item>NO reconnection. If the connection drops, the
///     <see cref="Disconnected"/> event fires and the UI is responsible
///     for showing a "Reconnect?" dialog. (The TS source had auto-reconnect
///     via socket.io; that's a future feature.)</item>
///   <item>NO JWT / cookies. The client sends a <see cref="HelloMsg"/>
///     with the local <c>Profile</c> identity in the first frame after
///     the WebSocket upgrade.</item>
/// </list>
/// </para>
///
/// <para><b>Thread safety:</b> sends are serialised by a per-client
/// <see cref="SemaphoreSlim"/> (because <see cref="WebSocket.SendAsync"/>
/// is NOT safe for concurrent callers on the same instance). The read
/// loop runs on a ThreadPool task; event handlers are invoked on that
/// same task (UI subscribers must marshal to the UI thread).</para>
/// </summary>
public sealed class GameClient
{
    // ─── Config ──────────────────────────────────────────────────────

    private readonly Profile.Profile _profile;

    // ─── Runtime state ───────────────────────────────────────────────

    private ClientWebSocket? _ws;
    private Task? _readLoopTask;
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private readonly CancellationTokenSource _ownCts = new();

    /// <summary>
    /// The connection id assigned by the host in <see cref="WelcomeMsg"/>.
    /// Valid only after <see cref="Welcomed"/> fires. Use this as the
    /// <c>fromId</c> on outgoing <see cref="ChatMsg"/> /
    /// <see cref="ActionQueuedMsg"/> (although the host rewrites fromId
    /// from the socket identity anyway, sending the correct id lets the
    /// host skip the rewrite as an optimisation).
    /// </summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>
    /// The role the host assigned in <see cref="WelcomeMsg"/>.
    /// Always <see cref="MemberRole.Player"/> or
    /// <see cref="MemberRole.Spectator"/> (the host is never a client).
    /// </summary>
    public MemberRole Role { get; private set; } = MemberRole.Player;

    /// <summary>True after <see cref="ConnectAsync"/> succeeds (the
    /// handshake completed and the read loop is running).</summary>
    public bool IsConnected => _ws is not null
        && _ws.State == WebSocketState.Open
        && ConnectionId != Guid.Empty;

    /// <summary>
    /// Create a client for the given local profile. Does NOT connect —
    /// call <see cref="ConnectAsync"/> to open the WebSocket.
    /// </summary>
    public GameClient(Profile.Profile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    // ─── Events ──────────────────────────────────────────────────────

    /// <summary>Raised when the <see cref="WelcomeMsg"/> arrives and the
    /// handshake completes. <see cref="ConnectionId"/> and
    /// <see cref="Role"/> are set before this event fires.</summary>
    public event Action<WelcomeMsg>? Welcomed;

    /// <summary>Raised when another member joins the party (host or
    /// another client).</summary>
    public event Action<MemberJoinedMsg>? MemberJoined;

    /// <summary>Raised when a member leaves (disconnect or kick).</summary>
    public event Action<MemberLeftMsg>? MemberLeft;

    /// <summary>Raised when a member toggles their ready state.</summary>
    public event Action<MemberReadyMsg>? MemberReady;

    /// <summary>Raised when the party status changes (Lobby →
    /// Worldbuilding → …).</summary>
    public event Action<StatusChangedMsg>? StatusChanged;

    /// <summary>Raised when a chat message arrives (from anyone — the
    /// host's own chat is included).</summary>
    public event Action<ChatMsg>? ChatReceived;

    /// <summary>Raised when an action is queued (by anyone — including
    /// the local client's own action, echoed back by the host).</summary>
    public event Action<ActionQueuedMsg>? ActionQueued;

    /// <summary>Raised when an action is cancelled.</summary>
    public event Action<ActionCancelledMsg>? ActionCancelled;

    /// <summary>Raised when the host starts resolving a batch of actions.</summary>
    public event Action<ActionResolvingMsg>? ActionResolving;

    /// <summary>Raised on each streaming narrative text chunk. Append to
    /// the UI's narration buffer.</summary>
    public event Action<NarrativeDeltaMsg>? NarrativeDelta;

    /// <summary>Raised at the end of a GM turn with the full narration
    /// text + the tool calls made.</summary>
    public event Action<NarrativeFinalMsg>? NarrativeFinal;

    /// <summary>Raised when the host broadcasts a world state update
    /// (after each GM turn).</summary>
    public event Action<StateUpdateMsg>? StateUpdate;

    /// <summary>Raised when a GM turn ends.</summary>
    public event Action<TurnEndMsg>? TurnEnd;

    /// <summary>Raised when the host sends an error message.</summary>
    public event Action<ErrorMsg>? Error;

    /// <summary>Raised when the host kicks this client. The connection
    /// is closed immediately after this event fires.</summary>
    public event Action<KickedMsg>? Kicked;

    /// <summary>Raised when the WebSocket connection drops (network
    /// failure, host shutdown, or graceful close). The string carries
    /// the close status description (may be empty).</summary>
    public event Action<string>? Disconnected;

    // ─── Connect / disconnect ────────────────────────────────────────

    /// <summary>
    /// Open a WebSocket to <c>ws://&lt;host&gt;:&lt;port&gt;/</c>, send
    /// the <see cref="HelloMsg"/> handshake, and wait for the
    /// <see cref="WelcomeMsg"/> reply. Returns the WelcomeMsg on success;
    /// throws <see cref="InvalidOperationException"/> on reject or timeout.
    /// </summary>
    /// <param name="host">Host IP or hostname (e.g. "192.168.1.10").</param>
    /// <param name="port">Host's TCP port.</param>
    /// <param name="ct">Cancellation token (cancelling aborts the
    /// connect attempt + handshake).</param>
    public async Task<WelcomeMsg> ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (IsConnected)
            throw new InvalidOperationException("GameClient is already connected.");
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("host is required.", nameof(host));
        if (port <= 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        // Link the caller's CT with our own (so DisconnectAsync can
        // cancel an in-flight connect too).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _ownCts.Token);

        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{host}:{port}/"), linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            throw new InvalidOperationException($"Failed to connect to ws://{host}:{port}/: {ex.Message}", ex);
        }

        _ws = ws;

        // Send HelloMsg as the first frame.
        var hello = new HelloMsg
        {
            ProtocolVersion = Common.Version.Current,
            Nickname = _profile.Nickname,
            ProfileId = _profile.Id,
        };
        await SendAsyncInternalAsync(hello, linked.Token).ConfigureAwait(false);

        // Wait for WelcomeMsg (or RejectMsg). The handshake has a
        // 15-second timeout so a dead host doesn't hang the UI forever.
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var handshakeLinked = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, handshakeCts.Token);

        WelcomeMsg? welcome = null;
        RejectMsg? reject = null;

        // Read messages synchronously until we get Welcome or Reject.
        // (The full read loop starts after the handshake.)
        try
        {
            while (welcome is null && reject is null)
            {
                var (json, closed) = await ReceiveMessageAsync(ws, handshakeLinked.Token).ConfigureAwait(false);
                if (closed)
                {
                    throw new InvalidOperationException("Connection closed during handshake.");
                }

                var msg = MultiplayerJson.Deserialize(json);
                switch (msg)
                {
                    case WelcomeMsg w:
                        welcome = w;
                        break;
                    case RejectMsg r:
                        reject = r;
                        break;
                    // Ignore other messages during handshake (shouldn't happen
                    // but be tolerant — a misbehaving host might send a
                    // MemberJoinedMsg before Welcome).
                    default:
                        Trace.WriteLine($"[GameClient] Ignored pre-handshake message: {msg?.GetType().Name ?? "null"}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested)
        {
            await CloseAsyncInternalAsync(ws, WebSocketCloseStatus.NormalClosure, "handshake timeout").ConfigureAwait(false);
            throw new InvalidOperationException("Handshake timed out waiting for Welcome.");
        }

        if (reject is not null)
        {
            await CloseAsyncInternalAsync(ws, WebSocketCloseStatus.NormalClosure, "rejected").ConfigureAwait(false);
            throw new InvalidOperationException($"Host rejected connection: {reject.Reason}");
        }

        // Handshake succeeded — set the connection id + role, fire the
        // Welcomed event, and start the read loop.
        ConnectionId = welcome!.ConnectionId;
        Role = welcome.Role;

        _readLoopTask = Task.Run(() => ReadLoopAsync(_ownCts.Token));

        RaiseEvent(Welcomed, welcome);
        return welcome;
    }

    /// <summary>
    /// Send a message to the host. Serialised by the per-client
    /// <see cref="_sendSemaphore"/> so concurrent sends don't corrupt the
    /// WebSocket.
    /// </summary>
    public async Task SendAsync(NetMessage msg, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
            throw new InvalidOperationException("GameClient is not connected.");

        await SendAsyncInternalAsync(msg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Graceful disconnect: send a Close frame, wait for the read loop to
    /// finish, dispose the WebSocket. Safe to call multiple times.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _ownCts.Cancel();

        var ws = Interlocked.Exchange(ref _ws, null);
        if (ws is not null)
        {
            await CloseAsyncInternalAsync(ws, WebSocketCloseStatus.NormalClosure, "client disconnect").ConfigureAwait(false);
            ws.Dispose();
        }

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask.ConfigureAwait(false); } catch { /* ignore */ }
            _readLoopTask = null;
        }
    }

    // ─── Read loop ───────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null) return;

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                string json;
                bool closed;
                try
                {
                    (json, closed) = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (WebSocketException ex)
                {
                    Trace.WriteLine($"[GameClient] Read failed: {ex.Message}");
                    RaiseEvent(Disconnected, ex.Message);
                    break;
                }

                if (closed)
                {
                    RaiseEvent(Disconnected, "server closed connection");
                    break;
                }

                NetMessage? msg;
                try
                {
                    msg = MultiplayerJson.Deserialize(json);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[GameClient] Failed to parse message: {ex.Message}");
                    continue;
                }

                if (msg is null) continue;

                try
                {
                    DispatchIncoming(msg);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[GameClient] Dispatch failed for {msg.GetType().Name}: {ex}");
                }
            }
        }
        finally
        {
            // If the loop exits without Disconnected having been raised
            // (e.g. via cancellation), raise it now so the UI updates.
            if (!ct.IsCancellationRequested)
            {
                RaiseEvent(Disconnected, "connection lost");
            }
        }
    }

    private void DispatchIncoming(NetMessage msg)
    {
        switch (msg)
        {
            case WelcomeMsg w:        RaiseEvent(Welcomed, w); break;
            case MemberJoinedMsg m:   RaiseEvent(MemberJoined, m); break;
            case MemberLeftMsg m:     RaiseEvent(MemberLeft, m); break;
            case MemberReadyMsg m:    RaiseEvent(MemberReady, m); break;
            case StatusChangedMsg s:  RaiseEvent(StatusChanged, s); break;
            case ChatMsg c:           RaiseEvent(ChatReceived, c); break;
            case ActionQueuedMsg a:   RaiseEvent(ActionQueued, a); break;
            case ActionCancelledMsg a:RaiseEvent(ActionCancelled, a); break;
            case ActionResolvingMsg a:RaiseEvent(ActionResolving, a); break;
            case NarrativeDeltaMsg n: RaiseEvent(NarrativeDelta, n); break;
            case NarrativeFinalMsg n: RaiseEvent(NarrativeFinal, n); break;
            case StateUpdateMsg s:    RaiseEvent(StateUpdate, s); break;
            case TurnEndMsg t:        RaiseEvent(TurnEnd, t); break;
            case ErrorMsg e:          RaiseEvent(Error, e); break;
            case KickedMsg k:
                RaiseEvent(Kicked, k);
                // Host will close the connection; let the read loop pick
                // that up and raise Disconnected.
                break;
            case PingMsg ping:
                // Reply with PongMsg echoing the Ts. Fire-and-forget —
                // the read loop can't await without blocking subsequent
                // messages. Best-effort: a failed pong just means the
                // host's keepalive will time out later.
                _ = Task.Run(async () =>
                {
                    try { await SendAsync(new PongMsg { Ts = ping.Ts }, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* ignore */ }
                });
                break;
            case PongMsg:
                // Currently we don't initiate pings from the client side,
                // so pongs are ignored. Future: track RTT for connection
                // health monitoring.
                break;
            case HelloMsg:
            case RejectMsg:
                // Server-only messages — ignore.
                break;
            default:
                Trace.WriteLine($"[GameClient] Unhandled message kind: {msg.GetType().Name}");
                break;
        }
    }

    // ─── Internal helpers ────────────────────────────────────────────

    private async Task SendAsyncInternalAsync(NetMessage msg, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null) return;

        var bytes = MultiplayerJson.SerializeToUtf8Bytes(msg);
        await _sendSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    private static async Task CloseAsyncInternalAsync(WebSocket ws, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(status, description, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Receive one full WebSocket text message. Same accumulation logic
    /// as <c>HostServer.ReceiveMessageAsync</c> — duplicated here to keep
    /// the two classes independent (they don't share a base class).
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

    /// <summary>
    /// Raise an event with exception-safe dispatch. Each subscriber is
    /// wrapped in try/catch; one bad subscriber doesn't break the others.
    /// </summary>
    private static void RaiseEvent<T>(Action<T>? handler, T args)
    {
        if (handler is null) return;
        var delegates = handler.GetInvocationList();
        foreach (var d in delegates)
        {
            try { ((Action<T>)d)(args); }
            catch (Exception ex) { Trace.WriteLine($"[GameClient] event subscriber threw: {ex}"); }
        }
    }
}
