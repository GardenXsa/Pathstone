namespace MyGame.Core.Multiplayer.Protocol;

// ─────────────────────────────────────────────────────────────────────────
//  Handshake
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Client → Host. First message after the WebSocket upgrade. Carries the
/// client's protocol version + local profile identity. The host validates
/// the protocol version (must be compatible per
/// <see cref="MyGame.Core.Common.Version.IsCompatible"/>) and nickname
/// uniqueness, then either replies with <see cref="WelcomeMsg"/> (success)
/// or <see cref="RejectMsg"/> + closes the connection.
/// </summary>
public sealed record HelloMsg : NetMessage
{
    /// <summary>Protocol version the client speaks (compared to
    /// <see cref="MyGame.Core.Common.Version.Current"/>).</summary>
    public required string ProtocolVersion { get; init; }

    /// <summary>Display nickname from the local Profile.</summary>
    public required string Nickname { get; init; }

    /// <summary>Stable profile id from the local Profile.</summary>
    public required Guid ProfileId { get; init; }
}

/// <summary>
/// Host → Client. Successful handshake reply. Carries the server-assigned
/// connection id, the current party snapshot, and the role the client
/// has been assigned (Host is never assigned here — the host doesn't
/// send HelloMsg to itself; only Player/Spectator are sent).
/// </summary>
public sealed record WelcomeMsg : NetMessage
{
    /// <summary>Server-assigned connection id (use this for fromId in
    /// subsequent messages).</summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>Full party state at the moment of join.</summary>
    public required PartySnapshot Party { get; init; }

    /// <summary>Role assigned to the joining client.</summary>
    public required MemberRole Role { get; init; }
}

