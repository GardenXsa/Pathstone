using System.Net;
using System.Net.Http;
using System.Text;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.World;

namespace MyGame.Tests.AI.Agents;

/// <summary>
/// Unit tests for <see cref="WorldBuilderOrchestrator"/>'s pause/resume
/// feature (issue #19). Verifies that:
/// <list type="bullet">
///   <item><see cref="WorldBuilderOrchestrator.Pause"/> / <see cref="WorldBuilderOrchestrator.Resume"/>
///     flip the <see cref="WorldBuilderOrchestrator.Paused"/> flag from the
///     UI thread (no background task needed for the flag itself — the
///     polling loop in WaitIfPausedAsync is what consumes it).</item>
///   <item><see cref="WorldBuilderOrchestrator.SaveState"/> snapshots the
///     current stage / plan / iteration into a <see cref="WorldBuilderState"/>
///     record that can be JSON-serialised + restored via
///     <see cref="WorldBuilderOrchestrator.LoadState"/>.</item>
///   <item>A loaded state causes the orchestrator to skip the planner AI
///     call when the loaded stage marks planning as done.</item>
/// </list>
/// </summary>
public class WorldBuilderOrchestratorPauseTests
{
    /// <summary>
    /// The Paused flag defaults to false on a fresh orchestrator (so a
    /// normal Run doesn't block).
    /// </summary>
    [Fact]
    public void Paused_DefaultsFalse()
    {
        var orch = MakeOrchestrator();
        Assert.False(orch.Paused);
    }

    /// <summary>
    /// Pause() flips Paused to true; Resume() flips it back to false.
    /// Idempotent — calling Pause() twice keeps Paused=true.
    /// </summary>
    [Fact]
    public void Pause_Then_Resume_TogglesFlag()
    {
        var orch = MakeOrchestrator();
        orch.Pause();
        Assert.True(orch.Paused);
        orch.Pause(); // idempotent
        Assert.True(orch.Paused);
        orch.Resume();
        Assert.False(orch.Paused);
    }

    /// <summary>
    /// SaveState() on a fresh orchestrator returns a state with Stage
    /// "planning" (the initial stage), a null Plan, an empty Messages
    /// list, and Iteration 0.
    /// </summary>
    [Fact]
    public void SaveState_FreshOrchestrator_ReturnsPlanningStage()
    {
        var orch = MakeOrchestrator();
        var state = orch.SaveState();
        Assert.NotNull(state);
        Assert.Equal("planning", state.Stage);
        Assert.Null(state.Plan);
        Assert.NotNull(state.Messages);
        Assert.Empty(state.Messages);
        Assert.Equal(0, state.Iteration);
    }

    /// <summary>
    /// LoadState() restores the stage / plan / iteration. The next
    /// SaveState() reflects the loaded values (so a round-trip works).
    /// </summary>
    [Fact]
    public void LoadState_RestoresStagePlanIteration()
    {
        var orch = MakeOrchestrator();
        var plan = MakeMinimalPlan("Тестовый мир");
        var state = new WorldBuilderState
        {
            Stage = "committer_done",
            Plan = plan,
            Iteration = 2,
        };
        orch.LoadState(state);

        var saved = orch.SaveState();
        Assert.Equal("committer_done", saved.Stage);
        Assert.NotNull(saved.Plan);
        Assert.Equal("Тестовый мир", saved.Plan!.Title);
        Assert.Equal(2, saved.Iteration);
    }

    /// <summary>
    /// LoadState(null) throws ArgumentNullException (defensive — a null
    /// state would NRE the orchestrator's resume path).
    /// </summary>
    [Fact]
    public void LoadState_Null_Throws()
    {
        var orch = MakeOrchestrator();
        Assert.Throws<ArgumentNullException>(() => orch.LoadState(null!));
    }

