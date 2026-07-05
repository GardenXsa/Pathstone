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
/// Unit tests for the save-file migration pipeline. Covers the v1→v2
/// step (Item.Weight backfill from template) and the no-op fast-path
/// when the save is already at the current version.
/// </summary>
public class SaveMigratorTests
{
    /// <summary>
    /// Build a minimal world wired up with the default content registry.
    /// The migrator needs the registry to look up item templates during
    /// the v1→v2 backfill.
    /// </summary>
    private static GameWorld MakeWorld()
    {
        var registries = ContentRegistry.LoadDefault();
        var world = new GameWorld
        {
            Rng = new Rng(1),
            Registries = registries,
        };
        return world;
    }

    /// <summary>
    /// Make an item instance that looks like a v1 save: Weight=0 (the
    /// default before v2), with a known TemplateId. The migrator should
    /// backfill Weight from the template on load.
    /// </summary>
    private static Item MakeV1Item(string templateId, string name = "v1 item")
    {
        var item = new Item
        {
            Id = EntityId.NewId(),
            TemplateId = templateId,
            Name = name,
            Quantity = 1,
            Weight = 0, // pre-v2 default — this is what we're backfilling
        };
        return item;
    }

    [Fact]
    public void Migrate_v1_to_v2_BackfillsItemWeight()
    {
        var world = MakeWorld();
        // wpn_shortsword has weight 2 in data.json.
        var item = MakeV1Item("wpn_shortsword", "Короткий меч");
        world.Items.Add(item);

        // Sanity: before migration, Weight is 0.
        Assert.Equal(0, item.Weight);

        SaveMigrator.MigrateWorld(world, fromVersion: 1);

        // After migration, Weight should match the template's weight.
        var tpl = world.Registries.Items.Get("wpn_shortsword");
        Assert.NotNull(tpl);
        Assert.Equal(tpl!.Weight, item.Weight);
        Assert.Equal(2, item.Weight); // wpn_shortsword is 2 lb in data.json
    }

    [Fact]
    public void Migrate_v1_to_v2_BackfillsInventoryAndEquippedItems()
    {
        // The v1→v2 step also targets characters' carried inventory +
        // equipped slots, not just loose ground items. Build a player
        // with a v1 short sword equipped and a v1 health potion in the
        // inventory, then verify both get backfilled.
        var world = MakeWorld();

        var loc = new Location { Id = EntityId.NewId(), Name = "loc" };
        world.AddLocation(loc);

        var player = new Player
        {
            Id = EntityId.NewId(),
            Name = "Tester",
            LocationId = loc.Id,
        };
        var sword = MakeV1Item("wpn_shortsword", "Короткий меч");
        var armor = MakeV1Item("arm_leather", "Кожаный доспех");
        player.Equipped["weapon"] = sword;
        player.Equipped["armor"] = armor;
        player.Inventory.Items.Add(MakeV1Item("cns_health_potion", "Зелье лечения"));
        world.SpawnPlayer(player);

        SaveMigrator.MigrateWorld(world, fromVersion: 1);

        var swordTpl = world.Registries.Items.Get("wpn_shortsword")!;
        var armorTpl = world.Registries.Items.Get("arm_leather")!;
        var potionTpl = world.Registries.Items.Get("cns_health_potion")!;

        Assert.Equal(swordTpl.Weight, sword.Weight);    // 2
        Assert.Equal(armorTpl.Weight, armor.Weight);    // 10
        Assert.Equal(potionTpl.Weight,
            player.Inventory.Items[0].Weight);          // 0.5
    }

    [Fact]
    public void Migrate_v1_to_v2_UnknownTemplate_LeavesZero()
    {
        // An item with a TemplateId that doesn't exist in the registry
        // can't be backfilled — the migrator leaves Weight at 0 (the
        // pre-v2 default) rather than throwing or guessing.
        var world = MakeWorld();
        var item = MakeV1Item("this_template_does_not_exist", "мусор");
        world.Items.Add(item);

        SaveMigrator.MigrateWorld(world, fromVersion: 1);

        Assert.Equal(0, item.Weight);
    }

