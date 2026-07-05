using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

// 'World' is both a namespace and a type — alias to avoid the collision.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Core.Engine;

/// <summary>
/// Tracks player milestones and unlocks achievements. Issue #86.
/// Stored in World.Flags["achievements"] as JSON array of ids.
/// </summary>
public static class AchievementTracker
{
    private static readonly List<Achievement> _definitions = new()
    {
        new("first_combat", "Первый бой", "Участвуйте в первом бою",
            w => w.Combat is { Active: true } || w.Npcs.Any(n => !n.IsAlive)),
        new("first_quest", "Первый квест", "Завершите первый квест",
            w => w.Quests.Any(q => q.Status == QuestStatus.Completed)),
        new("explorer_10", "Исследователь", "Посетите 10 локаций",
            w => w.Locations.Count(l => l.Visited) >= 10),
        new("explorer_25", "Картограф", "Посетите 25 локаций",
            w => w.Locations.Count(l => l.Visited) >= 25),
        new("level_5", "Опытный", "Достигните 5-го уровня",
            w => (w.ActivePlayer?.Level ?? 0) >= 5),
        new("level_10", "Ветеран", "Достигните 10-го уровня",
            w => (w.ActivePlayer?.Level ?? 0) >= 10),
        new("wealthy", "Богач", "Накопите 500 золота",
            w => (w.ActivePlayer?.Inventory.Currency ?? 0) >= 500),
        new("slayer", "Убийца", "Победите 10 врагов",
            w => w.Npcs.Count(n => !n.IsAlive) >= 10),
    };

    public static List<Achievement> CheckMilestones(GameWorld world)
    {
        var unlocked = GetUnlocked(world);
        var newlyUnlocked = new List<Achievement>();
        foreach (var ach in _definitions)
        {
            if (unlocked.Contains(ach.Id)) continue;
            if (ach.Check(world))
            {
                newlyUnlocked.Add(ach);
                unlocked.Add(ach.Id);
            }
        }
        if (newlyUnlocked.Count > 0) SetUnlocked(world, unlocked);
        return newlyUnlocked;
    }

    public static List<string> GetUnlocked(GameWorld world)
    {
        if (world.Flags is null) return new();
        if (!world.Flags.TryGetValue("achievements", out var val)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(val.ToString() ?? "") ?? new(); }
        catch { return new(); }
    }

    public static void SetUnlocked(GameWorld world, List<string> ids)
    {
        world.Flags ??= new();
        world.Flags["achievements"] = JsonSerializer.Serialize(ids);
    }

    public static IReadOnlyList<Achievement> All => _definitions;
}

public sealed record Achievement(string Id, string Name, string Description, System.Func<GameWorld, bool> Check);
