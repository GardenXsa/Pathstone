using MyGame.Core.AI.Tools;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

// 'World' is both a namespace and a type — alias the type so the
// compiler doesn't see this as namespace usage when we type a variable
// as the World type. Matches the convention in CombatDeathToolsTests.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Tests.AI.Tools;

/// <summary>
/// ENGINE-DEPTH (issues #34 / #36 / #43): targeted unit tests for the
/// five new engine-depth tools — set_weather / get_weather /
/// adjust_reputation / get_factions / get_lore — plus the
/// WorldBuilderCommitter.CommitFactions + CommitLore stages. All tools
/// are optional (no-op on worlds that haven't activated the subsystem),
/// so these tests cover both the activated and the inactive paths.
/// </summary>
public class EngineDepthToolsTests
{
    private static (ToolRegistry reg, GameWorld world) MakeRegistry(long seed = 42)
    {
        var world = DefaultWorld.Create(seed: seed);
        return (new ToolRegistry(world), world);
    }

    // ─── Weather (issue #34) ──────────────────────────────────────────────

    [Fact]
    public async Task SetWeather_SetsCurrentWeatherAndForecast()
    {
        var (reg, world) = MakeRegistry();
        Assert.Null(world.CurrentWeather);

        var result = await reg.ExecuteAsync("c1", "set_weather",
            """{"weather":"rain","forecast":"К вечеру гроза"}""");
        Assert.False(result.IsError);
        Assert.Equal("rain", world.CurrentWeather);
        Assert.Equal("К вечеру гроза", world.WeatherForecast);
        Assert.Contains("Дождь", result.Content);
        Assert.Contains("К вечеру гроза", result.Content);
    }

