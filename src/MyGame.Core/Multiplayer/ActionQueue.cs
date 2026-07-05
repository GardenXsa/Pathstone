using System.Collections.Concurrent;
using System.Diagnostics;

namespace MyGame.Core.Multiplayer;

// ─────────────────────────────────────────────────────────────────────────
//  PlayerAction
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// One player action pending GM resolution. Port of <c>QueuedAction</c>
/// from <c>lib/action-queue.ts</c>, simplified for the desktop rewrite:
///
/// <list type="bullet">
///   <item>Drops <c>saveId</c> — the desktop host has exactly one active
///     save per <see cref="HostServer"/>, so the save id is implicit.</item>
///   <item>Drops <c>playerEntityId</c> — entity-id resolution is the
///     host's job, not the queue's. The host's GameMaster will look up
///     the active player by <see cref="PlayerId"/> (which is the
///     member's <see cref="MemberInfo.ConnectionId"/>) at turn-processing
///     time.</item>
///   <item>Drops <c>resolving</c>/<c>resolved</c> boolean flags — the
///     queue is "pending only"; once drained, actions leave the queue
///     entirely. This matches the spec's <c>DrainAll()</c> contract
///     (drain = remove-and-return) and avoids the half-state the TS
///     source had (resolving-but-not-resolved actions lingering in the
///     queue).</item>
/// </list>
///
/// The action id (<see cref="Id"/>) is a string (Guid's "N" format) so
/// the wire messages <see cref="Protocol.ActionQueuedMsg"/> /
/// <see cref="Protocol.ActionCancelMsg"/> can carry it as a bare string.
/// </summary>
public sealed record PlayerAction
{
    /// <summary>
    /// Unique id (Guid's "N" format, 32-char lowercase hex). Generated
    /// by the client when they submit the action and echoed back by the
    /// host. Used as the cancellation key.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The member's <see cref="MemberInfo.ConnectionId"/>. Lets the GM
    /// correlate the action with the submitting player.
    /// </summary>
    public required Guid PlayerId { get; init; }

    /// <summary>
    /// The member's <see cref="MemberInfo.Nickname"/>. Denormalised so
    /// the GM's system prompt can show "Иван: attacks the goblin"
    /// without an extra lookup.
    /// </summary>
    public required string PlayerNickname { get; init; }

    /// <summary>The free-text action the player typed (already trimmed
    /// + capped at 4000 chars by the host).</summary>
    public required string Text { get; init; }

    /// <summary>UTC moment the action was submitted.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────
//  ActionQueue
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thread-safe queue of pending player actions. Port of
/// <c>ActionQueueManager</c> from <c>lib/action-queue.ts</c>.
///
/// <para>
/// The TS source kept a per-<c>saveId</c> map of queues plus a per-save
/// <c>EventEmitter</c>. The desktop port collapses this to a single
/// in-memory queue per <see cref="HostServer"/> instance (one save per
/// host — see <see cref="PlayerAction"/> notes) and replaces the
/// EventEmitter with C# events.
/// </para>
///
/// <para><b>Concurrency:</b> the spec suggests <c>Channel&lt;T&gt;</c> or
/// <c>BlockingCollection</c>. Both are optimised for the producer/
/// consumer streaming pattern (one-at-a-time <c>Take</c>), but the
/// <see cref="DrainAll"/> contract wants <em>bulk</em> removal of all
/// pending items in one call, which Channel doesn't support cleanly
/// (you'd have to loop <c>TryRead</c> until empty). A plain
/// <c>List&lt;T&gt;</c> guarded by a <c>lock</c> is simpler, correct,
/// and the right tool for this access pattern. Documented as a small
/// deviation in the worklog.</para>
///
/// <para><b>Events:</b> the queue raises <see cref="Enqueued"/> and
/// <see cref="Cancelled"/> for the HostSession to react (e.g. wake up
/// the GM turn loop). Subscribers are invoked under the queue's lock
/// (so the queue is consistent at event time), but exceptions in
/// subscribers are caught + traced (one bad subscriber can't break the
/// queue).</para>
/// </summary>
public sealed class ActionQueue
{
    private readonly List<PlayerAction> _pending = new();
    private readonly object _lock = new();

