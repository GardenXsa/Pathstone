using MyGame.Core.AI.Tools;
using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

// 'World' is both a namespace and a type — alias the type so the
// compiler doesn't see this as namespace usage when we type a variable
// as the World type. We can still use the namespace import for other
// types like DefaultWorld / CombatState.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Tests.AI.Tools;

/// <summary>
/// COMBAT-DEATH (issues #88, #63): targeted unit tests for the four
/// new combat/death tools (start_combat / end_combat / next_turn /
/// death_save) plus the deal_damage branches they interact with
/// (player death-saves, NPC death during combat, auto-end combat).
/// </summary>
public class CombatDeathToolsTests
{
    private static (ToolRegistry reg, GameWorld world) MakeRegistry(long seed = 42)
    {
        var world = DefaultWorld.Create(seed: seed);
        return (new ToolRegistry(world), world);
    }

    private static Npc SpawnHostile(GameWorld world, string name, int hp, int dex = 10)
    {
        var npc = new Npc
        {
            Id = EntityId.NewId(),
            Name = name,
            LocationId = world.ActivePlayer!.LocationId,
            Disposition = "hostile",
            IsAlive = true,
            Attributes = new() { ["dex"] = dex },
            Resources = new() { ["hp"] = hp, ["ac"] = 10 },
        };
        world.SpawnNpc(npc);
        return npc;
    }

    [Fact]
    public async Task StartCombat_InstallsCombatState_WithPlayerPlusHostiles()
    {
        var (reg, world) = MakeRegistry();
        var hostile = SpawnHostile(world, "Гоблин", hp: 7, dex: 14);

        var result = await reg.ExecuteAsync("c1", "start_combat", "{}");
        Assert.False(result.IsError);
        Assert.NotNull(world.Combat);
        Assert.True(world.Combat!.Active);
        Assert.Equal(1, world.Combat.Round);
        Assert.Equal(0, world.Combat.CurrentActorIndex);

        // Player + 1 hostile = 2 combatants.
        Assert.Equal(2, world.Combat.TurnOrder.Count);
        // Both combatants should appear by name in the result.
        Assert.Contains("Странник", result.Content);
        Assert.Contains("Гоблин", result.Content);
        // Initiative should be listed.
        Assert.Contains("Инициатива", result.Content);
    }

    [Fact]
    public async Task StartCombat_NoHostiles_StartsAnywayWithWarning()
    {
        // Default world's player starts in the village (no hostiles there
        // — only quest givers + merchants).
        var (reg, world) = MakeRegistry();

        var result = await reg.ExecuteAsync("c1", "start_combat", "{}");
        Assert.False(result.IsError);
        Assert.NotNull(world.Combat);
        // Just the player.
        Assert.Single(world.Combat!.TurnOrder);
        Assert.Contains("Враждебных NPC", result.Content);
    }

    [Fact]
    public async Task EndCombat_ClearsCombatState()
    {
        var (reg, world) = MakeRegistry();
        SpawnHostile(world, "Гоблин", hp: 7);

        await reg.ExecuteAsync("c1", "start_combat", "{}");
        Assert.NotNull(world.Combat);

        var result = await reg.ExecuteAsync("c2", "end_combat", "{}");
        Assert.False(result.IsError);
        Assert.Null(world.Combat);
        Assert.Contains("Бой окончен", result.Content);
    }

