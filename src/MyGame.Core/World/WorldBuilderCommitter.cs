using System.Text;
using MyGame.Core.AI;
using MyGame.Core.Common;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

namespace MyGame.Core.World;

/// <summary>
/// Deterministic plan→world committer. Takes a <see cref="WorldPlan"/>
/// (produced by the world-planner AI) and mutates a <see cref="World"/> to
/// match it: registers custom templates, creates locations and connects
/// them into a graph, spawns NPCs / buildings, grants the player starter
/// gear, sets the world title.
///
/// <para>
/// No AI calls here — this stage is pure data transformation. The planner
/// already made the creative decisions; the committer just materialises
/// them. This is intentionally simpler than the TS source's TODO-driven
/// single-agent loop (which needed <c>add_todo</c> / <c>mark_todo_done</c>
/// / <c>end_worldbuilding</c> tools and ~200 iterations). The desktop MVP
/// trades flexibility for reliability: one planner call + one deterministic
/// commit + one narrator call.
/// </para>
///
/// <para>
/// The committer is IDEMPOTENT in the sense that re-running it on a world
/// that already has the plan's locations will not duplicate them — it
/// matches by location name. This lets the orchestrator safely re-enter
/// the commit stage after a crash without creating a parallel world.
/// </para>
/// </summary>
public sealed class WorldBuilderCommitter
{
    private readonly World _world;
    private readonly ContentRegistry _registries;

    /// <summary>
    /// Map from plan location name → world Location, built during
    /// <see cref="CommitLocations"/>. Used by every later stage to resolve
    /// <c>PlanNpc.Location</c> / <c>PlanBuilding.Location</c> name
    /// references to real <see cref="EntityId"/>s.
    /// </summary>
    private readonly Dictionary<string, Location> _locationByName =
        new(StringComparer.Ordinal);

    public WorldBuilderCommitter(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _registries = world.Registries ?? throw new ArgumentException(
            "World.Registries must be wired before committing a plan.", nameof(world));
    }

    /// <summary>
    /// Commit the full plan to the world. Stages run in order:
    /// custom templates → locations → population → buildings → content →
    /// starter player → title. Each stage is fault-tolerant: a single
    /// broken entry is skipped (and counted in the result), the rest
    /// proceed.
    /// </summary>
    public CommitStats Commit(WorldPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        var stats = new CommitStats();

        // Stage A: register custom templates so later stages can reference
        // them by id (the planner may invent non-fantasy items / NPCs /
        // buildings for cyberpunk / sci-fi / custom settings).
        CommitCustomTemplates(plan, stats);

        // Stage B: create locations + connect exits. Must run before any
        // entity spawn — NPCs / buildings need a valid LocationId.
        CommitLocations(plan, stats);

        // Stage C: spawn planned NPCs at their locations.
        CommitPopulation(plan, stats);

        // Stage D: place planned buildings at their locations.
        CommitBuildings(plan, stats);

        // Stage E: grant starter gear + currency to the player. Also
        // creates the starter player if the world has none (e.g. fresh
        // build vs. host re-build).
        CommitContent(plan, stats);

        // Stage F: set the world title + atmosphere note. The title is the
        // primary display name in the UI ("Мир: «Тёмные Шпили Велариса»").
        CommitTitle(plan);

        return stats;
    }

    // ─── Stage A: custom templates ───────────────────────────────────────

    internal void CommitCustomTemplates(WorldPlan plan, CommitStats stats)
    {
        foreach (var tpl in plan.CustomItemTemplates ?? new())
        {
            try
            {
                _registries.Items.Register(ToItemTemplate(tpl));
                stats.CustomItems++;
            }
            catch (Exception ex)
            {
                stats.Errors.Add($"item template «{tpl.Id}»: {ex.Message}");
            }
        }

        foreach (var tpl in plan.CustomNpcTemplates ?? new())
        {
            try
            {
                _registries.Npcs.Register(ToNpcTemplate(tpl));
                stats.CustomNpcs++;
            }
            catch (Exception ex)
            {
                stats.Errors.Add($"npc template «{tpl.Id}»: {ex.Message}");
            }
        }

        foreach (var tpl in plan.CustomBuildingTemplates ?? new())
        {
            try
            {
                _registries.Buildings.Register(ToBuildingTemplate(tpl));
                stats.CustomBuildings++;
            }
            catch (Exception ex)
            {
                stats.Errors.Add($"building template «{tpl.Id}»: {ex.Message}");
            }
        }
    }

