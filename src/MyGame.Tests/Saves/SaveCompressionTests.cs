using System.IO;
using System.IO.Compression;
using System.Text;
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
/// Unit tests for save-file gzip compression (issue #80). Covers:
/// <list type="bullet">
///   <item>Compressed save round-trips (save → load → verify world matches).</item>
///   <item>Uncompressed save still loads (backward compat with old saves).</item>
///   <item>Compressed save is smaller than uncompressed for the same world.</item>
///   <item>Toggling CompressSaves between writes switches the on-disk layout
///     and the next load still works (forward + backward compat).</item>
///   <item>meta.json stays uncompressed (ListSaves fast-path).</item>
/// </list>
/// </summary>
public class SaveCompressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SaveManager _manager;
    private readonly ContentRegistry _registries;

    public SaveCompressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MyGameCompressTests_" + Guid.NewGuid().ToString("N"));
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

    // ─── Round-trip ────────────────────────────────────────────────────

    [Fact]
    public void Compressed_Save_RoundTripsWorld()
    {
        // Default CompressSaves=false (matches the SaveManager ctor
        // default used by the other test suites). Toggle it ON for
        // this test to exercise the gzip path.
        _manager.CompressSaves = true;

        var world = MakeWorld();
        // Mutate the world so we can verify the loaded copy isn't a
        // fresh DefaultWorld (e.g. the turn counter, an item).
        world.Turn = 13;
        world.Items.Add(new() { Id = EntityId.NewId(), Name = "compressed-test-item" });

        var meta = _manager.CreateSave("Compressed", world);

        // Verify the on-disk layout: world.json.gz exists, world.json
        // (plain) does NOT.
        var saveDir = Path.Combine(_tempDir, meta.Id);
        Assert.True(File.Exists(Path.Combine(saveDir, "world.json.gz")),
            "world.json.gz should exist when CompressSaves=true");
        Assert.False(File.Exists(Path.Combine(saveDir, "world.json")),
            "world.json (plain) should NOT exist when CompressSaves=true");
        Assert.True(File.Exists(Path.Combine(saveDir, "log.json.gz")));
        Assert.True(File.Exists(Path.Combine(saveDir, "state.json.gz")));
        // meta.json stays uncompressed (ListSaves fast-path).
        Assert.True(File.Exists(Path.Combine(saveDir, "meta.json")));
        Assert.False(File.Exists(Path.Combine(saveDir, "meta.json.gz")));

        // Round-trip: load the compressed save back.
        var loaded = _manager.LoadAll(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(13, loaded!.Value.world.Turn);
        Assert.Contains(loaded.Value.world.Items,
            i => i.Name == "compressed-test-item");
    }

    [Fact]
    public void Compressed_LoadWorld_RoundTripsRng()
    {
        _manager.CompressSaves = true;

        var world = MakeWorld();
        for (int i = 0; i < 5; i++) world.Rng.Next();
        world.RngState = world.Rng.State;
        var stateBefore = world.Rng.State;

        var meta = _manager.CreateSave("RNG", world);
        var loaded = _manager.LoadWorld(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(stateBefore, loaded!.RngState);

        // Continuing the stream should produce the same outputs.
        for (int i = 0; i < 5; i++)
            Assert.Equal(world.Rng.Next(), loaded.Rng.Next());
    }

    // ─── Backward compatibility ───────────────────────────────────────

    [Fact]
    public void Uncompressed_Save_Still_Loads_WhenCompressFlagTrue()
    {
        // Write an UNCOMPRESSED save (CompressSaves=false), then turn
        // the flag on and verify the save still loads (backward compat:
        // the loader probes for .gz first, falls back to plain .json).
        _manager.CompressSaves = false;
        var world = MakeWorld();
        world.Turn = 7;
        var meta = _manager.CreateSave("Plain", world);

        var saveDir = Path.Combine(_tempDir, meta.Id);
        Assert.True(File.Exists(Path.Combine(saveDir, "world.json")));
        Assert.False(File.Exists(Path.Combine(saveDir, "world.json.gz")));

        // Now flip the flag ON and load. The save should still load
        // because TryReadText falls back to plain .json.
        _manager.CompressSaves = true;
        var loaded = _manager.LoadAll(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded!.Value.world.Turn);
    }

    [Fact]
    public void Compressed_Save_Still_Loads_WhenCompressFlagFalse()
    {
        // Write a COMPRESSED save, then turn the flag OFF and verify
        // the save still loads (forward compat: a save written with
        // compression ON loads fine when the manager later has
        // compression OFF).
        _manager.CompressSaves = true;
        var world = MakeWorld();
        world.Turn = 21;
        var meta = _manager.CreateSave("Compressed", world);

        _manager.CompressSaves = false;
        var loaded = _manager.LoadAll(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(21, loaded!.Value.world.Turn);
    }

    // ─── Toggle mid-session ───────────────────────────────────────────

    [Fact]
    public void ToggleCompression_BetweenSaves_SwitchesLayoutAndKeepsLoadable()
    {
        // First save: uncompressed.
        _manager.CompressSaves = false;
        var world = MakeWorld();
        world.Turn = 1;
        var meta = _manager.CreateSave("Toggle", world);

        var saveDir = Path.Combine(_tempDir, meta.Id);
        Assert.True(File.Exists(Path.Combine(saveDir, "world.json")));

        // Second save: compressed — should DELETE the plain world.json
        // and write world.json.gz.
        _manager.CompressSaves = true;
        world.Turn = 2;
        _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

        Assert.True(File.Exists(Path.Combine(saveDir, "world.json.gz")));
        Assert.False(File.Exists(Path.Combine(saveDir, "world.json")),
            "stale plain world.json should be deleted after a compressed save");

        var loaded = _manager.LoadAll(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Value.world.Turn);

        // Third save: uncompressed again — should delete the .gz and
        // write plain .json.
        _manager.CompressSaves = false;
        world.Turn = 3;
        _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

        Assert.True(File.Exists(Path.Combine(saveDir, "world.json")));
        Assert.False(File.Exists(Path.Combine(saveDir, "world.json.gz")),
            "stale world.json.gz should be deleted after an uncompressed save");

        loaded = _manager.LoadAll(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Value.world.Turn);
    }

    // ─── Size reduction ───────────────────────────────────────────────

    [Fact]
    public void Compressed_Save_IsSmaller_ThanUncompressed()
    {
        // Build the same world twice: once compressed, once uncompressed.
        // The compressed world.json.gz must be smaller than the plain
        // world.json. (DefaultWorld has a fair amount of text — names,
        // descriptions — so gzip should shrink it meaningfully.)
        var world = MakeWorld();

        // Compressed save.
        _manager.CompressSaves = true;
        var compressedMeta = _manager.CreateSave("C", world);
        var compressedWorldPath = Path.Combine(_tempDir, compressedMeta.Id, "world.json.gz");
        var compressedSize = new FileInfo(compressedWorldPath).Length;

        // Uncompressed save.
        _manager.CompressSaves = false;
        var plainMeta = _manager.CreateSave("P", world);
        var plainWorldPath = Path.Combine(_tempDir, plainMeta.Id, "world.json");
        var plainSize = new FileInfo(plainWorldPath).Length;

        // The compressed size should be strictly smaller. (Gzip on a
        // non-trivial JSON blob will always beat raw UTF-8 here —
        // DefaultWorld has lots of repeated structure.)
        Assert.True(compressedSize < plainSize,
            $"compressed world.json.gz ({compressedSize} B) should be smaller than " +
            $"plain world.json ({plainSize} B)");
    }

    // ─── Magic bytes / file integrity ─────────────────────────────────

    [Fact]
    public void Compressed_File_HasGzipMagicBytes()
    {
        // Gzip files start with the magic bytes 1F 8B. This is a sanity
        // check that we're writing real gzip (not just renaming a plain
        // file with a .gz extension).
        _manager.CompressSaves = true;
        var world = MakeWorld();
        var meta = _manager.CreateSave("Magic", world);

        var gzPath = Path.Combine(_tempDir, meta.Id, "world.json.gz");
        var bytes = File.ReadAllBytes(gzPath);
        Assert.True(bytes.Length >= 2, "world.json.gz should not be empty");
        Assert.Equal(0x1F, bytes[0]);
        Assert.Equal(0x8B, bytes[1]);
    }

    [Fact]
    public void Compressed_File_DecompressesToValidJson()
    {
        // Manually decompress world.json.gz and verify the result is
        // the same JSON the World.ToJson() produced. This catches any
        // encoding bug (e.g. wrong codepage, truncated stream).
        _manager.CompressSaves = true;
        var world = MakeWorld();
        var expectedJson = world.ToJson();
        var meta = _manager.CreateSave("Manual", world);

        var gzPath = Path.Combine(_tempDir, meta.Id, "world.json.gz");
        string decompressed;
        using (var fs = File.OpenRead(gzPath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var reader = new StreamReader(gz, Encoding.UTF8))
        {
            decompressed = reader.ReadToEnd();
        }

        Assert.Equal(expectedJson, decompressed);
    }

    // ─── ListSaves still works with compressed saves ─────────────────

    [Fact]
    public void ListSaves_ReturnsCompressedSaves()
    {
        // ListSaves reads meta.json (always uncompressed) — should work
        // the same with compression ON.
        _manager.CompressSaves = true;
        var world = MakeWorld();
        var meta = _manager.CreateSave("Listed", world);

        var saves = _manager.ListSaves();
        Assert.Contains(saves, s => s.Id == meta.Id && s.Name == "Listed");
    }
}
