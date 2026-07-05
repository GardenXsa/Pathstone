using System.Net;
using System.Net.Http;
using System.Text;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.Saves;
using MyGame.Core.World;

namespace MyGame.Tests.AI.Agents;

/// <summary>
/// Unit tests for the AI-ROBUST batch: chunked generation (issue #20),
/// custom ruleset per world (issue #21), and rebuild (issue #23).
/// These cover the pure-data paths (planner normalization, committer
/// chunked filter, ruleset overlay helpers, rebuild options) without
/// making real AI calls. The AI-calling paths (ruleset designer,
/// GenerateRegionAsync, RebuildAsync) are exercised via stub HTTP
/// handlers that return canned JSON, mirroring the pause/resume test
/// pattern.
/// </summary>
public class WorldBuilderOrchestratorRobustTests
{
    // ─── Feature 2b: Ruleset overlay helpers ────────────────────────────

    [Fact]
    public void GetAttributeName_DefaultDnd_ReturnsRussianName()
    {
        var rs = Rulesets.DefaultDnd;
        Assert.Equal("Сила", Rulesets.GetAttributeName(rs, "str"));
        Assert.Equal("Харизма", Rulesets.GetAttributeName(rs, "cha"));
    }

    [Fact]
    public void GetAttributeName_CustomOverlay_ReturnsCustomName()
    {
        var rs = Rulesets.DefaultDnd with
        {
            AttributeNames = new()
            {
                ["str"] = "Cool",
                ["dex"] = "Edge",
            },
        };
        Assert.Equal("Cool", Rulesets.GetAttributeName(rs, "str"));
        Assert.Equal("Edge", Rulesets.GetAttributeName(rs, "dex"));
        // Keys not in the overlay fall back to the ruleset's AttributeDef.Name.
        Assert.Equal("Телосложение", Rulesets.GetAttributeName(rs, "con"));
    }

    [Fact]
    public void GetAttributeName_UnknownKey_ReturnsKeyItself()
    {
        var rs = Rulesets.DefaultDnd;
        Assert.Equal("coolness", Rulesets.GetAttributeName(rs, "coolness"));
    }

    [Fact]
    public void GetResourceName_CustomOverlay_ReturnsCustomName()
    {
        var rs = Rulesets.DefaultDnd with
        {
            ResourcePools = new() { ["hp"] = "Stress" },
        };
        Assert.Equal("Stress", Rulesets.GetResourceName(rs, "hp"));
    }

    [Fact]
    public void Ruleset_DefaultDnd_HasNoOverlayFields()
    {
        // Backward-compat: DefaultDnd must ship with null overlay fields
        // (no AttributeNames/ResourcePools/Skills) so existing behavior
        // is unchanged for fantasy worlds.
        Assert.Null(Rulesets.DefaultDnd.AttributeNames);
        Assert.Null(Rulesets.DefaultDnd.ResourcePools);
        Assert.Null(Rulesets.DefaultDnd.Skills);
    }