    private static ItemTemplate ToItemTemplate(PlanCustomItemTemplate t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Description = t.Description,
        Category = t.Category,
        Weight = t.Weight ?? 0.5,
        Value = t.Value ?? 0,
        Rarity = t.Rarity ?? "common",
        Stackable = false,
        Weapon = t.Weapon is null ? null : new WeaponSpec
        {
            Type = t.Weapon.Type,
            Damage = new Damage(t.Weapon.Damage.Dice, t.Weapon.Damage.Type),
            Finesse = t.Weapon.Finesse,
            TwoHanded = t.Weapon.TwoHanded,
            Range = t.Weapon.Range,
        },
        Armor = t.Armor is null ? null : new ArmorSpec
        {
            BaseAc = t.Armor.BaseAc,
            Type = t.Armor.Type,
            DexBonusMax = t.Armor.DexBonusMax,
            StealthDisadvantage = t.Armor.StealthDisadvantage,
        },
        Consumable = t.Consumable is null ? null : new ConsumableSpec
        {
            Healing = t.Consumable.Healing,
            Effects = t.Consumable.Effect is null ? null : new()
            {
                new() { Name = t.Consumable.Effect, Description = t.Consumable.Effect, Duration = 0 },
            },
        },
    };

    private static NpcTemplate ToNpcTemplate(PlanCustomNpcTemplate t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Race = t.Race,
        Class = t.Class,
        Level = t.Level,
        Attributes = t.Attributes ?? new(),
        Resources = t.Resources,
        Disposition = t.Disposition,
        Behavior = t.Behavior,
        Equipment = t.Equipment,
        Description = t.Description,
    };

    private static BuildingTemplate ToBuildingTemplate(PlanCustomBuildingTemplate t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Type = t.Type,
        Description = t.Description,
        Enterable = t.Enterable,
    };

    // ─── Stage B: locations ──────────────────────────────────────────────