    [Fact]
    public async Task EndCombat_WhenNotActive_ReportsInactive()
    {
        var (reg, world) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "end_combat", "{}");
        Assert.False(result.IsError);
        Assert.Contains("не был активен", result.Content);
    }

    [Fact]
    public async Task NextTurn_AdvancesActor_WrapsAndIncrementsRound()
    {
        var (reg, world) = MakeRegistry();
        SpawnHostile(world, "Гоблин 1", hp: 7);
        SpawnHostile(world, "Гоблин 2", hp: 7);

        await reg.ExecuteAsync("c1", "start_combat", "{}");
        var combat = world.Combat!;
        var count = combat.TurnOrder.Count;
        Assert.True(count >= 3); // player + 2 goblins

        // First next_turn → actor index 1, still round 1.
        var r1 = await reg.ExecuteAsync("c2", "next_turn", "{}");
        Assert.False(r1.IsError);
        Assert.Equal(1, combat.CurrentActorIndex);
        Assert.Equal(1, combat.Round);

        // Second next_turn → actor index 2, still round 1.
        var r2 = await reg.ExecuteAsync("c3", "next_turn", "{}");
        Assert.False(r2.IsError);
        Assert.Equal(2, combat.CurrentActorIndex);
        Assert.Equal(1, combat.Round);

        // Third next_turn → wraps to 0, round 2.
        var r3 = await reg.ExecuteAsync("c4", "next_turn", "{}");
        Assert.False(r3.IsError);
        Assert.Equal(0, combat.CurrentActorIndex);
        Assert.Equal(2, combat.Round);
        Assert.Contains("Раунд 2", r3.Content);
    }

    [Fact]
    public async Task NextTurn_WithoutCombat_ReturnsError()
    {
        var (reg, _) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "next_turn", "{}");
        Assert.True(result.IsError);
        Assert.Contains("Бой не активен", result.Content);
    }

    [Fact]
    public async Task DealDamage_OnPlayerAtZeroHP_DoesNotKill_SeedsDeathSavesFlag()
    {
        var (reg, world) = MakeRegistry();
        var p = world.ActivePlayer!;
        // Drop the player's HP to 0 in one hit. Default-world player HP
        // is derived from con (default 10) — small enough to be killed
        // by 50 damage.
        int bigHit = p.Resources.TryGetValue("hp", out var hp) ? hp + 5 : 50;

        var result = await reg.ExecuteAsync("c1", "deal_damage",
            $"{{\"target\":\"player\",\"amount\":{bigHit}}}");

        Assert.False(result.IsError);
        // Player must NOT be auto-killed — death saves apply first.
        Assert.True(p.IsAlive);
        Assert.Equal(0, p.Resources["hp"]);
        // The deathSaves flag should be seeded as "0,0".
        Assert.NotNull(world.Flags);
        Assert.True(world.Flags!.ContainsKey("deathSaves"));
        Assert.Equal("0,0", world.Flags["deathSaves"].ToString());
        Assert.Contains("без сознания", result.Content);
    }

    [Fact]
    public async Task DealDamage_OnNpc_KillsAndRemovesFromTurnOrder()
    {
        var (reg, world) = MakeRegistry();
        var goblin = SpawnHostile(world, "Гоблин", hp: 5);
        await reg.ExecuteAsync("c1", "start_combat", "{}");

        var combat = world.Combat!;
        int countBefore = combat.TurnOrder.Count;

        var result = await reg.ExecuteAsync("c2", "deal_damage",
            $"{{\"target\":\"{goblin.Id}\",\"amount\":99}}");

        Assert.False(result.IsError);
        Assert.False(goblin.IsAlive);
        Assert.Equal(0, goblin.Resources["hp"]);
        // The goblin should have been removed from the turn order.
        Assert.DoesNotContain(combat.TurnOrder, c => c.EntityId == goblin.Id);
        Assert.Equal(countBefore - 1, combat.TurnOrder.Count);
        Assert.Contains("повержен", result.Content);
    }

    [Fact]
    public async Task DealDamage_AutoEndsCombat_WhenOnlyPlayerRemains()
    {
        var (reg, world) = MakeRegistry();
        var goblin = SpawnHostile(world, "Гоблин", hp: 5);
        await reg.ExecuteAsync("c1", "start_combat", "{}");
        Assert.NotNull(world.Combat);

        // Kill the only hostile → combat should auto-end.
        var result = await reg.ExecuteAsync("c2", "deal_damage",
            $"{{\"target\":\"{goblin.Id}\",\"amount\":99}}");

        Assert.False(result.IsError);
        Assert.Null(world.Combat);
        Assert.Contains("Бой окончен", result.Content);
    }

    [Fact]
    public async Task DeathSave_AtPositiveHP_ReturnsError()
    {
        var (reg, world) = MakeRegistry();
        var p = world.ActivePlayer!;
        // Ensure player has positive HP.
        p.Resources["hp"] = 10;

        var result = await reg.ExecuteAsync("c1", "death_save", "{}");
        Assert.True(result.IsError);
        Assert.Contains("не на 0 HP", result.Content);
    }

    [Fact]
    public async Task DeathSave_Stabilizes_AfterThreeSuccesses()
    {
        // Force the RNG so death_save rolls 10 (success) three times.
        // The Rng is a PCG32 — we can't directly inject a "10 next", so
        // we retry until we get three successes OR rig the world so the
        // player already has 2 successes going in.
        var (reg, world) = MakeRegistry();
        var p = world.ActivePlayer!;
        p.Resources["hp"] = 0;
        world.Flags ??= new Dictionary<string, object>();
        world.Flags["deathSaves"] = "2,0"; // 2 successes already.

        // Loop until we roll a 10+ to confirm stabilization, capping
        // attempts so the test can't infinite-loop on a bad seed.
        string lastContent = "";
        bool stabilized = false;
        for (int i = 0; i < 200; i++)
        {
            var result = await reg.ExecuteAsync("c1", "death_save", "{}");
            Assert.False(result.IsError);
            lastContent = result.Content;
            if (result.Content.Contains("стабилизирован"))
            {
                stabilized = true;
                break;
            }
            // If we died, the test setup is wrong.
            Assert.DoesNotContain("Игрок погиб", result.Content);
        }
        Assert.True(stabilized, $"Expected to stabilize within 200 rolls. Last: {lastContent}");
        // Player should still be alive but at 0 HP.
        Assert.True(p.IsAlive);
        Assert.Equal(0, p.Resources["hp"]);
    }

    [Fact]
    public async Task DeathSave_NaturalTwenty_RegainsOneHP()
    {
        var (reg, world) = MakeRegistry();
        var p = world.ActivePlayer!;
        p.Resources["hp"] = 0;
        world.Flags ??= new Dictionary<string, object>();
        world.Flags["deathSaves"] = "0,2"; // close to death.

        bool natural20 = false;
        for (int i = 0; i < 2000; i++)
        {
            // Reset state each iteration so we can keep trying for a
            // natural 20.
            p.Resources["hp"] = 0;
            world.Flags["deathSaves"] = "0,2";
            p.IsAlive = true;
            var result = await reg.ExecuteAsync("c1", "death_save", "{}");
            Assert.False(result.IsError);
            if (result.Content.Contains("Natural 20"))
            {
                natural20 = true;
                // After a nat 20, the player should be at 1 HP and
                // conscious (alive), and the deathSaves flag should be
                // cleared (no longer needed).
                Assert.Equal(1, p.Resources["hp"]);
                Assert.True(p.IsAlive);
                Assert.False(world.Flags.ContainsKey("deathSaves"));
                break;
            }
        }
        Assert.True(natural20, "Expected to roll a natural 20 within 2000 attempts.");
    }

    [Fact]
    public async Task DeathSave_ThreeFailures_KillsPlayer()
    {
        // Set the player up at 2 failures, then loop until we either
        // roll a 1 (2 failures → 4 clamped to 3 → dead) or accumulate
        // 3 failures through normal rolls.
        var (reg, world) = MakeRegistry();
        var p = world.ActivePlayer!;
        p.Resources["hp"] = 0;
        world.Flags ??= new Dictionary<string, object>();
        world.Flags["deathSaves"] = "0,2";

        bool died = false;
        for (int i = 0; i < 2000; i++)
        {
            if (!p.IsAlive)
            {
                died = true;
                break;
            }
            // Reset HP / saves for retry if a nat 20 happened.
            if (p.Resources.TryGetValue("hp", out var hp) && hp > 0)
            {
                p.Resources["hp"] = 0;
                world.Flags["deathSaves"] = "0,2";
            }
            var result = await reg.ExecuteAsync("c1", "death_save", "{}");
            Assert.False(result.IsError);
        }
        Assert.True(died, "Expected the player to die within 2000 attempts.");
        Assert.False(p.IsAlive);
        // The deathSaves flag should be cleared once the player dies
        // (no longer tracking — they're dead).
        Assert.False(world.Flags!.ContainsKey("deathSaves"));
    }

    [Fact]
    public async Task GetLocation_MarksDeadNpcsAsDead()
    {
        var (reg, world) = MakeRegistry();
        var goblin = SpawnHostile(world, "Гоблин", hp: 5);
        // Kill the goblin directly (no combat).
        goblin.IsAlive = false;

        var result = await reg.ExecuteAsync("c1", "get_location", "{}");
        Assert.False(result.IsError);
        Assert.Contains("Гоблин", result.Content);
        Assert.Contains("мёртв", result.Content);
    }

    [Fact]
    public void CombatState_SerializesAndRoundTrips()
    {
        var world = new GameWorld();
        var cs = new CombatState
        {
            Active = true,
            Round = 3,
            TurnOrder = new()
            {
                new Combatant(EntityId.NewId(), "Игрок", 18, true),
                new Combatant(EntityId.NewId(), "Гоблин", 12, false),
            },
            CurrentActorIndex = 1,
            StartedAtTurn = 7,
        };
        world.Combat = cs;

        var json = world.ToJson();
        var restored = GameWorld.FromJson(json, world.Registries);

        Assert.NotNull(restored.Combat);
        Assert.True(restored.Combat!.Active);
        Assert.Equal(3, restored.Combat.Round);
        Assert.Equal(2, restored.Combat.TurnOrder.Count);
        Assert.Equal("Игрок", restored.Combat.TurnOrder[0].Name);
        Assert.Equal("Гоблин", restored.Combat.TurnOrder[1].Name);
        Assert.Equal(18, restored.Combat.TurnOrder[0].Initiative);
        Assert.True(restored.Combat.TurnOrder[0].HasActedThisRound);
        Assert.False(restored.Combat.TurnOrder[1].HasActedThisRound);
        Assert.Equal(1, restored.Combat.CurrentActorIndex);
        Assert.Equal(7, restored.Combat.StartedAtTurn);
    }
}
