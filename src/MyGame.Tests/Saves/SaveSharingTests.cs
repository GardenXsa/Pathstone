using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MyGame.Core.Common;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). Alias to GameWorld to disambiguate, matching
// the convention used by SaveManager.cs.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Tests.Saves;

/// <summary>
/// Unit tests for the save sharing system (issue #33 —
/// <c>.pathstone-world</c> export/import). Covers:
/// <list type="bullet">
///   <item>ExportSave creates a valid zip with a manifest.json.</item>
///   <item>ImportSave extracts the zip and creates a new saveId.</item>
///   <item>Export → Import round-trip: world data matches.</item>
///   <item>Importing the same archive twice produces two saves (no collision).</item>
///   <item>Export preserves compression (world.json.gz stays .gz in the zip).</item>
///   <item>Export works even when the save has no manifest (re-export of imported save).</item>
/// </list>
/// </summary>
public class SaveSharingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SaveManager _manager;
    private readonly ContentRegistry _registries;

    public SaveSharingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MyGameShareTests_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// Open a .pathstone-world archive as a zip and return the list of
    /// entry names (for assertions about archive contents).
    /// </summary>
    private static List<string> ListZipEntries(string archivePath)
    {
        using var fs = File.OpenRead(archivePath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>
    /// Read a single text entry from a zip archive (UTF-8). Returns null
    /// if the entry doesn't exist.
    /// </summary>
    private static string? ReadZipEntry(string archivePath, string entryName)
    {
        using var fs = File.OpenRead(archivePath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName);
        if (entry is null) return null;
        using var es = entry.Open();
        using var reader = new StreamReader(es, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ─── Export ───────────────────────────────────────────────────────

    [Fact]
    public void Export_CreatesValidZipWithManifest()
    {
        var world = MakeWorld();
        world.Turn = 7;
        var meta = _manager.CreateSave("Exportable", world, ownerId: Guid.NewGuid());

        var exportPath = Path.Combine(_tempDir, "export1.pathstone-world");
        var ok = _manager.ExportSave(meta.Id, exportPath);
        Assert.True(ok);
        Assert.True(File.Exists(exportPath));

        // The zip should contain manifest.json + the 4 save files.
        var entries = ListZipEntries(exportPath);
        Assert.Contains("manifest.json", entries);
        Assert.Contains("meta.json", entries);
        Assert.Contains("world.json", entries);
        Assert.Contains("log.json", entries);
        Assert.Contains("state.json", entries);

        // The manifest should have the expected fields.
        var manifestJson = ReadZipEntry(exportPath, "manifest.json");
        Assert.NotNull(manifestJson);
        var manifest = JsonSerializer.Deserialize<SaveManifest>(manifestJson!, WorldJson.Options);
        Assert.NotNull(manifest);
        Assert.Equal("Exportable", manifest!.SaveName);
        Assert.Equal(meta.OwnerId, manifest.OwnerProfileId);
        Assert.Equal(MyGame.Core.Common.Version.Current, manifest.EngineVersion);
        Assert.True(manifest.ExportTimestamp <= DateTimeOffset.UtcNow);
        Assert.True(manifest.ExportTimestamp > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Export_PreservesCompressionWhenEnabled()
    {
        // When the save is compressed, the zip should contain
        // world.json.gz (not world.json). The exporter copies files
        // verbatim — it doesn't re-compress or decompress.
        _manager.CompressSaves = true;
        var world = MakeWorld();
        var meta = _manager.CreateSave("Compressed", world);

        var exportPath = Path.Combine(_tempDir, "export2.pathstone-world");
        Assert.True(_manager.ExportSave(meta.Id, exportPath));

        var entries = ListZipEntries(exportPath);
        Assert.Contains("world.json.gz", entries);
        Assert.DoesNotContain("world.json", entries);
        // meta.json stays uncompressed even with compression on.
        Assert.Contains("meta.json", entries);
    }

    [Fact]
    public void Export_NonexistentSave_ReturnsFalse()
    {
        var exportPath = Path.Combine(_tempDir, "nope.pathstone-world");
        Assert.False(_manager.ExportSave("save_" + new string('a', 32), exportPath));
        Assert.False(File.Exists(exportPath));
    }

    [Fact]
    public void Export_InvalidSaveId_ReturnsFalse()
    {
        var exportPath = Path.Combine(_tempDir, "nope2.pathstone-world");
        Assert.False(_manager.ExportSave("not_a_real_save_id", exportPath));
    }

    [Fact]
    public void Export_AtomicWrite_DoesNotLeavePartialFileOnFailure()
    {
        // Exporting to a path whose directory doesn't exist should
        // fail cleanly without leaving a .pathstone-world file (it may
        // leave a .tmp file, but the final file should not exist).
        var world = MakeWorld();
        var meta = _manager.CreateSave("Atomic", world);

        var badPath = Path.Combine(_tempDir, "nonexistent_subdir", "out.pathstone-world");
        Assert.False(_manager.ExportSave(meta.Id, badPath));
        Assert.False(File.Exists(badPath));
    }

    // ─── Import ───────────────────────────────────────────────────────

    [Fact]
    public void Import_ExtractsArchiveAndCreatesNewSave()
    {
        var world = MakeWorld();
        world.Turn = 42;
        var originalMeta = _manager.CreateSave("Original", world);

        var exportPath = Path.Combine(_tempDir, "imp1.pathstone-world");
        Assert.True(_manager.ExportSave(originalMeta.Id, exportPath));

        var newSaveId = _manager.ImportSave(exportPath);
        Assert.NotNull(newSaveId);
        Assert.NotEqual(originalMeta.Id, newSaveId);
        Assert.StartsWith("save_", newSaveId);

        // The imported save should appear in ListSaves.
        var saves = _manager.ListSaves();
        Assert.Contains(saves, s => s.Id == newSaveId);

        // The imported save's meta should have the new saveId (not the
        // original) — the importer rewrites the Id to match the new
        // save directory.
        var importedMeta = _manager.LoadMeta(newSaveId!);
        Assert.NotNull(importedMeta);
        Assert.Equal(newSaveId, importedMeta!.Id);
        Assert.Equal("Original", importedMeta.Name);

        // The manifest sidecar should exist in the save dir with the
        // import timestamp set.
        var manifestPath = Path.Combine(_tempDir, newSaveId!, "manifest.json");
        Assert.True(File.Exists(manifestPath));
        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<SaveManifest>(manifestJson, WorldJson.Options);
        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.ImportTimestamp);
    }

    [Fact]
    public void Import_SameArchiveTwice_CreatesTwoSeparateSaves()
    {
        // The whole point of generating a new saveId on import is so
        // importing the same archive twice doesn't collide. Both
        // imports should succeed and produce distinct saveIds.
        var world = MakeWorld();
        var meta = _manager.CreateSave("Shared", world);

        var exportPath = Path.Combine(_tempDir, "imp2.pathstone-world");
        _manager.ExportSave(meta.Id, exportPath);

        var id1 = _manager.ImportSave(exportPath);
        var id2 = _manager.ImportSave(exportPath);

        Assert.NotNull(id1);
        Assert.NotNull(id2);
        Assert.NotEqual(id1, id2);

        var saves = _manager.ListSaves();
        Assert.Contains(saves, s => s.Id == id1);
        Assert.Contains(saves, s => s.Id == id2);
    }

    // ─── Round-trip ───────────────────────────────────────────────────

    [Fact]
    public void ExportImportRoundTrip_WorldDataMatches()
    {
        // The canonical test: build a world, save it, export it, import
        // it as a new save, load the imported save, and verify the
        // world matches the original.
        var world = MakeWorld();
        // Mutate to make the comparison non-trivial.
        world.Turn = 77;
        world.Items.Add(new()
        {
            Id = EntityId.NewId(),
            Name = "shared-item",
        });
        var meta = _manager.CreateSave("RoundTrip", world);

        var exportPath = Path.Combine(_tempDir, "rt.pathstone-world");
        Assert.True(_manager.ExportSave(meta.Id, exportPath));

        var importedId = _manager.ImportSave(exportPath);
        Assert.NotNull(importedId);

        var loaded = _manager.LoadAll(importedId!);
        Assert.NotNull(loaded);

        Assert.Equal(world.Turn, loaded!.Value.world.Turn);
        Assert.Equal(world.Players.Count, loaded.Value.world.Players.Count);
        Assert.Equal(world.Npcs.Count, loaded.Value.world.Npcs.Count);
        Assert.Equal(world.Locations.Count, loaded.Value.world.Locations.Count);
        Assert.Equal(world.Items.Count, loaded.Value.world.Items.Count);
        Assert.Contains(loaded.Value.world.Items, i => i.Name == "shared-item");

        // RNG state should also round-trip (continuing the stream on
        // the imported save should produce the same outputs as the
        // original).
        for (int i = 0; i < 5; i++)
            Assert.Equal(world.Rng.Next(), loaded.Value.world.Rng.Next());
    }

    [Fact]
    public void ExportImportRoundTrip_Works_WithCompression()
    {
        // Compression should be transparent to export/import — the
        // compressed save's world.json.gz is copied verbatim into the
        // zip, extracted verbatim on import, and the loaded world
        // matches.
        _manager.CompressSaves = true;
        var world = MakeWorld();
        world.Turn = 99;
        var meta = _manager.CreateSave("CRT", world);

        var exportPath = Path.Combine(_tempDir, "crt.pathstone-world");
        Assert.True(_manager.ExportSave(meta.Id, exportPath));

        // The archive should contain world.json.gz (compression
        // preserved). On import, the .gz is extracted verbatim — the
        // SaveManager's reader auto-detects it.
        var entries = ListZipEntries(exportPath);
        Assert.Contains("world.json.gz", entries);

        var importedId = _manager.ImportSave(exportPath);
        Assert.NotNull(importedId);

        var loaded = _manager.LoadAll(importedId!);
        Assert.NotNull(loaded);
        Assert.Equal(99, loaded!.Value.world.Turn);
    }

    // ─── Edge cases ───────────────────────────────────────────────────

    [Fact]
    public void Import_NonexistentFile_ReturnsNull()
    {
        Assert.Null(_manager.ImportSave(Path.Combine(_tempDir, "does_not_exist.pathstone-world")));
    }

    [Fact]
    public void Import_EmptyPath_ReturnsNull()
    {
        Assert.Null(_manager.ImportSave(""));
        Assert.Null(_manager.ImportSave(null!));
    }

    [Fact]
    public void Import_DoesNotImportManifestAsMetaFile()
    {
        // The importer writes manifest.json to the save dir as a
        // sidecar. ListSaves should NOT confuse it for a second save
        // (it scans immediate subdirs of saves/ for meta.json, and the
        // save dir's manifest.json is INSIDE the save dir — not a
        // sibling). Verify: after import, the save count is exactly 1
        // (the imported save), not 2.
        var world = MakeWorld();
        var meta = _manager.CreateSave("ToShare", world);
        var exportPath = Path.Combine(_tempDir, "edge.pathstone-world");
        _manager.ExportSave(meta.Id, exportPath);

        // Fresh manager in a fresh dir so we can count saves cleanly.
        var freshDir = Path.Combine(Path.GetTempPath(), "MyGameShareFresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(freshDir);
        try
        {
            var freshManager = new SaveManager(savesDirectory: freshDir, registries: _registries);
            var importedId = freshManager.ImportSave(exportPath);
            Assert.NotNull(importedId);

            var saves = freshManager.ListSaves();
            Assert.Single(saves);
            Assert.Equal(importedId, saves[0].Id);
        }
        finally
        {
            try { Directory.Delete(freshDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Import_RejectsZipSlipEntries()
    {
        // Construct a malicious zip that contains an entry with a
        // path-traversal name (../../etc/passwd). The importer should
        // sanitize the name and either skip the entry or extract it
        // with a flat name — but in NO case should it write outside
        // the save dir.
        var maliciousZipPath = Path.Combine(_tempDir, "malicious.pathstone-world");
        using (var fs = File.Create(maliciousZipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // A legitimate meta.json so the imported save has a valid meta.
            var meta = new SaveMeta
            {
                Id = "save_" + new string('a', 32),
                Name = "Malicious",
            };
            var metaJson = JsonSerializer.Serialize(meta, WorldJson.Options);
            var metaEntry = zip.CreateEntry("meta.json");
            using (var es = metaEntry.Open())
            {
                var bytes = Encoding.UTF8.GetBytes(metaJson);
                es.Write(bytes, 0, bytes.Length);
            }

            // A malicious entry that tries to escape via "..". Note:
            // entry.Name strips directory components on most platforms,
            // so we put the traversal in FullName and use a flat name
            // via CreateEntry(name) — but ZipArchiveEntry.Name is the
            // part after the last separator, so a "../escape.txt" name
            // would have Name "escape.txt" (sanitized to flat). Test
            // both shapes.
            var bad1 = zip.CreateEntry("../escape.txt");
            using (var es = bad1.Open())
            {
                var bytes = Encoding.UTF8.GetBytes("should not escape");
                es.Write(bytes, 0, bytes.Length);
            }
        }

        var importedId = _manager.ImportSave(maliciousZipPath);
        Assert.NotNull(importedId);

        // Verify no "escape.txt" was written OUTSIDE the save dir.
        var escapePath = Path.Combine(_tempDir, "escape.txt");
        Assert.False(File.Exists(escapePath),
            "Importer should not write files outside the save dir (zip-slip)");

        // The save dir should have meta.json (legit) and possibly an
        // escape.txt INSIDE the save dir (sanitized flat name). The
        // import succeeded; the malicious entry was sanitized.
        var saveDir = Path.Combine(_tempDir, importedId!);
        Assert.True(File.Exists(Path.Combine(saveDir, "meta.json")));
    }

    [Fact]
    public void Import_ArchiveWithoutManifest_StillImports()
    {
        // An archive without a manifest.json (e.g. hand-zipped save
        // dir) should still import — the manifest is optional. The
        // importer just won't have export metadata.
        var world = MakeWorld();
        var meta = _manager.CreateSave("NoManifest", world);

        var exportPath = Path.Combine(_tempDir, "nomanifest.pathstone-world");
        // Build the archive manually without a manifest entry.
        using (var fs = File.Create(exportPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var saveDir = Path.Combine(_tempDir, meta.Id);
            foreach (var file in Directory.EnumerateFiles(saveDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var entry = zip.CreateEntry(name);
                using var es = entry.Open();
                using var src = File.OpenRead(file);
                src.CopyTo(es);
            }
        }

        var importedId = _manager.ImportSave(exportPath);
        Assert.NotNull(importedId);

        var loaded = _manager.LoadAll(importedId!);
        Assert.NotNull(loaded);
        Assert.Equal("NoManifest", loaded!.Value.meta.Name);

        // The importer should write a manifest.json sidecar (with
        // ImportTimestamp set) even when the archive had no manifest —
        // the importer synthesizes one.
        var manifestPath = Path.Combine(_tempDir, importedId!, "manifest.json");
        Assert.True(File.Exists(manifestPath));
    }

    // ─── Re-export of an imported save ────────────────────────────────

    [Fact]
    public void ReExport_ImportedSave_ProducesFreshExportManifest()
    {
        // Importing a save writes a manifest.json sidecar to the save
        // dir (with ImportTimestamp). Re-exporting that save should
        // produce a fresh EXPORT manifest (NOT copy the save dir's
        // import manifest) — the exporter synthesizes a new manifest
        // from the save's current meta.
        var world = MakeWorld();
        var meta = _manager.CreateSave("ReExport", world);
        var exportPath1 = Path.Combine(_tempDir, "re1.pathstone-world");
        _manager.ExportSave(meta.Id, exportPath1);

        var importedId = _manager.ImportSave(exportPath1);
        Assert.NotNull(importedId);

        // Re-export the imported save.
        var exportPath2 = Path.Combine(_tempDir, "re2.pathstone-world");
        Assert.True(_manager.ExportSave(importedId!, exportPath2));

        // The re-export's manifest should be a fresh EXPORT manifest
        // (ImportTimestamp null, ExportTimestamp set to now-ish).
        var manifestJson = ReadZipEntry(exportPath2, "manifest.json");
        Assert.NotNull(manifestJson);
        var manifest = JsonSerializer.Deserialize<SaveManifest>(manifestJson!, WorldJson.Options);
        Assert.NotNull(manifest);
        Assert.Null(manifest!.ImportTimestamp);
        Assert.True(manifest.ExportTimestamp > DateTimeOffset.UtcNow.AddMinutes(-1));
    }
}