    internal void CommitLocations(WorldPlan plan, CommitStats stats)
    {
        // First pass: create all locations (no exits yet — they reference
        // siblings that may not exist on a forward pass).
        foreach (var pl in plan.Locations ?? new())
        {
            if (string.IsNullOrWhiteSpace(pl.Name))
            {
                stats.Errors.Add("location with empty name — skipped");
                continue;
            }

            // Idempotency: if a location with this name already exists
            // (re-commit / hybrid with DefaultWorld), reuse it.
            var existing = _world.Locations.FirstOrDefault(l =>
                string.Equals(l.Name, pl.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _locationByName[pl.Name] = existing;
                continue;
            }

            var loc = new Location
            {
                Id = EntityId.NewId(),
                Name = pl.Name,
                Description = pl.Description ?? string.Empty,
                Terrain = string.IsNullOrWhiteSpace(pl.Terrain) ? "plains" : pl.Terrain,
                Danger = pl.Danger,
                // Mark the start location as visited/discovered so the
                // player UI shows it immediately.
                Visited = pl.Role == LocationRole.Start,
                Discovered = pl.Role == LocationRole.Start || pl.Role == LocationRole.Hub,
            };
            _world.AddLocation(loc);
            _locationByName[pl.Name] = loc;
            stats.Locations++;
        }

        // Second pass: wire up exits. PlanLocation.Connections is a list
        // of target location names; we make the connection bidirectional
        // unless the source already has an exit to the target.
        foreach (var pl in plan.Locations ?? new())
        {
            if (!_locationByName.TryGetValue(pl.Name, out var from)) continue;

            foreach (var targetName in pl.Connections ?? new())
            {
                if (!_locationByName.TryGetValue(targetName, out var to)) continue;
                if (from.Exits.Any(e => e.To == to.Id)) continue; // already wired

                var direction = ResolveDirection(pl.Name, targetName, pl.DirectionFromHub);
                from.Exits.Add(new LocationExit
                {
                    To = to.Id,
                    Direction = direction,
                    Locked = false,
                });

                // Bidirectional: if the target doesn't yet point back,
                // add a reverse exit with the opposite direction.
                if (!to.Exits.Any(e => e.To == from.Id))
                {
                    to.Exits.Add(new LocationExit
                    {
                        To = from.Id,
                        Direction = OppositeDirection(direction),
                        Locked = false,
                    });
                }
            }
        }
    }

    /// <summary>
    /// Resolve a human-readable direction label for an exit. If the plan
    /// gave a <c>DirectionFromHub</c> for the source location, use that;
    /// otherwise derive from the cardinal relationship between names (we
    /// don't have real coordinates — names are the only signal). Falls
    /// back to the target name itself as the label.
    /// </summary>
    private static string ResolveDirection(string fromName, string toName, string? directionFromHub)
    {
        if (!string.IsNullOrWhiteSpace(directionFromHub))
            return directionFromHub;
        // Heuristic: if the target name contains a direction word, use it.
        var lower = toName.ToLowerInvariant();
        if (lower.Contains("север")) return "север";
        if (lower.Contains("юг")) return "юг";
        if (lower.Contains("восток")) return "восток";
        if (lower.Contains("запад")) return "запад";
        if (lower.Contains("лес")) return "к лесу";
        if (lower.Contains("пещер")) return "к пещере";
        if (lower.Contains("руин")) return "к руинам";
        if (lower.Contains("деревн") || lower.Contains("город")) return "к поселению";
        return $"к «{toName}»";
    }

    private static string OppositeDirection(string direction)
    {
        var d = direction.ToLowerInvariant();
        if (d.Contains("север")) return "юг";
        if (d.Contains("юг")) return "север";
        if (d.Contains("восток")) return "запад";
        if (d.Contains("запад")) return "восток";
        if (d.Contains("вглубь")) return "наружу";
        if (d.Contains("наружу") || d.Contains("наверх")) return "внутрь";
        return $"к началу";
    }

    // ─── Stage C: population ─────────────────────────────────────────────

    internal void CommitPopulation(WorldPlan plan, CommitStats stats)
    {
        foreach (var pn in plan.Npcs ?? new())
        {
            if (string.IsNullOrWhiteSpace(pn.Name) || string.IsNullOrWhiteSpace(pn.Template))
            {
                stats.Errors.Add("npc with empty name/template — skipped");
                continue;
            }
            if (!_locationByName.TryGetValue(pn.Location, out var loc))
            {
                stats.Errors.Add($"npc «{pn.Name}»: location «{pn.Location}» not found — skipped");
                continue;
            }

            var npc = _world.SpawnNpcFromTemplate(pn.Template, loc.Id);
            if (npc is null)
            {
                stats.Errors.Add($"npc «{pn.Name}»: template «{pn.Template}» not found — skipped");
                continue;
            }

            // Apply plan overrides.
            npc.Name = pn.Name;
            if (pn.Level > 0) npc.Level = pn.Level;
            if (!string.IsNullOrWhiteSpace(pn.Disposition))
                SetFlag(npc, "disposition", pn.Disposition);
            if (!string.IsNullOrWhiteSpace(pn.Behavior))
                SetFlag(npc, "behavior", pn.Behavior);
            if (!string.IsNullOrWhiteSpace(pn.Role))
                SetFlag(npc, "role", pn.Role);
            if (!string.IsNullOrWhiteSpace(pn.Notes))
                SetFlag(npc, "notes", pn.Notes);

            stats.Npcs++;
        }
    }

    // ─── Stage D: buildings ──────────────────────────────────────────────

    internal void CommitBuildings(WorldPlan plan, CommitStats stats)
    {
        foreach (var pb in plan.Buildings ?? new())
        {
            if (string.IsNullOrWhiteSpace(pb.Template))
            {
                stats.Errors.Add("building with empty template — skipped");
                continue;
            }
            if (!_locationByName.TryGetValue(pb.Location, out var loc))
            {
                stats.Errors.Add($"building «{pb.Template}»: location «{pb.Location}» not found — skipped");
                continue;
            }

            var tpl = _registries.Buildings.Get(pb.Template);
            if (tpl is null)
            {
                stats.Errors.Add($"building template «{pb.Template}» not found — skipped");
                continue;
            }

            var building = EntityFactory.CreateBuilding(tpl, loc.Id);
            if (!string.IsNullOrWhiteSpace(pb.NameOverride))
                building.Name = pb.NameOverride;
            _world.SpawnBuilding(building);
            stats.Buildings++;
        }
    }

    // ─── Stage E: content + starter player ───────────────────────────────

    internal void CommitContent(WorldPlan plan, CommitStats stats)
    {
        // Ensure a starter player exists. The plan doesn't carry a player
        // object (the planner designs the world; the player is added by
        // the host flow). If the world has no player yet, create a
        // generic adventurer at the Start location (or the first location).
        var player = _world.ActivePlayer ?? _world.Players.FirstOrDefault();
        if (player is null)
        {
            var startLoc = (_locationByName.Values.FirstOrDefault(l =>
                l.Name.Equals("start", StringComparison.OrdinalIgnoreCase))
                ?? _locationByName.Values.FirstOrDefault())!;

            if (startLoc is not null)
            {
                player = EntityFactory.CreatePlayer(new()
                {
                    Name = "Странник",
                    Race = "human",
                    Class = "adventurer",
                    Level = 1,
                    LocationId = startLoc.Id,
                    StartingCurrency = plan.StarterCurrency > 0 ? plan.StarterCurrency : 25,
                    Background = plan.Setting,
                }, _world.Ruleset);
                _world.SpawnPlayer(player);
                stats.StarterPlayerCreated = true;
            }
        }
        else if (plan.StarterCurrency > 0)
        {
            player.Inventory.Currency += plan.StarterCurrency;
        }

        if (player is null) return;

        // Grant starter gear.
        foreach (var tplId in plan.StarterGear ?? new())
        {
            var tpl = _registries.Items.Get(tplId);
            if (tpl is null)
            {
                stats.Errors.Add($"starter gear «{tplId}»: template not found — skipped");
                continue;
            }
            var item = EntityFactory.InstantiateItem(tpl, 1);
            player.Inventory.Items.Add(item);
            stats.StarterItems++;
        }

        // If the plan gave starter currency and the player already existed,
        // we already added it above. For a fresh player, CreatePlayer used
        // it as the starting currency — don't double-count.
    }

    // ─── Stage F: title ──────────────────────────────────────────────────

    internal void CommitTitle(WorldPlan plan)
    {
        // World doesn't have a dedicated Title field in the current model;
        // we stash it in Flags so the UI can read it without changing the
        // schema. A future schema revision should promote this to a real
        // field.
        if (_world.Flags is null) _world.Flags = new();
        _world.Flags["worldTitle"] = plan.Title;
        _world.Flags["worldTheme"] = plan.Theme;
        _world.Flags["worldAtmosphere"] = plan.Atmosphere;
        _world.Flags["worldSetting"] = plan.Setting;
        _world.Flags["startingHook"] = plan.StartingHook;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static void SetFlag(Entity e, string key, string value)
    {
        e.Flags ??= new();
        e.Flags[key] = value;
    }
}

/// <summary>
/// Statistics from <see cref="WorldBuilderCommitter.Commit"/>. Returned to
/// the orchestrator so it can report "Created N locations, M NPCs, K
/// buildings" to the progress UI.
/// </summary>
public sealed class CommitStats
{
    public int CustomItems { get; set; }
    public int CustomNpcs { get; set; }
    public int CustomBuildings { get; set; }
    public int Locations { get; set; }
    public int Npcs { get; set; }
    public int Buildings { get; set; }
    public int StarterItems { get; set; }
    public bool StarterPlayerCreated { get; set; }

    public List<string> Errors { get; } = new();

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append($"Локаций: {Locations}, NPC: {Npcs}, Зданий: {Buildings}");
        if (CustomItems > 0 || CustomNpcs > 0 || CustomBuildings > 0)
            sb.Append($" | Кастомных шаблонов: {CustomItems}/{CustomNpcs}/{CustomBuildings}");
        if (StarterItems > 0)
            sb.Append($" | Стартовых предметов: {StarterItems}");
        if (StarterPlayerCreated)
            sb.Append(" | Создан стартовый игрок");
        if (Errors.Count > 0)
            sb.Append($" | Ошибок: {Errors.Count}");
        return sb.ToString();
    }
}
