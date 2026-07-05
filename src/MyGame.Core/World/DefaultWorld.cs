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
        //
        // The valley is laid out on a coarse integer grid with the village
        // at (0,0). The existing 6 locations form a small cross; the new
        // 8 add a southern road + cemetery, an eastern crossroads hub with
        // a farm, a western watchtower + forest lake + mountain pass, and
        // an eastern old mine off the cave. Every connection is cardinal
        // (север/юг/восток/запад/вглубь/наружу) so the Connect helper's
        // reverse-direction switch covers it.
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

        // New: surrounding wilderness ring (8 locations, 14 total).
        var tower = NewLocation("Сторожевая башня",
            "Старая каменная башня на западном холме. Деревянный настил её верхней площадки ещё держит, и с него видно всю Долину Туманов — от перевала до пещер.",
            "landmark", -1, 0, danger: 0);
        var lake = NewLocation("Лесное озеро",
            "Тихий разлив ручья среди елей. Вода чёрная, гладкая, и иногда по ней бегут круги — не от ветра. На берегу — хижина отшельника.",
            "coast", -1, 1, danger: 1);
        var crossroads = NewLocation("Развилка",
            "Перекрёсток трёх дорог: на север — к деревне, на юг — к ферме, на запад — к руинам. Покосившийся дорожный столб, заросший мхом.",
            "road", 1, -1, danger: 0);
        var farm = NewLocation("Заброшенная ферма",
            "Дряхлая изба с провалившейся крышей и заросшее бурьяном поле. На заборе сохнет тряпка — то ли память о хозяине, то ли сигнал разбойникам.",
            "farmland", 1, -2, danger: 2);
        var southRoad = NewLocation("Тракт на юг",
            "Старая мощёная дорога, уходящая на юг мимо кладбища. Камни местами выворочены, но путь ещё различим.",
            "road", 0, -2, danger: 1);
        var cemetery = NewLocation("Старое кладбище",
            "Покосившиеся надгробия, склеп с ржавой решёткой, чья-то свежая яма. По ночам здесь, говорят, танцуют бледные огни.",
            "ruin", 0, -3, danger: 3);
        var pass = NewLocation("Перевал",
            "Каменистая тропа, вьющаяся между скал к западу от руин. Ветер здесь злой и холодный — он несёт запах снежных вершин.",
            "mountain", -1, -1, danger: 3);
        var oldMine = NewLocation("Старая шахта",
            "Брошенный наклонник с гнилыми крепями. В тьме поблёскивает слюда — и чьи-то глаза. Из глубины доносится стук кирки, хотя никого живого тут не осталось.",
            "underground", 1, 2, danger: 4);

        // Connect them (bidirectional).
        // Existing cross:
        Connect(village, forest, "север");
        Connect(village, river, "восток");
        Connect(village, ruins, "юг");
        Connect(forest, cave, "север");
        Connect(cave, deepCave, "вглубь");
        ruins.Exits.Add(new LocationExit { To = ruins.Id, Direction = "вниз (locked)", Locked = true });

        // New connections (cardinal — see Connect helper for reverse-dir
        // switch). All exits are unique per-location so the WorldPanel
        // can list them without ambiguity.
        Connect(village, tower, "запад");         // village ↔ tower
        Connect(forest, lake, "запад");           // forest  ↔ lake
        Connect(cave, oldMine, "восток");         // cave    ↔ oldMine
        Connect(ruins, crossroads, "восток");     // ruins   ↔ crossroads
        Connect(crossroads, farm, "юг");          // xroads  ↔ farm
        Connect(ruins, southRoad, "юг");          // ruins   ↔ southRoad
        Connect(southRoad, cemetery, "юг");       // sRoad   ↔ cemetery
        Connect(ruins, pass, "запад");            // ruins   ↔ pass

        world.AddLocation(village);
        world.AddLocation(forest);
        world.AddLocation(river);
        world.AddLocation(ruins);
        world.AddLocation(cave);
        world.AddLocation(deepCave);
        world.AddLocation(tower);
        world.AddLocation(lake);
        world.AddLocation(crossroads);
        world.AddLocation(farm);
        world.AddLocation(southRoad);
        world.AddLocation(cemetery);
        world.AddLocation(pass);
        world.AddLocation(oldMine);

        village.Visited = true;
        village.Discovered = true;
        forest.Discovered = true;   // visible on the map but not yet visited
        tower.Discovered = true;    // seen from the village square
        river.Discovered = true;    // the ford is visible from the village
        southRoad.Discovered = true;// the southern road is visible from the village

        // ─── Buildings in the village ──────────────────────────────────────
        SpawnBuildingFromTemplate(world, "bld_tavern", village.Id, registries);
        SpawnBuildingFromTemplate(world, "bld_smithy", village.Id, registries);
        SpawnBuildingFromTemplate(world, "bld_temple", village.Id, registries);
        SpawnBuildingFromTemplate(world, "bld_guard_tower", village.Id, registries);

        // Watchtower (a separate landmark location): a small fortress
        // building with a guard occupant. The guard gives the
        // "Зачистить шахту" quest below.
        SpawnBuildingFromTemplate(world, "bld_guard_tower", tower.Id, registries);

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

        // ─── New NPCs at the expanded locations ────────────────────────────
        //
        // Watchtower: a town guard (already spawned as the building's
        // occupant above — the guard tower template lists npc_town_guard
        // as an occupant). Find them and store the id for the quest giver
        // field below. (The guard is at the tower location, not the
        // village — the village's bld_guard_tower also spawned a guard,
        // but we want the tower one for this quest.)
        var towerGuard = world.Npcs.FirstOrDefault(n =>
            n.LocationId == tower.Id && n.TemplateId == "npc_town_guard");

        // Lake: a wandering merchant by the water (the closest thing to
        // a "hermit" the content pack offers — a lone traveler camped
        // out by the lake). Gives the "Найти пропавшего караванщика"
        // quest below.
        var lakeHermit = world.SpawnNpcFromTemplate("npc_merchant_traveler", lake.Id);

        // Crossroads: a second merchant (the merchant's colleague, asking
        // passers-by if they've seen the missing caravan).
        // — Actually to keep quest-giver discovery simple, the same lake
        // hermit gives the caravan quest. No NPC here; the crossroads is
        // a narrative anchor only.

        // Farm: a bandit ambush.
        world.SpawnNpcFromTemplate("npc_bandit", farm.Id);
        world.SpawnNpcFromTemplate("npc_bandit", farm.Id);

        // Cemetery: an undead (a ghost — same template, different
        // location, suggests "the dead walk here too").
        world.SpawnNpcFromTemplate("npc_ghost", cemetery.Id);

        // Old mine: goblins + an owlbear (a tougher monster for the
        // "Зачистить шахту" quest target).
        world.SpawnNpcFromTemplate("npc_goblin", oldMine.Id);
        world.SpawnNpcFromTemplate("npc_goblin", oldMine.Id);
        world.SpawnNpcFromTemplate("npc_owl_bear_cub", oldMine.Id);

        // Mountain pass: a wolf (mountain wolf).
        world.SpawnNpcFromTemplate("npc_wolf", pass.Id);

        // ─── Quests ────────────────────────────────────────────────────────
        //
        // Existing: "Потерянный амулет" — given by the village elder.
        // New: "Зачистить шахту" — given by the watchtower guard.
        // New: "Найти пропавшего караванщика" — given by the lake hermit.

        // Quest 1: Lost amulet (existing — keep).
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

        // Quest 2: Clear the old mine — given by the watchtower guard.
        // Only add the quest if the guard actually spawned (defensive —
        // if bld_guard_tower didn't list npc_town_guard as an occupant,
        // towerGuard would be null).
        if (towerGuard is not null)
        {
            var mineQuest = new Quest
            {
                Id = EntityId.NewId(),
                Name = "Зачистить шахту",
                Description = "Страж с башни просит зачистить старую шахту к востоку от пещеры — гоблины и совомедведь сделали оттуда логово и угрожают тракту.",
                GiverNpcId = towerGuard.Id,
                Status = QuestStatus.Active,
                Objectives = new()
                {
                    new() { Id = "obj_mine_kill_goblins", Description = "Уничтожить гоблинов в Старой шахте" },
                    new() { Id = "obj_mine_kill_beast", Description = "Справиться с совомедведем в шахте" },
                    new() { Id = "obj_mine_report", Description = "Доложить стражу на башне об успехе" },
                },
                Reward = new QuestReward
                {
                    Currency = 75,
                    Experience = 150,
                    Items = new() { "tre_gem_ruby" },
                },
            };
            world.Quests.Add(mineQuest);
        }

        // Quest 3: Find the missing caravaneer — given by the lake hermit.
        if (lakeHermit is not null)
        {
            var caravanQuest = new Quest
            {
                Id = EntityId.NewId(),
                Name = "Найти пропавшего караванщика",
                Description = "Странствующий торговец у лесного озера просит разузнать судьбу своего напарника — караван пропал по тракту на юг, и последним местом, где его видели, была Развилка.",
                GiverNpcId = lakeHermit.Id,
                Status = QuestStatus.Active,
                Objectives = new()
                {
                    new() { Id = "obj_caravan_xroads", Description = "Осмотреть Развилку на следы каравана" },
                    new() { Id = "obj_caravan_farm", Description = "Проверить Заброшенную ферму — возможно, разбойники причастны" },
                    new() { Id = "obj_caravan_return", Description = "Вернуться к торговцу у озера с вестями" },
                },
                Reward = new QuestReward
                {
                    Currency = 60,
                    Experience = 120,
                    Items = new() { "misc_scroll_map" },
                },
            };
            world.Quests.Add(caravanQuest);
        }

        // ─── No starting player ───────────────────────────────────────────
        // The world is created WITHOUT a player. The player is created on
        // the CharacterCreation screen (single-player + host) or the
        // multiplayer character-create step, where the user picks name /
        // race / class / background — and the class profile grants the
        // appropriate starter gear (see CharacterCreationViewModel.
        // GetClassProfile). Pre-equipping an auto-player «Странник» here
        // would bypass the user's class choice and ignore the
        // StartSceneAgent's role-appropriate setup (issue #106).
        //
        // The village is still marked Visited/Discovered so the
        // CharacterCreation screen can default the starting location to
        // the village (the most sensible spawn point for this world).

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
