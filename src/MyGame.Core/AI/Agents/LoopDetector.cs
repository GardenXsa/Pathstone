namespace MyGame.Core.AI.Agents;

/// <summary>
/// A nudge injected into the working conversation history when the
/// <see cref="LoopDetector"/> flags the GM as repeating itself. Null when
/// no nudge is needed (the call was clean / within bounds).
/// </summary>
public sealed record LoopNudge(string Text);

/// <summary>
/// Per-turn tool-call loop detector. Records the signature
/// (<c>(toolName, argsHash)</c>) of each tool call the GM makes inside one
/// <see cref="GameMaster.ProcessActionAsync"/> turn and flags when the GM
/// is stuck in a repetition pattern.
///
/// <para>
/// Two rules are checked on every <see cref="Record"/> call:
/// </para>
/// <list type="number">
///   <item><b>Direct repeat</b> — the same <c>(toolName, argsHash)</c>
///     appears 3+ times in the last 5 tool calls. The GM is hammering the
///     same tool with the same arguments.</item>
///   <item><b>Ping-pong</b> — the last 4 tool calls form an alternating
///     A, B, A, B pattern (two distinct tools, each appearing twice in
///     alternation).</item>
/// </list>
///
/// <para>
/// When a rule fires, a <see cref="LoopNudge"/> is returned with a
/// Russian-language system message reminding the GM to vary its approach.
/// At most <see cref="MaxNudges"/> nudges are emitted per turn — after
/// that the detector gives up and lets <see cref="GameMaster._maxIterations"/>
/// cap the loop. (The spec: "If it doesn't [self-correct] after 2 nudges,
/// let the iteration cap handle it.")
/// </para>
///
/// <para>
/// The args hash is a plain <see cref="string.GetHashCode()"/> of the raw
/// JSON args string — that's enough for in-memory equality detection
/// within one turn (we're not persisting or comparing across turns/processes).
/// </para>
///
/// <para>
/// This helper is NOT thread-safe — one instance is owned by a single
/// <see cref="GameMaster"/> and reset per turn.
/// </para>
/// </summary>
public sealed class LoopDetector
{
    /// <summary>
    /// Maximum nudges emitted per turn before we stop trying (the iteration
    /// cap then handles termination). Per spec: "If it doesn't [self-correct]
    /// after 2 nudges, let the iteration cap handle it."
    /// </summary>
    public const int MaxNudges = 2;

    // Sliding window of (toolName, argsHash) signatures, oldest-first.
    private readonly List<(string ToolName, int ArgsHash)> _history = new();
    private int _nudgeCount;

    /// <summary>
    /// Reset the detector for a new turn. Called by
    /// <see cref="GameMaster.ProcessActionAsync"/> at the start of each
    /// invocation so detection is per-turn only.
    /// </summary>
    public void Reset()
    {
        _history.Clear();
        _nudgeCount = 0;
    }

    /// <summary>
    /// Record one tool call and return a <see cref="LoopNudge"/> if a
    /// repetition pattern was detected. Returns null when the call was
    /// clean (no pattern detected, or the per-turn nudge cap has been
    /// reached and we're letting the iteration cap handle termination).
    /// </summary>
    /// <param name="toolName">Tool name (e.g. <c>"move_player"</c>).</param>
    /// <param name="argsJson">Raw JSON-args string from the model. Used
    /// only for its hash (so structural differences like key ordering
    /// count as different calls — same as the underlying model output).</param>
    public LoopNudge? Record(string toolName, string? argsJson)
    {
        if (string.IsNullOrEmpty(toolName)) return null;

        var argsHash = argsJson?.GetHashCode(StringComparison.Ordinal) ?? 0;
        _history.Add((toolName, argsHash));

        // Nudge cap reached — let the iteration cap handle termination.
        if (_nudgeCount >= MaxNudges) return null;

        // Rule 1: direct repeat — same (toolName, argsHash) 3+ times in
        // the last 5 calls.
        if (_history.Count >= 3)
        {
            var window = _history.Skip(Math.Max(0, _history.Count - 5)).ToList();
            var counts = window
                .GroupBy(x => x)
                .Select(g => (Sig: g.Key, Count: g.Count()))
                .ToList();
            var topRepeat = counts.MaxBy(c => c.Count);
            if (topRepeat.Count >= 3)
            {
                _nudgeCount++;
                return new LoopNudge(
                    $"[SYSTEM] Ты повторяешь одно и то же действие " +
                    $"({topRepeat.Sig.ToolName} с теми же аргументами). " +
                    $"Попробуй другой подход или заверши наррацию без инструментов.");
            }
        }

        // Rule 2: ping-pong — last 4 calls form A, B, A, B.
        if (_history.Count >= 4)
        {
            var n = _history.Count;
            var a = _history[n - 4];
            var b = _history[n - 3];
            var c = _history[n - 2];
            var d = _history[n - 1];
            if (a.Equals(c) && b.Equals(d) && !a.Equals(b))
            {
                _nudgeCount++;
                return new LoopNudge(
                    $"[SYSTEM] Ты чередуешь одни и те же два действия " +
                    $"({a.ToolName} и {b.ToolName}). " +
                    $"Попробуй другой подход или заверши наррацию без инструментов.");
            }
        }

        return null;
    }
}