/// <summary>
/// Host → Client. Handshake rejection. Sent when the protocol version is
/// incompatible, the nickname is invalid/taken, or the party is full.
/// The host closes the connection immediately after sending.
/// </summary>
public sealed record RejectMsg : NetMessage
{
    /// <summary>Human-readable rejection reason (RU, matches the
    /// Profile.ValidateNickname error strings).</summary>
    public required string Reason { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────
//  Lobby
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Host → All. A new member has completed the handshake and joined the
/// party. The new member also receives this (after their WelcomeMsg) so
/// they see themselves in the roster.
/// </summary>
public sealed record MemberJoinedMsg : NetMessage
{
    /// <summary>The member that just joined.</summary>
    public required MemberInfo Member { get; init; }
}

/// <summary>
/// Host → All. A member has disconnected (graceful close or network
/// drop). The host keeps the member in the roster briefly with status
/// Disconnected before removing them; this message signals the
/// disconnection event.
/// </summary>
public sealed record MemberLeftMsg : NetMessage
{
    /// <summary>Connection id of the member that left.</summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>Human-readable reason ("disconnected", "kicked", "timeout").</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Client → Host (then Host → All). A member toggles their ready state
/// in the lobby. The host validates the transition (Pending ⇄ Ready)
/// and re-broadcasts to all clients.
/// </summary>
public sealed record MemberReadyMsg : NetMessage
{
    /// <summary>Connection id of the member whose ready state changed.</summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>New ready state.</summary>
    public required bool Ready { get; init; }
}

/// <summary>
/// Lobby chat. Client → Host → All (symmetric). One-to-many chat for
/// the lobby/character-creation phases. Host relays verbatim after
/// sanitising the text (trim + 1000-char cap, matching the TS source).
/// </summary>
public sealed record ChatMsg : NetMessage
{
    /// <summary>Connection id of the sender (host rewrites this from the
    /// socket identity — clients cannot spoof another member's id).</summary>
    public required Guid FromId { get; init; }

    /// <summary>Sender's nickname (denormalised for client convenience).</summary>
    public required string FromNickname { get; init; }

    /// <summary>Chat text (already trimmed + capped at 1000 chars by the host).</summary>
    public required string Text { get; init; }

    /// <summary>UTC moment the host received the message.</summary>
    public required DateTimeOffset Ts { get; init; }
}

/// <summary>
/// Host → All. Party lifecycle status changed (Lobby → Worldbuilding →
/// CharacterCreation → Playing). Clients use this to switch UI screens.
/// </summary>
public sealed record StatusChangedMsg : NetMessage
{
    /// <summary>New party status.</summary>
    public required PartyStatus Status { get; init; }

    /// <summary>Current turn (relevant when transitioning into Playing).</summary>
    public int Turn { get; init; }

    /// <summary>Save id (relevant when transitioning into Playing).</summary>
    public string? SaveId { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────
//  Game — actions
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Player action queued for GM processing. Client → Host → All
/// (symmetric). The client generates the <see cref="ActionId"/> locally
/// (so they can track/cancel it before the host broadcasts it back); the
/// host validates, enqueues in the action queue, and re-broadcasts to
/// all clients so everyone's UI shows the new pending action.
/// </summary>
public sealed record ActionQueuedMsg : NetMessage
{
    /// <summary>Client-generated unique id for this action.</summary>
    public required string ActionId { get; init; }

    /// <summary>Connection id of the submitting player (host rewrites
    /// from socket identity — clients cannot spoof).</summary>
    public required Guid FromId { get; init; }

    /// <summary>Submitter's nickname (denormalised).</summary>
    public required string FromNickname { get; init; }

    /// <summary>Player's free-text action (trimmed, capped at 4000 chars).</summary>
    public required string Text { get; init; }

    /// <summary>UTC moment the action was submitted.</summary>
    public required DateTimeOffset Ts { get; init; }
}

/// <summary>
/// Client → Host. Cancel a previously-queued action. The host removes
/// it from the action queue (if it hasn't been picked up by the GM yet)
/// and re-broadcasts an <see cref="ActionCancelledMsg"/> to all clients.
/// </summary>
public sealed record ActionCancelMsg : NetMessage
{
    /// <summary>The action id to cancel.</summary>
    public required string ActionId { get; init; }
}

/// <summary>
/// Host → All. An action has been cancelled (either by the original
/// submitter or by the host). Clients remove it from their pending list.
/// </summary>
public sealed record ActionCancelledMsg : NetMessage
{
    /// <summary>The action id that was cancelled.</summary>
    public required string ActionId { get; init; }
}

/// <summary>
/// Host → All. The GM has picked up a batch of actions for resolution.
/// Clients mark these actions as "resolving" in the UI (greyed out,
/// non-cancellable). Sent before the first <see cref="NarrativeDeltaMsg"/>
/// for the turn.
/// </summary>
public sealed record ActionResolvingMsg : NetMessage
{
    /// <summary>Ids of the actions being resolved in this batch.</summary>
    public required IReadOnlyList<string> ActionIds { get; init; } = Array.Empty<string>();
}

// ─────────────────────────────────────────────────────────────────────────
//  Game — narrative + state
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Host → All. Streaming narrative text delta. Clients append to the
/// current narration buffer. Sent incrementally as the GM streams tokens
/// (in the current implementation the GM is non-streaming, so the
/// HostSession sends one large delta with the full narration followed by
/// a <see cref="NarrativeFinalMsg"/>; the streaming wire format is in
/// place so a future streaming GM can drop in without protocol changes).
/// </summary>
public sealed record NarrativeDeltaMsg : NetMessage
{
    /// <summary>Text chunk to append to the current narration.</summary>
    public required string TextDelta { get; init; }

    /// <summary>
    /// Turn number this narration belongs to. Lets clients correlate
    /// deltas with the eventual <see cref="TurnEndMsg"/>.
    /// </summary>
    public int Turn { get; init; }
}

/// <summary>
/// Host → All. Final narration for the turn — full text + the tool calls
/// the GM made. Clients replace their accumulated delta buffer with this
/// canonical full text (in case any deltas were lost or arrived out of
/// order) and render the tool-call feed.
/// </summary>
public sealed record NarrativeFinalMsg : NetMessage
{
    /// <summary>Full narration text for the turn.</summary>
    public required string FullText { get; init; }

    /// <summary>Tool calls the GM made this turn (may be empty).</summary>
    public IReadOnlyList<ToolEvent> ToolEvents { get; init; } = Array.Empty<ToolEvent>();

    /// <summary>Turn number this narration finalises.</summary>
    public int Turn { get; init; }
}

/// <summary>
/// Host → All. World state update — either a full snapshot (the entire
/// World JSON) or, in a future revision, a diff. Clients replace their
/// local World cache with this. Sent after each GM turn so all clients
/// see the new entity state (HP, inventory, NPC positions, …).
/// </summary>
public sealed record StateUpdateMsg : NetMessage
{
    /// <summary>
    /// World state as a JSON string (World.ToJson()). Sent as a string
    /// (not a nested object) so the wire format stays simple and the
    /// client can either deserialize into a World or stash the raw JSON
    /// for later.
    /// </summary>
    public required string WorldJson { get; init; }

    /// <summary>Current turn number after the update.</summary>
    public int Turn { get; init; }

    /// <summary>
    /// True when <see cref="WorldJson"/> is a full World snapshot;
    /// false when it's a diff (future). Currently always true.
    /// </summary>
    public bool Full { get; init; } = true;
}

/// <summary>
/// Host → All. The current GM turn has ended. Clients stop appending to
/// the narration buffer and re-enable the action input.
/// </summary>
public sealed record TurnEndMsg : NetMessage
{
    /// <summary>The turn number that just ended.</summary>
    public required int TurnNumber { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────
//  System
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Host → All (or Host → One). Generic error message — bad input, server
/// fault, etc. The host doesn't close the connection on Error; the client
/// can decide whether to recover or disconnect.
/// </summary>
public sealed record ErrorMsg : NetMessage
{
    /// <summary>Human-readable error message (RU).</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Host → Client. The client has been kicked (e.g. host closed the
/// party, or the client was misbehaving). The host closes the connection
/// immediately after sending.
/// </summary>
public sealed record KickedMsg : NetMessage
{
    /// <summary>Human-readable kick reason.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Bidirectional. Keepalive ping. Either side may send; the other side
/// replies with <see cref="PongMsg"/> with the same <see cref="Ts"/>.
/// </summary>
public sealed record PingMsg : NetMessage
{
    /// <summary>UTC moment the ping was sent.</summary>
    public required DateTimeOffset Ts { get; init; }
}

/// <summary>
/// Bidirectional. Keepalive pong — echoes the ping's <see cref="Ts"/>.
/// </summary>
public sealed record PongMsg : NetMessage
{
    /// <summary>UTC moment of the original ping.</summary>
    public required DateTimeOffset Ts { get; init; }
}
