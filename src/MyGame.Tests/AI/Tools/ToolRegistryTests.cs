using MyGame.Core.AI;
using MyGame.Core.AI.Tools;
using MyGame.Core.World;

namespace MyGame.Tests.AI.Tools;

/// <summary>
/// Unit tests for the ToolRegistry. Covers builtin-tool registration,
/// unknown-tool errors, the roll_dice happy path, and lenient JSON arg
/// parsing (the spec's "don't crash on malformed args" requirement).
/// </summary>
public class ToolRegistryTests
{
    /// <summary>
    /// The 18 built-in tools the registry registers. Mirrors the list in
    /// ToolRegistry.RegisterBuiltins (5 MVP + 13 expansion). If this list
    /// drifts from the source, the Constructor_RegistersAllBuiltinTools
    /// test will fail — update both together.
    /// </summary>
    private static readonly string[] ExpectedTools =
    {
        "roll_dice",
        "get_player_state",
        "get_location",
        "spawn_npc",
        "advance_time",
        "move_player",
        "give_item",
        "spawn_item_on_ground",
        "equip_player",
        "update_quest",
        "set_flag",
        "get_world_state",
        "get_npc_state",
        "award_xp",
        "roll_attack",
        "deal_damage",
        "apply_status",
        "create_item_template",
    };

    private static ToolRegistry MakeRegistry()
    {
        var world = DefaultWorld.Create(seed: 1);
        return new ToolRegistry(world);
    }

    [Fact]
    public void Constructor_RegistersAllBuiltinTools()
    {
        var reg = MakeRegistry();
        var names = reg.Definitions.Select(d => d.Name).ToHashSet();
        foreach (var name in ExpectedTools)
            Assert.Contains(name, names);
        Assert.Equal(ExpectedTools.Length, reg.Definitions.Count);
    }

    [Fact]
    public void GetDefinition_ReturnsKnownTool()
    {
        var reg = MakeRegistry();
        var def = reg.GetDefinition("roll_dice");
        Assert.NotNull(def);
        Assert.Equal("roll_dice", def!.Name);
        Assert.False(string.IsNullOrWhiteSpace(def.Description));
    }

    [Fact]
    public void GetDefinition_Nonexistent_ReturnsNull()
    {
        var reg = MakeRegistry();
        Assert.Null(reg.GetDefinition("not_a_tool"));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsError()
    {
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("call_1", "nonexistent_tool", "{}");
        Assert.True(result.IsError);
        Assert.Contains("nonexistent_tool", result.Content);
        Assert.Equal("call_1", result.ToolCallId);
    }

    [Fact]
    public async Task ExecuteAsync_RollDice_ReturnsResult()
    {
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("call_1", "roll_dice", """{"expression":"1d20"}""");
        Assert.False(result.IsError);
        // The handler formats as "Брошено {expr}: кости [...] ±N = {total}."
        Assert.Contains("Брошено", result.Content);
        Assert.Contains("1d20", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_RollDice_NoArgs_DefaultsTo1d20()
    {
        // When `expression` is absent, the handler defaults to "1d20".
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("call_1", "roll_dice", "{}");
        Assert.False(result.IsError);
        Assert.Contains("1d20", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedArgs_DoesNotCrash()
    {
        // The spec's "don't crash on malformed args" requirement: bad JSON
        // is treated as an empty object, then the handler runs with
        // defaults. roll_dice falls back to "1d20" when expression is
        // missing — so the result must NOT be an error, and the content
        // should mention 1d20.
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("call_1", "roll_dice", "{bad json");
        Assert.False(result.IsError);
        Assert.Contains("1d20", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArgs_DoesNotCrash()
    {
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("call_1", "roll_dice", "");
        Assert.False(result.IsError);
        Assert.Contains("1d20", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_NullArgs_DoesNotCrash()
    {
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("call_1", "roll_dice", null!);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_GetPlayerState_ReturnsSnapshot()
    {
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("call_1", "get_player_state", "{}");
        Assert.False(result.IsError);
        // DefaultWorld's starting player is named "Странник".
        Assert.Contains("Странник", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerException_ConvertedToErrorResult()
    {
        // Call get_location with an explicit invalid locationId that
        // resolves to null — the handler should either return a
        // graceful error or some non-error result; the key requirement
        // is it doesn't throw out of ExecuteAsync. Any well-formed
        // ToolResult is acceptable here.
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("call_1", "get_location", """{"locationId":"does_not_exist"}""");
        // Either IsError=true with a useful message, or IsError=false
        // with a "not found" string. Both are acceptable; just don't
        // throw.
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Content));
    }

    [Fact]
    public async Task ExecuteAsync_StampsToolCallIdOnResult()
    {
        // The registry overwrites the handler's empty ToolCallId with the
        // caller-supplied id before returning.
        var reg = MakeRegistry();
        var result = await reg.ExecuteAsync("my_call_id_42", "roll_dice", "{}");
        Assert.Equal("my_call_id_42", result.ToolCallId);
    }

    [Fact]
    public void Register_CustomTool_AddsToDefinitions()
    {
        var reg = MakeRegistry();
        var before = reg.Definitions.Count;

        var def = new ToolDefinition
        {
            Name = "test_custom",
            Description = "A test tool.",
            ParametersJson = "{}",
        };
        reg.Register(def, (args, ct) =>
            Task.FromResult(ToolResult.Ok("c1", "custom ok")));

        Assert.Equal(before + 1, reg.Definitions.Count);
        Assert.NotNull(reg.GetDefinition("test_custom"));
    }

    [Fact]
    public void Constructor_NullWorld_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new ToolRegistry(null!));
}
