namespace MyGame.Core.AI.Agents;

/// <summary>
/// One delegated sub-task for the world-builder's optional pet-agent
/// phase. The orchestrator runs each delegation as a separate
/// <see cref="PetAgent"/> with its own LLM conversation + tool loop,
/// after the deterministic committer stage and before the narrator.
///
/// <para>
/// Use cases: mass NPC spawning ("засели 10 разбойников в лесу"), batch
/// custom-item creation ("создай 5 артефактов с разными свойствами"),
/// flavour population ("расставь маркеры лора по 8 локациям"). The pet
/// agent has full tool access (spawn_npc, give_item, etc.) and reports
/// back via <c>pet_done</c>.
/// </para>
///
/// <para>
/// Delegations are OPTIONAL — if none are provided, the orchestrator
/// skips the pet phase entirely (the default). This keeps the simple
/// 3-stage pipeline (planner → commit → narrate) as the default; pet
/// delegation is an opt-in for users who want richer worlds.
/// </para>
/// </summary>
public sealed record PetDelegation
{
    /// <summary>
    /// Display name shown in the progress UI ("Pet: заселение леса").
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// The task description handed to the pet agent. Should be concrete
    /// and self-contained — the pet agent doesn't see the orchestrator's
    /// other context. Example: "Засели 8 разбойников (templateId:
    /// npc_bandit) в локации «Северный Лес». Дай каждому имя и
    /// описание. Заверши вызовом pet_done."
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Optional per-pet AI settings override. If null, the orchestrator's
    /// main <see cref="AiClient"/> settings are used.
    /// </summary>
    public AiSettings? Settings { get; init; }

    /// <summary>
    /// Optional iteration cap for this delegation. If null, the pet
    /// agent's default (<see cref="PetAgent.DefaultMaxIterations"/>) is
    /// used.
    /// </summary>
    public int? MaxIterations { get; init; }
}
