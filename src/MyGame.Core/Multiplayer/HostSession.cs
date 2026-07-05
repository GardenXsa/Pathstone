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
        _server.MemberLeft += m => RaiseEvent(MemberLeft, m);
        _server.ChatReceived += c => RaiseEvent(ChatReceived, c);
        _server.ActionQueued += a => RaiseEvent(ActionQueued, a);
        _server.ActionCancelled += id => RaiseEventNullable(ActionCancelled, id);
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
        return port;
    }

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

            // Build the combined prompt: one block per action, with the
            // player's nickname + the action text. The GM's system prompt
            // already instructs it on how to handle multi-player turns.
            var prompt = BuildCombinedPrompt(actions);

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
                result = await _gm.ProcessActionAsync(prompt, ct).ConfigureAwait(false);
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

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Build the combined prompt for the GM from a batch of player
    /// actions. Each action is rendered as a quoted block with the
    /// player's nickname, so the GM can address multiple players in one
    /// narration. Example:
    /// <code>
    /// Игрок Иван:
    ///   "атакую гоблина мечом"
    ///
    /// Игрок Мария:
    ///   "кастую огненный шар"
    /// </code>
    /// </summary>
    private static string BuildCombinedPrompt(IReadOnlyList<PlayerAction> actions)
    {
        if (actions.Count == 1)
        {
            return $"{actions[0].PlayerNickname}: {actions[0].Text}";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Несколько игроков действуют одновременно в этом ходу:");
        sb.AppendLine();
        foreach (var a in actions)
        {
            sb.AppendLine($"Игрок {a.PlayerNickname}:");
            sb.AppendLine($"  \"{a.Text}\"");
            sb.AppendLine();
        }
        sb.AppendLine("Опиши разрешение всех этих действий в одной общей сцене.");
        return sb.ToString();
    }

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
