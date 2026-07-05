using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MyGame.Desktop.Converters;

/// <summary>
/// Maps an item rarity string ("common" / "uncommon" / "rare" /
/// "veryRare" / "legendary" / "artifact") to the matching color brush
/// used by the inventory panel. Falls back to the common-gray brush
/// for unknown values.
///
/// <para>
/// ITEM-RARITY (issue #66). Avalonia 11+ doesn't allow binding the
/// <c>Classes</c> property (it accepts a string literal only), so
/// instead of <c>Classes="{Binding RarityClass}"</c> we bind
/// <c>Foreground</c>/<c>BorderBrush</c> directly via this converter.
/// </para>
///
/// <para>
/// Color palette (matches the spec):
/// <list type="bullet">
///   <item>common    → <c>#9ca3af</c> (gray)</item>
///   <item>uncommon  → <c>#10b981</c> (green)</item>
///   <item>rare      → <c>#3b82f6</c> (blue)</item>
///   <item>veryRare  → <c>#a855f7</c> (purple)</item>
///   <item>legendary → <c>#f59e0b</c> (orange)</item>
///   <item>artifact  → <c>#ef4444</c> (red)</item>
/// </list>
/// </para>
/// </summary>
public sealed class RarityBrushConverter : IValueConverter
{
    /// <summary>Shared immutable instance for XAML static-resource use.</summary>
    public static readonly RarityBrushConverter Instance = new();

    private static readonly IReadOnlyDictionary<string, IBrush> Brushes =
        new Dictionary<string, IBrush>(StringComparer.Ordinal)
        {
            ["common"]    = new SolidColorBrush(Color.Parse("#9ca3af")),
            ["uncommon"]  = new SolidColorBrush(Color.Parse("#10b981")),
            ["rare"]      = new SolidColorBrush(Color.Parse("#3b82f6")),
            ["veryRare"]  = new SolidColorBrush(Color.Parse("#a855f7")),
            ["legendary"] = new SolidColorBrush(Color.Parse("#f59e0b")),
            ["artifact"]  = new SolidColorBrush(Color.Parse("#ef4444")),
        };

    /// <summary>Default brush (common gray) used for unknown rarities.</summary>
    public static IBrush Fallback => Brushes["common"];

    /// <summary>Look up the brush for a rarity string. Never null.</summary>
    public static IBrush For(string? rarity) =>
        rarity is not null && Brushes.TryGetValue(rarity, out var b) ? b : Fallback;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => For(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("RarityBrushConverter is one-way only.");
}
