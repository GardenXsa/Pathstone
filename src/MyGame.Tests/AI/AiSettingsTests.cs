using MyGame.Core.AI;

namespace MyGame.Tests.AI;

/// <summary>
/// Unit tests for <see cref="AiSettings"/>'s per-role model overrides
/// (issue #26). Verifies the fallback chain (role override → main Model)
/// and that null/whitespace overrides are treated as "not set".
/// </summary>
public class AiSettingsTests
{
    [Fact]
    public void GetModelForRole_NoOverrides_FallsBackToMainModel()
    {
        // When no role-specific overrides are set, every role returns
        // the main Model. This is the default behaviour for existing
        // settings.json files (which don't have the new fields).
        var s = new AiSettings { Model = "gpt-4o-mini" };

        Assert.Equal("gpt-4o-mini", s.GetModelForRole(AiRole.Planner));
        Assert.Equal("gpt-4o-mini", s.GetModelForRole(AiRole.GM));
        Assert.Equal("gpt-4o-mini", s.GetModelForRole(AiRole.Narrator));
        Assert.Equal("gpt-4o-mini", s.GetModelForRole(AiRole.Pet));
    }

    [Fact]
    public void GetModelForRole_WithOverride_ReturnsOverrideForThatRole()
    {
        // Each role-specific field, when set, overrides the main Model
        // for that role only — the other roles still fall back.
        var s = new AiSettings
        {
            Model = "gpt-4o-mini",
            PlannerModel = "gpt-4o",
            GMModel = "deepseek-chat",
            NarratorModel = "claude-3-haiku",
            PetModel = "llama3.1:8b",
        };

        Assert.Equal("gpt-4o",         s.GetModelForRole(AiRole.Planner));
        Assert.Equal("deepseek-chat",  s.GetModelForRole(AiRole.GM));
        Assert.Equal("claude-3-haiku", s.GetModelForRole(AiRole.Narrator));
        Assert.Equal("llama3.1:8b",    s.GetModelForRole(AiRole.Pet));
    }

    [Fact]
    public void GetModelForRole_WhitespaceOverride_TreatedAsUnset()
    {
        // A whitespace-only override is treated as "not set" — GetModelForRole
        // falls back to the main Model. This matches how the SettingsViewModel
        // persists empty text boxes (null) and how the user expects the
        // "по умолчанию" placeholder to behave.
        var s = new AiSettings
        {
            Model = "gpt-4o-mini",
            PlannerModel = "   ",
            GMModel = "",
            NarratorModel = "\t",
            PetModel = null,
        };

        Assert.Equal("gpt-4o-mini", s.GetModelForRole(AiRole.Planner));
        Assert.Equal("gpt-4o-mini", s.GetModelForRole(AiRole.GM));
        Assert.Equal("gpt-4o-mini", s.GetModelForRole(AiRole.Narrator));
        Assert.Equal("gpt-4o-mini", s.GetModelForRole(AiRole.Pet));
    }

    [Fact]
    public void GetModelForRole_TrimsOverride()
    {
        // Leading/trailing whitespace on a non-empty override is trimmed
        // so the model id sent to the provider is clean. (A model id with
        // a trailing space would 404 at most providers.)
        var s = new AiSettings
        {
            Model = "gpt-4o-mini",
            PlannerModel = "  gpt-4o  ",
        };

        Assert.Equal("gpt-4o", s.GetModelForRole(AiRole.Planner));
    }

    [Fact]
    public void GetModelForRole_PartialOverrides_OnlySpecifiedRolesOverride()
    {
        // A common setup: override only the planner (creative) and the GM
        // (frequent), leave narrator + pet on the main model.
        var s = new AiSettings
        {
            Model = "gpt-4o-mini",
            PlannerModel = "gpt-4o",
            GMModel = "deepseek-chat",
        };

        Assert.Equal("gpt-4o",        s.GetModelForRole(AiRole.Planner));
        Assert.Equal("deepseek-chat", s.GetModelForRole(AiRole.GM));
        Assert.Equal("gpt-4o-mini",   s.GetModelForRole(AiRole.Narrator));
        Assert.Equal("gpt-4o-mini",   s.GetModelForRole(AiRole.Pet));
    }

    [Fact]
    public void AiSettings_WithExpression_PreservesRoleOverrides()
    {
        // The record's `with` syntax must preserve role-specific fields
        // when updating other fields (e.g. changing the main Model).
        // This is the pattern used by AiClient.WithModel + the agent
        // constructors to derive role-specific clients.
        var s = new AiSettings
        {
            Model = "gpt-4o-mini",
            PlannerModel = "gpt-4o",
            GMModel = "deepseek-chat",
        };

        var derived = s with { MaxTokens = 500 };

        Assert.Equal("gpt-4o",        derived.PlannerModel);
        Assert.Equal("deepseek-chat", derived.GMModel);
        Assert.Equal("gpt-4o-mini",   derived.Model);
        Assert.Equal(500,             derived.MaxTokens);
    }

    [Fact]
    public void AiSettings_JsonRoundTrip_PreservesRoleOverrides()
    {
        // Serialising to JSON + deserialising back must preserve the
        // role-specific overrides (System.Text.Json's default behaviour
        // for init-only record properties — this test guards against a
        // future [JsonIgnore] accident).
        var s = new AiSettings
        {
            Model = "gpt-4o-mini",
            ApiKey = "sk-test",
            PlannerModel = "gpt-4o",
            GMModel = "deepseek-chat",
            NarratorModel = "claude-3-haiku",
            PetModel = "llama3.1:8b",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AiSettings>(json);

        Assert.NotNull(back);
        Assert.Equal("gpt-4o",         back!.PlannerModel);
        Assert.Equal("deepseek-chat",  back.GMModel);
        Assert.Equal("claude-3-haiku", back.NarratorModel);
        Assert.Equal("llama3.1:8b",    back.PetModel);
        Assert.Equal("gpt-4o-mini",    back.Model);
        Assert.Equal("sk-test",        back.ApiKey);
    }

    [Fact]
    public void AiSettings_OldJsonWithoutOverrides_LoadsWithNulls()
    {
        // A settings.json written before issue #26 doesn't have the
        // role-specific fields. Deserialising it must not throw, and the
        // role fields default to null (GetModelForRole then falls back
        // to Model). This is the backward-compatibility guarantee.
        var oldJson = """
            {
              "BaseUrl": "https://api.openai.com/v1",
              "ApiKey": "sk-test",
              "Model": "gpt-4o-mini",
              "Temperature": 0.7,
              "MaxTokens": 2000
            }
            """;

        var back = System.Text.Json.JsonSerializer.Deserialize<AiSettings>(oldJson);

        Assert.NotNull(back);
        Assert.Equal("gpt-4o-mini", back!.Model);
        Assert.Null(back.PlannerModel);
        Assert.Null(back.GMModel);
        Assert.Null(back.NarratorModel);
        Assert.Null(back.PetModel);
        // And the fallback chain works as expected.
        Assert.Equal("gpt-4o-mini", back.GetModelForRole(AiRole.GM));
    }
}
