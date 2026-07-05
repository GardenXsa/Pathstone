namespace MyGame.Core.AI;

/// <summary>
/// Identifies an AI agent role within the pipeline. Used by
/// <see cref="AiSettings.GetModelForRole"/> to pick a role-specific
/// model override (or fall back to <see cref="AiSettings.Model"/>).
/// </summary>
public enum AiRole
{
    /// <summary>
    /// World-builder planner. Designs the world structure from the brief
    /// (locations, NPCs, buildings, theme, atmosphere). One-shot AI call
    /// per world build.
    /// </summary>
    Planner,

    /// <summary>
    /// In-session Game Master. Runs the tool-call loop that narrates the
    /// player's actions and mutates the world. Long-running, multi-turn.
    /// </summary>
    GM,

    /// <summary>
    /// World-builder narrator. Writes the atmospheric opening narration
    /// after the deterministic committer has populated the world.
    /// </summary>
    Narrator,

    /// <summary>
    /// Pet agent. A delegated sub-agent for focused sub-tasks during
    /// world-build (e.g. "spawn 5 bandits in the forest"). Has its own
    /// tool-call loop, shorter iteration cap.
    /// </summary>
    Pet,
}

/// <summary>
/// Connection + sampling settings for the OpenAI-compatible AI provider.
///
/// Port-side record (no direct TS counterpart — the TS source kept these
/// fields on <c>lib/settings.ts</c>'s <c>AISettings</c> interface, which
/// mixed UI/server concerns with pure connection data). This C# record is
/// the pure transport layer: it carries only what
/// <see cref="AiClient.ChatAsync"/> needs to format a request, and it
/// serialises plainly (System.Text.Json defaults) so a settings file on
/// disk round-trips losslessly.
///
/// <para>
/// There is NO reference to the z-ai-web-dev-sdk here. The desktop rewrite
/// talks to any OpenAI-compatible provider (OpenAI, LLMost, DeepSeek,
/// local LM Studio, etc.) over plain HTTP — see <see cref="AiClient"/>.
/// </para>
///
/// <para>
/// <b>Multi-model support (issue #26):</b> the four optional role-specific
/// model fields (<see cref="PlannerModel"/>, <see cref="GMModel"/>,
/// <see cref="NarratorModel"/>, <see cref="PetModel"/>) let the user pick
/// different models for each agent role. When a role-specific field is
/// null/empty, <see cref="GetModelForRole"/> falls back to
/// <see cref="Model"/> (the main one). This is useful for mixing a strong
/// model for the planner (creative) with a cheaper model for the GM
/// (frequent) — without spinning up separate provider accounts.
/// </para>
/// </summary>
public sealed record AiSettings
{
    /// <summary>
    /// Base URL of the OpenAI-compatible API, WITHOUT the trailing
    /// <c>/chat/completions</c> path. Defaults to OpenAI's public endpoint.
    /// <see cref="AiClient"/> appends <c>/chat/completions</c> itself, so a
    /// base URL of <c>https://api.openai.com/v1</c> resolves to
    /// <c>https://api.openai.com/v1/chat/completions</c>.
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    /// <summary>
    /// Bearer token sent in the <c>Authorization</c> header. Null/empty
    /// means anonymous (only valid for local providers that don't enforce
    /// auth). The <see cref="AiClient"/> skips the <c>Authorization</c>
    /// header when ApiKey is null so local servers don't reject the request,
    /// and throws a typed <see cref="AiException"/> if the provider returns
    /// 401/403.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Model id passed in the request body (<c>gpt-4o-mini</c>,
    /// <c>deepseek-chat</c>, <c>llama3.1:8b</c>, …). Defaults to OpenAI's
    /// cheapest current model — the desktop MVP targets low cost-per-turn.
    ///
    /// <para>
    /// This is the <b>default</b> model used for any role that doesn't
    /// have a role-specific override set. Per-role overrides live in
    /// <see cref="PlannerModel"/> / <see cref="GMModel"/> /
    /// <see cref="NarratorModel"/> / <see cref="PetModel"/> (issue #26).
    /// </para>
    /// </summary>
    public string Model { get; init; } = "gpt-4o-mini";

    /// <summary>
    /// Sampling temperature, 0.0 = greedy, 2.0 = max chaos. Default 0.7 is
    /// the OpenAI-recommended starting point for creative narrative.
    /// </summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>
    /// Hard cap on completion tokens per request. Default 2000 is enough
    /// for a couple of paragraphs of narration + a tool-call round. The
    /// world-builder orchestrator uses a higher cap (configured at the
    /// call site, not here — this record is the shared default).
    /// </summary>
    public int MaxTokens { get; init; } = 2000;

    // ─── Per-role model overrides (issue #26) ───────────────────────────
    //
    // Each is an OPTIONAL model id; when null/empty, GetModelForRole
    // returns the main <see cref="Model"/>. Set a role-specific override
    // to use a different model for that role (e.g. a stronger model for
    // the planner, a cheaper one for the GM).
    //
    // The fields are init-only (the record is immutable) — update via
    // `with { ... }`. They serialise plainly so existing settings.json
    // files load with nulls for missing fields (backward-compatible).

    /// <summary>
    /// Model id for the world-builder <see cref="AiRole.Planner"/>. When
    /// null/empty, falls back to <see cref="Model"/>. Use this to use a
    /// stronger creative model for world-planning (one-shot per build).
    /// </summary>
    public string? PlannerModel { get; init; }

    /// <summary>
    /// Model id for the in-session <see cref="AiRole.GM"/>. When
    /// null/empty, falls back to <see cref="Model"/>. Use this to use a
    /// cheaper / faster model for the GM's frequent tool-call loop.
    /// </summary>
    public string? GMModel { get; init; }

    /// <summary>
    /// Model id for the world-builder <see cref="AiRole.Narrator"/>.
    /// When null/empty, falls back to <see cref="Model"/>. Use this to
    /// pick a more literary model for the atmospheric opening narration.
    /// </summary>
    public string? NarratorModel { get; init; }

    /// <summary>
    /// Model id for <see cref="AiRole.Pet"/> (delegated sub-agents during
    /// world-build). When null/empty, falls back to <see cref="Model"/>.
    /// </summary>
    public string? PetModel { get; init; }

    /// <summary>
    /// Resolve the model id to use for the given role. Returns the
    /// role-specific override when set (non-null and non-empty after
    /// trimming); otherwise falls back to <see cref="Model"/>.
    ///
    /// <para>
    /// Used by <see cref="AiClient.WithModel"/> + the agent constructors
    /// to derive a role-specific client at the start of each run. The
    /// fallback chain is: role override (if set) → main Model.
    /// </para>
    /// </summary>
    public string GetModelForRole(AiRole role) => role switch
    {
        AiRole.Planner  => string.IsNullOrWhiteSpace(PlannerModel)  ? Model : PlannerModel!.Trim(),
        AiRole.GM       => string.IsNullOrWhiteSpace(GMModel)       ? Model : GMModel!.Trim(),
        AiRole.Narrator => string.IsNullOrWhiteSpace(NarratorModel) ? Model : NarratorModel!.Trim(),
        AiRole.Pet      => string.IsNullOrWhiteSpace(PetModel)      ? Model : PetModel!.Trim(),
        _ => Model,
    };
}
