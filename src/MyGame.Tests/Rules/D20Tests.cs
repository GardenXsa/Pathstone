using MyGame.Core.Common;
using MyGame.Core.Rules;

namespace MyGame.Tests.Rules;

/// <summary>
/// Unit tests for the D20 dice primitives. Covers Roll value ranges,
/// the default-sides convenience, RollStat bounds, and a statistical
/// sanity check on advantage vs disadvantage.
/// </summary>
public class D20Tests
{
    private const int SampleCount = 1000;

    [Fact]
    public void Roll_ReturnsValueInRange()
    {
        var rng = new Rng(42);
        for (int i = 0; i < SampleCount; i++)
        {
            var v = D20.Roll(rng, 20);
            Assert.InRange(v, 1, 20);
        }
    }

    [Fact]
    public void Roll_DefaultSidesIs20()
    {
        var rng = new Rng(7);
        for (int i = 0; i < SampleCount; i++)
        {
            // No `sides` arg → defaults to 20. Result must be in [1, 20].
            var v = D20.Roll(rng);
            Assert.InRange(v, 1, 20);
        }
    }

    [Fact]
    public void Roll_CustomSidesRespected()
    {
        var rng = new Rng(13);
        for (int i = 0; i < SampleCount; i++)
        {
            var v = D20.Roll(rng, 6);
            Assert.InRange(v, 1, 6);
        }
    }

    [Fact]
    public void Roll_ZeroOrNegativeSides_ReturnsZero()
    {
        var rng = new Rng(1);
        Assert.Equal(0, D20.Roll(rng, 0));
        Assert.Equal(0, D20.Roll(rng, -5));
    }

    [Fact]
    public void Roll_NullRng_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => D20.Roll(null!));
    }

    [Fact]
    public void RollWithModifier_AddsModifier()
    {
        var rngA = new Rng(99);
        var rngB = new Rng(99);
        // Same seed → same base roll; RollWithModifier adds the modifier.
        var raw = D20.Roll(rngA, 20);
        Assert.Equal(raw + 5, D20.RollWithModifier(rngB, 20, 5));
        Assert.Equal(raw - 3, D20.RollWithModifier(new Rng(99), 20, -3));
    }

    [Fact]
    public void RollStat_ReturnsValueInRange()
    {
        var rng = new Rng(2024);
        for (int i = 0; i < 100; i++)
        {
            var v = D20.RollStat(rng);
            // 4d6-drop-lowest: min 3 (four 1s) — max 18 (three 6s).
            Assert.InRange(v, 3, 18);
        }
    }

    [Fact]
    public void RollStat_DropsLowest_AtLeastThree()
    {
        // Hard to test the drop-lowest mechanic directly without
        // intercepting the inner rolls. Sanity check: every result is
        // >= 3 (3d6 floor) and <= 18 (3d6 ceiling). A result of exactly
        // 3 is possible only if all four dice rolled 1 — rare but legal.
        var rng = new Rng(55);
        for (int i = 0; i < 500; i++)
        {
            var v = D20.RollStat(rng);
            Assert.True(v >= 3, $"RollStat returned {v} < 3");
            Assert.True(v <= 18, $"RollStat returned {v} > 18");
        }
    }

    [Fact]
    public void RollStat_DistributionFavorsMiddle()
    {
        // 4d6-drop-lowest has a bell-ish curve centered near 13. Over
        // a large sample the mean should be in [10, 15] — far from the
        // extremes. (Not asserting exact statistics — just sanity.)
        var rng = new Rng(12345);
        double sum = 0;
        const int N = 2000;
        for (int i = 0; i < N; i++)
            sum += D20.RollStat(rng);
        var mean = sum / N;
        Assert.InRange(mean, 10.0, 15.0);
    }

    [Fact]
    public void RollStat_NullRng_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => D20.RollStat(null!));
    }

    [Fact]
    public void Advantage_ReturnsValueInRange()
    {
        var rng = new Rng(11);
        for (int i = 0; i < SampleCount; i++)
        {
            var v = D20.Advantage(rng, 20);
            Assert.InRange(v, 1, 20);
        }
    }

    [Fact]
    public void Disadvantage_ReturnsValueInRange()
    {
        var rng = new Rng(22);
        for (int i = 0; i < SampleCount; i++)
        {
            var v = D20.Disadvantage(rng, 20);
            Assert.InRange(v, 1, 20);
        }
    }

    [Fact]
    public void Advantage_GreaterOrEqualToDisadvantage_OnAverage()
    {
        // Statistical sanity: over many rolls, advantage's average should
        // be >= disadvantage's average. Using a fixed seed for determinism
        // — the property holds robustly for any non-degenerate PRNG with
        // far more samples than 100 pairs, so a single seed suffices.
        var rngAdv = new Rng(31337);
        var rngDis = new Rng(31337);

        const int Pairs = 500;
        double advSum = 0, disSum = 0;
        for (int i = 0; i < Pairs; i++)
        {
            advSum += D20.Advantage(rngAdv, 20);
            disSum += D20.Disadvantage(rngDis, 20);
        }

        var advAvg = advSum / Pairs;
        var disAvg = disSum / Pairs;
        Assert.True(advAvg >= disAvg,
            $"Advantage average {advAvg} should be >= Disadvantage average {disAvg}");
    }

    [Fact]
    public void Advantage_NullRng_Throws() =>
        Assert.Throws<ArgumentNullException>(() => D20.Advantage(null!));

    [Fact]
    public void Disadvantage_NullRng_Throws() =>
        Assert.Throws<ArgumentNullException>(() => D20.Disadvantage(null!));
}
