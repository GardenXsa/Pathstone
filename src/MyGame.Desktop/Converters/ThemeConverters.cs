using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.Converters;

/// <summary>
/// Two-way string-equality converter used to bind a string property to
/// a group of <c>RadioButton.IsChecked</c> values (issue #47).
///
/// <para>
/// Forward direction: take the bound string value, return <c>true</c> if
/// it equals the <c>ConverterParameter</c> (case-insensitive). Used so
/// each RadioButton reflects "is this the currently selected option?".
/// </para>
///
/// <para>
/// Reverse direction: when the RadioButton becomes checked, write the
/// <c>ConverterParameter</c> back to the source property (so e.g.
/// clicking the "Light" radio sets <c>ThemeMode = "Light"</c>). When the
/// RadioButton becomes unchecked, return
/// <see cref="AvaloniaProperty.UnsetValue"/> so the source is left alone
/// — otherwise unchecking one radio would null-out the property before
/// the newly-checked radio's <c>ConvertBack</c> fires.
/// </para>
///
/// <para>
/// Used by the SettingsView's «Оформление» tab to bind the three theme-
/// mode RadioButtons (Dark / Light / System) to
/// <see cref="ViewModels.SettingsViewModel.ThemeMode"/>.
/// </para>
/// </summary>
public sealed class ThemeModeEqualsConverter : IValueConverter
{
    /// <summary>Shared immutable instance for XAML {x:Static} use.</summary>
    public static readonly ThemeModeEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        var p = parameter as string;
        if (s is null || p is null) return false;
        return string.Equals(s, p, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only the "checked → true" transition should write back; the
        // "unchecked → false" transition returns UnsetValue so the source
        // keeps its current value (the newly-checked sibling radio will
        // fire its own ConvertBack with true).
        if (value is bool b && b)
            return parameter as string ?? "Dark";
        return AvaloniaProperty.UnsetValue;
    }
}

/// <summary>
/// One-way converter that maps an accent preset name (Indigo / Emerald /
/// Amber / Rose / Cyan / Violet) to a <see cref="SolidColorBrush"/>
/// filled with that accent's hex color (issue #47).
///
/// <para>
/// Delegates to <see cref="ThemeService.GetAccentHex"/> so the palette
/// stays in one place. Used by the SettingsView's accent-swatch buttons
/// (each swatch's <c>Background</c> is bound to its name via this
/// converter so the swatch shows the actual color it represents).
/// </para>
/// </summary>
public sealed class AccentHexConverter : IValueConverter
{
    /// <summary>Shared immutable instance for XAML {x:Static} use.</summary>
    public static readonly AccentHexConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string;
        var hex = ThemeService.GetAccentHex(name ?? "");
        // Avalonia 12's Brush class doesn't expose a TryParse, so we
        // parse via Color.TryParse (which does exist) and wrap in a
        // SolidColorBrush. Falls back to gray on any parse failure.
        if (Color.TryParse(hex, out var c))
            return new SolidColorBrush(c);
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("AccentHexConverter is one-way only.");
}

/// <summary>
/// One-way converter that maps an int N to N-1, clamped to a minimum of
/// 1. Used by the inventory panel's split-stack dialog to bind the
/// NumericUpDown's Maximum to <c>SplitTarget.Quantity - 1</c> (the user
/// can split at most N-1 units so the original stack still has 1 left).
/// </summary>
public sealed class SplitMaxMinusOneConverter : IValueConverter
{
    /// <summary>Shared immutable instance for XAML {x:Static} use.</summary>
    public static readonly SplitMaxMinusOneConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i) return Math.Max(1, i - 1);
        if (value is long l) return (int)Math.Max(1L, l - 1);
        return 1;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("SplitMaxMinusOneConverter is one-way only.");
}
