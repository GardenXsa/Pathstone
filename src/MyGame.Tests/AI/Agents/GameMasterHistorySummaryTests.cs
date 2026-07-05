using System.Net.Http;
using MyGame.Core.AI;
using MyGame.Core.AI.Agents;
using MyGame.Core.AI.Prompts;
using MyGame.Core.AI.Tools;
using MyGame.Core.World;

namespace MyGame.Tests.AI.Agents;

/// <summary>
/// Unit tests for <see cref="GameMaster"/>'s public context-state API
/// added in issue #25: the <see cref="GameMaster.HistorySummary"/>
/// get/set property. Verifies that the summary can be restored after a
/// save/load (the host assigns the persisted value back here) and that
/// null/blank values are normalised to null.
/// </summary>
public class GameMasterHistorySummaryTests
{
    /// <summary>
    /// Build a GameMaster bound to a default world. The AiClient is
    /// constructed with a stub HttpClient — these tests never make an
    /// actual HTTP call (they exercise property get/set only).
    /// </summary>
    private static GameMaster MakeGm()
    {
        var world = DefaultWorld.Create(seed: 1);
        var ai = new AiClient(new AiSettings { ApiKey = "test" }, new HttpClient());
        var prompts = new PromptLoader(enableHotReload: false);
        var tools = new ToolRegistry(world);
        return new GameMaster(ai, world, prompts, tools);
    }

    [Fact]
    public void HistorySummary_DefaultsToNull()
    {
        // A fresh GameMaster has no summary — summarization hasn't
        // happened yet.
        var gm = MakeGm();
        Assert.Null(gm.HistorySummary);
    }

    [Fact]
    public void HistorySummary_CanBeSetAndRetrieved()
    {
        // The host (GameViewModel) sets this property on save load to
        // restore the persisted summary. The getter must return the
        // exact value that was set.
        var gm = MakeGm();
        const string summary = "Игрок встретил торговца Арвина в Деревне.";

        gm.HistorySummary = summary;

        Assert.Equal(summary, gm.HistorySummary);
    }

    [Fact]
    public void HistorySummary_SetToEmpty_NormalisesToNull()
    {
        // An empty string is normalised to null — the GameMaster treats
        // them the same way (no summary). This avoids the working-list
        // logic having to handle both cases.
        var gm = MakeGm();

        gm.HistorySummary = string.Empty;

        Assert.Null(gm.HistorySummary);
    }

    [Fact]
    public void HistorySummary_SetToWhitespace_NormalisesToNull()
    {
        // A whitespace-only string is also normalised to null — the
        // SettingsViewModel persists blanks as null, and we want the
        // same behaviour here for consistency.
        var gm = MakeGm();

        gm.HistorySummary = "   \n\t  ";

        Assert.Null(gm.HistorySummary);
    }

    [Fact]
    public void HistorySummary_CanBeClearedBySettingNull()
    {
        // Setting null clears the summary — useful if the host wants
        // to force a fresh summarization pass on the next turn.
        var gm = MakeGm();
        gm.HistorySummary = "some summary";

        gm.HistorySummary = null;

        Assert.Null(gm.HistorySummary);
    }

    [Fact]
    public void HistorySummary_SetDoesNotAffectHistory()
    {
        // Setting HistorySummary must NOT clear or modify the in-memory
        // _history list. The two are independent state: history is the
        // raw messages, summary is the compressed older portion.
        var gm = MakeGm();
        Assert.Empty(gm.History);

        gm.HistorySummary = "some summary";

        Assert.Equal("some summary", gm.HistorySummary);
        Assert.Empty(gm.History); // unchanged
    }

    [Fact]
    public void ResetHistory_DoesNotClearSummary()
    {
        // ResetHistory clears the in-memory message list ONLY — the
        // summary is independent state and must survive a history reset.
        // (The host uses ResetHistory on save load, then restores the
        // summary separately.)
        var gm = MakeGm();
        gm.HistorySummary = "persisted summary";

        gm.ResetHistory();

        Assert.Equal("persisted summary", gm.HistorySummary);
    }

    [Fact]
    public void Constructor_AcceptsNewOptionalParametersWithoutBreaking()
    {
        // The new optional ctor parameters (aiSettings, maxContextTokens)
        // must not break the existing 4-arg + maxIterations call pattern.
        // This is the backward-compat guarantee for existing call sites
        // (and the existing GameMasterBatchTests).
        var world = DefaultWorld.Create(seed: 1);
        var ai = new AiClient(new AiSettings { ApiKey = "test" }, new HttpClient());
        var prompts = new PromptLoader(enableHotReload: false);
        var tools = new ToolRegistry(world);

        // Old call pattern — no new args.
        var gm1 = new GameMaster(ai, world, prompts, tools);
        Assert.NotNull(gm1);

        // Old call pattern with maxIterations.
        var gm2 = new GameMaster(ai, world, prompts, tools, maxIterations: 4);
        Assert.NotNull(gm2);

        // New call pattern with all args.
        var gm3 = new GameMaster(ai, world, prompts, tools,
            maxIterations: 4,
            aiSettings: new AiSettings { Model = "gpt-4o", GMModel = "deepseek-chat" },
            maxContextTokens: 8000);
        Assert.NotNull(gm3);
    }
}
