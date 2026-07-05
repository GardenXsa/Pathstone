using MyGame.Core.Common;
using MyGame.Core.Rules;

namespace MyGame.Tests.Rules;

/// <summary>
/// Unit tests for the d20 check resolver. Covers crit-success / crit-failure
/// bands, success-by-meeting-DC, and explicit-failure. The Rng can't be
/// easily mocked (it's a sealed class), so each test finds a seed whose
/// first d20 roll produces the value needed for the scenario.
/// </summary>
public class CheckTests
{
    /// <summary>
    /// Find a seed such that the FIRST call to D20.Roll(rng, 20) returns
    /// <paramref name="targetRoll"/>. The returned Rng is fresh (no rolls
    /// consumed) so the test can hand it straight to
    /// <see cref="Check.RollCheck"/> which will draw that same first roll.
    /// </summary>
    private static Rng FindRngWithFirstRoll(int targetRoll)
    {
        // Each d20 face has a 1/20 = 5% probability, so a hit shows up
        // within ~20 seeds on average. Cap the search at 100k seeds for
        // safety; if we ever blow through that we'd want to know.
        for (long seed = 0; seed < 100_000; seed++)
        {
            // Probe a throwaway copy so we don't consume the candidate.
            if (D20.Roll(new Rng(seed), 20) == targetRoll)
                return new Rng(seed);
        }
        throw new InvalidOperationException(
            $"Could not find a seed producing a first-roll d20 of {targetRoll}.");
    }

    [Fact]
    public void RollCheck_Natural20_IsCriticalSuccess()
    {
        var rng = FindRngWithFirstRoll(20);
        var result = Check.RollCheck(rng, modifier: 0, dc: 25);

        Assert.Equal(20, result.Roll);
        Assert.True(result.CriticalSuccess);
        Assert.False(result.CriticalFailure);
        // A natural 20 is always a success regardless of DC.
        Assert.True(result.Success);
    }

    [Fact]
    public void RollCheck_Natural20_SucceedsEvenAgainstImpossibleDC()
    {
        // Crit success ignores DC entirely. DC 100 would normally be
        // unreachable, but a natural 20 still wins.
        var rng = FindRngWithFirstRoll(20);
        var result = Check.RollCheck(rng, modifier: 0, dc: 100);

        Assert.True(result.CriticalSuccess);
        Assert.True(result.Success);
    }

    [Fact]
    public void RollCheck_Natural1_IsCriticalFailure()
    {
        var rng = FindRngWithFirstRoll(1);
        var result = Check.RollCheck(rng, modifier: 50, dc: 5);

        Assert.Equal(1, result.Roll);
        Assert.True(result.CriticalFailure);
        Assert.False(result.CriticalSuccess);
        // A natural 1 is always a failure even if the modifier would
        // otherwise push Total over the DC.
        Assert.False(result.Success);
    }

    [Fact]
    public void RollCheck_Natural1_FailsEvenWithHugeModifier()
    {
        var rng = FindRngWithFirstRoll(1);
        // Total = 1 + 1000 = 1001 >> dc 5, but nat 1 still fails.
        var result = Check.RollCheck(rng, modifier: 1000, dc: 5);
        Assert.False(result.Success);
        Assert.True(result.CriticalFailure);
    }

    [Fact]
    public void RollCheck_TotalMeetsDC_IsSuccess()
    {
        // Roll 10, modifier 5, dc 15 → Total 15 = dc → success, no crit.
        var rng = FindRngWithFirstRoll(10);
        var result = Check.RollCheck(rng, modifier: 5, dc: 15);

        Assert.Equal(10, result.Roll);
        Assert.Equal(5, result.Modifier);
        Assert.Equal(15, result.Total);
        Assert.True(result.Success);
        Assert.False(result.CriticalSuccess);
        Assert.False(result.CriticalFailure);
    }

    [Fact]
    public void RollCheck_TotalExceedsDC_IsSuccess()
    {
        var rng = FindRngWithFirstRoll(15);
        var result = Check.RollCheck(rng, modifier: 3, dc: 10);

        Assert.Equal(15, result.Roll);
        Assert.Equal(3, result.Modifier);
        Assert.Equal(18, result.Total);
        Assert.True(result.Success);
        Assert.False(result.CriticalSuccess);
        Assert.False(result.CriticalFailure);
    }

    [Fact]
    public void RollCheck_TotalBelowDC_IsFailure()
    {
        var rng = FindRngWithFirstRoll(5);
        var result = Check.RollCheck(rng, modifier: 0, dc: 15);

        Assert.Equal(5, result.Roll);
        Assert.Equal(0, result.Modifier);
        Assert.Equal(5, result.Total);
        Assert.False(result.Success);
        Assert.False(result.CriticalSuccess);
        Assert.False(result.CriticalFailure);
    }

    [Fact]
    public void RollCheck_EnumDcOverload_MatchesIntDc()
    {
        var rng = FindRngWithFirstRoll(12);
        var viaEnum = Check.RollCheck(rng, modifier: 5, DifficultyClass.Hard);
        // Hard = 15. Total = 12 + 5 = 17 → success.
        Assert.True(viaEnum.Success);
        Assert.Equal(15, (int)DifficultyClass.Hard);
    }

    [Fact]
    public void RollCheck_NullRng_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => Check.RollCheck(null!, modifier: 0, dc: 10));

    [Fact]
    public void RollCheck_AllFacesProduceValidResult()
    {
        // Sanity: for every possible d20 face (1..20), the resolver should
        // produce a well-formed CheckResult (no exceptions, Total = roll +
        // modifier, modifier echoed back).
        for (int face = 1; face <= 20; face++)
        {
            var rng = FindRngWithFirstRoll(face);
            var r = Check.RollCheck(rng, modifier: 4, dc: 12);
            Assert.Equal(face, r.Roll);
            Assert.Equal(4, r.Modifier);
            Assert.Equal(face + 4, r.Total);
            // Crit bands only on 1 and 20.
            Assert.Equal(face == 20, r.CriticalSuccess);
            Assert.Equal(face == 1, r.CriticalFailure);
        }
    }
}
