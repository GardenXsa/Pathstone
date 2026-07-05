using System.Diagnostics;
using System.Net.WebSockets;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Multiplayer;
using MyGame.Core.Multiplayer.Protocol;
using MyGame.Core.World;
using MyGame.Tests.AI;

// 'Profile' is both a namespace (MyGame.Core.Profile) and a type (the
// record inside it). Same alias trick as HostSession.cs — alias the
// type to a different name to sidestep the namespace/type collision.
using GameProfile = MyGame.Core.Profile.Profile;

namespace MyGame.Tests.Multiplayer;

/// <summary>
/// Integration tests for the multiplayer host + client protocol (issue #57).
/// Each test spins up a real <see cref="HostServer"/> (or
/// <see cref="HostSession"/> for action-resolution tests) on an
/// OS-assigned port and connects one or more <see cref="GameClient"/>
/// instances over loopback <c>ws://localhost:&lt;port&gt;/</c>.
///
/// <para>
/// <b>Only the AI layer is stubbed:</b> a <see cref="StubAiClient"/>
/// returns canned <see cref="ChatResponse"/> objects so the GM turn
/// produces deterministic narration without contacting a real LLM
/// provider. Everything else — the WebSocket transport, the JSON wire
/// protocol, the <see cref="HostServer"/>'s accept loop, the
/// <see cref="GameClient"/>'s read loop, the
/// <see cref="HostSession"/> turn orchestration, the
/// <see cref="ActionQueue"/> draining — is exercised end-to-end.
/// </para>
///
/// <para>
/// <b>Determinism:</b> every wait uses a
/// <see cref="TaskCompletionSource{TResult}"/> wired up to the relevant
/// event handler, awaited with a 10-second timeout. No
/// <c>Thread.Sleep</c> / <c>Task.Delay</c> polling. If the expected
/// event doesn't fire, the test fails fast rather than hanging the
/// suite.
/// </para>
///
/// <para>
/// <b>Cleanup:</b> each test's host + clients are torn down in a
/// <c>finally</c> block (or via <see cref="IAsyncDisposable"/>) so
/// background read loops don't leak across tests. The
/// <see cref="HostServer"/>'s accept loop is stopped via its shutdown
/// token; client <see cref="ClientWebSocket"/>s are closed + disposed.
/// </para>
/// </summary>
public sealed class HostClientIntegrationTests : IAsyncDisposable
{
    // 10-second timeout for every event wait. Generous enough for a
    // loopback WebSocket round-trip (typically &lt;5ms) + the
    // HostServer's ThreadPool-scheduled read loop start-up. Tight
    // enough to fail a hanging test before the suite's overall
    // timeout.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly List<Func<Task>> _cleanupSteps = new();
    private readonly List<CancellationTokenSource> _ctsToDispose = new();