    /// <summary>
    /// A RunAsync with a loaded state marking planning_done SKIPS the
    /// planner AI call. We verify by stubbing the HTTP handler to count
    /// non-streaming POSTs to /chat/completions (the planner + narrator
    /// are the only non-streaming calls). To avoid the narrator call
    /// racing in before we can assert, we PAUSE the orchestrator before
    /// running it — the pause checkpoint right after planning blocks
    /// before the narrator stage starts, so NonStreamCallCount stays 0.
    /// </summary>
    [Fact]
    public async Task RunAsync_LoadedStateSkipsPlanning_NoPlannerCall()
    {
        var handler = new StubHandler();
        // The narrator response would only be requested if the
        // orchestrator reaches the narrator stage — paused-before-
        // narrator means this never gets returned.
        handler.NonStreamResponse = """
            {"choices":[{"message":{"role":"assistant","content":"вступление"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}
            """;
        var http = new HttpClient(handler);
        var ai = new AiClient(new AiSettings { ApiKey = "test" }, http);
        var world = DefaultWorld.Create(seed: 1);
        var prompts = new PromptLoader(enableHotReload: false);
        var tools = new ToolRegistry(world);

        var orch = new WorldBuilderOrchestrator(ai, world, prompts, tools);
        // Load a state that marks planning as done with a real plan.
        orch.LoadState(new WorldBuilderState
        {
            Stage = "planning_done",
            Plan = MakeMinimalPlan("Тест"),
        });

        // PAUSE the orchestrator before running. The first pause
        // checkpoint is right after planning (which we've skipped) —
        // the orchestrator will hit it almost immediately and block
        // there, never reaching the narrator call. This lets us
        // assert NonStreamCallCount == 0 without racing the narrator.
        orch.Pause();
        var progress = new Progress<WorldBuildProgress>(_ => { });

        // Run on a background task so the pause doesn't block the test
        // thread. Use a cancellation token that we'll cancel after the
        // assertion to release the orchestrator.
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => orch.RunAsync(
            new WorldPlanRequest { Brief = "test" }, progress, cts.Token));

        // Give the orchestrator a moment to skip planning + reach the
        // pause checkpoint. The pause loop polls every 100ms, so 300ms
        // is enough for the orchestrator to settle.
        await Task.Delay(300);

        // Planner call should NOT have been made — the loaded state
        // marked planning_done, so the orchestrator skipped straight
        // to the pause checkpoint. (The narrator call would happen
        // AFTER the pause checkpoint, so it hasn't been made either.)
        Assert.Equal(0, handler.NonStreamCallCount);

        // Cleanup: cancel the token so the paused orchestrator's
        // polling loop exits + the task completes. Resume first so the
        // polling loop processes the cancellation promptly.
        orch.Resume();
        cts.Cancel();
        try { await task; }
        catch (OperationCanceledException) { /* expected */ }
        catch (AiException) { /* also acceptable — stub response may not parse */ }
    }

    /// <summary>
    /// Helper: build a minimal orchestrator for unit tests that don't
    /// run the full pipeline. The HTTP handler is a stub but the tests
    /// above only test the synchronous API (Pause/Resume/SaveState/
    /// LoadState) so no HTTP call is made.
    /// </summary>
    private static WorldBuilderOrchestrator MakeOrchestrator()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var ai = new AiClient(new AiSettings { ApiKey = "test" }, http);
        var world = DefaultWorld.Create(seed: 1);
        var prompts = new PromptLoader(enableHotReload: false);
        var tools = new ToolRegistry(world);
        return new WorldBuilderOrchestrator(ai, world, prompts, tools);
    }

    /// <summary>
    /// Build a minimal <see cref="WorldPlan"/> with all required fields
    /// set (Title, Theme, Setting, Atmosphere, StartingHook). Used by
    /// tests that need a plan to stage in a WorldBuilderState but don't
    /// care about the plan's content.
    /// </summary>
    private static WorldPlan MakeMinimalPlan(string title) => new()
    {
        Title = title,
        Theme = "test-theme",
        Setting = "test-setting",
        Atmosphere = "test-atmosphere",
        StartingHook = "test-hook",
    };

    /// <summary>
    /// Stub <see cref="HttpMessageHandler"/> that returns canned
    /// responses + counts the number of streaming / non-streaming calls.
    /// Non-streaming = planner + narrator calls; streaming = GM tool
    /// loop (not used by the orchestrator but kept for consistency with
    /// the GameMaster test stubs).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string StreamResponse { get; set; } = "ok";
        public string NonStreamResponse { get; set; } = "summary";
        public int StreamCallCount { get; private set; }
        public int NonStreamCallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken)
                .GetAwaiter().GetResult() ?? string.Empty;
            var isStream = body.Contains("\"stream\":true", StringComparison.Ordinal);

            if (isStream)
            {
                StreamCallCount++;
                var sse = $"data: {MakeStreamChunk(StreamResponse)}\n\ndata: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                });
            }
            else
            {
                NonStreamCallCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(NonStreamResponse, Encoding.UTF8, "application/json"),
                });
            }
        }

        private static string MakeStreamChunk(string content)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(content));
            var escaped = doc.RootElement.GetRawText();
            return $$"""{"choices":[{"delta":{"content":{{escaped}}},"finish_reason":null}]}""";
        }
    }
}
