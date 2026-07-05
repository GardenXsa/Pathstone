using MyGame.Core.Common;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

namespace MyGame.Core.World;

/// <summary>
/// Factory that creates a fresh starting <see cref="World"/> for the "New
/// Game" flow.
///
/// Port of <c>engine/content/defaultWorld.ts</c>. The TS file is 900+ lines
/// and builds «Эльдрион» — a continent-sized hand-authored world with 5
/// regions, hundreds of NPCs, and procedural-generation hooks. That's
/// tightly coupled to the AI planner (<c>WorldPlan</c> types) and the
/// procedural engine (<c>procedural.ts</c>) which don't exist in the C# port
/// yet. Per the task spec ("If a TS file is huge (&gt;500 lines) or has
/// features clearly tied to the Next.js app, SKIP those parts"), the
/// C# port instead builds a SMALL hand-authored starting scene — the
/// classic «Долина Туманов» village + a few surrounding locations,
/// populated from the content pack in <c>Content/data.json</c>.
///
/// This is enough for the desktop app's MVP "New Game" experience: the
/// player wakes up in a tavern village, can explore the surrounding forest
/// and caves, talk to a few NPCs, and pick up a starting quest. The AI
/// world-builder subsystem (a later task) replaces this with bespoke worlds.
/// </summary>
public static class DefaultWorld
{
    /// <summary>
    /// Create a fresh starting World. The seed defaults to a fixed value
    /// (12345) so the same New Game always produces the same starting scene;
    /// callers can override it for "random New Game".
    /// </summary>
    public static World Create(long seed = 12345)
    {
        var registries = ContentRegistry.LoadDefault();
        var ruleset = Rulesets.DefaultDnd;
        var calendar = Calendar.DefaultFantasyCalendar;

        var world = new World
        {
            Seed = seed,
            Rng = new Rng(seed),
            Clock = GameTime.Start,
            CalendarSpec = calendar,
            Ruleset = ruleset,
            Turn = 0,
        };
        // Wire the content registry onto the world so SpawnNpcFromTemplate
        // can look up templates. (The World.Registries property defaults to
        // an empty registry when unset, so without this line the spawns
        // would silently no-op.)
        world.Registries = registries;

        // ─── Locations (small village + surrounding wilderness) ────────────
        var village = NewLocation("Деревня Туманная",
            "Небольшая деревня у реки, окружённая туманными лесами. Дымок из труб, лай собак, скрип колодезного журавля.",
            "village", 0, 0, danger: 0);
        var forest = NewLocation("Северный Лес",
            "Густой хвойный лес. Туман здесь гуще, а под ногами хрустят то ветки, то кости.",
            "forest", 0, 1, danger: 2);
        var river = NewLocation("Брод через Туманную",
            "Мелководный брод через реку Туманную. Вода холодная, течение быстрое, на том берегу видны камни старого тракта.",
            "coast", 1, 0, danger: 1);
        var ruins = NewLocation("Древние руины",
            "Обломки каменной кладки, поросшие мхом. Под ними, по слухам, скрывается гробница первых жителей долины.",
            "ruin", 0, -1, danger: 4);
        var cave = NewLocation("Вход в пещеру",
            "Тёмный провал в скале, из которого тянет холодом и сыростью. На камнях у входа — старые кости и следы когтей.",
            "cave", 0, 2, danger: 5);
        var deepCave = NewLocation("Глубокая пещера",
            "Сырой извилистый туннель, уходящий в недра холма. Где-то в темноте журчит вода и слышно дыхание чего-то крупного.",
            "underground", 0, 3, danger: 7);

        // Connect them (bidirectional).
        Connect(village, forest, "север");
        Connect(village, river, "восток");
        Connect(village, ruins, "юг");
        Connect(forest, cave, "север");
        Connect(cave, deepCave, "вглубь");
        ruins.Exits.Add(new LocationExit { To = ruins.Id, Direction = "вниз (locked)", Locked = true });

        world.AddLocation(village);
        world.AddLocation(forest);
        world.AddLocation(river);
        world.AddLocation(ruins);
        world.AddLocation(cave);
        world.AddLocation(deepCave);

        village.Visited = true;
        village.Discovered = true;
        forest.Discovered = true;   // visible on the map but not yet visited

        // ─── Buildings in the village ──────────────────────────────────────
        SpawnBuildingFromTemplate(world, "bld_tavern", village.Id, registries);
        SpawnBuildingFromTemplate(world, "bld_smithy", village.Id, registries);
        SpawnBuildingFromTemplate(world, "bld_temple", village.Id, registries);
        SpawnBuildingFromTemplate(world, "bld_guard_tower", village.Id, registries);

        // ─── NPCs at locations ─────────────────────────────────────────────
        // Village elder — quest giver.
        world.SpawnNpcFromTemplate("npc_village_elder", village.Id);
        // Tavern keeper, blacksmith, priest — spawned via building occupants.
        // (SpawnBuildingFromTemplate above already spawned them and wired them
        // to their buildings; the spawn helper also adds them to the village
        // location's NPC list.)

        // Wilderness: a wolf pack in the forest.
        world.SpawnNpcFromTemplate("npc_wolf", forest.Id);
        world.SpawnNpcFromTemplate("npc_wolf", forest.Id);

        // Cave: goblins.
        world.SpawnNpcFromTemplate("npc_goblin", cave.Id);
        world.SpawnNpcFromTemplate("npc_goblin", cave.Id);
        world.SpawnNpcFromTemplate("npc_goblin", deepCave.Id);

        // Ruins: a ghost (carrying the warden letter quest item).
        world.SpawnNpcFromTemplate("npc_ghost", ruins.Id);

        // Deep cave: an owlbear cub.
        world.SpawnNpcFromTemplate("npc_owl_bear_cub", deepCave.Id);

        // ─── A starting quest from the elder ───────────────────────────────
        var amuletItem = registries.Items.Get("qst_lost_amulet");
        var quest = new Quest
        {
            Id = EntityId.NewId(),
            Name = "Потерянный амулет",
            Description = "Старейшина Олдрин просит найти фамильный амулет, украденный гоблинами из пещеры.",
            GiverNpcId = world.Npcs.First(n => n.TemplateId == "npc_village_elder").Id,
            Status = QuestStatus.Active,
            Objectives = new()
            {
                new() { Id = "obj_find_amulet", Description = "Найти потерянный амулет в пещере гоблинов" },
                new() { Id = "obj_return_amulet", Description = "Вернуть амулет старейшине Олдрину" },
            },
            Reward = new QuestReward { Currency = 50, Experience = 100, Items = amuletItem is null ? null : new() { amuletItem.Id } },
        };
        world.Quests.Add(quest);

        // ─── Starting player ───────────────────────────────────────────────
        var player = EntityFactory.CreatePlayer(new()
        {
            Name = "Странник",
            Race = "human",
            Class = "adventurer",
            Level = 1,
            LocationId = village.Id,
            StartingCurrency = 25,
            Background = "Бродяга, пришедший в Долину Туманов с караваном. Цели — туманны, меч — острый.",
        }, ruleset);

        // Equip the player with a basic shortsword and leather armor.
        if (registries.Items.Get("wpn_shortsword") is { } sword)
            player.Equipped["weapon"] = EntityFactory.InstantiateItem(sword);
        if (registries.Items.Get("arm_leather") is { } armor)
            player.Equipped["armor"] = EntityFactory.InstantiateItem(armor);

        // Starting inventory: a health potion + a torch + a ration.
        foreach (var (tplId, qty) in new[] { ("cns_health_potion", 2), ("tool_torch", 3), ("cns_ration", 5) })
        {
            if (registries.Items.Get(tplId) is { } t)
                player.Inventory.Items.Add(EntityFactory.InstantiateItem(t, qty));
        }

        world.SpawnPlayer(player);

        return world;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static Location NewLocation(
        string name, string description, string terrain,
        int x, int y, int danger) =>
        new()
        {
            Id = EntityId.NewId(),
            Name = name,
            Description = description,
            Terrain = terrain,
            X = x,
            Y = y,
            Danger = danger,
            Visited = false,
            Discovered = false,
        };

    private static void Connect(Location from, Location to, string direction)
    {
        from.Exits.Add(new LocationExit { To = to.Id, Direction = direction });
        // Reverse direction — naive but fine for a hand-authored scene.
        string back = direction switch
        {
            "север" => "юг",
            "юг" => "север",
            "восток" => "запад",
            "запад" => "восток",
            "вглубь" => "наружу",
            _ => "назад",
        };
        to.Exits.Add(new LocationExit { To = from.Id, Direction = back });
    }

    private static Building SpawnBuildingFromTemplate(
        World world, string templateId, EntityId locationId, ContentRegistry registries)
    {
        var tpl = registries.Buildings.Get(templateId)
            ?? throw new InvalidOperationException($"Unknown building template: {templateId}");
        var building = EntityFactory.CreateBuilding(tpl, locationId);
        world.SpawnBuilding(building);

        // Spawn occupant templates (and wire them as shopkeepers / occupants).
        if (tpl.OccupantTemplateIds is not null)
        {
            foreach (var npcTplId in tpl.OccupantTemplateIds)
            {
                var npc = world.SpawnNpcFromTemplate(npcTplId, locationId);
                if (npc is not null)
                {
                    building.Occupants.Add(npc.Id);
                }
            }
        }
        if (tpl.ShopkeeperTemplateId is { } shopId)
        {
            // If the shopkeeper wasn't already an occupant, spawn them too.
            if (!building.Occupants.Any(id => world.GetNpc(id)?.TemplateId == shopId))
            {
                var shopNpc = world.SpawnNpcFromTemplate(shopId, locationId);
                if (shopNpc is not null)
                {
                    building.Occupants.Add(shopNpc.Id);
                    building.ShopkeeperNpcId = shopNpc.Id;
                }
            }
            else
            {
                // The occupant we already spawned is the shopkeeper.
                building.ShopkeeperNpcId = building.Occupants
                    .FirstOrDefault(id => world.GetNpc(id)?.TemplateId == shopId);
            }
        }
        return building;
    }
}
