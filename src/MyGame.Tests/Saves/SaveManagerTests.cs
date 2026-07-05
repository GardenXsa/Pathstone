using MyGame.Core.Common;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). Alias to GameWorld to disambiguate, matching
// the convention used in SaveManager.cs.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Tests.Saves;

/// <summary>
/// Unit tests for the file-based SaveManager. Each test uses a fresh temp
/// directory (via <see cref="TempSavesFixture"/>) so saves never leak
/// across tests or into the user's real profile directory.
/// </summary>
public class SaveManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SaveManager _manager;
    private readonly ContentRegistry _registries;

    public SaveManagerTests()
    {
        // Per-test temp dir under the system temp path. The SaveManager
        // ctor accepts an explicit savesDirectory so we don't have to
        // monkey-patch ProfileStore.DefaultProfileDirectory.
        _tempDir = Path.Combine(Path.GetTempPath(), "MyGameTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registries = ContentRegistry.LoadDefault();
        _manager = new SaveManager(savesDirectory: _tempDir, registries: _registries);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup; tests shouldn't fail on teardown */ }
    }

    private static GameWorld MakeWorld() => DefaultWorld.Create(seed: 42);

    [Fact]
    public void Constructor_SavesDirectory_IsExposed()
    {
        Assert.Equal(_tempDir, _manager.SavesDirectory);
    }

    [Fact]
    public void CreateSave_WritesFilesAndReturnsMeta()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("My Save", world, ownerId: Guid.NewGuid());

        Assert.False(string.IsNullOrEmpty(meta.Id));
        Assert.StartsWith("save_", meta.Id);
        Assert.Equal("My Save", meta.Name);
        Assert.True(Directory.Exists(Path.Combine(_tempDir, meta.Id)));
        Assert.True(File.Exists(Path.Combine(_tempDir, meta.Id, "meta.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, meta.Id, "world.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, meta.Id, "log.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, meta.Id, "state.json")));
    }

    [Fact]
    public void CreateSave_EmptyName_Throws()
    {
        var world = MakeWorld();
        Assert.Throws<ArgumentException>(() => _manager.CreateSave("", world));
        Assert.Throws<ArgumentException>(() => _manager.CreateSave("   ", world));
    }

    [Fact]
    public void CreateSave_NullWorld_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _manager.CreateSave("name", null!));

    [Fact]
    public void CreateSave_LoadAll_RoundTrips()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("Round-trip", world);

        var loaded = _manager.LoadAll(meta.Id);
        Assert.NotNull(loaded);

        var (loadedWorld, loadedMeta, loadedLog) = loaded.Value;
        Assert.Equal(meta.Id, loadedMeta.Id);
        Assert.Equal("Round-trip", loadedMeta.Name);
        Assert.NotNull(loadedWorld);
        Assert.Equal(world.Players.Count, loadedWorld.Players.Count);
        Assert.Equal(world.Npcs.Count, loadedWorld.Npcs.Count);
        Assert.Equal(world.Locations.Count, loadedWorld.Locations.Count);
        Assert.NotNull(loadedLog);
    }

    [Fact]
    public void LoadAll_NonexistentSaveId_ReturnsNull()
    {
        var loaded = _manager.LoadAll("save_" + new string('a', 32));
        Assert.Null(loaded);
    }

    [Fact]
    public void LoadAll_InvalidSaveId_ReturnsNull()
    {
        // Not a valid save_ + 32-hex format → the manager rejects before
        // touching the filesystem.
        Assert.Null(_manager.LoadAll("not_a_real_save_id"));
        Assert.Null(_manager.LoadAll(""));
        Assert.Null(_manager.LoadAll("../escape"));
    }

    [Fact]
    public void ListSaves_ReturnsCreatedSave()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("Listable", world);

        var saves = _manager.ListSaves();
        Assert.Contains(saves, s => s.Id == meta.Id && s.Name == "Listable");
    }

    [Fact]
    public void ListSaves_EmptyWhenNoSaves()
    {
        // Fresh manager on an empty dir.
        var empty = Path.Combine(Path.GetTempPath(), "MyGameTests_empty_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(empty);
            var m = new SaveManager(savesDirectory: empty);
            Assert.Empty(m.ListSaves());
        }
        finally
        {
            try { Directory.Delete(empty, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ListSaves_SortedByUpdatedAtDescending()
    {
        var world = MakeWorld();
        var a = _manager.CreateSave("a", world);
        Thread.Sleep(50); // ensure UpdatedAt differs
        var b = _manager.CreateSave("b", world);
        Thread.Sleep(50);
        var c = _manager.CreateSave("c", world);

        var saves = _manager.ListSaves();
        Assert.Equal(3, saves.Count);
        // Most recently played first.
        Assert.Equal(c.Id, saves[0].Id);
        Assert.Equal(b.Id, saves[1].Id);
        Assert.Equal(a.Id, saves[2].Id);
    }

    [Fact]
    public void DeleteSave_RemovesIt()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("To Delete", world);
        Assert.Contains(_manager.ListSaves(), s => s.Id == meta.Id);

        var deleted = _manager.DeleteSave(meta.Id);
        Assert.True(deleted);
        Assert.DoesNotContain(_manager.ListSaves(), s => s.Id == meta.Id);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, meta.Id)));
    }

    [Fact]
    public void DeleteSave_Nonexistent_ReturnsFalse()
    {
        Assert.False(_manager.DeleteSave("save_" + new string('0', 32)));
    }

    [Fact]
    public void DeleteSave_InvalidId_ReturnsFalse()
    {
        Assert.False(_manager.DeleteSave(""));
        Assert.False(_manager.DeleteSave("../escape"));
    }

    [Fact]
    public void LoadMeta_ReturnsMeta()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("Meta Test", world);

        var loaded = _manager.LoadMeta(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(meta.Id, loaded!.Id);
        Assert.Equal("Meta Test", loaded.Name);
    }

    [Fact]
    public void LoadWorld_RoundTripsRngState()
    {
        var world = MakeWorld();
        // Advance RNG past the construction state, then sync RngState so
        // the save captures the post-roll position. (World.RngState is the
        // serialized snapshot of the live Rng's state, but the live Rng
        // advances independently — the property isn't auto-synced.)
        for (int i = 0; i < 5; i++) world.Rng.Next();
        world.RngState = world.Rng.State;
        var stateBeforeSave = world.Rng.State;

        var meta = _manager.CreateSave("RNG Test", world);
        var loaded = _manager.LoadWorld(meta.Id);
        Assert.NotNull(loaded);
        Assert.Equal(stateBeforeSave, loaded!.RngState);

        // Continuing the stream should produce the same outputs.
        for (int i = 0; i < 5; i++)
            Assert.Equal(world.Rng.Next(), loaded.Rng.Next());
    }

    [Fact]
    public void SaveAll_UpdatesExistingSave()
    {
        var world = MakeWorld();
        var meta = _manager.CreateSave("Update Me", world);

        // Mutate the world: bump turn counter, add an item.
        world.Turn = 7;
        world.Items.Add(new() { Id = EntityId.NewId(), Name = "new item" });

        _manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

        var reloaded = _manager.LoadAll(meta.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(7, reloaded.Value.world.Turn);
        Assert.Single(reloaded.Value.world.Items);
    }

    [Fact]
    public void NewSaveId_IsWellFormed()
    {
        var id = SaveManager.NewSaveId();
        Assert.StartsWith("save_", id);
        Assert.Equal(37, id.Length);
        // Hex chars after "save_" prefix.
        foreach (var c in id.Substring(5))
            Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }
}
