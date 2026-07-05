namespace MyGame.Core.Common;

/// <summary>
/// Deterministic, seedable pseudo-random number generator.
///
/// Port of <c>engine/core/rng.ts</c>. The TS original used mulberry32 with a
/// 32-bit state; this C# port upgrades to <c>PCG32</c> (Permuted Congruential
/// Generator) with a 64-bit state — better statistical quality, longer
/// period, and a state that fits in a single <see langword="long"/> for
/// trivial save/load round-tripping.
///
/// Why seeded? Same reason as the TS port:
///  - dice rolls are reproducible from a save (anti-cheat / debugging / replay)
///  - the world simulation can be deterministic given a seed.
///
/// The engine holds one <see cref="Rng"/> per save; the seed/state is
/// persisted in save meta via <see cref="State"/> + <see cref="FromState"/>.
///
/// Thread safety: NOT thread-safe by design. The engine is single-threaded;
/// if a subsystem (e.g. an AI sub-agent) needs its own stream it should call
/// <see cref="Fork"/> to obtain an independent <see cref="Rng"/>.
/// </summary>
public sealed class Rng
{
    // PCG32 constants (see O'Neill 2014, pcg-random.org).
    // state = state * Multiplier + Increment  (mod 2^64)
    private const ulong Multiplier = 6364136223846793005UL;
    // Must be odd; this is the standard default stream/increment.
    private const ulong Increment = 1442695040888963407UL;

    private ulong _state;

    /// <summary>
    /// Create a fresh generator seeded from <paramref name="seed"/>. Two
    /// instances created with the same seed produce identical sequences.
    /// </summary>
    public Rng(long seed)
    {
        // Standard PCG seeding: start at zero, advance, mix in seed, advance.
        _state = 0UL;
        Step();
        _state = unchecked(_state + (ulong)seed);
        Step();
    }

    // Private ctor for FromState — bypasses the seeding dance.
    private Rng(ulong state)
    {
        _state = state;
    }

    /// <summary>
    /// Snapshot of the internal state. Persist this in save meta to allow
    /// exact round-trip via <see cref="FromState"/>.
    /// </summary>
    public long State
    {
        get => (long)_state;
        set => _state = (ulong)value;
    }

    /// <summary>
    /// Reconstruct a <see cref="Rng"/> from a previously snapshotted state.
    /// The restored generator continues exactly where the original left off.
    /// </summary>
    public static Rng FromState(long state) => new Rng((ulong)state);

    /// <summary>
    /// Advance the LCG state by one step (no output produced). Used during
    /// seeding to mix in the seed value.
    /// </summary>
    private void Step()
    {
        _state = unchecked(_state * Multiplier + Increment);
    }

    /// <summary>
    /// Advance the PCG state by one step and produce a 32-bit output.
    /// Private — callers use <see cref="Next"/> / <see cref="NextInt(int)"/>.
    /// </summary>
    private uint NextUint()
    {
        ulong oldstate = _state;
        _state = unchecked(oldstate * Multiplier + Increment);
        // PCG-XSH-RR output function.
        ulong xorshifted = ((oldstate >> 18) ^ oldstate) >> 27;
        int rot = (int)(oldstate >> 59);
        return (uint)((xorshifted >> rot) | (xorshifted << ((-rot) & 31)));
    }

    /// <summary>
    /// Uniform double in [0, 1). Uses the top 32 bits of state output divided
    /// by 2^32, so the result is strictly less than 1.
    /// </summary>
    public double Next() => NextUint() / 4294967296.0;

    /// <summary>
    /// Uniform integer in [0, max). Matches <see cref="Random.Next(int)"/>
    /// convention — exclusive upper bound.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">max &lt;= 0.</exception>
    public int NextInt(int max)
    {
        if (max <= 0)
            throw new ArgumentOutOfRangeException(nameof(max), "max must be positive.");
        return (int)(NextUint() % (uint)max);
    }

    /// <summary>
    /// Uniform integer in [min, max). Matches
    /// <see cref="Random.Next(int,int)"/> convention — exclusive upper bound.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">min &gt;= max.</exception>
    public int NextInt(int min, int max)
    {
        if (min >= max)
            throw new ArgumentOutOfRangeException(nameof(max), "max must be greater than min.");
        uint range = (uint)(max - min);
        return min + (int)(NextUint() % range);
    }

    /// <summary>
    /// Pick a uniformly random element from a non-empty list. Does not mutate
    /// the list.
    /// </summary>
    /// <exception cref="ArgumentException">items is empty.</exception>
    public T Pick<T>(IReadOnlyList<T> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (items.Count == 0)
            throw new ArgumentException("items must be non-empty.", nameof(items));
        return items[NextInt(items.Count)];
    }

    /// <summary>
    /// In-place Fisher-Yates shuffle. The list is mutated; no copy is made.
    /// Port of <c>rng.shuffle</c> from the TS (note: TS returned a new array,
    /// here we mutate to match <see cref="List{T}"/> / array idioms).
    /// </summary>
    public void Shuffle<T>(IList<T> list)
    {
        if (list is null) throw new ArgumentNullException(nameof(list));
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Spawn an independent <see cref="Rng"/> whose stream is derived from
    /// the current state. Consumes 64 bits of state from the parent. Useful
    /// for handing an AI sub-agent its own deterministic stream without
    /// sharing mutable state.
    /// </summary>
    public Rng Fork()
    {
        ulong lo = NextUint();
        ulong hi = NextUint();
        ulong combined = lo | (hi << 32);
        // Rng(long) does the full PCG seeding dance; the cast to long is
        // lossless because we feed it back through unchecked arithmetic.
        return new Rng((long)combined);
    }
}
