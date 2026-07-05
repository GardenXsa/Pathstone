using MyGame.Core.Common;

namespace MyGame.Core.Rules;

/// <summary>
/// Dice primitives shared across ALL rulesets.
///
/// Port of <c>engine/rules/d20.ts</c>. The TS module kept only the universal
/// pieces (flat dice rolling, advantage/disadvantage, stat roll); this C#
/// port preserves that scope and the exact roll semantics:
///  - <see cref="Roll(Rng, int)"/> returns 1..<paramref name="sides"/>
///    inclusive, matching <c>rng.die(sides)</c>.
///  - <see cref="RollStat(Rng)"/> does 4d6-drop-lowest summing 3, matching
///    the canonical D&amp;D ability-score generation.
///  - <see cref="Advantage(Rng, int)"/> / <see cref="Disadvantage(Rng, int)"/>
///    roll twice and take max / min respectively.
///
/// All randomness goes through the injected <see cref="Rng"/> so rolls are
/// reproducible from a save's seed.
/// </summary>
public static class D20
{
    /// <summary>
    /// Roll a single die with <paramref name="sides"/> faces. Returns
    /// 1..<paramref name="sides"/> inclusive; returns 0 if
    /// <paramref name="sides"/> &lt; 1 (matches TS <c>rng.die</c>).
    /// </summary>
    public static int Roll(Rng rng, int sides = 20)
    {
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        if (sides < 1) return 0;
        // NextInt(min, max) is exclusive of max, so +1 to make it inclusive.
        return rng.NextInt(1, sides + 1);
    }

    /// <summary>
    /// Roll a single die and add <paramref name="modifier"/>.
    /// Equivalent to <c>Roll(rng, sides) + modifier</c>.
    /// </summary>
    public static int RollWithModifier(Rng rng, int sides, int modifier)
    {
        return Roll(rng, sides) + modifier;
    }

    /// <summary>
    /// Generate an ability score: roll 4d6, drop the lowest, sum the
    /// remaining 3. Result is in [3, 18].
    /// </summary>
    public static int RollStat(Rng rng)
    {
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        Span<int> rolls = stackalloc int[4];
        for (int i = 0; i < 4; i++)
            rolls[i] = rng.NextInt(1, 7); // 1..6 inclusive

        // Sort ascending, sum the top three (drop rolls[0]).
        SortAscending(rolls);
        return rolls[1] + rolls[2] + rolls[3];
    }

    /// <summary>
    /// Roll twice, take the higher. Matches D&amp;D 5e advantage.
    /// </summary>
    public static int Advantage(Rng rng, int sides = 20)
    {
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        if (sides < 1) return 0;
        int a = rng.NextInt(1, sides + 1);
        int b = rng.NextInt(1, sides + 1);
        return Math.Max(a, b);
    }

    /// <summary>
    /// Roll twice, take the lower. Matches D&amp;D 5e disadvantage.
    /// </summary>
    public static int Disadvantage(Rng rng, int sides = 20)
    {
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        if (sides < 1) return 0;
        int a = rng.NextInt(1, sides + 1);
        int b = rng.NextInt(1, sides + 1);
        return Math.Min(a, b);
    }

    // Tiny insertion sort for a length-4 span. Avoids allocating an array or
    // pulling in Array.Sort for a hot path.
    private static void SortAscending(Span<int> values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            int v = values[i];
            int j = i - 1;
            while (j >= 0 && values[j] > v)
            {
                values[j + 1] = values[j];
                j--;
            }
            values[j + 1] = v;
        }
    }
}
