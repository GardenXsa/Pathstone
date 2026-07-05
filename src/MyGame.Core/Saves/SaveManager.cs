using System.IO;
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
///   meta.json    — SaveMeta (id, name, ownerId, partyId, characterName,
///                  worldTitle, createdAt, updatedAt, buildStatus,
///                  engineVersion, …)
///   world.json   — full World state (World.ToJson() round-trip)
///   log.json     — LogEntry[] (narrative / action / system / tool log)
///   state.json   — SaveState (turn, activePlayerId, playtimeMs, …)
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
    /// in a way that requires a migration on load. Currently 1 (first
    /// version of the desktop-port format).
    /// </summary>
    public const int CurrentStorageVersion = 1;

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
    /// </summary>
    public GameWorld? LoadWorld(string saveId)
    {
        if (!TryIsValidSaveId(saveId)) return null;
        var json = TryReadText(WorldPath(saveId));
        if (json is null) return null;
        try
        {
            return GameWorld.FromJson(json, Registries);
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
    /// </summary>
    public (GameWorld world, SaveMeta meta, LogEntry[] log)? LoadAll(string saveId)
    {
        var meta = LoadMeta(saveId);
        if (meta is null) return null;
        var world = LoadWorld(saveId);
        if (world is null) return null;
        var log = LoadLog(saveId) ?? Array.Empty<LogEntry>();
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
            AtomicWriteJson(WorldPath(saveId), world.ToJson(), rawString: true);
            AtomicWriteJson(LogPath(saveId), log);
            AtomicWriteJson(StatePath(saveId), state);
            AtomicWriteJson(MetaPath(saveId), updatedMeta);
        }
    }

    // ─── Delete ────────────────────────────────────────────────────────

    /// <summary>
    /// Delete a save directory entirely. No-op if the save doesn't exist
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

    private static string? TryReadText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path);
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
    /// writes to <c>{path}.tmp</c>, then <see cref="File.Move"/>s over
    /// the final path. A crash mid-write leaves the <c>.tmp</c> file
    /// (ignored by readers) rather than a truncated final file.
    /// </summary>
    /// <param name="rawString">
    /// If true, <paramref name="value"/> is treated as a pre-serialized
    /// JSON string and written verbatim (used for <c>world.json</c> —
    /// the World has its own ToJson() that we don't want to
    /// double-serialize).
    /// </param>
    private static void AtomicWriteJson(string path, object value, bool rawString = false)
    {
        var tmp = path + ".tmp";
        var json = rawString ? (string)value : JsonSerializer.Serialize(value, WorldJson.Options);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
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
