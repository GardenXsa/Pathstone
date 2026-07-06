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

    /// <summary>
    /// Estimated-token threshold above which the GameMaster triggers
    /// early summarization of the conversation history (issue #25). When
    /// the rough token count of (system prompt + summary + history + world
    /// state) exceeds 80% of this value, the oldest half of the history
    /// is sent to the summarizer and replaced with a short text recap.
    ///
    /// <para>
    /// Default 12000 — conservative for the typical 16k-context model
    /// class (gpt-4o-mini, deepseek-chat, llama3.1:8b). Larger-context
    /// models (32k, 128k) can safely set this higher; smaller-context
    /// models (8k) should set it lower. The 80% margin leaves room for
    /// the model's response + tool-call rounds before hitting the
    /// provider's hard limit.
    /// </para>
    /// </summary>
    public int MaxContextTokens { get; init; } = 12000;

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

    // ─── Appearance / theme (issue #47) ──────────────────────────────

    /// <summary>
    /// UI theme mode. One of <c>"Dark"</c> (default, matches the original
    /// Pathstone look), <c>"Light"</c>, or <c>"System"</c> (follows the OS
    /// preference at startup; we don't react to OS theme changes mid-session).
    /// Applied at app startup by <c>ThemeService.ApplyTheme</c> and on
    /// every SettingsStore.Changed event.
    /// </summary>
    public string ThemeMode { get; init; } = "Dark";

    /// <summary>
    /// Accent color preset name. One of <c>"Indigo"</c> (default),
    /// <c>"Emerald"</c>, <c>"Amber"</c>, <c>"Rose"</c>, <c>"Cyan"</c>,
    /// <c>"Violet"</c>. ThemeService resolves the name to a hex color and
    /// writes it into the AppAccent / AppAccentFg application resources.
    /// </summary>
    public string AccentColor { get; init; } = "Amber";

    // ─── Multiplayer (issue #78) ─────────────────────────────────────

    /// <summary>
    /// Whether the multiplayer client should auto-reconnect on an
    /// unexpected WebSocket drop (vs. surfacing the disconnect overlay
    /// and waiting for the user to click «Переподключиться»). Default
    /// false — explicit reconnect keeps the user in control.
    /// </summary>
    public bool AutoReconnect { get; init; } = false;

    // ─── Onboarding / tutorial (issue #73) ────────────────────────────

    /// <summary>
    /// Number of times the user has visited the main menu. Used to
    /// decide whether to show the first-time tooltip hints under the
    /// menu buttons (Новая игра / Создать мир / Хост игры). The hints
    /// are shown for the first three sessions (<c>SessionCount &lt; 3</c>)
    /// and hidden afterwards. Defaults to 0 — old settings.json files
    /// (written before this field existed) load with 0, which correctly
    /// triggers the hints for users who haven't seen them yet.
    /// </summary>
    /// <remarks>
    /// Incremented once per main-menu visit by
    /// <see cref="MyGame.Desktop.ViewModels.MainMenuViewModel"/> on
    /// construction. Never reset (a future "show hints again" affordance
    /// could reset it to 0).
    /// </remarks>
    public int SessionCount { get; init; } = 0;

    // ─── Saves (issue #80) ───────────────────────────────────────────

    /// <summary>
    /// Whether <see cref="MyGame.Core.Saves.SaveManager.SaveAll"/>
    /// gzip-compresses <c>world.json</c> / <c>log.json</c> /
    /// <c>state.json</c> on write (issue #80). <c>meta.json</c> stays
    /// uncompressed (it's small and ListSaves reads it on every
    /// save-list refresh). Default <c>true</c> — text-heavy world
    /// saves typically shrink 5-10× with gzip. Set to <c>false</c> for
    /// debugging (the save files become human-readable JSON).
    ///
    /// <para>
    /// <b>Backward compatibility:</b> reads always auto-detect
    /// compression (the SaveManager checks for <c>{file}.gz</c> first,
    /// then falls back to <c>{file}</c>), so a save written compressed
    /// loads fine when this is false, and vice versa. Toggling this
    /// between sessions is safe — the next <c>SaveAll</c> writes the
    /// new variant and deletes the stale one.
    /// </para>
    /// <para>
    /// The Desktop layer's <c>ServiceHost</c> reads this on startup and
    /// pushes it to the <c>SaveManager</c>'s
    /// <see cref="MyGame.Core.Saves.SaveManager.CompressSaves"/>
    /// property (which defaults to <c>false</c> so unit tests using a
    /// bare <c>SaveManager</c> keep writing plain JSON). A
    /// <c>SettingsStore.Changed</c> subscription keeps the property in
    /// sync if the user toggles the setting mid-session.
    /// </para>
    /// </summary>
    public bool CompressSaves { get; init; } = true;
}
