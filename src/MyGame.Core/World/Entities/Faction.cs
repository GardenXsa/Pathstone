namespace MyGame.Core.World.Entities;

/// <summary>
/// A faction in the world (state / guild / cult / band / corp / clan /
/// order). Engine-depth subsystem (issue #36): factions carry a numeric
/// reputation with the player (-100 hostile → +100 ally) that the GM
/// adjusts via the <c>adjust_reputation</c> tool, and an alignment label
/// derived from the reputation (hostile / neutral / ally). The
/// world-builder commits factions from <see cref="AI.WorldPlan.Factions"/>
/// at <see cref="WorldBuilderCommitter.CommitFactions"/>; everything
/// starts neutral (Reputation = 0).
///
/// <para>
/// <b>Serialization:</b> plain POCO + System.Text.Json via
/// <see cref="WorldJson.Options"/>. Round-trips through save/load because
/// every field is a primitive / string.
/// </para>
/// </summary>
public sealed class Faction
{
    /// <summary>
    /// Stable id. Usually derived from the faction name (slugified) at
    /// commit time so the GM can address it by name without an ID lookup.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>Display name (e.g. «Орден Серебряной Чаши»).</summary>
    public string Name { get; set; } = "";

    /// <summary>Flavor description (1–2 sentences from the plan).</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Alignment bucket derived from <see cref="Reputation"/>: <c>ally</c>
    /// (rep &gt;= 30), <c>hostile</c> (rep &lt;= -30), <c>neutral</c>
    /// otherwise. The <c>adjust_reputation</c> tool updates this in lock-
    /// step with reputation so callers always see a consistent pair.
    /// Free-form at commit time (the planner's <c>Alignment</c> string —
    /// good / evil / lawful / chaotic — is descriptive flavor only and
    /// is NOT propagated here).
    /// </summary>
    public string Alignment { get; set; } = "neutral";

    /// <summary>
    /// Numeric reputation with the player, clamped to [-100, 100]. 0 =
    /// neutral start. Drives <see cref="Alignment"/> automatically when
    /// changed via the <c>adjust_reputation</c> tool.
    /// </summary>
    public int Reputation { get; set; }

    /// <summary>
    /// Faction type (state / guild / cult / band / corp / clan / order).
    /// Pure flavor from the plan; not used mechanically.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Where the faction is based (region, capital, territory). Pure
    /// flavor from the plan; the GM may use it to narrate.
    /// </summary>
    public string? Territory { get; set; }

    /// <summary>
    /// Recompute <see cref="Alignment"/> from <see cref="Reputation"/>:
    /// &gt;= 30 → ally, &lt;= -30 → hostile, else neutral. Called by the
    /// <c>adjust_reputation</c> tool after every reputation delta so the
    /// two fields never drift. Public so a future rebalancer / save
    /// migrator can call it after bulk reputation edits.
    /// </summary>
    public void RecomputeAlignment()
    {
        Alignment = Reputation switch
        {
            >= 30 => "ally",
            <= -30 => "hostile",
            _ => "neutral",
        };
    }
}
