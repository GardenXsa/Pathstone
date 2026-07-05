namespace MyGame.Core.Multiplayer;

// ─────────────────────────────────────────────────────────────────────────
//  Enums
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Role a member plays in a multiplayer party. The host owns the simulation
/// (GameMaster runs in their process); players submit actions and receive
/// narrative/state; spectators receive everything but cannot submit actions.
/// </summary>
public enum MemberRole
{
    /// <summary>
    /// The desktop app that started the in-process WebSocket server.
    /// Owns the GameMaster + World + SaveManager. There is exactly one
    /// Host per party.
    /// </summary>
    Host = 0,

    /// <summary>
    /// A regular participant — submits actions, receives narrative.
    /// </summary>
    Player = 1,

    /// <summary>
    /// Read-only participant — receives narrative + state updates but
    /// cannot enqueue actions. Useful for streaming / observing.
    /// </summary>
    Spectator = 2,
}

/// <summary>
/// Lifecycle status of a single member. Mirrors the lobby flow:
/// Pending → Ready → Playing, with Disconnected as a terminal state.
/// </summary>
public enum MemberStatus
{
    /// <summary>Just joined, hasn't signalled ready yet.</summary>
    Pending = 0,

    /// <summary>Ready in the lobby — waiting for the host to start the game.</summary>
    Ready = 1,

    /// <summary>Actively playing — game has started and the member has a character.</summary>
    Playing = 2,

    /// <summary>Connection lost or gracefully closed. Member is kept in the
    /// roster briefly so the UI can show "X disconnected" before removing.</summary>
    Disconnected = 3,
}

/// <summary>
/// Lifecycle status of the whole party. Drives which UI screen is shown.
/// Mirrors the TS app's party.status field.
/// </summary>
public enum PartyStatus
{
    /// <summary>In the lobby — waiting for players to join and ready up.</summary>
    Lobby = 0,

    /// <summary>Host is generating a new world via the WorldBuilder orchestrator.</summary>
    Worldbuilding = 1,

    /// <summary>Players are creating their characters.</summary>
    CharacterCreation = 2,

    /// <summary>Game is in progress — GM is processing turns.</summary>
    Playing = 3,
}

// ─────────────────────────────────────────────────────────────────────────
//  MemberInfo
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// One member of a multiplayer party. Mirrors the TS app's
/// <c>party.members[]</c> shape, minus the auth/JWT fields (no auth in the
/// desktop rewrite — the local Profile is the identity).
///
/// <para>
/// A <see cref="MemberInfo"/> is created by the HostServer when a client
/// completes the HelloMsg → WelcomeMsg handshake. The host assigns
/// <see cref="ConnectionId"/> (a fresh Guid per connection); the client
/// supplies <see cref="ProfileId"/> + <see cref="Nickname"/> via the
/// HelloMsg (copied from their local <c>Profile</c>). The host validates
/// nickname uniqueness before assigning the connection id.
/// </para>
///
/// <para>
/// Record (immutable) — to update a member's status (e.g. Pending → Ready),
/// the HostServer constructs a new <see cref="MemberInfo"/> with the updated
/// field and replaces the entry in its members dictionary. The previous
/// instance is never mutated.
/// </para>
/// </summary>
public sealed record MemberInfo
{
    /// <summary>
    /// Server-assigned per-connection id. A new Guid every time a client
    /// connects (even if they reconnect with the same ProfileId). Used as
    /// the routing key for <see cref="HostServer.SendAsync"/> and as the
    /// <c>fromId</c> on chat / action messages.
    /// </summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>
    /// Stable profile id from the local <c>Profile.Id</c>. Survives
    /// reconnects (the same player joining twice has the same ProfileId
    /// but different ConnectionIds).
    /// </summary>
    public required Guid ProfileId { get; init; }

    /// <summary>
    /// Display name from the local <c>Profile.Nickname</c>. The host
    /// validates uniqueness per party — duplicates are rejected in the
    /// handshake with a <see cref="Protocol.RejectMsg"/>.
    /// </summary>
    public required string Nickname { get; init; }

    /// <summary>Role in the party. The host is always <see cref="MemberRole.Host"/>.</summary>
    public MemberRole Role { get; init; } = MemberRole.Player;

    /// <summary>Current lifecycle status of this member.</summary>
    public MemberStatus Status { get; init; } = MemberStatus.Pending;

    /// <summary>UTC moment the member joined the party.</summary>
    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.UtcNow;
}
