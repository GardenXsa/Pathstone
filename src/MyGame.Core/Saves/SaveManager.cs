using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MyGame.Core.Common;
using MyGame.Core.Profile;
using MyGame.Core.World;
using MyGame.Core.World.Content;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). Using-aliasing 'World' itself doesn't
// reliably win against the namespace import, so we alias to 'GameWorld'
// instead and use that name throughout this file.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Core.Saves;

/// <summary>
/// File-based save manager. Each save is a self-contained directory under
/// <see cref="SavesDirectory"/> holding four JSON files:
///
/// <code>
/// {SavesDirectory}/{saveId}/
///   meta.json         — SaveMeta (id, name, ownerId, partyId, characterName,
///                       worldTitle, createdAt, updatedAt, buildStatus,
///                       engineVersion, …) [always uncompressed]
///   world.json[.gz]   — full World state (World.ToJson() round-trip)
///   log.json[.gz]     — LogEntry[] (narrative / action / system / tool log)
///   state.json[.gz]   — SaveState (turn, activePlayerId, playtimeMs, …)
///   backups/{ts}/     — auto-rotating backups (issue #81)
///   manifest.json     — present on imported saves (issue #33), ignored by
///                       ListSaves / LoadAll
/// </code>
///
/// <para>
/// Port of <c>engine/saves/saveManager.ts</c>. The TS source used a
/// 3-directory split (<c>worlds/</c> + <c>characters/</c> + <c>runs/</c>)
/// to enable sharing generated worlds and characters between runs — a
/// feature the desktop rewrite defers to a later task. This port
/// collapses everything into ONE directory per save (the spec's
/// <c>{ProfileDirectory}/saves/{saveId}/</c> layout), which keeps load/
/// save code simple and atomic-folder-rename-friendly. A future task can
/// add the world/character sharing layer on top without breaking the
/// save schema (the layout just gains two more sibling directories).
/// </para>
/// <para>
/// <b>Compression (issue #80):</b> when <see cref="CompressSaves"/> is
/// true, <c>world.json</c> / <c>log.json</c> / <c>state.json</c> are
/// gzip-compressed on write (<c>{name}.json.gz</c>). <c>meta.json</c>
/// stays uncompressed (small, fast to read for <see cref="ListSaves"/>).
/// Reads auto-detect compression by checking for the <c>.gz</c> file
/// first — old uncompressed saves (<c>{name}.json</c>) still load
/// (backward compatibility).
/// </para>
/// <para>
/// <b>Backups (issue #81):</b> on every <see cref="SaveAll"/> call, the
/// current save directory is copied to
/// <c>backups/{timestamp}/</c> BEFORE the new files are written. The
/// last 5 backups per save are kept; older ones are auto-deleted
/// (the 30-day retention rule is also applied). Use
/// <see cref="ListBackups"/> + <see cref="RestoreBackup"/> to browse
/// and roll back.
/// </para>
/// <para>
/// <b>Sharing (issue #33):</b> <see cref="ExportSave"/> zips the save
/// directory into a <c>.pathstone-world</c> archive with a
/// <see cref="SaveManifest"/> sidecar. <see cref="ImportSave"/> unzips
/// it into a new save directory with a fresh <c>saveId</c> so importing
/// the same archive twice doesn't collide.
/// </para>
/// <para>
/// <b>Concurrency:</b> all writes are atomic (write to <c>{file}.tmp</c>
/// then <see cref="File.Move(string, string, bool)"/>). Reads tolerate
/// partial writes (return null on parse failure rather than throwing).
/// The class is thread-safe for independent saveIds; concurrent writes
/// to the SAME saveId will race (last writer wins on each file).
/// </para>
/// </summary>
public sealed class SaveManager
{
    /// <summary>
    /// On-disk save layout version. Bumped when the file layout changes
    /// in a way that requires a migration on load. Mirrors
    /// <see cref="SaveMigrator.CurrentStorageVersion"/> — keep the two
    /// in sync (the SaveMigrator is the single source of truth for
    /// what migrations exist; this const is the SaveManager's snapshot
    /// of "what version do NEW saves get?").
    /// </summary>
    public const int CurrentStorageVersion = SaveMigrator.CurrentStorageVersion;

    /// <summary>
    /// Maximum number of backup snapshots kept per save (issue #81).
    /// Older backups are auto-deleted on every <see cref="SaveAll"/>.
    /// The 30-day age limit (<see cref="BackupMaxAgeDays"/>) is applied
    /// in parallel — whichever rule is more restrictive wins.
    /// </summary>
    public const int MaxBackupsPerSave = 5;

    /// <summary>
    /// Maximum age (in days) of a backup before it's auto-deleted
    /// (issue #81). Backups older than this are pruned regardless of
    /// the <see cref="MaxBackupsPerSave"/> count; backups beyond the
    /// count are pruned regardless of age. Whichever rule is more
    /// restrictive wins (i.e. a backup is kept only if it's BOTH within
    /// the 5 most recent AND less than 30 days old).
    /// </summary>
    public const int BackupMaxAgeDays = 30;

    private readonly string _savesDirectory;
    private ContentRegistry? _registries;
    private readonly object _lock = new();

