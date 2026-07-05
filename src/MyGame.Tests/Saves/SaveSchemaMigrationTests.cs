using System.IO;
using System.Text.Json;
using MyGame.Core.Common;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;
using Xunit;

using GameWorld = MyGame.Core.World.World;

namespace MyGame.Tests.Saves;

/// <summary>
/// Tests that saves from older schema versions migrate correctly to the
/// current version. Uses in-memory fixture saves (no on-disk fixture
/// files needed — we construct old-format saves programmatically).
/// Issue #72.
/// </summary>
public class SaveSchemaMigrationTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PathstoneMigrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Migrate_v1_Save_Loads_On_Current_Version()
    {
        var dir = MakeTempDir();
        try
        {
            var saveId = SaveManager.NewSaveId();
            var saveDir = Path.Combine(dir, saveId);
            Directory.CreateDirectory(saveDir);

            // Construct a v1 SaveMeta (StorageVersion=1, no Item.Weight field).
            var oldMeta = new SaveMeta
            {
                Id = saveId,
                Name = "Migration Test World",
                OwnerId = Guid.NewGuid(),
                StorageVersion = 1, // old version
                EngineVersion = "0.1.0",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            // Construct a v1 World (items without Weight — simulates old save).
            var registries = ContentRegistry.LoadDefault();
            var world = new GameWorld { Seed = 42, Rng = new Rng(42), Registries = registries };
            var loc = new Location { Id = EntityId.NewId(), Name = "Test Loc", Terrain = "plains" };
            world.AddLocation(loc);

            // Add an item WITHOUT Weight (simulates old save format).
            var swordTpl = registries.Items.Get("wpn_shortsword")!;
            var oldItem = new Item { Id = EntityId.NewId(), Name = swordTpl.Name, TemplateId = swordTpl.Id, Weight = 0 };
            world.Items.Add(oldItem);
            loc.Items.Add(oldItem.Id);

            // Write the save in v1 format.
            File.WriteAllText(Path.Combine(saveDir, "meta.json"),
                JsonSerializer.Serialize(oldMeta, WorldJson.Options));
            File.WriteAllText(Path.Combine(saveDir, "world.json"), world.ToJson());
            File.WriteAllText(Path.Combine(saveDir, "log.json"), "[]");
            File.WriteAllText(Path.Combine(saveDir, "state.json"),
                JsonSerializer.Serialize(new { turn = 0, activePlayerId = (string?)null }, WorldJson.Options));

            // Load via SaveManager — should auto-migrate.
            var sm = new SaveManager(dir) { CompressSaves = false };
            var loaded = sm.LoadAll(saveId);

            Assert.NotNull(loaded);
            Assert.Equal(SaveMigrator.CurrentStorageVersion, loaded.Value.meta.StorageVersion);

            // The item's Weight should be backfilled from the template.
            var migratedItem = loaded.Value.world.Items.FirstOrDefault(i => i.TemplateId == "wpn_shortsword");
            Assert.NotNull(migratedItem);
            Assert.Equal(swordTpl.Weight, migratedItem!.Weight);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Current_Version_Save_Does_Not_Migrate()
    {
        var dir = MakeTempDir();
        try
        {
            var sm = new SaveManager(dir) { CompressSaves = false };
            var registries = ContentRegistry.LoadDefault();
            var world = DefaultWorld.Create(seed: 99);
            world.Registries = registries;
            var meta = sm.CreateSave("No Migration Test", world, Guid.NewGuid());

            // Load — should be current version already.
            var loaded = sm.LoadAll(meta.Id);
            Assert.NotNull(loaded);
            Assert.Equal(SaveMigrator.CurrentStorageVersion, loaded.Value.meta.StorageVersion);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Migration_Preserves_World_State()
    {
        var dir = MakeTempDir();
        try
        {
            var saveId = SaveManager.NewSaveId();
            var saveDir = Path.Combine(dir, saveId);
            Directory.CreateDirectory(saveDir);

            var registries = ContentRegistry.LoadDefault();
            var world = DefaultWorld.Create(seed: 7);
            world.Registries = registries;
            world.Turn = 42;
            world.Clock = new GameTime(5, 14, 30);

            var oldMeta = new SaveMeta
            {
                Id = saveId,
                Name = "Preserve Test",
                OwnerId = Guid.NewGuid(),
                StorageVersion = 1,
                EngineVersion = "0.1.0",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Turn = 42,
            };

            File.WriteAllText(Path.Combine(saveDir, "meta.json"),
                JsonSerializer.Serialize(oldMeta, WorldJson.Options));
            File.WriteAllText(Path.Combine(saveDir, "world.json"), world.ToJson());
            File.WriteAllText(Path.Combine(saveDir, "log.json"), "[]");
            File.WriteAllText(Path.Combine(saveDir, "state.json"),
                JsonSerializer.Serialize(new { turn = 42 }, WorldJson.Options));

            var sm = new SaveManager(dir) { CompressSaves = false };
            var loaded = sm.LoadAll(saveId);

            Assert.NotNull(loaded);
            Assert.Equal(42, loaded.Value.world.Turn);
            Assert.Equal(5, loaded.Value.world.Clock.Day);
            Assert.Equal(14, loaded.Value.world.Clock.Hour);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
