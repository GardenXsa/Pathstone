using System.Text.Json.Serialization;

namespace MyGame.Core.Multiplayer.Protocol;

// ─────────────────────────────────────────────────────────────────────────
//  NetMessage — polymorphic base
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Polymorphic base type for every message exchanged over the WebSocket
/// wire. Uses <c>System.Text.Json</c>'s .NET 7+ polymorphism support:
/// <c>[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]</c> writes
/// a <c>"kind"</c> field on every message; <c>[JsonDerivedType]</c> maps
/// each derived record to a discriminator string.
///
/// <para>
/// On the wire, a <see cref="HelloMsg"/> serialises as:
/// <code>
/// { "kind": "hello", "protocolVersion": "0.2.0", "nickname": "Бродяга", "profileId": "..." }
/// </code>
/// </para>
///
/// <para>
/// Deserialize with <c>JsonSerializer.Deserialize&lt;NetMessage&gt;(json,
/// MultiplayerJson.Options)</c> — the returned reference points to the
/// actual derived type. Serialize with
/// <c>JsonSerializer.Serialize&lt;NetMessage&gt;(msg, MultiplayerJson.Options)</c>
/// (the explicit <c>&lt;NetMessage&gt;</c> type parameter is REQUIRED —
/// serialising as the derived type would skip polymorphism and not write
/// the discriminator).
/// </para>
///
/// <para>
/// Wire convention (from the worklog): camelCase property names, raw
/// UTF-8 (no \uXXXX escapes for Cyrillic), null fields omitted. The
/// shared options live in <see cref="MultiplayerJson"/>.
/// </para>
///
/// <para><b>Discriminator strings</b> use snake_case to match the TS
/// source's event names (<c>lobby:member_joined</c>, <c>game:action_queued</c>,
/// …) — the C# port drops the namespace prefix and keeps the snake_case
/// suffix. This makes the wire format recognisable to anyone familiar with
/// the TS protocol.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", IgnoreUnrecognizedTypeDiscriminators = true)]
[JsonDerivedType(typeof(HelloMsg),            "hello")]
[JsonDerivedType(typeof(WelcomeMsg),          "welcome")]
[JsonDerivedType(typeof(RejectMsg),           "reject")]
// Lobby
[JsonDerivedType(typeof(MemberJoinedMsg),     "member_joined")]
[JsonDerivedType(typeof(MemberLeftMsg),       "member_left")]
[JsonDerivedType(typeof(MemberReadyMsg),      "member_ready")]
[JsonDerivedType(typeof(ChatMsg),             "chat")]
[JsonDerivedType(typeof(StatusChangedMsg),    "status_changed")]
// Game
[JsonDerivedType(typeof(ActionQueuedMsg),     "action_queued")]
[JsonDerivedType(typeof(ActionCancelMsg),     "action_cancel")]
[JsonDerivedType(typeof(ActionCancelledMsg),  "action_cancelled")]
[JsonDerivedType(typeof(ActionResolvingMsg),  "action_resolving")]
[JsonDerivedType(typeof(NarrativeDeltaMsg),   "narrative_delta")]
[JsonDerivedType(typeof(NarrativeFinalMsg),   "narrative_final")]
[JsonDerivedType(typeof(StateUpdateMsg),      "state_update")]
[JsonDerivedType(typeof(TurnEndMsg),          "turn_end")]
// System
[JsonDerivedType(typeof(ErrorMsg),            "error")]
[JsonDerivedType(typeof(KickedMsg),           "kicked")]
[JsonDerivedType(typeof(PingMsg),             "ping")]
[JsonDerivedType(typeof(PongMsg),             "pong")]
public abstract record NetMessage;

// ─────────────────────────────────────────────────────────────────────────
//  Helper records embedded in messages
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Snapshot of the party state, sent to a client in the
/// <see cref="WelcomeMsg"/> right after the handshake completes. Carries
/// the host's connection id (so clients know who the host is), the
/// current roster, the party lifecycle status, the save id (if the
/// party is in/after worldbuilding), and the current turn number.
/// </summary>
public sealed record PartySnapshot
{
    /// <summary>Connection id of the host (the player who started the server).</summary>
    public required Guid HostConnectionId { get; init; }

    /// <summary>Current roster (host + every connected client).</summary>
    public IReadOnlyList<MemberInfo> Members { get; init; } = Array.Empty<MemberInfo>();

    /// <summary>Party lifecycle status.</summary>
    public PartyStatus Status { get; init; } = PartyStatus.Lobby;

    /// <summary>
    /// Save id the party is playing (or null if still in the lobby
    /// before worldbuilding). Sent so joining clients can request the
    /// save's world JSON via <see cref="StateUpdateMsg"/>.
    /// </summary>
    public string? SaveId { get; init; }

    /// <summary>Current turn number (0 before the game starts).</summary>
    public int Turn { get; init; }
}

/// <summary>
/// One tool call executed by the GameMaster during a turn. Sent in the
/// <see cref="NarrativeFinalMsg"/> so clients can render a "tool call
/// feed" alongside the narration. Mirrors the
/// <see cref="MyGame.Core.AI.Agents.AppliedToolCall"/> port-side record.
/// </summary>
public sealed record ToolEvent
{
    /// <summary>Tool name (matches the registered tool definition).</summary>
    public required string Name { get; init; }

    /// <summary>Raw JSON-string arguments the model produced.</summary>
    public required string ArgsJson { get; init; }

    /// <summary>Human-readable tool result text.</summary>
    public required string Result { get; init; }

    /// <summary>Whether the tool call errored.</summary>
    public bool IsError { get; init; }
}
