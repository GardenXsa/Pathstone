using MyGame.Core.Common;

namespace MyGame.Core.Rules;

/// <summary>
/// Standard difficulty classes for a d20 check, modelled on D&amp;D 5e's
/// table. Use as the <c>dc</c> argument to <see cref="Check.RollCheck"/> via
/// the enum overload.
/// </summary>
public enum DifficultyClass
{
    /// <summary>Very easy task.</summary>
    Easy = 5,

    /// <summary>Moderate task.</summary>
    Medium = 10,

    /// <summary>Tough task.</summary>
    Hard = 15,

    /// <summary>Very tough task.</summary>
    VeryHard = 20,

    /// <summary>All but impossible.</summary>
    NearlyImpossible = 25,
}

/// <summary>
/// Outcome of a d20 check resolution.
///
/// Port of the relevant slice of <c>engine/rules/check.ts</c>. The TS
/// <c>CheckResult</c> was a big multi-mechanic struct (d20, d100, 3d6, PbtA,
/// d6pool, ...). The C# port (per the task spec) keeps only the d20 mechanic
/// — the desktop rewrite is d20-only at this layer — but uses the same field
/// names where applicable so a future port of the other mechanics slots in
/// cleanly.
///
/// Crit rules: a natural 20 is always a <see cref="CriticalSuccess"/> (and
/// counts as <see cref="Success"/>); a natural 1 is always a
/// <see cref="CriticalFailure"/> (and never <see cref="Success"/>); any other
/// roll is a success iff <see cref="Total"/> &gt;= DC.
/// </summary>
public readonly record struct CheckResult(
    int Roll,
    int Modifier,
    int Total,
    bool Success,
    bool CriticalSuccess,
    bool CriticalFailure);

/// <summary>
/// d20 skill/check resolver.
/// </summary>
public static class Check
{
    /// <summary>
    /// Resolve a d20 check against <paramref name="dc"/>.
    /// Roll = natural d20 result (1..20). Modifier is added to produce
    /// Total. Crit success on natural 20, crit failure on natural 1, else
    /// success iff Total &gt;= dc.
    /// </summary>
    public static CheckResult RollCheck(Rng rng, int modifier, int dc)
    {
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        int roll = D20.Roll(rng, 20);
        int total = roll + modifier;

        bool critSuccess = roll == 20;
        bool critFailure = roll == 1;

        bool success;
        if (critSuccess) success = true;
        else if (critFailure) success = false;
        else success = total >= dc;

        return new CheckResult(
            Roll: roll,
            Modifier: modifier,
            Total: total,
            Success: success,
            CriticalSuccess: critSuccess,
            CriticalFailure: critFailure);
    }

    /// <summary>
    /// Convenience overload taking a <see cref="DifficultyClass"/> enum
    /// instead of a raw int DC.
    /// </summary>
    public static CheckResult RollCheck(Rng rng, int modifier, DifficultyClass dc)
        => RollCheck(rng, modifier, (int)dc);
}
