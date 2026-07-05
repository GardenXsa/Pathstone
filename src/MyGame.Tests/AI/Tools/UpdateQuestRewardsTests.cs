using System.Text.Json;
using MyGame.Core.AI.Tools;
using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Tests.AI.Tools;

/// <summary>
/// Unit tests for the <c>update_quest</c> tool's issue-#70 behavior:
/// completing a quest STAGES the reward in
/// <see cref="Quest.UnclaimedRewards"/> rather than granting it inline.
/// The player must claim via the Quest panel UI (the GameViewModel
/// handles the actual grant).
/// </summary>
public class UpdateQuestRewardsTests
{
    /// <summary>
    /// A fresh <see cref="Quest"/> has <see cref="Quest.UnclaimedRewards"/>
    /// = null. Old saves without the field also load with null (System.Text.Json
    /// defaults missing properties to null for nullable reference types).
    /// </summary>
    [Fact]
    public void Quest_UnclaimedRewards_DefaultsNull()
    {
        var q = new Quest();
        Assert.Null(q.UnclaimedRewards);
    }

    /// <summary>
    /// Calling <c>update_quest</c> with action=complete STAGES the
    /// reward in UnclaimedRewards (does NOT grant currency / XP / items
    /// to the player). The tool result text tells the player to go
    /// claim via the Quest panel.
    /// </summary>
    [Fact]
    public async Task UpdateQuest_Complete_StagesReward_NotGranted()
    {
        var world = DefaultWorld.Create(seed: 1);
        var player = world.ActivePlayer ?? world.Players.First();
        var currencyBefore = player.Inventory.Currency;
        var xpBefore = player.Experience ?? 0;
        var itemsBefore = player.Inventory.Items.Count;

        // Spawn a quest with a reward, set it Active so completion is
        // a meaningful transition.
        var quest = new Quest
        {
            Id = EntityId.NewId(),
            Name = "Тестовый квест",
            Status = QuestStatus.Active,
            Reward = new QuestReward
            {
                Currency = 100,
                Experience = 50,
                Items = new() { "item_potion_health" },
            },
        };
        world.Quests.Add(quest);

        var reg = new ToolRegistry(world);
        var args = $$"""{"questId":"{{quest.Id.Value}}","action":"complete"}""";
        var result = await reg.ExecuteAsync("call_1", "update_quest", args);

        // The tool result should be the "staged" message — NOT the old
        // "received N gold + N XP + item" message.
        Assert.False(result.IsError);
        Assert.Contains("выполнен", result.Content);
        Assert.Contains("Награда ожидает получения", result.Content);

        // Quest status changed to Completed.
        Assert.Equal(QuestStatus.Completed, quest.Status);

        // Reward staged in UnclaimedRewards (not granted to player).
        Assert.NotNull(quest.UnclaimedRewards);
        Assert.Equal(100, quest.UnclaimedRewards!.Currency);
        Assert.Equal(50, quest.UnclaimedRewards.Experience);

        // Player's currency / XP / items UNCHANGED — the reward is
        // staged, not granted.
        Assert.Equal(currencyBefore, player.Inventory.Currency);
        Assert.Equal(xpBefore, player.Experience ?? 0);
        Assert.Equal(itemsBefore, player.Inventory.Items.Count);
    }

    /// <summary>
    /// Completing a quest with NO reward still stages null in
    /// UnclaimedRewards (which is the same as the default — there's
    /// nothing to claim, so the Quest panel won't show the
    /// «Получить награду» button). This is the correct behavior: a
    /// rewardless quest just transitions to Completed, no claim step.
    /// </summary>
    [Fact]
    public async Task UpdateQuest_Complete_NoReward_StagesNull()
    {
        var world = DefaultWorld.Create(seed: 1);
        var quest = new Quest
        {
            Id = EntityId.NewId(),
            Name = "Без награды",
            Status = QuestStatus.Active,
            Reward = null,
        };
        world.Quests.Add(quest);

        var reg = new ToolRegistry(world);
        var args = $$"""{"questId":"{{quest.Id.Value}}","action":"complete"}""";
        var result = await reg.ExecuteAsync("call_1", "update_quest", args);

        Assert.False(result.IsError);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        // Reward was null → UnclaimedRewards is also null (nothing to claim).
        Assert.Null(quest.UnclaimedRewards);
    }

    /// <summary>
    /// The <see cref="Quest.UnclaimedRewards"/> field round-trips
    /// through JSON serialization (so saves persist the unclaimed state
    /// across reloads). When the field is null, it serializes to null
    /// (or omitted); when set, it round-trips the reward values.
    /// </summary>
    [Fact]
    public void Quest_UnclaimedRewards_JsonRoundTrip()
    {
        var q = new Quest
        {
            Id = EntityId.NewId(),
            Name = "Тест",
            UnclaimedRewards = new QuestReward
            {
                Currency = 250,
                Experience = 75,
                Items = new() { "item_potion_health", "item_dagger" },
            },
        };

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        var json = JsonSerializer.Serialize(q, opts);
        var restored = JsonSerializer.Deserialize<Quest>(json, opts);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.UnclaimedRewards);
        Assert.Equal(250, restored.UnclaimedRewards!.Currency);
        Assert.Equal(75, restored.UnclaimedRewards.Experience);
        Assert.Equal(2, restored.UnclaimedRewards.Items!.Count);
    }

    /// <summary>
    /// An old save (serialized without the UnclaimedRewards field)
    /// deserializes with UnclaimedRewards = null — backward-compatible
    /// (no migration needed).
    /// </summary>
    [Fact]
    public void Quest_OldSaveWithoutUnclaimedRewards_LoadsAsNull()
    {
        // A quest JSON without the unclaimedRewards field (mimics an
        // old save written before issue #70).
        var json = """
            {
              "id": "quest_old_1",
              "name": "Старый квест",
              "kind": "quest",
              "status": 2,
              "reward": {"currency": 50, "experience": 10}
            }
            """;
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        var restored = JsonSerializer.Deserialize<Quest>(json, opts);

        Assert.NotNull(restored);
        Assert.Null(restored!.UnclaimedRewards);
        // The reward itself DID load (it's the pre-#70 field).
        Assert.NotNull(restored.Reward);
        Assert.Equal(50, restored.Reward!.Currency);
    }
}