    /// <param name="savesDirectory">
    /// Optional override for <see cref="SavesDirectory"/>. Defaults to
    /// <c>{ProfileStore.DefaultProfileDirectory}/saves</c>.
    /// </param>
    /// <param name="registries">
    /// Optional <see cref="ContentRegistry"/> used to rehydrate loaded
    /// worlds (World needs the live template registry because templates
    /// aren't persisted in saves). If null, loads
    /// <see cref="ContentRegistry.LoadDefault"/> on first use.
    /// </param>
    public SaveManager(string? savesDirectory = null, ContentRegistry? registries = null)
    {
        _savesDirectory = savesDirectory
            ?? Path.Combine(ProfileStore.DefaultProfileDirectory, "saves");
        _registries = registries;
    }

    /// <summary>
    /// Root directory holding all saves (one subdir per saveId).
    /// </summary>
    public string SavesDirectory => _savesDirectory;

    /// <summary>
    /// When true, <see cref="SaveAll"/> gzip-compresses
    /// <c>world.json</c> / <c>log.json</c> / <c>state.json</c> on write
    /// (issue #80). <c>meta.json</c> stays uncompressed (it's small and
    /// <see cref="ListSaves"/> reads it on every save-list refresh).
    /// Default is <c>false</c> so the unit tests (which construct a
    /// bare <see cref="SaveManager"/> without wiring Settings) keep
    /// writing plain JSON; the Desktop layer's ServiceHost reads
    /// <see cref="Profile.Settings.CompressSaves"/> (default <c>true</c>)
    /// on startup and sets this property accordingly. Reads always
    /// auto-detect compression, so toggling this between sessions is
    /// safe (a save written compressed loads fine when this is false,
    /// and vice versa).
    /// </summary>
    public bool CompressSaves { get; set; } = false;

    /// <summary>
    /// Lazily-loaded default content registry (used when no registry was
    /// injected in the ctor). Cached so repeated loads share one
    /// registry instance.
    /// </summary>
    private ContentRegistry Registries => _registries ??= ContentRegistry.LoadDefault();

    // ─── Path helpers ──────────────────────────────────────────────────

    private string SaveDir(string saveId) => Path.Combine(_savesDirectory, saveId);
    private string MetaPath(string saveId) => Path.Combine(SaveDir(saveId), "meta.json");
    private string WorldPath(string saveId) => Path.Combine(SaveDir(saveId), "world.json");
    private string LogPath(string saveId) => Path.Combine(SaveDir(saveId), "log.json");
    private string StatePath(string saveId) => Path.Combine(SaveDir(saveId), "state.json");
    private string BackupsDir(string saveId) => Path.Combine(SaveDir(saveId), "backups");

    // ─── Create ────────────────────────────────────────────────────────

    /// <summary>
    /// Create a brand-new save slot. Generates a fresh <c>saveId</c>,
    /// writes all four files (meta / world / log / state), and returns
    /// the new <see cref="SaveMeta"/>. The log is initialized to an
    /// empty array (the caller can <see cref="SaveAll"/> later with a
    /// populated log).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is null/empty.
    /// </exception>
    public SaveMeta CreateSave(string name, GameWorld world, Guid? ownerId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Save name cannot be empty.", nameof(name));
        if (world is null) throw new ArgumentNullException(nameof(world));

        var saveId = NewSaveId();
        var now = DateTimeOffset.UtcNow;

        var meta = new SaveMeta
        {
            Id = saveId,
            Name = name.Trim(),
            OwnerId = ownerId ?? Guid.Empty,
            CharacterName = world.Players.FirstOrDefault()?.Name,
            CharacterLevel = world.Players.FirstOrDefault()?.Level,
            WorldTitle = world.Locations.FirstOrDefault()?.Name,
            LocationName = world.GetLocation(world.Players.FirstOrDefault()?.LocationId ?? default)?.Name,
            CreatedAt = now,
            UpdatedAt = now,
            PlaytimeMs = world.PlaytimeMs,
            Turn = world.Turn,
            BuildStatus = BuildStatus.None,
            EngineVersion = Common.Version.Current,
            StorageVersion = CurrentStorageVersion,
        };

        SaveAll(saveId, world, meta, Array.Empty<LogEntry>());
        return meta;
    }

    // ─── Read ──────────────────────────────────────────────────────────

    /// <summary>
    /// Read a save's meta. Returns null if the save directory or
    /// <c>meta.json</c> doesn't exist, or if the file is corrupt.
    /// </summary>
    public SaveMeta? LoadMeta(string saveId)
    {
        if (!TryIsValidSaveId(saveId)) return null;
        return TryReadJson<SaveMeta>(MetaPath(saveId));
    }