    [Fact]
    public void Migrate_v1_to_v2_NoTemplateId_LeavesZero()
    {
        // Hand-spawned items (no TemplateId) can't be backfilled either.
        var world = MakeWorld();
        var item = new Item
        {
            Id = EntityId.NewId(),
            TemplateId = null,
            Name = "Безымянный предмет",
            Weight = 0,
        };
        world.Items.Add(item);

        SaveMigrator.MigrateWorld(world, fromVersion: 1);

        Assert.Equal(0, item.Weight);
    }

    [Fact]
    public void Migrate_AlreadyCurrent_NoOp()
    {
        // A world already at the current version should be returned
        // unchanged. The migrator must not re-touch items that already
        // have a non-zero Weight (idempotence).
        var world = MakeWorld();
        var tpl = world.Registries.Items.Get("wpn_shortsword")!;
        var item = new Item
        {
            Id = EntityId.NewId(),
            TemplateId = "wpn_shortsword",
            Name = "Уже обновлённый меч",
            Weight = tpl.Weight, // already at template weight (v2 state)
        };
        world.Items.Add(item);
        var weightBefore = item.Weight;

        SaveMigrator.MigrateWorld(world, fromVersion: SaveMigrator.CurrentStorageVersion);

        Assert.Equal(weightBefore, item.Weight); // unchanged
    }

    [Fact]
    public void Migrate_FromOlderThanCurrent_RunsAllSteps()
    {
        // fromVersion=1 with CurrentStorageVersion=2 runs exactly one
        // step (v1→v2). The loop bound is CurrentStorageVersion, so a
        // future v3 migration would extend this — for now we just
        // verify the v1→v2 step ran (Weight backfilled) and the world
        // is returned (not null).
        var world = MakeWorld();
        var item = MakeV1Item("wpn_club", "Дубина");
        world.Items.Add(item);

        var returned = SaveMigrator.MigrateWorld(world, fromVersion: 1);

        Assert.Same(world, returned); // mutated in place AND returned
        var tpl = world.Registries.Items.Get("wpn_club")!;
        Assert.Equal(tpl.Weight, item.Weight); // backfilled
    }

    [Fact]
    public void MigrateMeta_BumpsStorageVersionAndEngineVersion()
    {
        // A v1 meta with an older EngineVersion should be bumped to the
        // current StorageVersion + EngineVersion by MigrateMeta.
        var oldMeta = new SaveMeta
        {
            Id = "save_test",
            Name = "old",
            StorageVersion = 1,
            EngineVersion = "0.1.0", // older than Core.Common.Version.Current
        };

        var migrated = SaveMigrator.MigrateMeta(oldMeta, fromVersion: 1);

        Assert.Equal(SaveMigrator.CurrentStorageVersion, migrated.StorageVersion);
        Assert.Equal(MyGame.Core.Common.Version.Current, migrated.EngineVersion);
    }

    [Fact]
    public void MigrateMeta_AlreadyCurrent_StillIdempotent()
    {
        // A meta already at the current version should remain at the
        // current version (the bump is a no-op; the EngineVersion
        // refresh is also idempotent).
        var current = new SaveMeta
        {
            Id = "save_test",
            Name = "current",
            StorageVersion = SaveMigrator.CurrentStorageVersion,
            EngineVersion = MyGame.Core.Common.Version.Current,
        };

        var migrated = SaveMigrator.MigrateMeta(current, fromVersion: SaveMigrator.CurrentStorageVersion);

        Assert.Equal(SaveMigrator.CurrentStorageVersion, migrated.StorageVersion);
        Assert.Equal(MyGame.Core.Common.Version.Current, migrated.EngineVersion);
    }

    [Fact]
    public void MigrateWorld_NullWorld_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SaveMigrator.MigrateWorld(null!, fromVersion: 1));
    }

    [Fact]
    public void MigrateMeta_NullMeta_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SaveMigrator.MigrateMeta(null!, fromVersion: 1));
    }

    [Fact]
    public void CurrentStorageVersion_IsAtLeast2()
    {
        // The Item.Weight backfill (v1→v2) is the trigger for bumping
        // the version. This test guards against accidental rollback.
        Assert.True(SaveMigrator.CurrentStorageVersion >= 2);
    }
}