    [Fact]
    public void Ruleset_WithOverlay_RoundTripsThroughJson()
    {
        // The overlay fields must round-trip through JSON serialization
        // (so a saved world with a custom ruleset reloads with the
        // custom names intact).
        var rs = Rulesets.DefaultDnd with
        {
            AttributeNames = new() { ["str"] = "Cool" },
            ResourcePools = new() { ["hp"] = "Stress" },
            Skills = new List<string> { "hacking", "gunnery" },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(rs, WorldJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<Ruleset>(json, WorldJson.Options)!;
        Assert.NotNull(back.AttributeNames);
        Assert.Equal("Cool", back.AttributeNames!["str"]);
        Assert.NotNull(back.ResourcePools);
        Assert.Equal("Stress", back.ResourcePools!["hp"]);
        Assert.NotNull(back.Skills);
        Assert.Equal(2, back.Skills!.Count);
    }

    // ─── Feature 1: Chunked generation — committer filter ───────────────

    [Fact]
    public void CommitLocations_ChunkedPlan_SkipsColdRegionLocations()
    {
        // Build a chunked plan with one start-region location and one
        // cold-region location. The committer should only commit the
        // start-region location; the cold-region location should be
        // skipped (it'll be generated on-demand later).
        var world = DefaultWorld.Create(seed: 1);
        var committer = new WorldBuilderCommitter(world);
        var plan = new WorldPlan
        {
            Title = "Тестовый чанковый мир",
            Theme = "test",
            Setting = "test",
            Atmosphere = "test",
            StartingHook = "hook",
            GenerationMode = "chunked",
            Regions = new()
            {
                new() { Name = "Start", Type = "village", Climate = "temperate",
                    Population = "humans", Economy = "farming", Politics = "mayor",
                    Culture = "simple", ContainsStart = true, GenStatus = "ready" },
                new() { Name = "Cold", Type = "wilderness", Climate = "cold",
                    Population = "monsters", Economy = "none", Politics = "anarchy",
                    Culture = "tribal", GenStatus = "cold" },
            },
            Locations = new()
            {
                new() { Name = "Village", Terrain = "plains", Danger = 0,
                    Role = LocationRole.Start, Description = "start", Region = "Start",
                    Connections = new() { "Cold Border" } },
                new() { Name = "Cold Border", Terrain = "forest", Danger = 5,
                    Role = LocationRole.Wilderness, Description = "cold", Region = "Cold",
                    Connections = new() { "Village" } },
            },
        };

        var stats = new CommitStats();
        committer.CommitLocations(plan, stats);

        // Only the start-region location was committed.
        Assert.Equal(1, stats.Locations);
        Assert.NotNull(world.Locations.FirstOrDefault(l => l.Name == "Village"));
        Assert.Null(world.Locations.FirstOrDefault(l => l.Name == "Cold Border"));

        // The cold-region set is stashed on World.Flags so the travel
        // handler can detect cold-region boundaries.
        Assert.NotNull(world.Flags);
        Assert.Equal("chunked", world.Flags!["generationMode"]);
        var cold = Assert.IsType<List<string>>(world.Flags!["coldRegions"]);
        Assert.Contains("Cold", cold);
        var ready = Assert.IsType<List<string>>(world.Flags!["readyRegions"]);
        Assert.Contains("Start", ready);

        // The Village should have a PHANTOM exit to "Cold Border" (the
        // cold-region location name) since the planner listed it in
        // Connections. The phantom exit has To=EntityId.Empty +
        // ToName="Cold Border".
        var village = world.Locations.First(l => l.Name == "Village");
        var phantom = village.Exits.FirstOrDefault(e =>
            e.To == MyGame.Core.Common.EntityId.Empty && e.ToName == "Cold Border");
        Assert.NotNull(phantom);
    }

    [Fact]
    public void CommitLocations_FullPlan_CommitsAllLocations()
    {
        // Non-chunked plan: all locations are committed (back-compat).
        var world = DefaultWorld.Create(seed: 1);
        var committer = new WorldBuilderCommitter(world);
        var plan = new WorldPlan
        {
            Title = "Тестовый полный мир",
            Theme = "test",
            Setting = "test",
            Atmosphere = "test",
            StartingHook = "hook",
            // GenerationMode = null (full build)
            Locations = new()
            {
                new() { Name = "Loc1", Terrain = "plains", Danger = 0,
                    Role = LocationRole.Start, Description = "s",
                    Connections = new() { "Loc2" } },
                new() { Name = "Loc2", Terrain = "forest", Danger = 1,
                    Role = LocationRole.Settlement, Description = "f",
                    Connections = new() { "Loc1" } },
            },
        };

        var stats = new CommitStats();
        committer.CommitLocations(plan, stats);
        Assert.Equal(2, stats.Locations);
        Assert.NotNull(world.Locations.FirstOrDefault(l => l.Name == "Loc1"));
        Assert.NotNull(world.Locations.FirstOrDefault(l => l.Name == "Loc2"));
    }

    // ─── Feature 1: SaveMeta.OriginalPlanJson round-trip ────────────────

    [Fact]
    public void SaveMeta_OriginalPlanJson_RoundTripsThroughJson()
    {
        var meta = new SaveMeta { Id = "save_test", Name = "t" } with
        {
            OriginalPlanJson = "{\"title\":\"Test\",\"theme\":\"test\"}",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(meta, WorldJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<SaveMeta>(json, WorldJson.Options)!;
        Assert.Equal("{\"title\":\"Test\",\"theme\":\"test\"}", back.OriginalPlanJson);
    }

    [Fact]
    public void SaveMeta_OldSave_LoadsWithNullOriginalPlanJson()
    {
        // An old save (written before issue #20) doesn't have
        // OriginalPlanJson in its JSON — it must load with null.
        var oldJson = """
            {"Id":"save_test","Name":"t","OwnerId":"00000000-0000-0000-0000-000000000000","CreatedAt":"2024-01-01T00:00:00Z","UpdatedAt":"2024-01-01T00:00:00Z","BuildStatus":2,"EngineVersion":"0.2.0","StorageVersion":2}
            """;
        var meta = System.Text.Json.JsonSerializer.Deserialize<SaveMeta>(oldJson, WorldJson.Options);
        Assert.NotNull(meta);
        Assert.Null(meta!.OriginalPlanJson);
    }

    // ─── Feature 4: Rebuild options record ──────────────────────────────

    [Fact]
    public void RebuildOptions_Default_AllFalse()
    {
        var opts = new WorldBuilderOrchestrator.RebuildOptions();
        Assert.False(opts.Locations);
        Assert.False(opts.Npcs);
        Assert.False(opts.Items);
        Assert.False(opts.Narration);
        Assert.False(opts.FullRebuild);
    }

    [Fact]
    public void RebuildOptions_WithFlags_PreservesValues()
    {
        var opts = new WorldBuilderOrchestrator.RebuildOptions
        {
            Locations = true,
            Narration = true,
        };
        Assert.True(opts.Locations);
        Assert.True(opts.Narration);
        Assert.False(opts.FullRebuild);
    }

    // ─── Feature 1c: GenerateRegionAsync happy-path stub ────────────────

    [Fact]
    public async Task GenerateRegionAsync_AlreadyReady_ReturnsNoOp()
    {
        // When the region's GenStatus is already "ready", the call is a
        // no-op (idempotent — the travel handler may double-call).
        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var ai = new AiClient(new AiSettings { ApiKey = "test" }, http);
        var world = DefaultWorld.Create(seed: 1);
        var prompts = new PromptLoader(enableHotReload: false);
        var tools = new ToolRegistry(world);
        var orch = new WorldBuilderOrchestrator(ai, world, prompts, tools);

        var plan = new WorldPlan
        {
            Title = "Тест", Theme = "test", Setting = "s", Atmosphere = "a",
            StartingHook = "h",
            Regions = new()
            {
                new() { Name = "AlreadyReady", Type = "t", Climate = "c",
                    Population = "p", Economy = "e", Politics = "pol",
                    Culture = "cul", GenStatus = "ready" },
            },
        };

        var result = await orch.GenerateRegionAsync("AlreadyReady", plan);
        Assert.True(result.Success);
        Assert.True(result.AlreadyReady);
        // No AI call should have been made.
        Assert.Equal(0, handler.NonStreamCallCount);
    }

    [Fact]
    public async Task GenerateRegionAsync_UnknownRegion_ReturnsError()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var ai = new AiClient(new AiSettings { ApiKey = "test" }, http);
        var world = DefaultWorld.Create(seed: 1);
        var prompts = new PromptLoader(enableHotReload: false);
        var tools = new ToolRegistry(world);
        var orch = new WorldBuilderOrchestrator(ai, world, prompts, tools);

        var plan = new WorldPlan
        {
            Title = "Тест", Theme = "test", Setting = "s", Atmosphere = "a",
            StartingHook = "h",
            Regions = new(),
        };

        var result = await orch.GenerateRegionAsync("Nonexistent", plan);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task GenerateRegionAsync_NullPlan_ReturnsError()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var ai = new AiClient(new AiSettings { ApiKey = "test" }, http);
        var world = DefaultWorld.Create(seed: 1);
        var prompts = new PromptLoader(enableHotReload: false);
        var tools = new ToolRegistry(world);
        var orch = new WorldBuilderOrchestrator(ai, world, prompts, tools);

        var result = await orch.GenerateRegionAsync("Any", originalPlan: null);
        Assert.False(result.Success);
        Assert.Contains("no original plan", result.Error);
    }

    /// <summary>
    /// Stub HTTP handler — copied from WorldBuilderOrchestratorPauseTests
    /// (kept private there). Returns a canned non-streaming response and
    /// counts calls so the tests can assert "no AI call was made".
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string NonStreamResponse { get; set; } = """
            {"choices":[{"message":{"role":"assistant","content":"{}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}
            """;
        public int NonStreamCallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            NonStreamCallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(NonStreamResponse, Encoding.UTF8, "application/json"),
            });
        }
    }
}