    [Fact]
    public async Task SetWeather_NoForecast_Works()
    {
        var (reg, world) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "set_weather",
            """{"weather":"fog"}""");
        Assert.False(result.IsError);
        Assert.Equal("fog", world.CurrentWeather);
        Assert.Null(world.WeatherForecast);
        Assert.Contains("Туман", result.Content);
    }

    [Fact]
    public async Task SetWeather_InvalidWeather_ReturnsError()
    {
        var (reg, world) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "set_weather",
            """{"weather":"hurricane"}""");
        Assert.True(result.IsError);
        Assert.Null(world.CurrentWeather);
        Assert.Contains("hurricane", result.Content);
        // The valid list should be enumerated.
        Assert.Contains("clear", result.Content);
        Assert.Contains("rain", result.Content);
    }

    [Fact]
    public async Task SetWeather_MissingWeatherParam_ReturnsError()
    {
        var (reg, _) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "set_weather", "{}");
        Assert.True(result.IsError);
        Assert.Contains("weather", result.Content);
    }

    [Fact]
    public async Task SetWeather_NormalizesCase()
    {
        // Model may emit "Rain" or "RAIN" — normalize to lowercase.
        var (reg, world) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "set_weather",
            """{"weather":"RAIN"}""");
        Assert.False(result.IsError);
        Assert.Equal("rain", world.CurrentWeather);
    }

    [Fact]
    public async Task GetWeather_WhenNotSet_ReportsNoWeather()
    {
        var (reg, _) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "get_weather", "{}");
        Assert.False(result.IsError);
        Assert.Contains("не задана", result.Content);
    }

    [Fact]
    public async Task GetWeather_WhenSet_ReturnsLabelAndForecast()
    {
        var (reg, world) = MakeRegistry();
        world.CurrentWeather = "storm";
        world.WeatherForecast = "Ураган усиливается";

        var result = await reg.ExecuteAsync("c1", "get_weather", "{}");
        Assert.False(result.IsError);
        Assert.Contains("Гроза", result.Content);
        Assert.Contains("Ураган усиливается", result.Content);
    }

    [Theory]
    [InlineData("clear", "Ясно")]
    [InlineData("rain", "Дождь")]
    [InlineData("storm", "Гроза")]
    [InlineData("fog", "Туман")]
    [InlineData("snow", "Снег")]
    [InlineData("overcast", "Облачно")]
    public async Task SetWeather_AllCanonicalTags_Accepted(string tag, string label)
    {
        var (reg, world) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "set_weather",
            $$"""{"weather":"{{tag}}"}""");
        Assert.False(result.IsError);
        Assert.Equal(tag, world.CurrentWeather);
        Assert.Contains(label, result.Content);
    }

    // ─── Factions (issue #36) ─────────────────────────────────────────────

    private static GameWorld MakeWorldWithFactions()
    {
        var world = DefaultWorld.Create(seed: 1);
        world.Factions.Add(new Faction
        {
            Id = "silver_chalice",
            Name = "Орден Серебряной Чаши",
            Description = "Святые рыцари.",
            Alignment = "neutral",
            Reputation = 0,
            Type = "order",
        });
        world.Factions.Add(new Faction
        {
            Id = "thieves_guild",
            Name = "Гильдия Воров",
            Description = "Тёмные дельцы.",
            Alignment = "neutral",
            Reputation = 0,
            Type = "guild",
        });
        return world;
    }

    [Fact]
    public async Task AdjustReputation_PositiveDelta_ClampsAndUpdatesAlignment()
    {
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        var result = await reg.ExecuteAsync("c1", "adjust_reputation",
            """{"factionName":"Орден Серебряной Чаши","delta":40}""");
        Assert.False(result.IsError);
        var f = world.Factions.First(f => f.Name == "Орден Серебряной Чаши");
        Assert.Equal(40, f.Reputation);
        Assert.Equal("ally", f.Alignment);
        Assert.Contains("40", result.Content);
        Assert.Contains("ally", result.Content);
    }

    [Fact]
    public async Task AdjustReputation_NegativeDelta_MakesHostile()
    {
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        var result = await reg.ExecuteAsync("c1", "adjust_reputation",
            """{"factionName":"Гильдия Воров","delta":-50}""");
        Assert.False(result.IsError);
        var f = world.Factions.First(f => f.Name == "Гильдия Воров");
        Assert.Equal(-50, f.Reputation);
        Assert.Equal("hostile", f.Alignment);
        Assert.Contains("hostile", result.Content);
    }

    [Fact]
    public async Task AdjustReputation_BoundaryRep30_IsAlly()
    {
        // Rep == 30 should be ally (>= 30); rep == 29 should be neutral.
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        await reg.ExecuteAsync("c1", "adjust_reputation",
            """{"factionName":"Орден Серебряной Чаши","delta":30}""");
        var f = world.Factions.First(f => f.Name == "Орден Серебряной Чаши");
        Assert.Equal("ally", f.Alignment);
    }

    [Fact]
    public async Task AdjustReputation_BoundaryRepNeg30_IsHostile()
    {
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        await reg.ExecuteAsync("c1", "adjust_reputation",
            """{"factionName":"Орден Серебряной Чаши","delta":-30}""");
        var f = world.Factions.First(f => f.Name == "Орден Серебряной Чаши");
        Assert.Equal("hostile", f.Alignment);
    }

    [Fact]
    public async Task AdjustReputation_ClampsTo100()
    {
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        await reg.ExecuteAsync("c1", "adjust_reputation",
            """{"factionName":"Орден Серебряной Чаши","delta":250}""");
        var f = world.Factions.First(f => f.Name == "Орден Серебряной Чаши");
        Assert.Equal(100, f.Reputation);
    }

    [Fact]
    public async Task AdjustReputation_ClampsToNeg100()
    {
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        await reg.ExecuteAsync("c1", "adjust_reputation",
            """{"factionName":"Орден Серебряной Чаши","delta":-500}""");
        var f = world.Factions.First(f => f.Name == "Орден Серебряной Чаши");
        Assert.Equal(-100, f.Reputation);
    }

    [Fact]
    public async Task AdjustReputation_UnknownFaction_ReturnsErrorWithList()
    {
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        var result = await reg.ExecuteAsync("c1", "adjust_reputation",
            """{"factionName":"Несуществующая","delta":10}""");
        Assert.True(result.IsError);
        Assert.Contains("Несуществующая", result.Content);
        // The valid list should be enumerated in the error.
        Assert.Contains("Орден Серебряной Чаши", result.Content);
    }

    [Fact]
    public async Task GetFactions_EmptyWorld_ReportsNoFactions()
    {
        var (reg, _) = MakeRegistry();
        var result = await reg.ExecuteAsync("c1", "get_factions", "{}");
        Assert.False(result.IsError);
        Assert.Contains("нет фракций", result.Content);
    }

    [Fact]
    public async Task GetFactions_WithFactions_ListsAll()
    {
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        var result = await reg.ExecuteAsync("c1", "get_factions", "{}");
        Assert.False(result.IsError);
        Assert.Contains("Орден Серебряной Чаши", result.Content);
        Assert.Contains("Гильдия Воров", result.Content);
    }

    [Fact]
    public async Task AdjustReputation_MissingParams_ReturnsError()
    {
        var world = MakeWorldWithFactions();
        var reg = new ToolRegistry(world);

        var r1 = await reg.ExecuteAsync("c1", "adjust_reputation", "{}");
        Assert.True(r1.IsError);

        var r2 = await reg.ExecuteAsync("c1", "adjust_reputation",
            """{"factionName":"Орден Серебряной Чаши"}""");
        Assert.True(r2.IsError);
    }

    // ─── Lore (issue #43) ─────────────────────────────────────────────────

    private static GameWorld MakeWorldWithLore()
    {
        var world = DefaultWorld.Create(seed: 1);
        world.Lore = new LoreDatabase();
        world.Lore.Entries.Add(new LoreEntry
        {
            Topic = "history",
            Content = "Эпоха Туманов длилась 300 лет.",
        });
        world.Lore.Entries.Add(new LoreEntry
        {
            Topic = "magic",
            Content = "Магия берётся из лунного света.",
        });
        return world;
    }

    [Fact]
    public async Task GetLore_NoTopic_ReturnsAvailableTopics()
    {
        var world = MakeWorldWithLore();
        var reg = new ToolRegistry(world);

        var result = await reg.ExecuteAsync("c1", "get_lore", "{}");
        Assert.False(result.IsError);
        Assert.Contains("history", result.Content);
        Assert.Contains("magic", result.Content);
    }

    [Fact]
    public async Task GetLore_WithTopic_ReturnsEntry()
    {
        var world = MakeWorldWithLore();
        var reg = new ToolRegistry(world);

        var result = await reg.ExecuteAsync("c1", "get_lore",
            """{"topic":"history"}""");
        Assert.False(result.IsError);
        Assert.Contains("Эпоха Туманов", result.Content);
        Assert.Contains("history", result.Content);
    }

    [Fact]
    public async Task GetLore_UnknownTopic_ReturnsErrorWithList()
    {
        var world = MakeWorldWithLore();
        var reg = new ToolRegistry(world);

        var result = await reg.ExecuteAsync("c1", "get_lore",
            """{"topic":"deities"}""");
        Assert.True(result.IsError);
        Assert.Contains("deities", result.Content);
        // Valid topics should be enumerated.
        Assert.Contains("history", result.Content);
        Assert.Contains("magic", result.Content);
    }

    [Fact]
    public async Task GetLore_NoDatabase_ReportsNoLore()
    {
        var (reg, _) = MakeRegistry();
        // Default world has no lore DB.
        var result = await reg.ExecuteAsync("c1", "get_lore", "{}");
        Assert.False(result.IsError);
        Assert.Contains("нет базы лора", result.Content);
    }

    [Fact]
    public async Task GetLore_CaseInsensitiveTopic()
    {
        var world = MakeWorldWithLore();
        var reg = new ToolRegistry(world);

        var result = await reg.ExecuteAsync("c1", "get_lore",
            """{"topic":"HISTORY"}""");
        Assert.False(result.IsError);
        Assert.Contains("Эпоха Туманов", result.Content);
    }

    // ─── WorldBuilderCommitter stages ─────────────────────────────────────

    [Fact]
    public void CommitFactions_RegistersAllPlanFactions_NeutralStart()
    {
        var world = DefaultWorld.Create(seed: 1);
        var committer = new WorldBuilderCommitter(world);
        var plan = new MyGame.Core.AI.WorldPlan
        {
            Title = "T", Theme = "t", Setting = "s", Atmosphere = "a", StartingHook = "h",
            Factions = new()
            {
                new() { Name = "Орден Зари", Description = "Рыцари.", Alignment = "good", Type = "order" },
                new() { Name = "Тёмный Культ", Description = "Злодеи.", Alignment = "evil", Type = "cult" },
            },
        };

        var stats = new CommitStats();
        committer.CommitFactions(plan, stats);

        Assert.Equal(2, stats.Factions);
        Assert.Equal(2, world.Factions.Count);
        // All factions start neutral regardless of plan alignment.
        Assert.All(world.Factions, f => Assert.Equal("neutral", f.Alignment));
        Assert.All(world.Factions, f => Assert.Equal(0, f.Reputation));
        Assert.NotNull(world.Factions.FirstOrDefault(f => f.Name == "Орден Зари"));
        Assert.NotNull(world.Factions.FirstOrDefault(f => f.Name == "Тёмный Культ"));
    }

    [Fact]
    public void CommitFactions_Idempotent_NoDuplicatesOnRerun()
    {
        var world = DefaultWorld.Create(seed: 1);
        var committer = new WorldBuilderCommitter(world);
        var plan = new MyGame.Core.AI.WorldPlan
        {
            Title = "T", Theme = "t", Setting = "s", Atmosphere = "a", StartingHook = "h",
            Factions = new()
            {
                new() { Name = "Гильдия Купцов", Description = "Торговцы.", Alignment = "neutral" },
            },
        };

        var stats1 = new CommitStats();
        committer.CommitFactions(plan, stats1);
        Assert.Equal(1, stats1.Factions);
        Assert.Single(world.Factions);

        // Re-run — should skip the already-registered faction.
        var stats2 = new CommitStats();
        committer.CommitFactions(plan, stats2);
        Assert.Equal(0, stats2.Factions);
        Assert.Single(world.Factions);
    }

    [Fact]
    public void CommitFactions_EmptyFactionsList_NoOp()
    {
        var world = DefaultWorld.Create(seed: 1);
        var committer = new WorldBuilderCommitter(world);
        var plan = new MyGame.Core.AI.WorldPlan
        {
            Title = "T", Theme = "t", Setting = "s", Atmosphere = "a", StartingHook = "h",
            Factions = new(),
        };

        var stats = new CommitStats();
        committer.CommitFactions(plan, stats);
        Assert.Equal(0, stats.Factions);
        Assert.Empty(world.Factions);
    }

    [Fact]
    public void CommitLore_PopulatesEntriesFromPlan()
    {
        var world = DefaultWorld.Create(seed: 1);
        var committer = new WorldBuilderCommitter(world);
        var plan = new MyGame.Core.AI.WorldPlan
        {
            Title = "T", Theme = "t", Setting = "s", Atmosphere = "a", StartingHook = "h",
            Cosmology = new()
            {
                Origin = "Древние боги",
                Nature = "Плоский мир",
                HigherPowers = "Боги Зари",
                CosmicThreats = "Тёмные Лорды",
            },
            History = new()
            {
                new() { Name = "Эпоха Туманов", YearsAgo = "300 лет назад",
                    Events = new() { "Основание деревни" }, Legacy = "Туманы до сих пор." },
            },
            MagicSystem = new()
            {
                Type = "Природная",
                Source = "Лунный свет",
                Wielders = "Жрицы",
                Limits = "Только ночью",
                PublicAttitude = "Страх",
            },
            Cultures = new()
            {
                new() { Name = "Туманники", Race = "human", Customs = "Молятся луне",
                    AttitudeToOutsiders = "Подозрительно" },
            },
            Economy = new()
            {
                Currencies = "Серебро",
                TradeRoutes = "Тракт",
                KeyGoods = "Меха и травы",
                MajorGuilds = "Гильдия Купцов",
                Prosperity = "Бедное",
            },
            CurrentEvents = "Сейчас идёт война на севере.",
        };

        var stats = new CommitStats();
        committer.CommitLore(plan, stats);

        Assert.Equal(6, stats.LoreEntries);
        Assert.NotNull(world.Lore);
        Assert.Equal(6, world.Lore!.Entries.Count);
        Assert.NotNull(world.Lore.Get("deities"));
        Assert.NotNull(world.Lore.Get("history"));
        Assert.NotNull(world.Lore.Get("magic"));
        Assert.NotNull(world.Lore.Get("cultures"));
        Assert.NotNull(world.Lore.Get("economy"));
        Assert.NotNull(world.Lore.Get("current events"));
        Assert.Contains("Лунный свет", world.Lore.Get("magic")!.Content);
        Assert.Contains("война", world.Lore.Get("current events")!.Content);
    }

    [Fact]
    public void CommitLore_EmptyPlan_LeavesLoreNull()
    {
        var world = DefaultWorld.Create(seed: 1);
        var committer = new WorldBuilderCommitter(world);
        var plan = new MyGame.Core.AI.WorldPlan
        {
            Title = "T", Theme = "t", Setting = "s", Atmosphere = "a", StartingHook = "h",
            // No lore fields — Lore should stay null.
        };

        var stats = new CommitStats();
        committer.CommitLore(plan, stats);
        Assert.Equal(0, stats.LoreEntries);
        Assert.Null(world.Lore);
    }

    // ─── Day/night helper (issue #35) ─────────────────────────────────────

    [Theory]
    [InlineData(0, "ночь")]
    [InlineData(4, "ночь")]
    [InlineData(5, "рассвет")]
    [InlineData(7, "рассвет")]
    [InlineData(8, "утро")]
    [InlineData(11, "утро")]
    [InlineData(12, "день")]
    [InlineData(16, "день")]
    [InlineData(17, "вечер")]
    [InlineData(19, "вечер")]
    [InlineData(20, "ночь")]
    [InlineData(23, "ночь")]
    public void GetTimeOfDayLabel_CoversAllBuckets(int hour, string expected)
    {
        Assert.Equal(expected, GameTime.GetTimeOfDayLabel(hour));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(12, false)]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(23, true)]
    public void IsNight_MatchesBoundary(int hour, bool expected)
    {
        Assert.Equal(expected, GameTime.IsNight(hour));
    }

    [Fact]
    public void GameTime_ToDisplayWithTimeOfDay_AppendsLabel()
    {
        var t = new GameTime(3, 14, 0);
        Assert.Equal("День 3, 14:00 — день", t.ToDisplayWithTimeOfDay());

        var night = new GameTime(3, 22, 30);
        Assert.Equal("День 3, 22:30 — ночь", night.ToDisplayWithTimeOfDay());
    }
}
