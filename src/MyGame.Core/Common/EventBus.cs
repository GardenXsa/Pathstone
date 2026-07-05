using System.Collections.Concurrent;
using System.Diagnostics;

namespace MyGame.Core.Common;

/// <summary>
/// Tiny typed in-process pub/sub bus.
///
/// Port of <c>engine/core/eventBus.ts</c>. The TS version kept a single set
/// of <c>EngineEventListener</c>s and dispatched every event through them;
/// the C# rewrite is generic — <see cref="Subscribe{T}"/> registers a handler
/// for a specific event type and <see cref="Publish{T}"/> only invokes
/// handlers registered for that exact type. This gives compile-time typing
/// and avoids the runtime type-switching the TS engine had to do.
///
/// Thread safety: uses a <see cref="ConcurrentDictionary{TKey,TValue}"/> to
/// map event type → handler list, and takes a brief lock on the per-type list
/// during subscribe/unsubscribe and during the snapshot phase of publish.
/// Handlers themselves run outside the lock so a handler that subscribes more
/// handlers cannot deadlock the bus.
///
/// Listeners must never break the engine: any exception thrown by a handler
/// is swallowed and logged via <see cref="Trace"/>, mirroring the TS
/// <c>try/catch</c> around each listener invocation.
/// </summary>
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Type, object> _handlersByType = new();

    /// <summary>
    /// Register a handler for events of type <typeparamref name="T"/>.
    /// Returns an <see cref="IDisposable"/> token; disposing it unsubscribes
    /// the handler. Safe to dispose multiple times.
    /// </summary>
    public IDisposable Subscribe<T>(Action<T> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var list = (List<Action<T>>)_handlersByType.GetOrAdd(
            typeof(T),
            _ => new List<Action<T>>());

        lock (list)
        {
            list.Add(handler);
        }

        return new SubscriptionToken<T>(list, handler);
    }

    /// <summary>
    /// Publish an event to all registered handlers of type
    /// <typeparamref name="T"/>. Exceptions in handlers are caught and
    /// traced; they do not stop dispatch to subsequent handlers.
    /// </summary>
    public void Publish<T>(T evt)
    {
        if (!_handlersByType.TryGetValue(typeof(T), out var raw)) return;
        var list = (List<Action<T>>)raw;

        Action<T>[] snapshot;
        lock (list)
        {
            snapshot = list.Count == 0 ? Array.Empty<Action<T>>() : list.ToArray();
        }

        foreach (var h in snapshot)
        {
            try
            {
                h(evt);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EventBus] listener threw: {ex}");
            }
        }
    }

    /// <summary>Remove all handlers for all event types.</summary>
    public void Clear() => _handlersByType.Clear();

    /// <summary>
    /// Unsubscribe token returned by <see cref="Subscribe{T}"/>. Holds a
    /// reference to its handler list so it doesn't need a back-pointer to the
    /// bus. Disposing twice is a no-op.
    /// </summary>
    private sealed class SubscriptionToken<T> : IDisposable
    {
        private readonly List<Action<T>> _list;
        private Action<T>? _handler; // nulled out on Dispose for idempotency

        public SubscriptionToken(List<Action<T>> list, Action<T> handler)
        {
            _list = list;
            _handler = handler;
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h is null) return;
            lock (_list)
            {
                _list.Remove(h);
            }
        }
    }
}
