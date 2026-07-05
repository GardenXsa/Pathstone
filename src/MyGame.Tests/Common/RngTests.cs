using MyGame.Core.Common;

namespace MyGame.Tests.Common;

/// <summary>
/// Unit tests for the PCG32 deterministic PRNG. Covers the value ranges,
/// determinism / state round-trip, fork independence, and the
/// shuffle / pick helpers.
/// </summary>
public class RngTests
{
    private const int SampleCount = 1000;

    [Fact]
    public void Next_ReturnsValueInUnitInterval()
    {
        var rng = new Rng(42);
        for (int i = 0; i < SampleCount; i++)
        {
            var v = rng.Next();
            Assert.InRange(v, 0.0, 1.0);
            Assert.True(v < 1.0, $"Next() returned >= 1.0: {v}");
        }
    }

    [Fact]
    public void NextInt_MaxIsExclusive()
    {
        var rng = new Rng(7);
        for (int i = 0; i < SampleCount; i++)
        {
            var v = rng.NextInt(10);
            Assert.InRange(v, 0, 9);
        }
    }

    [Fact]
    public void NextInt_MinMax_RespectsBounds()
    {
        var rng = new Rng(123);
        for (int i = 0; i < SampleCount; i++)
        {
            var v = rng.NextInt(5, 10);
            // min inclusive, max exclusive (matches System.Random semantics)
            Assert.InRange(v, 5, 9);
        }
    }

    [Fact]
    public void NextInt_NonPositiveMax_Throws()
    {
        var rng = new Rng(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(-5));
    }

    [Fact]
    public void NextInt_MinGreaterThanOrEqualMax_Throws()
    {
        var rng = new Rng(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(6, 5));
    }

    [Fact]
    public void SameSeed_ProducesIdenticalSequence()
    {
        var a = new Rng(987654321L);
        var b = new Rng(987654321L);
        for (int i = 0; i < 100; i++)
            Assert.Equal(a.Next(), b.Next());
    }

    [Fact]
    public void State_RoundTrips()
    {
        // Capture state mid-stream and reconstruct; the new Rng must
        // continue exactly where the original left off.
        var original = new Rng(42);
        for (int i = 0; i < 10; i++)
            original.Next();

        var snapshot = original.State;
        var restored = Rng.FromState(snapshot);

        for (int i = 0; i < 10; i++)
            Assert.Equal(original.Next(), restored.Next());
    }

    [Fact]
    public void State_Setter_RestoresStream()
    {
        var original = new Rng(2024);
        for (int i = 0; i < 5; i++) original.Next();
        var snap = original.State;

        var other = new Rng(0);
        other.State = snap;

        for (int i = 0; i < 10; i++)
            Assert.Equal(original.Next(), other.Next());
    }

    [Fact]
    public void Fork_ProducesIndependentStream()
    {
        var parent = new Rng(0xDEADBEEF);
        var child = parent.Fork();

        // The fork consumes state from the parent — so the parent's next
        // output is NOT what it would have been without forking. The child
        // also produces its own values; assert neither matches a fresh
        // parent at the same seed.
        var freshParent = new Rng(0xDEADBEEF);

        // freshParent didn't fork, so its outputs differ from parent's
        // (whose state advanced by 2 uint pulls during Fork).
        var parentNext = parent.Next();
        var freshNext = freshParent.Next();
        Assert.NotEqual(parentNext, freshNext);

        // And the child produces values distinct from both.
        var childNext = child.Next();
        Assert.NotEqual(parentNext, childNext);
        Assert.NotEqual(freshNext, childNext);
    }

    [Fact]
    public void Fork_IsDeterministic()
    {
        // Two parents with the same seed must produce identical forks —
        // the forked stream is a function of the parent's state.
        var p1 = new Rng(555);
        var p2 = new Rng(555);
        var c1 = p1.Fork();
        var c2 = p2.Fork();
        for (int i = 0; i < 50; i++)
            Assert.Equal(c1.Next(), c2.Next());
    }

    [Fact]
    public void Shuffle_PreservesElements()
    {
        var rng = new Rng(3);
        var original = Enumerable.Range(1, 50).ToList();
        var copy = original.ToList();
        rng.Shuffle(copy);

        // Same elements (multiset equality), possibly different order.
        Assert.Equal(
            original.OrderBy(x => x),
            copy.OrderBy(x => x));

        // Sanity: with a 50-element list and a non-degenerate PRNG, the
        // shuffle is overwhelmingly likely to move at least one element.
        // If it didn't, that's a strong signal the shuffle is a no-op.
        var moved = original.Where((t, i) => copy[i] != t).Count();
        Assert.True(moved > 0, "Shuffle did not change the list ordering.");
    }

    [Fact]
    public void Shuffle_EmptyAndSingle_DoesNotThrow()
    {
        var rng = new Rng(1);
        var empty = new List<int>();
        rng.Shuffle(empty);
        Assert.Empty(empty);

        var single = new List<string> { "x" };
        rng.Shuffle(single);
        Assert.Equal("x", single[0]);
    }

    [Fact]
    public void Shuffle_NullList_Throws()
    {
        var rng = new Rng(1);
        Assert.Throws<ArgumentNullException>(() => rng.Shuffle<int>(null!));
    }

    [Fact]
    public void Pick_ReturnsElementFromList()
    {
        var rng = new Rng(99);
        var list = new List<int> { 10, 20, 30, 40, 50 };
        for (int i = 0; i < 100; i++)
        {
            var picked = rng.Pick(list);
            Assert.Contains(picked, list);
        }
    }

    [Fact]
    public void Pick_EmptyList_Throws()
    {
        var rng = new Rng(1);
        Assert.Throws<ArgumentException>(() => rng.Pick(new List<int>()));
    }

    [Fact]
    public void Pick_NullList_Throws()
    {
        var rng = new Rng(1);
        Assert.Throws<ArgumentNullException>(() => rng.Pick<int>(null!));
    }

    [Fact]
    public void Pick_DistributesReasonably()
    {
        // Statistical sanity: over many picks from a 2-element list, both
        // elements should be picked. (Not asserting exact distribution —
        // that's flaky — just that both show up at all.)
        var rng = new Rng(4242);
        var list = new List<int> { 0, 1 };
        var seen = new bool[2];
        for (int i = 0; i < 200; i++)
            seen[rng.Pick(list)] = true;
        Assert.True(seen[0] && seen[1], "Pick never returned one of the elements.");
    }
}
