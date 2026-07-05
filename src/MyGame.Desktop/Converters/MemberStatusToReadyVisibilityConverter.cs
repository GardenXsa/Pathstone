using System;
using System.Globalization;
using Avalonia.Data.Converters;
using MyGame.Core.Multiplayer;

namespace MyGame.Desktop.Converters;

/// <summary>
/// Issue #77 — visibility converter for the lobby's per-member
/// ready-status indicators. Bound to a <see cref="MemberStatus"/> enum
/// value; the <c>ConverterParameter</c> is a string naming the status
/// to match ("Ready" / "Pending" / "Playing" / "Disconnected"). Returns
/// true when the bound status matches the parameter, false otherwise.
///
/// <para>
/// Typical XAML usage (two TextBlocks side-by-side, one shown when the
/// member is Ready, the other when Pending):
/// <code>
/// &lt;TextBlock Text="✓"
///            IsVisible="{Binding Status,
///                Converter={x:Static conv:MemberStatusToReadyVisibilityConverter.Instance},
///                ConverterParameter=Ready}"/&gt;
/// &lt;TextBlock Text="○"
///            IsVisible="{Binding Status,
///                Converter={x:Static conv:MemberStatusToReadyVisibilityConverter.Instance},
///                ConverterParameter=Pending}"/&gt;
/// </code>
/// </para>
/// <para>
/// Status matching is case-insensitive against the enum's name, so
/// either <c>Ready</c> or <c>ready</c> works as the parameter. Unknown
/// parameter strings always return false (the indicator stays hidden).
/// </para>
/// </summary>
public sealed class MemberStatusToReadyVisibilityConverter : IValueConverter
{
    /// <summary>Shared immutable instance for XAML static-resource use.</summary>
    public static readonly MemberStatusToReadyVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MemberStatus status) return false;
        if (parameter is not string paramName || string.IsNullOrEmpty(paramName)) return false;
        return status.ToString().Equals(paramName, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("MemberStatusToReadyVisibilityConverter is one-way only.");
}
