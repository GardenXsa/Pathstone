using System.Text.Json;
using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). The test file lives in MyGame.Tests.World,
// so a bare `World` resolves to the test namespace. Alias the type to
// GameWorld (matching the convention used by SaveManager.cs).
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Tests.World;

/// <summary>
/// Unit tests for the World aggregate root. Covers ToJson/FromJson
/// round-tripping, RNG state preservation across save/load, player
/// spawning, and FindEntity lookups across collections.
/// </summary>
public class WorldTests
{
    [Fact]
    public void ToJson_FromJson_RoundTrips()
    {
        // DefaultWorld is the canonical seed-built world — it exercises
        // every entity collection, the calendar, the ruleset, and the
        // RNG state. If a round-trip is stable on it, we're confident
        // the schema is fully reversible.
        var world = DefaultWorld.Create(seed: 42);
        var registries = world.Registries;

        var json1 = world.ToJson();
        var restored = GameWorld.FromJson(json1, registries);
        var json2 = restored.ToJson();

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void ToJson_FromJson_PreservesEntityCounts()
    {
        var world = DefaultWorld.Create(seed: 7);
        var json = world.ToJson();
        var restored = GameWorld.FromJson(json, world.Registries);

        Assert.Equal(world.Players.Count, restored.Players.Count);
        Assert.Equal(world.Npcs.Count, restored.Npcs.Count);
        Assert.Equal(world.Items.Count, restored.Items.Count);
        Assert.Equal(world.Buildings.Count, restored.Buildings.Count);
        Assert.Equal(world.Locations.Count, restored.Locations.Count);
        Assert.Equal(world.Quests.Count, restored.Quests.Count);
    }

    [Fact]
    public void ToJson_FromJson_PreservesSeedAndClock()
    {
        var world = DefaultWorld.Create(seed: 1234);
        var restored = GameWorld.FromJson(world.ToJson(), world.Registries);

        Assert.Equal(world.Seed, restored.Seed);
        Assert.Equal(world.Clock, restored.Clock);
        Assert.Equal(world.Turn, restored.Turn);
        Assert.Equal(world.Ruleset.Id, restored.Ruleset.Id);
    }

    [Fact]
    public void RngState_RoundTripsThroughJson()
    {
        // Roll the RNG a few times, snapshot state, round-trip through
        // JSON, then roll a few more — the restored world's RNG must
        // produce the same sequence as the original.
        //
        // Note: World.RngState is the SERIALIZED snapshot of the live
        // Rng's state, but the live Rng advances independently — the
        // property isn't auto-synced. To capture the post-roll state
        // into the snapshot, we explicitly copy it back before ToJson.
        // (The SaveManager does the same thing internally when saving.)
        var world = DefaultWorld.Create(seed: 999);
        for (int i = 0; i < 5; i++)
            world.Rng.Next();
        world.RngState = world.Rng.State;

        var stateBeforeSave = world.Rng.State;
        var restored = GameWorld.FromJson(world.ToJson(), world.Registries);
        Assert.Equal(stateBeforeSave, restored.RngState);

        // Both RNGs should now produce identical next-5 outputs.
        for (int i = 0; i < 5; i++)
            Assert.Equal(world.Rng.Next(), restored.Rng.Next());
    }

    [Fact]
    public void FromJson_EmptyString_Throws() =>
        Assert.Throws<ArgumentException>(() => GameWorld.FromJson("", new ContentRegistry()));

    [Fact]
    public void FromJson_NullRegistries_Throws()
    {
        var world = DefaultWorld.Create(seed: 1);
        var json = world.ToJson();
        Assert.Throws<ArgumentNullException>(() => GameWorld.FromJson(json, null!));
    }

    [Fact]
    public void SpawnPlayer_SetsActivePlayer()
    {
        var world = new GameWorld { Rng = new Rng(1) };
        Assert.Null(world.ActivePlayerId);
        Assert.Null(world.ActivePlayer);

        var loc = new Location { Id = EntityId.NewId(), Name = "Старт" };
        world.AddLocation(loc);

        var player = new Player
        {
            Id = EntityId.NewId(),
            Name = "Герой",
            LocationId = loc.Id,
        };
        world.SpawnPlayer(player);

        Assert.Equal(player.Id, world.ActivePlayerId);
        Assert.NotNull(world.ActivePlayer);
        Assert.Equal(player.Id, world.ActivePlayer!.Id);
        Assert.Single(world.Players);
    }

    [Fact]
    public void SpawnPlayer_FirstPlayerBecomesActive_SecondDoesNotReplace()
    {
        var world = new GameWorld { Rng = new Rng(1) };
        var loc = new Location { Id = EntityId.NewId(), Name = "loc" };

        var p1 = new Player { Id = EntityId.NewId(), Name = "p1", LocationId = loc.Id };
        var p2 = new Player { Id = EntityId.NewId(), Name = "p2", LocationId = loc.Id };

        world.SpawnPlayer(p1);
        world.SpawnPlayer(p2);

        // Active player should remain the first one spawned.
        Assert.Equal(p1.Id, world.ActivePlayerId);
        Assert.Equal(2, world.Players.Count);
    }

    [Fact]
    public void SpawnPlayer_NullPlayer_Throws()
    {
        var world = new GameWorld { Rng = new Rng(1) };
        Assert.Throws<ArgumentNullException>(() => world.SpawnPlayer(null!));
    }

    [Fact]
    public void FindEntity_FindsAcrossCollections()
    {
        var world = new GameWorld { Rng = new Rng(1) };

        var loc = new Location { Id = EntityId.NewId(), Name = "loc" };
        world.AddLocation(loc);

        var player = new Player { Id = EntityId.NewId(), Name = "player", LocationId = loc.Id };
        world.SpawnPlayer(player);

        var npc = new Npc { Id = EntityId.NewId(), Name = "npc", LocationId = loc.Id };
        world.SpawnNpc(npc);

        var building = new Building { Id = EntityId.NewId(), Name = "bld", LocationId = loc.Id };
        world.SpawnBuilding(building);

        var item = new Item { Id = EntityId.NewId(), Name = "item" };
        world.SpawnItemOnGround(item, loc.Id);

        var quest = new Quest { Id = EntityId.NewId(), Name = "quest" };
        world.Quests.Add(quest);

        Assert.NotNull(world.FindEntity(player.Id));
        Assert.NotNull(world.FindEntity(npc.Id));
        Assert.NotNull(world.FindEntity(building.Id));
        Assert.NotNull(world.FindEntity(item.Id));
        Assert.NotNull(world.FindEntity(loc.Id));
        Assert.NotNull(world.FindEntity(quest.Id));

        // Names match so we know the index points to the right entity.
        Assert.Equal("player", world.FindEntity(player.Id)!.Name);
        Assert.Equal("npc", world.FindEntity(npc.Id)!.Name);
        Assert.Equal("bld", world.FindEntity(building.Id)!.Name);
        Assert.Equal("item", world.FindEntity(item.Id)!.Name);
        Assert.Equal("loc", world.FindEntity(loc.Id)!.Name);
        Assert.Equal("quest", world.FindEntity(quest.Id)!.Name);
    }

    [Fact]
    public void FindEntity_Nonexistent_ReturnsNull()
    {
        var world = new GameWorld { Rng = new Rng(1) };
        Assert.Null(world.FindEntity(EntityId.NewId()));
    }

    [Fact]
    public void GetPlayer_GetNpc_GetLocation_GetBuilding_GetItem_GetQuest_AllResolve()
    {
        var world = new GameWorld { Rng = new Rng(1) };
        var loc = new Location { Id = EntityId.NewId(), Name = "loc" };
        world.AddLocation(loc);
        var player = new Player { Id = EntityId.NewId(), Name = "p", LocationId = loc.Id };
        world.SpawnPlayer(player);
        var npc = new Npc { Id = EntityId.NewId(), Name = "n", LocationId = loc.Id };
        world.SpawnNpc(npc);
        var bld = new Building { Id = EntityId.NewId(), Name = "b", LocationId = loc.Id };
        world.SpawnBuilding(bld);
        var item = new Item { Id = EntityId.NewId(), Name = "i" };
        world.SpawnItemOnGround(item, loc.Id);
        var quest = new Quest { Id = EntityId.NewId(), Name = "q" };
        world.Quests.Add(quest);

        Assert.NotNull(world.GetPlayer(player.Id));
        Assert.NotNull(world.GetNpc(npc.Id));
        Assert.NotNull(world.GetLocation(loc.Id));
        Assert.NotNull(world.GetBuilding(bld.Id));
        Assert.NotNull(world.GetItem(item.Id));
        Assert.NotNull(world.GetQuest(quest.Id));
    }

    [Fact]
    public void Player_Property_ThrowsWhenNoPlayers()
    {
        var world = new GameWorld { Rng = new Rng(1) };
        Assert.Throws<InvalidOperationException>(() => _ = world.Player);
    }

    [Fact]
    public void DefaultWorld_Create_ProducesNonEmptyWorld()
    {
        var world = DefaultWorld.Create(seed: 1);
        Assert.NotEmpty(world.Locations);
        Assert.NotEmpty(world.Players);
        Assert.NotEmpty(world.Npcs);
        Assert.NotEmpty(world.Buildings);
        Assert.NotEmpty(world.Quests);
        Assert.NotNull(world.ActivePlayerId);
        Assert.NotNull(world.Ruleset);
    }

    [Fact]
    public void DefaultWorld_Create_RngStateIsDeterministicBySeed()
    {
        // DefaultWorld.Create doesn't roll the engine RNG during construction
        // (entity IDs are minted via EntityId.NewId, which uses Random.Shared,
        // not the world's Rng). So two worlds with the same seed have the
        // same RNG state and produce identical RNG output sequences — even
        // though their entity IDs / serialized JSON differ.
        var a = DefaultWorld.Create(seed: 12345);
        var b = DefaultWorld.Create(seed: 12345);
        Assert.Equal(a.RngState, b.RngState);
        Assert.Equal(a.Rng.State, b.Rng.State);
        for (int i = 0; i < 10; i++)
            Assert.Equal(a.Rng.Next(), b.Rng.Next());
    }
}