    /// <summary>
    /// Raised after an action is enqueued. The handler runs under the
    /// queue's lock (so <see cref="Count"/> is consistent at event
    /// time) but exception-safe (a thrown handler is caught + traced).
    /// </summary>
    public event Action<PlayerAction>? Enqueued;

    /// <summary>
    /// Raised after an action is cancelled. Passes the action id that
    /// was cancelled (or null if the id wasn't found). Same lock +
    /// exception-safety contract as <see cref="Enqueued"/>.
    /// </summary>
    public event Action<string?>? Cancelled;

    /// <summary>
    /// Add an action to the queue. Returns the same <see cref="PlayerAction"/>
    /// (the caller already constructed it; this method doesn't mutate it).
    /// </summary>
    public PlayerAction Enqueue(PlayerAction action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));

        lock (_lock)
        {
            _pending.Add(action);
            Raise(Enqueued, action);
        }
        return action;
    }

    /// <summary>
    /// Cancel (remove) an action by id. Returns true if the action was
    /// found and removed; false if it wasn't in the queue (already
    /// drained, already cancelled, or never existed).
    /// </summary>
    public bool Cancel(string actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return false;

        lock (_lock)
        {
            var idx = _pending.FindIndex(a => a.Id == actionId);
            if (idx < 0)
            {
                Raise(Cancelled, null);
                return false;
            }
            _pending.RemoveAt(idx);
            Raise(Cancelled, actionId);
            return true;
        }
    }

    /// <summary>
    /// Remove and return ALL pending actions. The returned list is a
    /// snapshot — mutating it has no effect on the queue (which is now
    /// empty). Returns an empty list if no actions are pending.
    /// </summary>
    public IReadOnlyList<PlayerAction> DrainAll()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return Array.Empty<PlayerAction>();
            var snapshot = _pending.ToArray();
            _pending.Clear();
            return snapshot;
        }
    }

    /// <summary>Peek at the current pending actions without removing
    /// them. Returns a snapshot — safe to enumerate outside the lock.</summary>
    public IReadOnlyList<PlayerAction> PeekAll()
    {
        lock (_lock)
        {
            return _pending.ToArray();
        }
    }

    /// <summary>Current number of pending actions.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _pending.Count;
        }
    }

    /// <summary>
    /// True if at least one pending action is in the queue. Slightly
    /// cheaper than <see cref="Count"/> > 0 because it doesn't need to
    /// take the lock just to check emptiness (the lock is still taken
    /// for the read, but the comparison is simpler).
    /// </summary>
    public bool HasPending => Count > 0;

    /// <summary>Clear the queue (e.g. on game end).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _pending.Clear();
        }
    }

    // ── Event helper ──────────────────────────────────────────────────

    /// <summary>
    /// Raise an event under the lock, catching any subscriber exception
    /// so a single bad handler can't corrupt the queue or break other
    /// subscribers. Mirrors the EventBus exception-safety contract.
    /// Works for both reference and value types, and for nullable
    /// annotations (the <c>T?</c> annotation is erased at runtime, so a
    /// single generic method covers all cases — including the
    /// <see cref="Cancelled"/> event which passes null when an unknown
    /// id is cancelled).
    /// </summary>
    private static void Raise<T>(Action<T>? handler, T args)
    {
        if (handler is null) return;
        // Snapshot the invocation list so a handler that unsubscribes
        // mid-dispatch doesn't cause CollectionModified.
        var delegates = handler.GetInvocationList();
        foreach (var d in delegates)
        {
            try
            {
                ((Action<T>)d)(args);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ActionQueue] subscriber threw: {ex}");
            }
        }
    }
}
