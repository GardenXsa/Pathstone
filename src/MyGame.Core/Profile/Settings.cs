using MyGame.Core.AI;

namespace MyGame.Core.Profile;

/// <summary>
/// User-configurable application settings, persisted as
/// <c>settings.json</c> next to <c>profile.json</c> in the profile
/// directory. Combines the AI connection settings (the only thing the TS
/// source's <c>lib/settings.ts</c> persisted) with the desktop-specific
/// tunables the rewrite needs (last server host/port, autosave cadence,
/// UI preferences, etc.).
/// </summary>
/// <remarks>
/// <b>Why a single record:</b> the TS app split this into several stores
/// (AI settings in <c>lib/settings.ts</c>, server config in
/// <c>lib/server-config.ts</c>, session in <c>lib/session-persistence.ts</c>)
/// because each had a different persistence mechanism (localStorage /
/// env / API call). The desktop rewrite has ONE persistence mechanism
/// (a JSON file in AppData), so collapsing them into a single record is
/// simpler and atomic to write.
/// </remarks>
public sealed record Settings
{
    // ─── AI connection ─────────────────────────────────────────────────

    /// <summary>
    /// OpenAI-compatible endpoint settings (base URL, API key, model,
    /// temperature, max tokens). Drives every
    /// <see cref="AiClient.ChatAsync"/> call.
    /// </summary>
    public AiSettings Ai { get; init; } = new();

    /// <summary>
    /// Hard cap on tool-call iterations per GM turn. Stops a misbehaving
    /// model from looping spawn-NPC forever. Default 8 (matches the TS
    /// <c>maxIterations</c> default).
    /// </summary>
    public int MaxToolIterations { get; init; } = 8;

    // ─── Multiplayer ───────────────────────────────────────────────────

    /// <summary>
    /// Last server host the user connected to (for the "Recent servers"
    /// dropdown in the multiplayer menu). Null if the user has never
    /// joined a remote host.
    /// </summary>
    public string? LastServerHost { get; init; }

    /// <summary>
    /// Last server port. Null if the user has never joined a remote host.
    /// When hosting locally, this is the port the in-process WebSocket
    /// listener bound to last time.
    /// </summary>
    public int? LastServerPort { get; init; }

    // ─── Gameplay / engine tunables ────────────────────────────────────

    /// <summary>
    /// Autosave cadence in seconds. 0 = disabled (user must save
    /// manually). Default 120 (every 2 minutes) — matches the TS app's
    /// autosave behavior.
    /// </summary>
    public int AutosaveIntervalSeconds { get; init; } = 120;

    /// <summary>
    /// Whether narrative events stream into the UI as they're generated
    /// (true) or only after the GM turn completes (false). Streaming
    /// gives better UX but uses slightly more CPU.
    /// </summary>
    public bool StreamNarrative { get; init; } = true;

    /// <summary>
    /// UI language code (<c>"ru"</c>, <c>"en"</c>, …). Defaults to
    /// <c>"ru"</c> — the TS source is Russian-first.
    /// </summary>
    public string Language { get; init; } = "ru";

    // ─── Audio / accessibility ─────────────────────────────────────────

    /// <summary>Master volume 0..100. 0 = muted.</summary>
    public int MasterVolume { get; init; } = 80;

    /// <summary>Whether sound effects are enabled at all.</summary>
    public bool SoundEnabled { get; init; } = true;

    /// <summary>
    /// Whether the UI animates transitions. Disable for users on slow
    /// machines or with motion sensitivity.
    /// </summary>
    public bool EnableAnimations { get; init; } = true;
}
