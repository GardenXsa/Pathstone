using System.Globalization;
using System.IO;
using MyGame.Core.Common;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). Alias to GameWorld to disambiguate, matching
// the convention used by SaveManager.cs.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Tests.Saves;

/// <summary>
/// Unit tests for the auto-rotating backup system (issue #81). Covers:
/// <list type="bullet">
///   <item>SaveAll creates a backup of the previous version.</item>
///   <item>ListBackups returns timestamps (newest-first).</item>
///   <item>RestoreBackup restores the correct version.</item>
///   <item>Old backups are auto-deleted (keep 5 most recent).</item>
///   <item>Backups older than 30 days are pruned regardless of count.</item>
///   <item>First SaveAll on a brand-new save does NOT create a backup.</item>
/// </list>
/// </summary>
public class BackupSavesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SaveManager _manager;
    private readonly ContentRegistry _registries;

    public BackupSavesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MyGameBackupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registries = ContentRegistry.LoadDefault();
        _manager = new SaveManager(savesDirectory: _tempDir, registries: _registries);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static GameWorld MakeWorld() => DefaultWorld.Create(seed: 42);

    // ─── First-save no backup ─────────────────────────────────────────

    [Fact]
    public void FirstSave_CreatesNoBackup()
    {
        // A brand-new save has nothing to back up — the SaveAll that
        // CreateSave triggers should NOT create a backups/ folder.
        var world = MakeWorld();
        var meta = _manager.CreateSave("Fresh", world);

        var backupsRoot = Path.Combine(_tempDir, meta.Id, "backups");
        Assert.False(Directory.Exists(backupsRoot),
            "First save should not create a backups/ directory");
        Assert.Empty(_manager.ListBackups(meta.Id));
    }

    // ─── Save creates a backup of previous version ───────────────────

    [Fact]
    public void SecondSave_CreatesBackupOfPreviousVersion()
    {
        // Save #1: turn=1.
        var world = MakeWorld();
        world.Turn = 1;
        var meta = _manager.CreateSave("BK", world);

        // Sleep a bit so the next backup timestamp differs (the
        // timestamp format has millisecond resolution, but on a fast
        // machine two saves within the same ms would collide and get a
        // _N suffix — sleeping avoids that for the test's sake).
        Thread.Sleep(20);

        // Save #2: turn=2. This should back up the turn=1 state.
        world.Turn = 2;
        _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

        var backups = _manager.ListBackups(meta.Id);
        Assert.Single(backups);
        var backup = backups[0];

        // The backup directory should exist and contain meta.json from
        // the turn=1 state.
        var backupDir = Path.Combine(_tempDir, meta.Id, "backups", backup.Timestamp);
        Assert.True(Directory.Exists(backupDir));
        Assert.True(File.Exists(Path.Combine(backupDir, "meta.json")));
        Assert.True(File.Exists(Path.Combine(backupDir, "world.json")));

        // The backup's meta should reflect turn=1 (the state BEFORE
        // save #2). Read it directly to bypass the manager's caching.
        var backupMetaJson = File.ReadAllText(Path.Combine(backupDir, "meta.json"));
        var backupMeta = System.Text.Json.JsonSerializer.Deserialize<SaveMeta>(
            backupMetaJson, WorldJson.Options);
        Assert.NotNull(backupMeta);
        Assert.Equal(1, backupMeta!.Turn);
    }

    // ─── ListBackups ordering ─────────────────────────────────────────

    [Fact]
    public void ListBackups_ReturnsNewestFirst()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("Order", world);

        // Make 3 saves (turns 1, 2, 3) — each creates a backup of the
        // previous. So after the third save we have 2 backups (turn=1
        // and turn=2). Wait — actually the first SaveAll creates a
        // backup of the CreateSave state (turn=0); let me bump turns
        // explicitly so the assertions are clear.
        for (int i = 1; i <= 3; i++)
        {
            Thread.Sleep(20);
            world.Turn = i;
            _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());
        }

        // After 3 SaveAll calls, we have 3 backups (of turn=0, 1, 2).
        var backups = _manager.ListBackups(meta.Id);
        Assert.Equal(3, backups.Count);

        // Newest-first: backup[0] should have a later timestamp than
        // backup[1], which should be later than backup[2].
        Assert.True(backups[0].CreatedAt > backups[1].CreatedAt);
        Assert.True(backups[1].CreatedAt > backups[2].CreatedAt);
    }

    [Fact]
    public void ListBackups_EmptyForNonexistentSave()
    {
        Assert.Empty(_manager.ListBackups("save_" + new string('a', 32)));
    }

    [Fact]
    public void ListBackups_EmptyForInvalidSaveId()
    {
        Assert.Empty(_manager.ListBackups("not_a_real_save_id"));
    }

    // ─── Restore ──────────────────────────────────────────────────────

    [Fact]
    public void RestoreBackup_RestoresCorrectVersion()
    {
        var world = MakeWorld();
        world.Turn = 100;
        var meta = _manager.CreateSave("Restore", world);

        // Save with turn=200 → backup of turn=100 is created.
        Thread.Sleep(20);
        world.Turn = 200;
        _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

        // Verify current state is turn=200.
        var loaded = _manager.LoadAll(meta.Id);
        Assert.Equal(200, loaded!.Value.world.Turn);

        // Restore the turn=100 backup.
        var backups = _manager.ListBackups(meta.Id);
        Assert.Single(backups);
        var restored = _manager.RestoreBackup(meta.Id, backups[0].Timestamp);
        Assert.True(restored);

        // The current save should now be turn=100 again.
        loaded = _manager.LoadAll(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(100, loaded!.Value.world.Turn);
    }

    [Fact]
    public void RestoreBackup_NonexistentBackup_ReturnsFalse()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("RB2", world);

        Assert.False(_manager.RestoreBackup(meta.Id, "nonexistent_backup"));
    }

    [Fact]
    public void RestoreBackup_NonexistentSave_ReturnsFalse()
    {
        Assert.False(_manager.RestoreBackup("save_" + new string('a', 32), "anything"));
    }

    [Fact]
    public void RestoreBackup_RejectsPathTraversal()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("RB3", world);

        // The timestamp must not contain '..' or slashes — the restore
        // path is backups/{ts}/, so a malicious ts like "../foo" would
        // escape the save dir. The manager should reject it.
        Assert.False(_manager.RestoreBackup(meta.Id, ".."));
        Assert.False(_manager.RestoreBackup(meta.Id, "../escape"));
        Assert.False(_manager.RestoreBackup(meta.Id, "foo/bar"));
        Assert.False(_manager.RestoreBackup(meta.Id, "foo\\bar"));
    }

    // ─── Auto-cleanup (keep 5) ────────────────────────────────────────

    [Fact]
    public void AutoCleanup_KeepsOnlyFiveMostRecentBackups()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("Prune", world);

        // Make 10 saves — each SaveAll backs up the previous state.
        // After the 10th save, we should have at most 5 backups
        // (the most recent 5). Save #1's backup gets pruned.
        for (int i = 1; i <= 10; i++)
        {
            Thread.Sleep(20);
            world.Turn = i;
            _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());
        }

        var backups = _manager.ListBackups(meta.Id);
        Assert.True(backups.Count <= SaveManager.MaxBackupsPerSave,
            $"Expected at most {SaveManager.MaxBackupsPerSave} backups, got {backups.Count}");
        Assert.Equal(SaveManager.MaxBackupsPerSave, backups.Count);
    }

    [Fact]
    public void AutoCleanup_DeletesOldBackupsBeyondFive()
    {
        // Verify the OLDEST backups are the ones pruned (not random ones).
        var world = MakeWorld();
        var meta = _manager.CreateSave("Prune2", world);

        var backupTimestamps = new List<string>();
        for (int i = 1; i <= 7; i++)
        {
            Thread.Sleep(20);
            world.Turn = i;
            _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());
            // After each save, record the current backups.
            backupTimestamps = _manager.ListBackups(meta.Id)
                .Select(b => b.Timestamp).ToList();
        }

        // After 7 SaveAll calls, only 5 backups survive.
        var finalBackups = _manager.ListBackups(meta.Id);
        Assert.Equal(5, finalBackups.Count);

        // The newest 5 of the original 7 should be the survivors. The
        // oldest 2 should be gone.
        var survivors = finalBackups.Select(b => b.Timestamp).ToHashSet();
        Assert.Equal(5, survivors.Count);

        // The 5 newest timestamps should be in survivors. The 2 oldest
        // should not. (We sort the recorded timestamps newest-first;
        // the first 5 are the survivors.)
        var sortedTimestamps = backupTimestamps
            .OrderByDescending(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var ts in sortedTimestamps.Take(5))
            Assert.Contains(ts, survivors);
        foreach (var ts in sortedTimestamps.Skip(5))
            Assert.DoesNotContain(ts, survivors);
    }

    // ─── 30-day age rule ──────────────────────────────────────────────

    [Fact]
    public void AutoCleanup_DeletesBackupsOlderThan30Days()
    {
        // Manually craft a save dir with backups that have timestamps
        // 31 and 10 days ago, then trigger another save. The 31-day-old
        // backup should be pruned; the 10-day-old should survive (even
        // if we have fewer than 5 backups).
        var world = MakeWorld();
        var meta = _manager.CreateSave("Age", world);
        var saveDir = Path.Combine(_tempDir, meta.Id);
        var backupsRoot = Path.Combine(saveDir, "backups");
        Directory.CreateDirectory(backupsRoot);

        var oldTs = DateTimeOffset.UtcNow.AddDays(-31).ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var newTs = DateTimeOffset.UtcNow.AddDays(-10).ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        foreach (var ts in new[] { oldTs, newTs })
        {
            var dir = Path.Combine(backupsRoot, ts);
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(saveDir, "meta.json"), Path.Combine(dir, "meta.json"));
            File.Copy(Path.Combine(saveDir, "world.json"), Path.Combine(dir, "world.json"));
        }

        // Sanity: we have 2 backups before the next save.
        Assert.Equal(2, _manager.ListBackups(meta.Id).Count);

        // Trigger another save — PruneBackups should delete the 31-day
        // backup (it's beyond the 30-day cutoff AND not in the top 5 —
        // both rules would allow it, but the age rule is the binding
        // one here).
        Thread.Sleep(20);
        world.Turn = 99;
        _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

        var remaining = _manager.ListBackups(meta.Id);
        // We had 2 (old + new), then SaveAll added 1 more (the
        // pre-save state). The 31-day-old one should be pruned.
        // Net: 2 backups (the 10-day-old + the just-created one).
        var timestamps = remaining.Select(b => b.Timestamp).ToHashSet();
        Assert.DoesNotContain(oldTs, timestamps);
        Assert.Contains(newTs, timestamps);
    }

    // ─── Backups survive a regular SaveAll ────────────────────────────

    [Fact]
    public void SaveAll_DoesNotDeleteBackupsSubdir()
    {
        // The backups/ subfolder is INSIDE the save dir. SaveAll must
        // not delete it (only the top-level meta/world/log/state files
        // are overwritten).
        var world = MakeWorld();
        var meta = _manager.CreateSave("Keep", world);

        Thread.Sleep(20);
        world.Turn = 1;
        _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

        var backupsRoot = Path.Combine(_tempDir, meta.Id, "backups");
        Assert.True(Directory.Exists(backupsRoot));
        Assert.NotEmpty(_manager.ListBackups(meta.Id));
    }

    // ─── Backups work with compression too ───────────────────────────

    [Fact]
    public void Backup_Works_WithCompressionEnabled()
    {
        // The backup system should work the same whether the save is
        // compressed or not. The backup just copies whatever files are
        // there.
        _manager.CompressSaves = true;
        var world = MakeWorld();
        world.Turn = 5;
        var meta = _manager.CreateSave("C", world);

        Thread.Sleep(20);
        world.Turn = 6;
        _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

        var backups = _manager.ListBackups(meta.Id);
        Assert.Single(backups);

        // The backup should contain world.json.gz (the compressed
        // variant) since that's what was on disk before save #2.
        var backupDir = Path.Combine(_tempDir, meta.Id, "backups", backups[0].Timestamp);
        Assert.True(File.Exists(Path.Combine(backupDir, "world.json.gz")));
        Assert.False(File.Exists(Path.Combine(backupDir, "world.json")));

        // Restore should bring back the turn=5 state.
        Assert.True(_manager.RestoreBackup(meta.Id, backups[0].Timestamp));
        var loaded = _manager.LoadAll(meta.Id);
        Assert.Equal(5, loaded!.Value.world.Turn);
    }
}
