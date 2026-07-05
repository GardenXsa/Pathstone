using MyGame.Core.Common;

namespace MyGame.Core.Saves;

/// <summary>
/// Build status of a save's world — surfaces in the saves list so the UI
/// can show a "Resume build" button for interrupted builds and a "Play"
/// button for completed ones.
/// </summary>
public enum BuildStatus
{
    /// <summary>No world-builder has run yet (fresh save).</summary>
    None = 0,

    /// <summary>World-builder is mid-flight (some stages done, more pending).</summary>
    Building = 1,

    /// <summary>World-builder completed; the save is fully playable.</summary>
    Done = 2,

    /// <summary>World-builder failed mid-flight (API error, etc.).</summary>
    Error = 3,
}

/// <summary>
/// Metadata for a single save. Persisted as <c>meta.json</c> in the
/// save directory. The saves-list UI reads ONLY this file per slot (no
/// need to deserialize the full World), so keep it small and
/// self-contained.
/// </summary>
/// <remarks>
/// Port-side consolidation of the TS <c>SaveMeta</c> shape from
/// <c>engine/types/index.ts</c>. The TS source had ~15 fields; this port
/// keeps the spec-required subset (id, name, ownerId, partyId,
/// characterName, worldTitle, timestamps, buildStatus, engineVersion)
/// plus a few extras the UI needs (turn, playtimeMs, characterLevel,
/// locationName, storageVersion). Skipped: <c>worldId</c>/<c>characterId</c>
/// (the split-storage indirection isn't needed in the desktop port —
/// each save is one self-contained directory).
/// </remarks>
public sealed record SaveMeta
{
    /// <summary>
    /// Stable save id (e.g. <c>save_3f2a1b8c9d0e4f5a8b7c6d5e4f3a2b1c</c>).
    /// Used as the on-disk directory name and as the wire-protocol id
    /// when telling the host which save to load.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name (player-editable via the saves list).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Profile id of the save's owner. Lets a future "share save" feature
    /// attribute the save to its creator even after import.
    /// </summary>
    public Guid OwnerId { get; init; }

    /// <summary>
    /// Multiplayer party id this save is bound to, if any. Null for a
    /// single-player save. When non-null, only that party's host can
    /// load this save (prevents a second party from forking it).
    /// </summary>
    public Guid? PartyId { get; init; }

    /// <summary>Character name snapshot for the saves-list preview.</summary>
    public string? CharacterName { get; init; }

    /// <summary>Character level snapshot (saves-list preview).</summary>
    public int? CharacterLevel { get; init; }

    /// <summary>
    /// Current location name snapshot for the saves-list preview. Null
    /// if the player hasn't been spawned yet (mid world-build).
    /// </summary>
    public string? LocationName { get; init; }

    /// <summary>
    /// Title of the generated world (from the world-builder's plan).
    /// Distinct from <see cref="Name"/> (which is the save slot name the
    /// player chose); <see cref="WorldTitle"/> is the in-fiction world
    /// name (e.g. «Долина Туманов»).
    /// </summary>
    public string? WorldTitle { get; init; }

    /// <summary>UTC timestamp when the save was first created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp of the last <c>SaveAll</c> write.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Cumulative playtime in ms across all sessions on this save.</summary>
    public long PlaytimeMs { get; init; }

    /// <summary>Engine turn counter snapshot.</summary>
    public int Turn { get; init; }

    /// <summary>World-builder build status (drives the "Resume" vs "Play" button).</summary>
    public BuildStatus BuildStatus { get; init; } = BuildStatus.None;

    /// <summary>
    /// Engine version that wrote this save. Set to
    /// <see cref="Common.Version.Current"/> on every write. Used by the
    /// load path to decide whether the save is loadable by the current
    /// engine (see <see cref="Common.Version.IsCompatible"/>).
    /// </summary>
    public string EngineVersion { get; init; } = Common.Version.Current;

    /// <summary>
    /// Save-file layout version. Bumped when the on-disk file layout
    /// changes in a way that requires a migration on load. Currently 2
    /// (v2 = item-instance Weight field backfill — older saves had
    /// <c>Item.Weight = 0</c> default; the migrator backfills from the
    /// item's template on load). NOT compatible with the TS app's
    /// <c>SPLIT_STORAGE_VERSION = 2</c> saves, which use a 3-directory
    /// layout that this port deliberately collapses.
    ///
    /// <para>
    /// <b>Settable</b> (not init-only) so the
    /// <see cref="SaveMigrator"/> can bump the version on an in-memory
    /// meta after running migrations. (Init-only would force a
    /// <c>with</c>-expression clone, which works but is wasteful for a
    /// single-field update.)
    /// </para>
    /// </summary>
    public int StorageVersion { get; set; } = SaveMigrator.CurrentStorageVersion;

    /// <summary>
    /// Cumulative prompt (input) tokens billed across all sessions on
    /// this save. Persists across save/load so the token-billing widget
    /// in the top bar can show a meaningful "Сессия: Nk токенов" total
    /// that survives a reload. Defaults to 0 (older saves load with 0).
    /// </summary>
    public int SessionPromptTokens { get; init; }

    /// <summary>
    /// Cumulative completion (output) tokens billed across all sessions
    /// on this save. See <see cref="SessionPromptTokens"/>.
    /// </summary>
    public int SessionCompletionTokens { get; init; }

    /// <summary>
    /// Text summary of the older (already-summarised) portion of the
    /// GameMaster's conversation history (issue #25). Null when no
    /// summarization has happened yet on this save. Persisted across
    /// save/load so the GM doesn't lose context: on save, the host copies
    /// <c>GameMaster.HistorySummary</c> here; on load, the host restores
    /// it by assigning back.
    ///
    /// <para>
    /// The summary is a short (~300 words) Russian-language recap of the
    /// oldest half of the conversation, produced by the GM's
    /// summarization pass. It's prepended to the working message list as
    /// a user message (<c>## Предыдущие события (изложение)</c>) so the
    /// model sees the compressed history before the recent turns.
    /// </para>
    ///
    /// <para>
    /// <b>Backward compatibility:</b> old saves (written before issue #25)
    /// don't have this field — they load with <c>null</c>, which the GM
    /// treats as "no summary yet" (it'll start fresh summarization when
    /// the history grows). No migration needed.
    /// </para>
    /// </summary>
    public string? HistorySummary { get; init; }

    /// <summary>
    /// Original <see cref="WorldPlan"/> JSON produced by the planner
    /// during the world-build (issue #20 — chunked generation). When the
    /// build ran in <c>chunked</c> mode, only the start region's
    /// locations were committed; other regions are marked
    /// <c>GenStatus="cold"</c> in the plan and are generated on-demand
    /// when the player travels toward them. This field holds the full
    /// plan JSON so <see cref="WorldBuilderOrchestrator.GenerateRegionAsync"/>
    /// can reload it + ask the AI to fill in a specific region without
    /// re-running the planner from scratch.
    /// </summary>
    /// <remarks>
    /// <b>Backward compatibility:</b> old saves (written before issue #20)
    /// don't have this field — they load with <c>null</c>, which the
    /// travel handler treats as "no chunked regions to generate" (the
    /// world was built full in the first pass). No migration needed.
    /// </remarks>
    public string? OriginalPlanJson { get; init; }
}