    /// <summary>
    /// Per-test cleanup. Runs all registered cleanup steps in reverse
    /// order (LIFO — clients before host) so disconnects happen before
    /// the host stops accepting. Safe to call multiple times.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Stop clients first so the HostServer's MemberLeft broadcast
        // doesn't race with the listener shutdown.
        var exceptions = new List<Exception>();
        for (int i = _cleanupSteps.Count - 1; i >= 0; i--)
        {
            try { await _cleanupSteps[i]().ConfigureAwait(false); }
            catch (Exception ex) { exceptions.Add(ex); }
        }
        _cleanupSteps.Clear();
        foreach (var cts in _ctsToDispose)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* ignore */ }
        }
        _ctsToDispose.Clear();

        if (exceptions.Count > 0)
            throw new AggregateException("Cleanup failed", exceptions);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Build a fresh <see cref="Profile"/> with the given nickname.
    /// Each call produces a new ProfileId so two clients with the same
    /// nickname would still be distinct profiles (the HostServer
    /// validates nickname uniqueness, not profile id uniqueness).
    /// </summary>
    private static GameProfile MakeProfile(string nickname) =>
        new() { Nickname = nickname };

    /// <summary>
    /// Start a fresh <see cref="HostServer"/> on an OS-assigned port
    /// bound to <c>localhost</c> (single-machine testing — no admin
    /// privileges needed, no LAN exposure). The shutdown CTS is
    /// registered for disposal; the server's StopAsync is registered
    /// for cleanup. Returns the started server + the actual port.
    /// </summary>
    private async Task<(HostServer server, int port, CancellationTokenSource cts)> StartHostAsync(
        GameProfile hostProfile)
    {
        var cts = new CancellationTokenSource();
        _ctsToDispose.Add(cts);
        var server = new HostServer(
            hostProfile,
            requestedPort: 0,
            shutdownToken: cts.Token,
            bindHost: "localhost");
        var port = await server.StartAsync();
        _cleanupSteps.Add(async () => await server.StopAsync());
        return (server, port, cts);
    }

    /// <summary>
    /// Connect a <see cref="GameClient"/> to <c>ws://localhost:&lt;port&gt;/</c>
    /// and await the Welcomed event. The client's DisconnectAsync is
    /// registered for cleanup. Returns the client + the WelcomeMsg.
    /// </summary>
    private async Task<(GameClient client, WelcomeMsg welcome)> ConnectClientAsync(
        GameProfile profile, int port)
    {
        var client = new GameClient(profile);
        _cleanupSteps.Add(async () =>
        {
            try { await client.DisconnectAsync(); }
            catch { /* ignore — already disconnected */ }
        });

        // Wire up a TaskCompletionSource for the Welcomed event BEFORE
        // calling ConnectAsync so we don't miss the event (ConnectAsync
        // raises Welcomed inside its handshake wait, but the read loop
        // starts AFTER ConnectAsync returns — so the race is only
        // between ConnectAsync returning and our handler wiring).
        // Belt-and-suspenders: subscribe before ConnectAsync.
        var welcomedTcs = new TaskCompletionSource<WelcomeMsg>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Welcomed += w => welcomedTcs.TrySetResult(w);

        using var connectCts = new CancellationTokenSource(WaitTimeout);
        try
        {
            await client.ConnectAsync("localhost", port, connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"GameClient.ConnectAsync timed out after {WaitTimeout.TotalSeconds}s.");
        }

        // ConnectAsync returns AFTER the Welcomed event is raised
        // (it awaits the WelcomeMsg internally). But the event handler
        // is invoked synchronously inside ConnectAsync, so by the time
        // we get here welcomedTcs is already completed. Still, use a
        // timeout to be defensive against future changes to ConnectAsync.
        var welcome = await welcomedTcs.Task.WaitAsync(WaitTimeout);
        return (client, welcome);
    }

    /// <summary>
    /// Build a <see cref="HostSession"/> wired to a <see cref="GameMaster"/>
    /// that uses a <see cref="StubAiClient"/> returning the given canned
    /// narration. The session is started (server bound to an
    /// OS-assigned port on <c>localhost</c>) and ready to accept
    /// clients. The shutdown CTS + StopAsync are registered for cleanup.
    /// </summary>
    private async Task<(HostSession session, int port, StubAiClient ai, CancellationTokenSource cts)>
        StartHostSessionAsync(GameProfile hostProfile, string cannedNarration)
    {
        var world = DefaultWorld.Create(seed: 1);
        var ai = new StubAiClient(new ChatResponse
        {
            Content = cannedNarration,
            FinishReason = "stop",
            PromptTokens = 12,
            CompletionTokens = 34,
        });
        var prompts = new PromptLoader(enableHotReload: false);
        var tools = new ToolRegistry(world);
        var gm = new GameMaster(ai, world, prompts, tools);

        var cts = new CancellationTokenSource();
        _ctsToDispose.Add(cts);
        var session = new HostSession(
            hostProfile, world, gm,
            saveManager: null,
            saveId: null,
            requestedPort: 0,
            shutdownToken: cts.Token,
            bindHost: "localhost");
        var port = await session.StartAsync();
        _cleanupSteps.Add(async () => await session.StopAsync());
        return (session, port, ai, cts);
    }

    /// <summary>
    /// Wait for a TaskCompletionSource with the suite-wide timeout.
    /// Throws <see cref="TimeoutException"/> with a helpful message on
    /// timeout so the test output names the event that didn't fire.
    /// </summary>
    private static async Task<T> WaitForAsync<T>(TaskCompletionSource<T> tcs, string eventName)
    {
        try
        {
            return await tcs.Task.WaitAsync(WaitTimeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Timed out after {WaitTimeout.TotalSeconds}s waiting for {eventName}.");
        }
    }

    /// <summary>
    /// Build a TaskCompletionSource wired to the given event via the
    /// supplied subscribe/unsubscribe callbacks. The returned tuple
    /// holds the TCS (to await) + an unsubscribe action (to detach the
    /// handler after the event fires, so a stale handler doesn't bleed
    /// into the next test).
    /// </summary>
    private static (TaskCompletionSource<T> tcs, Action unsubscribe) WireEvent<T>(
        Action<Action<T>> subscribe,
        Action<Action<T>> unsubscribe,
        string label) where T : notnull
    {
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(T args) => tcs.TrySetResult(args);
        subscribe(Handler);
        return (tcs, () => unsubscribe(Handler));
    }

    // ─── Tests ──────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #57: a fresh HostServer starts on an OS-assigned port and
    /// a GameClient completes the Hello → Welcome handshake over
    /// loopback. The WelcomeMsg must carry the server-assigned
    /// ConnectionId, the host's nickname in the party snapshot, and
    /// the Lobby status.
    /// </summary>
    [Fact]
    public async Task HostServer_Starts_And_Client_Connects()
    {
        var hostProfile = MakeProfile("Host");
        var (server, port, _) = await StartHostAsync(hostProfile);

        var clientProfile = MakeProfile("Alice");
        var (client, welcome) = await ConnectClientAsync(clientProfile, port);

        // ConnectionId assigned by the host — non-empty Guid.
        Assert.NotEqual(Guid.Empty, welcome.ConnectionId);
        Assert.Equal(MemberRole.Player, welcome.Role);

        // Party snapshot includes the host (with its nickname) AND the
        // just-joined client (the host broadcasts MemberJoinedMsg
        // after WelcomeMsg, but the snapshot the client received in
        // WelcomeMsg reflects the moment of join — at that point the
        // host is already a member, and the new client is added to
        // the roster slightly later via MemberJoinedMsg).
        Assert.Equal(PartyStatus.Lobby, welcome.Party.Status);
        Assert.Contains(welcome.Party.Members, m => m.Nickname == "Host" && m.Role == MemberRole.Host);

        // The GameClient exposes the assigned ConnectionId.
        Assert.Equal(welcome.ConnectionId, client.ConnectionId);
    }

    /// <summary>
    /// Issue #57: when a second client joins, the first client
    /// receives a <see cref="MemberJoinedMsg"/> carrying the new
    /// member's nickname + Player role.
    /// </summary>
    [Fact]
    public async Task HostServer_Client_Join_Broadcasts_MemberJoined()
    {
        var hostProfile = MakeProfile("Host");
        var (server, port, _) = await StartHostAsync(hostProfile);

        var (client1, _) = await ConnectClientAsync(MakeProfile("Alice"), port);

        // Wire up the MemberJoined listener BEFORE client2 connects
        // so we don't miss the broadcast.
        var (joinedTcs, unsub) = WireEvent<MemberJoinedMsg>(
            h => client1.MemberJoined += h,
            h => client1.MemberJoined -= h,
            "MemberJoined");

        try
        {
            var (client2, _) = await ConnectClientAsync(MakeProfile("Bob"), port);

            var joined = await WaitForAsync(joinedTcs, "client1.MemberJoined");

            // The joined member is client2 ("Bob"), a Player.
            Assert.Equal("Bob", joined.Member.Nickname);
            Assert.Equal(MemberRole.Player, joined.Member.Role);
            Assert.NotEqual(Guid.Empty, joined.Member.ConnectionId);
        }
        finally
        {
            unsub();
        }
    }

    /// <summary>
    /// Issue #57: when the host sends a lobby chat message via
    /// <see cref="HostServer.BroadcastAsync(NetMessage, CancellationToken)"/>,
    /// every connected client receives the <see cref="ChatMsg"/> via
    /// their <see cref="GameClient.ChatReceived"/> event.
    /// </summary>
    [Fact]
    public async Task HostServer_Chat_Relayed_To_All_Clients()
    {
        var hostProfile = MakeProfile("Host");
        var (server, port, cts) = await StartHostAsync(hostProfile);

        var (client1, _) = await ConnectClientAsync(MakeProfile("Alice"), port);
        var (client2, _) = await ConnectClientAsync(MakeProfile("Bob"), port);

        // Wire up chat listeners BEFORE broadcasting so we don't miss
        // the message. Use TaskCompletionSource per client.
        var chat1Tcs = new TaskCompletionSource<ChatMsg>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var chat2Tcs = new TaskCompletionSource<ChatMsg>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler1(ChatMsg c) => chat1Tcs.TrySetResult(c);
        void Handler2(ChatMsg c) => chat2Tcs.TrySetResult(c);
        client1.ChatReceived += Handler1;
        client2.ChatReceived += Handler2;

        try
        {
            // Broadcast a chat on behalf of the host. The HostServer
            // relays to all connected clients.
            var chatMsg = new ChatMsg
            {
                FromId = server.HostConnectionId,
                FromNickname = "Host",
                Text = "hello party",
                Ts = DateTimeOffset.UtcNow,
            };
            await server.BroadcastAsync(chatMsg, cts.Token);

            // Both clients should receive the chat.
            var received1 = await WaitForAsync(chat1Tcs, "client1.ChatReceived");
            var received2 = await WaitForAsync(chat2Tcs, "client2.ChatReceived");

            Assert.Equal("hello party", received1.Text);
            Assert.Equal("Host", received1.FromNickname);
            Assert.Equal("hello party", received2.Text);
            Assert.Equal("Host", received2.FromNickname);
        }
        finally
        {
            client1.ChatReceived -= Handler1;
            client2.ChatReceived -= Handler2;
        }
    }

    /// <summary>
    /// Issue #57: a client submits an action via
    /// <see cref="ClientSession.SubmitActionAsync"/>; the host's
    /// <see cref="HostSession.ProcessNextTurnAsync"/> drains the queue,
    /// calls the GameMaster (which uses the
    /// <see cref="StubAiClient"/> to return canned narration), and
    /// broadcasts <see cref="NarrativeFinalMsg"/> +
    /// <see cref="StateUpdateMsg"/> to all clients.
    ///
    /// <para>
    /// Verifies the full pipeline: client-side action submission →
    /// HostServer ActionQueued broadcast → HostSession drain + GM turn
    /// → NarrativeDelta + NarrativeFinal + StateUpdate + TurnEnd
    /// broadcasts back to the client.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HostServer_Action_Queued_And_Resolved()
    {
        const string cannedNarration = "Ты осмотрелся. Таверна гудит тихой беседой.";
        var hostProfile = MakeProfile("Host");
        var (session, port, stubAi, cts) = await StartHostSessionAsync(hostProfile, cannedNarration);

        // Connect a client. Use the ClientSession wrapper for action
        // submission convenience.
        var clientProfile = MakeProfile("Alice");
        var clientSession = new ClientSession(clientProfile);
        _cleanupSteps.Add(async () =>
        {
            try { await clientSession.DisconnectAsync(); }
            catch { /* ignore */ }
        });

        var welcomedTcs = new TaskCompletionSource<WelcomeMsg>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        clientSession.Welcomed += w => welcomedTcs.TrySetResult(w);

        using var connectCts = new CancellationTokenSource(WaitTimeout);
        await clientSession.ConnectAsync("localhost", port, connectCts.Token);
        await welcomedTcs.Task.WaitAsync(WaitTimeout);

        // Wire up listeners for the narrative + state-update events.
        var narrativeFinalTcs = new TaskCompletionSource<NarrativeFinalMsg>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stateUpdateTcs = new TaskCompletionSource<StateUpdateMsg>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var turnEndTcs = new TaskCompletionSource<TurnEndMsg>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinal(NarrativeFinalMsg m) => narrativeFinalTcs.TrySetResult(m);
        void OnState(StateUpdateMsg m) => stateUpdateTcs.TrySetResult(m);
        void OnTurnEnd(TurnEndMsg m) => turnEndTcs.TrySetResult(m);
        clientSession.NarrativeFinal += OnFinal;
        clientSession.StateUpdate += OnState;
        clientSession.TurnEnd += OnTurnEnd;

        try
        {
            // Submit an action via the client session. The host's
            // HostServer relays ActionQueuedMsg back; the host's
            // batching window (issue #12) then fires
            // ProcessNextTurnAsync. With a single ready non-spectator
            // member (the host — the client is Pending), the window
            // fires IMMEDIATELY (no 5-second countdown). The GM turn
            // runs, broadcasting NarrativeDelta + NarrativeFinal +
            // StateUpdate + TurnEnd back to the client.
            //
            // We don't call ProcessNextTurnAsync from the test — we
            // let the batching window do it. To verify the turn ran,
            // we wait for the client's NarrativeFinal event. If the
            // action never reached the host (network failure), the
            // wait times out after 10s and the test fails clearly.
            await clientSession.SubmitActionAsync("осмотреться", cts.Token);

            // The client should now receive NarrativeFinal + StateUpdate +
            // TurnEnd. Wait for each (10s timeout each).
            var final = await WaitForAsync(narrativeFinalTcs, "NarrativeFinal");
            var stateUpdate = await WaitForAsync(stateUpdateTcs, "StateUpdate");
            var turnEnd = await WaitForAsync(turnEndTcs, "TurnEnd");

            // The canned narration is what the GM produced.
            Assert.Equal(cannedNarration, final.FullText);
            // Tool events should be empty (the stub returned no tool calls).
            Assert.Empty(final.ToolEvents);
            // Token counts match the canned response.
            Assert.Equal(12, final.PromptTokens);
            Assert.Equal(34, final.CompletionTokens);
            // Turn should match across all three broadcast messages.
            Assert.Equal(turnEnd.TurnNumber, final.Turn);
            Assert.Equal(turnEnd.TurnNumber, stateUpdate.Turn);
            Assert.True(turnEnd.TurnNumber > 0, "Turn number should be > 0 after a GM turn.");
            // State update carries a non-empty world JSON snapshot.
            Assert.False(string.IsNullOrEmpty(stateUpdate.WorldJson));
            // The stub made exactly one streaming call (the GM loop
            // exits on iteration 1 because the canned response has no
            // tool calls).
            Assert.Equal(1, stubAi.StreamChatWithToolsCallCount);
            Assert.Equal(0, stubAi.ChatCallCount);
        }
        finally
        {
            clientSession.NarrativeFinal -= OnFinal;
            clientSession.StateUpdate -= OnState;
            clientSession.TurnEnd -= OnTurnEnd;
        }
    }

    /// <summary>
    /// Issue #57: when a client disconnects (graceful close), the
    /// host raises <see cref="HostServer.MemberLeft"/> and broadcasts
    /// <see cref="MemberLeftMsg"/> to remaining clients.
    /// </summary>
    [Fact]
    public async Task HostServer_Client_Disconnect_Broadcasts_MemberLeft()
    {
        var hostProfile = MakeProfile("Host");
        var (server, port, cts) = await StartHostAsync(hostProfile);

        var (client1, _) = await ConnectClientAsync(MakeProfile("Alice"), port);
        var (client2, _) = await ConnectClientAsync(MakeProfile("Bob"), port);

        // Wire up the MemberLeft listener on client1 BEFORE client2
        // disconnects so we don't miss the broadcast.
        var (leftTcs, unsub) = WireEvent<MemberLeftMsg>(
            h => client1.MemberLeft += h,
            h => client1.MemberLeft -= h,
            "MemberLeft");

        // Also wire up the host-side MemberLeft event.
        var hostLeftTcs = new TaskCompletionSource<MemberInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void HostHandler(MemberInfo m) => hostLeftTcs.TrySetResult(m);
        server.MemberLeft += HostHandler;

        try
        {
            // Graceful disconnect from client2.
            await client2.DisconnectAsync();

            // The host should raise MemberLeft for client2.
            var hostLeft = await WaitForAsync(hostLeftTcs, "HostServer.MemberLeft");
            Assert.Equal("Bob", hostLeft.Nickname);

            // The remaining client1 should receive a MemberLeftMsg.
            var left = await WaitForAsync(leftTcs, "client1.MemberLeft");
            Assert.Equal(client2.ConnectionId, left.ConnectionId);
        }
        finally
        {
            server.MemberLeft -= HostHandler;
            unsub();
        }
    }

    /// <summary>
    /// Issue #57: when the host calls
    /// <see cref="HostServer.KickAsync"/>, the kicked client receives a
    /// <see cref="KickedMsg"/> and its connection is closed (its
    /// <see cref="GameClient.Disconnected"/> event fires with
    /// <see cref="DisconnectedInfo.Intentional"/> = true).
    /// </summary>
    [Fact]
    public async Task HostServer_Kick_Removes_Client()
    {
        var hostProfile = MakeProfile("Host");
        var (server, port, cts) = await StartHostAsync(hostProfile);

        var (client, welcome) = await ConnectClientAsync(MakeProfile("Alice"), port);

        // Wire up Kicked + Disconnected listeners BEFORE the kick so
        // we don't miss either event.
        var kickedTcs = new TaskCompletionSource<KickedMsg>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTcs = new TaskCompletionSource<DisconnectedInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnKicked(KickedMsg k) => kickedTcs.TrySetResult(k);
        void OnDisconnected(DisconnectedInfo info) => disconnectedTcs.TrySetResult(info);
        client.Kicked += OnKicked;
        client.Disconnected += OnDisconnected;

        try
        {
            // Host kicks the client.
            await server.KickAsync(welcome.ConnectionId, "test kick", cts.Token);

            // The client should receive KickedMsg.
            var kicked = await WaitForAsync(kickedTcs, "Kicked");
            Assert.Equal("test kick", kicked.Reason);

            // The client should disconnect (intentional — the GameClient
            // sets _intentionalDisconnect = true on Kicked).
            var disconnected = await WaitForAsync(disconnectedTcs, "Disconnected");
            Assert.True(disconnected.Intentional,
                "Kick should mark the disconnect as intentional so the UI " +
                "suppresses the reconnect overlay.");
        }
        finally
        {
            client.Kicked -= OnKicked;
            client.Disconnected -= OnDisconnected;
        }
    }
}