    /// <summary>
    /// Read a save's world. Returns null if the save directory or
    /// <c>world.json</c> doesn't exist, or if the file is corrupt. The
    /// loaded World is wired up with the content registry (default
    /// <see cref="ContentRegistry.LoadDefault"/> unless one was injected
    /// in the ctor) so template lookups work immediately.
    ///
    /// <para>
    /// <b>Compression:</b> transparently decompresses
    /// <c>world.json.gz</c> if present (issue #80). Falls back to
    /// <c>world.json</c> for old uncompressed saves.
    /// </para>
    ///
    /// <para>
    /// <b>Migration:</b> the save's <c>meta.json</c> is read in
    /// parallel to determine the save's
    /// <see cref="SaveMeta.StorageVersion"/>. If it predates
    /// <see cref="SaveMigrator.CurrentStorageVersion"/>,
    /// <see cref="SaveMigrator.MigrateWorld"/> is run on the
    /// deserialized world before returning it. The on-disk world.json
    /// is NOT re-written by this call (a future task can add
    /// lazy re-save so the next load skips the migrator).
    /// </para>
    /// </summary>
    public GameWorld? LoadWorld(string saveId)
    {
        if (!TryIsValidSaveId(saveId)) return null;
        var json = TryReadText(WorldPath(saveId));
        if (json is null) return null;
        try
        {
            var world = GameWorld.FromJson(json, Registries);

            // Run migration if the save predates the current schema.
            // The storage version lives in meta.json (not world.json),
            // so we read meta here just to get the version. If meta is
            // missing/corrupt, default to v1 (the migrator runs the
            // v1→v2 backfill defensively — it's idempotent).
            var meta = TryReadJson<SaveMeta>(MetaPath(saveId));
            var fromVersion = meta?.StorageVersion ?? 1;
            if (fromVersion < SaveMigrator.CurrentStorageVersion)
            {
                SaveMigrator.MigrateWorld(world, fromVersion);
            }
            return world;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to deserialize world for {saveId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read a save's log. Returns an empty array if the save directory
    /// or <c>log.json</c> doesn't exist (a save created by a partial
    /// write may legitimately have no log yet). Returns null only on
    /// hard parse failure (caller can decide whether to treat as empty
    /// or surface the error).
    /// </summary>
    public LogEntry[]? LoadLog(string saveId)
    {
        if (!TryIsValidSaveId(saveId)) return null;
        var log = TryReadJson<LogEntry[]>(LogPath(saveId));
        return log ?? Array.Empty<LogEntry>();
    }

    /// <summary>
    /// Full load: meta + world + log in one call. Returns null if any
    /// of the three is missing (a half-written save is treated as not
    /// loadable — the saves-list UI should hide such saves or mark them
    /// "corrupt").
    ///
    /// <para>
    /// <b>Migration:</b> if the meta's
    /// <see cref="SaveMeta.StorageVersion"/> predates
    /// <see cref="SaveMigrator.CurrentStorageVersion"/>, the world is
    /// migrated (via <see cref="LoadWorld"/>) and the meta is updated
    /// via <see cref="SaveMigrator.MigrateMeta"/>. The returned meta
    /// has the bumped version — callers that re-save will persist the
    /// new version so the next load skips the migrator.
    /// </para>
    /// </summary>
    public (GameWorld world, SaveMeta meta, LogEntry[] log)? LoadAll(string saveId)
    {
        var meta = LoadMeta(saveId);
        if (meta is null) return null;
        var world = LoadWorld(saveId);
        if (world is null) return null;
        var log = LoadLog(saveId) ?? Array.Empty<LogEntry>();

        // Bump the meta's StorageVersion + EngineVersion to current.
        // LoadWorld already ran the world migration (above); this just
        // refreshes the meta so a subsequent SaveAll persists the new
        // version. No-op when the meta was already at current.
        var fromVersion = meta.StorageVersion;
        if (fromVersion < SaveMigrator.CurrentStorageVersion
            || meta.EngineVersion != Common.Version.Current)
        {
            meta = SaveMigrator.MigrateMeta(meta, fromVersion);
        }
        return (world, meta, log);
    }

    /// <summary>
    /// Scan <see cref="SavesDirectory"/>, returning the meta of every
    /// loadable save, sorted by <see cref="SaveMeta.UpdatedAt"/>
    /// descending (most recently played first). Corrupt / partial saves
    /// are silently skipped (the saves-list UI shouldn't crash because
    /// one save's meta.json is half-written).
    /// </summary>
    public IReadOnlyList<SaveMeta> ListSaves()
    {
        var result = new List<SaveMeta>();

        string[] subdirs;
        try
        {
            if (!Directory.Exists(_savesDirectory)) return result;
            subdirs = Directory.GetDirectories(_savesDirectory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to list saves in {_savesDirectory}: {ex.Message}");
            return result;
        }

        foreach (var dir in subdirs)
        {
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) continue;
            var dirName = Path.GetFileName(dir);
            try
            {
                var meta = JsonSerializer.Deserialize<SaveMeta>(
                    File.ReadAllText(metaPath), WorldJson.Options);
                if (meta is null) continue;
                // If the meta's Id got out of sync with the directory
                // name (manual copy / rename by the user), trust the
                // directory name — it's the source of truth on disk.
                if (string.IsNullOrEmpty(meta.Id) || meta.Id != dirName)
                {
                    meta = meta with { Id = dirName };
                }
                result.Add(meta);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[SaveManager] Skipping corrupt meta at {metaPath}: {ex.Message}");
            }
        }

        result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    // ─── Write ─────────────────────────────────────────────────────────

    /// <summary>
    /// Persist the world + meta + log for an existing save. Overwrites
    /// all four files atomically (per-file atomic; the four-file set is
    /// not transactional, but the meta file is written LAST so a crash
    /// mid-<see cref="SaveAll"/> leaves a save whose meta points at the
    /// previous world state — loadable but slightly stale, never
    /// corrupt). Preserves <see cref="SaveMeta.CreatedAt"/> from the
    /// existing meta on disk (if present); refreshes
    /// <see cref="SaveMeta.UpdatedAt"/> to now.
    ///
    /// <para>
    /// <b>Backup (issue #81):</b> BEFORE writing the new files, the
    /// current save directory is copied to
    /// <c>backups/{timestamp}/</c> (if a previous save exists). The
    /// last <see cref="MaxBackupsPerSave"/> backups are kept; older
    /// ones (and any older than <see cref="BackupMaxAgeDays"/> days)
    /// are auto-deleted.
    /// </para>
    /// <para>
    /// <b>Compression (issue #80):</b> when <see cref="CompressSaves"/>
    /// is true, <c>world.json</c> / <c>log.json</c> /
    /// <c>state.json</c> are written as <c>{name}.json.gz</c>
    /// (gzip-compressed) and any stale plain-<c>.json</c> files are
    /// deleted. When false, the inverse happens (plain <c>.json</c>
    /// written, stale <c>.gz</c> deleted).
    /// </para>
    /// </summary>
    public void SaveAll(string saveId, GameWorld world, SaveMeta meta, LogEntry[] log)
    {
        if (!TryIsValidSaveId(saveId))
            throw new ArgumentException("Invalid save id.", nameof(saveId));
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (meta is null) throw new ArgumentNullException(nameof(meta));
        if (log is null) throw new ArgumentNullException(nameof(log));

        lock (_lock)
        {
            Directory.CreateDirectory(SaveDir(saveId));

            // Issue #81 — back up the current save BEFORE overwriting
            // it. The first SaveAll on a brand-new save has nothing to
            // back up (the directory was just created). Best-effort:
            // backup failures are logged but don't abort the save.
            try { BackupBeforeSave(saveId); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[SaveManager] Backup-before-save failed for {saveId}: {ex.Message}");
            }

            // Preserve createdAt from existing meta (if any) — a
            // SaveAll call shouldn't reset the save's birthday.
            var existing = TryReadJson<SaveMeta>(MetaPath(saveId));
            var createdAt = existing?.CreatedAt ?? meta.CreatedAt;
            var now = DateTimeOffset.UtcNow;

            var updatedMeta = meta with
            {
                Id = saveId,
                CreatedAt = createdAt,
                UpdatedAt = now,
                PlaytimeMs = world.PlaytimeMs,
                Turn = world.Turn,
                EngineVersion = Common.Version.Current,
                StorageVersion = CurrentStorageVersion,
                // Refresh the denormalized character/location snapshots
                // so the saves-list preview stays current.
                CharacterName = world.Players.FirstOrDefault()?.Name ?? meta.CharacterName,
                CharacterLevel = world.Players.FirstOrDefault()?.Level ?? meta.CharacterLevel,
            };
            var playerId = world.ActivePlayerId ?? world.Players.FirstOrDefault()?.Id;
            if (playerId is { } pid)
            {
                var loc = world.GetLocation(world.GetPlayer(pid)?.LocationId ?? default);
                if (loc is not null) updatedMeta = updatedMeta with { LocationName = loc.Name };
            }

            var state = new SaveState
            {
                EngineVersion = Common.Version.Current,
                ActivePlayerId = world.ActivePlayerId,
                StartedAt = world.StartedAt,
                PlaytimeMs = world.PlaytimeMs,
                Turn = world.Turn,
            };

            // Write order: world, log, state, then meta LAST. A crash
            // before the meta write leaves the previous meta pointing at
            // the previous (still-loadable) world — never at a missing
            // or half-written world.
            //
            // meta.json is ALWAYS uncompressed (ListSaves reads it
            // hot-path on every save-list refresh; gzip-ing a ~1 KB
            // file would slow ListSaves with no size win). The other
            // three follow the CompressSaves flag.
            AtomicWriteJson(WorldPath(saveId), world.ToJson(), rawString: true, compress: CompressSaves);
            AtomicWriteJson(LogPath(saveId), log, compress: CompressSaves);
            AtomicWriteJson(StatePath(saveId), state, compress: CompressSaves);
            AtomicWriteJson(MetaPath(saveId), updatedMeta, compress: false);
        }
    }

    // ─── Delete ────────────────────────────────────────────────────────

    /// <summary>
    /// Delete a save directory entirely (including its
    /// <c>backups/</c> subdirectory). No-op if the save doesn't exist
    /// (returns false). Returns true on successful deletion. Failures
    /// (e.g. file in use) are logged to <c>Trace</c> and return false.
    /// </summary>
    public bool DeleteSave(string saveId)
    {
        if (!TryIsValidSaveId(saveId)) return false;
        var dir = SaveDir(saveId);
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to delete save {saveId}: {ex.Message}");
            return false;
        }
    }

    // ─── Backups (issue #81) ───────────────────────────────────────────

    /// <summary>
    /// List all backups for a save, sorted newest-first. Returns an
    /// empty list if the save has no backups (or doesn't exist). Each
    /// entry's <see cref="SaveBackup.Timestamp"/> is the on-disk backup
    /// directory name — pass it to <see cref="RestoreBackup"/> to roll
    /// the save back to that snapshot.
    /// </summary>
    public IReadOnlyList<SaveBackup> ListBackups(string saveId)
    {
        var result = new List<SaveBackup>();
        if (!TryIsValidSaveId(saveId)) return result;
        var backupsRoot = BackupsDir(saveId);
        if (!Directory.Exists(backupsRoot)) return result;

        string[] dirs;
        try { dirs = Directory.GetDirectories(backupsRoot); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to list backups in {backupsRoot}: {ex.Message}");
            return result;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            // The backup dir name is "yyyyMMddHHmmssfff" optionally
            // followed by "_N" (same-ms collision suffix). Parse the
            // leading 17 chars as the UTC timestamp.
            var tsStr = name.Contains('_')
                ? name.Substring(0, name.IndexOf('_'))
                : name;
            if (!DateTimeOffset.TryParseExact(
                tsStr,
                "yyyyMMddHHmmssfff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var ts))
            {
                // Unrecognized backup dir name — skip rather than
                // crashing the whole list (a user might have dropped a
                // stray folder there).
                continue;
            }

            long size = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(f).Length; }
                    catch { /* skip locked / vanished file */ }
                }
            }
            catch { /* size is best-effort */ }

            result.Add(new SaveBackup
            {
                Timestamp = name,
                CreatedAt = ts,
                SizeBytes = size,
            });
        }

        result.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return result;
    }

    /// <summary>
    /// Restore a save from a previously-taken backup. The current save
    /// files (meta / world / log / state) are replaced with the backup's
    /// copies. The <c>backups/</c> subdirectory is preserved (so the
    /// user can roll back further if needed). Returns false if the save
    /// or the named backup doesn't exist; true on success.
    /// </summary>
    /// <param name="saveId">
    /// The save to restore into (must already exist on disk).
    /// </param>
    /// <param name="backupTimestamp">
    /// The <see cref="SaveBackup.Timestamp"/> of the backup to restore
    /// (returned by <see cref="ListBackups"/>).
    /// </param>
    /// <remarks>
    /// The restore is destructive — the current (pre-restore) save
    /// state is NOT auto-backed-up before being overwritten. If the
    /// user wants to undo a restore, they can pick an earlier backup
    /// (the restore itself doesn't create a new backup, so the
    /// pre-restore state isn't recoverable through the backup system).
    /// A future task could add a pre-restore snapshot if needed.
    /// </remarks>
    public bool RestoreBackup(string saveId, string backupTimestamp)
    {
        if (!TryIsValidSaveId(saveId)) return false;
        if (string.IsNullOrEmpty(backupTimestamp)) return false;
        // Reject path-traversal / weird names. The timestamp is a dir
        // name, but we still validate it to keep the restore path safe.
        if (backupTimestamp.Contains('/') || backupTimestamp.Contains('\\')
            || backupTimestamp.Contains("..", StringComparison.Ordinal))
            return false;

        var saveDir = SaveDir(saveId);
        if (!Directory.Exists(saveDir)) return false;

        var backupDir = Path.Combine(BackupsDir(saveId), backupTimestamp);
        if (!Directory.Exists(backupDir)) return false;

        lock (_lock)
        {
            // Delete the current top-level save files (meta / world /
            // log / state, both .json and .json.gz variants, plus any
            // stray .tmp files from a crashed previous write). The
            // backups/ subdir is preserved.
            foreach (var file in Directory.EnumerateFiles(saveDir, "*", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(file); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[SaveManager] Failed to delete {file} during restore: {ex.Message}");
                }
            }

            // Copy the backup's files back into the save dir.
            foreach (var file in Directory.EnumerateFiles(backupDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                try { File.Copy(file, Path.Combine(saveDir, name), overwrite: true); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[SaveManager] Failed to restore {file}: {ex.Message}");
                }
            }
        }
        return true;
    }

    // ─── Sharing: export / import (issue #33) ──────────────────────────

    /// <summary>
    /// Export a save as a <c>.pathstone-world</c> zip archive. The
    /// archive contains:
    /// <list type="bullet">
    ///   <item><c>manifest.json</c> — <see cref="SaveManifest"/> with
    ///     world title, engine version, owner profile id, export
    ///     timestamp.</item>
    ///   <item><c>meta.json</c> — copied verbatim from the save dir.</item>
    ///   <item><c>world.json</c> or <c>world.json.gz</c> — copied
    ///     verbatim (compression preserved).</item>
    ///   <item><c>log.json</c>[<c>.gz</c>] — copied verbatim.</item>
    ///   <item><c>state.json</c>[<c>.gz</c>] — copied verbatim.</item>
    /// </list>
    /// The output is written atomically (<c>{path}.tmp</c> → move) so
    /// a partial archive never appears at <paramref name="outputPath"/>.
    /// Returns false if the save doesn't exist; true on success.
    /// </summary>
    public bool ExportSave(string saveId, string outputPath)
    {
        if (!TryIsValidSaveId(saveId)) return false;
        if (string.IsNullOrEmpty(outputPath)) return false;
        var saveDir = SaveDir(saveId);
        if (!Directory.Exists(saveDir)) return false;

        var meta = TryReadJson<SaveMeta>(MetaPath(saveId));
        var manifest = new SaveManifest
        {
            WorldTitle = !string.IsNullOrEmpty(meta?.WorldTitle) ? meta!.WorldTitle : meta?.Name,
            SaveName = meta?.Name,
            EngineVersion = Common.Version.Current,
            StorageVersion = CurrentStorageVersion,
            OwnerProfileId = meta?.OwnerId ?? Guid.Empty,
            ExportTimestamp = DateTimeOffset.UtcNow,
        };

        // Write to a .tmp file first, then atomically move to the
        // destination. A crash mid-export leaves a .tmp file (which the
        // user can ignore / delete) rather than a truncated .pathstone-world.
        var tmpPath = outputPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmpPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                // manifest.json — written from the in-memory manifest
                // (NOT copied from the save dir, because the save dir's
                // manifest.json — if any — is the IMPORT manifest from
                // when this save was imported; we want a fresh EXPORT
                // manifest reflecting THIS export).
                WriteZipEntryText(zip, "manifest.json",
                    JsonSerializer.Serialize(manifest, WorldJson.Options));

                // Copy all top-level save files verbatim (preserves
                // compression: world.json.gz stays world.json.gz).
                // Skip .tmp files (crashed writes) and any pre-existing
                // manifest.json (we just wrote a fresh one above).
                foreach (var file in Directory.EnumerateFiles(saveDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                    WriteZipEntryFromStream(zip, name, file);
                }
            }
            File.Move(tmpPath, outputPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to export save {saveId} to {outputPath}: {ex.Message}");
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
            catch { /* best-effort cleanup */ }
            return false;
        }
    }

    /// <summary>
    /// Import a <c>.pathstone-world</c> archive as a new save. The
    /// archive is unzipped into a fresh <c>{saveId}/</c> directory
    /// (a new saveId is generated so importing the same archive twice
    /// produces two separate saves instead of colliding). The archive's
    /// <c>manifest.json</c> (if present) is updated with the import
    /// timestamp and stored as a sidecar in the save dir (ignored by
    /// <see cref="ListSaves"/> / <see cref="LoadAll"/>). The meta's Id
    /// is rewritten to match the new saveId and UpdatedAt is refreshed.
    /// Returns the new saveId, or null on failure.
    /// </summary>
    /// <param name="inputPath">
    /// Path to the <c>.pathstone-world</c> zip archive.
    /// </param>
    /// <remarks>
    /// <b>Zip-slip defense:</b> entry names are sanitized to a flat
    /// single-level filename (any directory components or
    /// <c>..</c> traversal is rejected) so a malicious archive can't
    /// write outside the save dir.
    /// </remarks>
    public string? ImportSave(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath)) return null;
        if (!File.Exists(inputPath)) return null;

        var saveId = NewSaveId();
        var saveDir = SaveDir(saveId);

        try
        {
            Directory.CreateDirectory(saveDir);
            SaveManifest? manifest = null;

            using (var src = File.OpenRead(inputPath))
            using (var zip = new ZipArchive(src, ZipArchiveMode.Read, leaveOpen: false))
            {
                foreach (var entry in zip.Entries)
                {
                    // Skip directory entries (FullName ends with '/') —
                    // we create the save dir upfront, no nested dirs
                    // are needed (entries are flat-sanitized below).
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var safeName = SanitizeZipEntryName(entry.Name);
                    if (safeName is null) continue;

                    var destPath = Path.Combine(saveDir, safeName);

                    if (safeName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        // Read the manifest, stamp the import timestamp,
                        // write the updated manifest as a sidecar.
                        using (var es = entry.Open())
                        using (var reader = new StreamReader(es, Encoding.UTF8))
                        {
                            var json = reader.ReadToEnd();
                            try { manifest = JsonSerializer.Deserialize<SaveManifest>(json, WorldJson.Options); }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Trace.WriteLine(
                                    $"[SaveManager] Failed to parse manifest.json in archive: {ex.Message}");
                            }
                        }
                        var updated = (manifest ?? new SaveManifest()) with
                        {
                            ImportTimestamp = DateTimeOffset.UtcNow,
                        };
                        File.WriteAllText(destPath,
                            JsonSerializer.Serialize(updated, WorldJson.Options));
                        continue;
                    }

                    // Copy the entry verbatim (preserves compression).
                    using (var es = entry.Open())
                    using (var dest = File.Create(destPath))
                    {
                        es.CopyTo(dest);
                    }
                }
            }

            // Rewrite the meta's Id to match the new saveId (the
            // archive's meta.json has the ORIGINAL saveId, which won't
            // match the new save directory name — ListSaves would
            // re-stamp it on read, but rewriting here keeps the on-disk
            // meta consistent with the dir name and lets LoadAll find
            // it). Refresh UpdatedAt so the imported save sorts to the
            // top of the saves list.
            var meta = TryReadJson<SaveMeta>(MetaPath(saveId));
            if (meta is not null)
            {
                var now = DateTimeOffset.UtcNow;
                var updated = meta with
                {
                    Id = saveId,
                    // Preserve CreatedAt (the original creation date —
                    // useful provenance) but refresh UpdatedAt so the
                    // save sorts to the top of the list after import.
                    UpdatedAt = now,
                };
                AtomicWriteJson(MetaPath(saveId), updated, compress: false);
            }

            // If the archive had no manifest.json, synthesize one so
            // the save dir always carries import provenance. (When the
            // archive DID have a manifest, it was already written above
            // with the import timestamp stamped on it.)
            var manifestPath = Path.Combine(saveDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                var synthesized = new SaveManifest
                {
                    SaveName = meta?.Name,
                    WorldTitle = !string.IsNullOrEmpty(meta?.WorldTitle) ? meta!.WorldTitle : meta?.Name,
                    EngineVersion = meta?.EngineVersion ?? Common.Version.Current,
                    StorageVersion = meta?.StorageVersion ?? CurrentStorageVersion,
                    OwnerProfileId = meta?.OwnerId ?? Guid.Empty,
                    ImportTimestamp = DateTimeOffset.UtcNow,
                };
                File.WriteAllText(manifestPath,
                    JsonSerializer.Serialize(synthesized, WorldJson.Options));
            }

            return saveId;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to import save from {inputPath}: {ex.Message}");
            // Best-effort cleanup of the half-created save dir so the
            // user doesn't end up with a corrupt save slot.
            try { if (Directory.Exists(saveDir)) Directory.Delete(saveDir, recursive: true); }
            catch { /* best-effort */ }
            return null;
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Generate a new save id: <c>save_</c> + 32-char lowercase hex
    /// (Guid's "N" format). Mirrors the spec's <c>save_{Guid:N}</c>
    /// convention.
    /// </summary>
    public static string NewSaveId() => $"save_{Guid.NewGuid():N}";

    /// <summary>
    /// Reject obviously-bad save ids before they hit the filesystem
    /// (path traversal, empty, etc.). A valid save id is
    /// <c>save_</c> + 32 hex chars.
    /// </summary>
    private static bool TryIsValidSaveId(string saveId)
    {
        if (string.IsNullOrEmpty(saveId)) return false;
        if (saveId.Length != 37) return false;            // "save_" (5) + 32 hex
        if (!saveId.StartsWith("save_", StringComparison.Ordinal)) return false;
        for (int i = 5; i < saveId.Length; i++)
        {
            var c = saveId[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    /// <summary>
    /// Read a save file's text. Auto-detects gzip compression: if
    /// <c>{path}.gz</c> exists, decompress it; otherwise read
    /// <c>{path}</c> as plain UTF-8 text. Returns null if neither file
    /// exists or on read/parse failure (logged to <c>Trace</c>).
    ///
    /// <para>
    /// This is the heart of the compression feature's backward
    /// compatibility: a save written compressed (<c>.json.gz</c>)
    /// loads fine when <see cref="CompressSaves"/> is false, and a
    /// save written uncompressed (<c>.json</c>) loads fine when
    /// <see cref="CompressSaves"/> is true. The flag only affects
    /// writes — reads always probe both extensions.
    /// </para>
    /// </summary>
    private static string? TryReadText(string path)
    {
        var gzPath = path + ".gz";
        try
        {
            // Prefer the .gz variant if present (newer compressed
            // saves). Fall back to plain .json for old saves.
            if (File.Exists(gzPath))
            {
                using var fs = File.OpenRead(gzPath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to read {path}: {ex.Message}");
            return null;
        }
    }

    private static T? TryReadJson<T>(string path)
    {
        var raw = TryReadText(path);
        if (raw is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(raw, WorldJson.Options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to deserialize {path}: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// Atomic write helper. Serializes <paramref name="value"/> to JSON,
    /// writes to <c>{path}.tmp</c> (or <c>{path}.gz.tmp</c> when
    /// compressing), then <see cref="File.Move"/>s over the final path.
    /// A crash mid-write leaves the <c>.tmp</c> file (ignored by
    /// readers) rather than a truncated final file.
    /// </summary>
    /// <param name="rawString">
    /// If true, <paramref name="value"/> is treated as a pre-serialized
    /// JSON string and written verbatim (used for <c>world.json</c> —
    /// the World has its own ToJson() that we don't want to
    /// double-serialize).
    /// </param>
    /// <param name="compress">
    /// If true, gzip-compress the bytes and write to
    /// <c>{path}.gz</c> (and delete any stale plain <c>{path}</c>).
    /// If false, write plain text to <c>{path}</c> (and delete any
    /// stale <c>{path}.gz</c>). The clean-up keeps the save dir from
    /// accumulating both compressed and uncompressed variants of the
    /// same file when the user toggles <see cref="CompressSaves"/>.
    /// </param>
    private static void AtomicWriteJson(string path, object value, bool rawString = false, bool compress = false)
    {
        var json = rawString ? (string)value : JsonSerializer.Serialize(value, WorldJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        var finalPath = compress ? path + ".gz" : path;
        var tmpPath = finalPath + ".tmp";

        using (var fs = File.Create(tmpPath))
        {
            if (compress)
            {
                // Optimal compression — save size matters more than
                // save speed (saves are written at most once per
                // autosave interval, typically 2 min).
                using var gz = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: false);
                gz.Write(bytes, 0, bytes.Length);
            }
            else
            {
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        File.Move(tmpPath, finalPath, overwrite: true);

        // Clean up the OTHER variant so toggling CompressSaves doesn't
        // leave stale files that confuse readers (TryReadText prefers
        // .gz, so a stale .gz would shadow a freshly-written plain
        // .json — bad). Best-effort: a failed delete is logged but
        // doesn't abort the save.
        var otherPath = compress ? path : path + ".gz";
        if (File.Exists(otherPath))
        {
            try { File.Delete(otherPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[SaveManager] Failed to delete stale {otherPath}: {ex.Message}");
            }
        }
    }

    // ─── Backup helpers (issue #81) ────────────────────────────────────

    /// <summary>
    /// Copy the current save directory's top-level files to
    /// <c>backups/{timestamp}/</c>. Skipped if the save directory
    /// doesn't exist or has no save files (the first SaveAll on a
    /// brand-new save has nothing to back up). After copying, prunes
    /// old backups per <see cref="MaxBackupsPerSave"/> +
    /// <see cref="BackupMaxAgeDays"/>.
    /// </summary>
    /// <remarks>
    /// Must be called BEFORE the new files are written — otherwise the
    /// backup would contain the NEW state instead of the previous one.
    /// </remarks>
    private void BackupBeforeSave(string saveId)
    {
        var saveDir = SaveDir(saveId);
        if (!Directory.Exists(saveDir)) return;

        // Only back up if there's at least one save file (meta.json is
        // the canonical marker — a save dir without it is half-created
        // or a stray folder). This also avoids backing up a dir that
        // only contains a backups/ subfolder.
        var hasMeta = File.Exists(MetaPath(saveId));
        if (!hasMeta) return;

        var backupsRoot = BackupsDir(saveId);
        Directory.CreateDirectory(backupsRoot);

        // Timestamp: yyyyMMddHHmmssfff (UTC, sortable, lexical =
        // chronological). Collisions within the same millisecond get
        // a _N suffix so each backup has a unique dir name.
        var tsStr = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var backupDir = Path.Combine(backupsRoot, tsStr);
        var suffix = 1;
        while (Directory.Exists(backupDir))
        {
            backupDir = Path.Combine(backupsRoot, $"{tsStr}_{suffix}");
            suffix++;
        }
        Directory.CreateDirectory(backupDir);

        // Copy the save files (NOT the backups/ subfolder — that would
        // recursively nest backups). TopDirectoryOnly keeps it flat.
        foreach (var file in Directory.EnumerateFiles(saveDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            // Skip .tmp files (crashed writes) — they're not part of
            // the canonical save state.
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(backupDir, name), overwrite: true);
        }

        PruneBackups(backupsRoot);
    }

    /// <summary>
    /// Delete backups that are older than <see cref="BackupMaxAgeDays"/>
    /// OR beyond the <see cref="MaxBackupsPerSave"/> most recent.
    /// Whichever rule is more restrictive wins (i.e. a backup is kept
    /// only if it's BOTH within the N most recent AND less than
    /// <see cref="BackupMaxAgeDays"/> days old). Best-effort: deletion
    /// failures are logged but don't abort the prune.
    /// </summary>
    private static void PruneBackups(string backupsRoot)
    {
        if (!Directory.Exists(backupsRoot)) return;

        string[] dirs;
        try { dirs = Directory.GetDirectories(backupsRoot); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SaveManager] Failed to enumerate backups in {backupsRoot}: {ex.Message}");
            return;
        }

        // Parse each backup dir name into (timestamp, fullPath) so we
        // can apply both rules. Dirs with unparseable names are left
        // alone (defensive — a user might have dropped a stray folder).
        var parsed = new List<(DateTimeOffset Ts, string Dir)>();
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            var tsStr = name.Contains('_')
                ? name.Substring(0, name.IndexOf('_'))
                : name;
            if (DateTimeOffset.TryParseExact(
                tsStr,
                "yyyyMMddHHmmssfff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var ts))
            {
                parsed.Add((ts, dir));
            }
        }

        // Sort newest-first so we can identify the N most recent.
        parsed.Sort((a, b) => b.Ts.CompareTo(a.Ts));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-BackupMaxAgeDays);
        var toDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Rule 1: delete backups older than BackupMaxAgeDays.
        foreach (var (ts, dir) in parsed)
        {
            if (ts < cutoff) toDelete.Add(dir);
        }

        // Rule 2: delete backups beyond the N most recent (by index in
        // the sorted list). Whichever rule is more restrictive wins —
        // since both add to the same set, a backup is kept only if it's
        // BOTH within the N most recent AND less than BackupMaxAgeDays
        // old.
        foreach (var (_, dir) in parsed.Skip(MaxBackupsPerSave))
        {
            toDelete.Add(dir);
        }

        foreach (var dir in toDelete)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[SaveManager] Failed to prune backup {dir}: {ex.Message}");
            }
        }
    }

    // ─── Zip helpers (issue #33) ───────────────────────────────────────

    /// <summary>
    /// Write a UTF-8 text entry to a zip archive. Used for
    /// <c>manifest.json</c> (the only entry we synthesize from memory;
    /// the save files are streamed from disk via
    /// <see cref="WriteZipEntryFromStream"/>).
    /// </summary>
    private static void WriteZipEntryText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        es.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Stream a disk file into a zip entry. The file is copied verbatim
    /// (no re-compression at the zip layer beyond the entry's own
    /// <see cref="CompressionLevel.Optimal"/> — for already-gzipped
    /// save files this is a near-no-op since gzip output is
    /// incompressible). Used for <c>meta.json</c> / <c>world.json(.gz)</c>
    /// / <c>log.json(.gz)</c> / <c>state.json(.gz)</c>.
    /// </summary>
    private static void WriteZipEntryFromStream(ZipArchive zip, string entryName, string sourcePath)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        using var src = File.OpenRead(sourcePath);
        src.CopyTo(es);
    }

    /// <summary>
    /// Sanitize a zip entry name to a flat, single-level filename.
    /// Rejects path-traversal (<c>..</c>) and rooted paths, and strips
    /// any directory components so a malicious archive can't write
    /// outside the save directory (the classic "zip-slip" attack).
    /// Returns null if the name is empty or wholly invalid after
    /// sanitizing.
    /// </summary>
    private static string? SanitizeZipEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        // GetFileName strips directory components (and rejects trailing
        // slashes — but entry.Name already has no trailing slash for
        // file entries; we called this from a path where entry.Name is
        // just the file name). Belt-and-suspenders: also reject '..'.
        var safe = Path.GetFileName(name);
        if (string.IsNullOrEmpty(safe)) return null;
        if (safe.Contains("..", StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(name)) return null;
        return safe;
    }

    /// <summary>
    /// Runtime engine state stored in <c>state.json</c>. Mostly a
    /// denormalized subset of <see cref="World"/> (so the saves-list UI
    /// and a future "quick resume" path can read engine state without
    /// loading the full world) plus an <see cref="Extra"/> bag for
    /// future additions (token usage, build state, etc.).
    /// </summary>
    private sealed record SaveState
    {
        public string EngineVersion { get; init; } = Common.Version.Current;
        public EntityId? ActivePlayerId { get; init; }
        public long StartedAt { get; init; }
        public long PlaytimeMs { get; init; }
        public int Turn { get; init; }
        public Dictionary<string, object>? Extra { get; init; }
    }
}
