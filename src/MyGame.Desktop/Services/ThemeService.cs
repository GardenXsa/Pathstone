using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using MyGame.Core.Profile;

namespace MyGame.Desktop.Services;

/// <summary>
/// Runtime theme + accent + animation switching (issue #47 + #46).
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["AppBackground"] = "#0F1115",
        ["AppSurface"]    = "#171A21",
        ["AppSurfaceAlt"] = "#1F2330",
        ["AppBorder"]     = "#2C3142",
        ["AppForeground"] = "#E6E8EC",
        ["AppMuted"]      = "#8A93A6",
        ["AppDanger"]     = "#E5484D",
        ["AppSuccess"]    = "#3DD68C",
    };

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["AppBackground"] = "#FAFAFA",
        ["AppSurface"]    = "#FFFFFF",
        ["AppSurfaceAlt"] = "#F3F4F6",
        ["AppBorder"]     = "#D1D5DB",
        ["AppForeground"] = "#1F2937",
        ["AppMuted"]      = "#6B7280",
        ["AppDanger"]     = "#DC2626",
        ["AppSuccess"]    = "#059669",
    };

    private static readonly Dictionary<string, (string Accent, string AccentFg)> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Indigo"]  = ("#7C5CFF", "#FFFFFF"),
        ["Emerald"] = ("#10B981", "#FFFFFF"),
        ["Amber"]   = ("#F59E0B", "#1F2937"),
        ["Rose"]    = ("#F43F5E", "#FFFFFF"),
        ["Cyan"]    = ("#06B6D4", "#FFFFFF"),
        ["Violet"]  = ("#8B5CF6", "#FFFFFF"),
    };

    public static IReadOnlyList<string> AccentPresetNames { get; } =
        new[] { "Indigo", "Emerald", "Amber", "Rose", "Cyan", "Violet" };

    public static string GetAccentHex(string name) =>
        Accents.TryGetValue(name ?? "", out var v) ? v.Accent : Accents["Indigo"].Accent;

    public static string GetAccentFgHex(string name) =>
        Accents.TryGetValue(name ?? "", out var v) ? v.AccentFg : Accents["Indigo"].AccentFg;

    /// <summary>
    /// Apply theme + accent + animation flag. Never throws — all errors
    /// are caught and logged via Trace.
    /// </summary>
    public static void ApplyTheme(string themeMode, string accentName, bool enableAnimations)
    {
        try
        {
            ApplyThemeInternal(themeMode, accentName, enableAnimations);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ThemeService] ApplyTheme FAILED: {ex}");
        }
    }

    private static void ApplyThemeInternal(string themeMode, string accentName, bool enableAnimations)
    {
        var app = Application.Current;
        if (app is null) return;

        // 1) FluentTheme variant.
        var mode = NormalizeMode(themeMode);
        app.RequestedThemeVariant = mode switch
        {
            "Light"  => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _        => ThemeVariant.Dark,
        };

        // 2) Custom palette.
        var useDark = ResolveIsDark(mode);
        var palette = useDark ? DarkPalette : LightPalette;

        // Parse + set Color resources.
        foreach (var (key, hex) in palette)
        {
            if (Color.TryParse(hex, out var color))
            {
                app.Resources[key] = color;
            }
        }

        // 3) Accent colors.
        var accentHex = GetAccentHex(accentName);
        var accentFgHex = GetAccentFgHex(accentName);
        if (Color.TryParse(accentHex, out var accentColor))
            app.Resources["AppAccent"] = accentColor;
        if (Color.TryParse(accentFgHex, out var accentFgColor))
            app.Resources["AppAccentFg"] = accentFgColor;

        // 4) Update SolidColorBrush resources. Use TryGetResource to
        //    safely read the existing brush (IResourceDictionary does NOT
        //    support indexer reading — only writing). If the brush exists
        //    and is a SolidColorBrush, mutate its Color in place (safe —
        //    Color is observable). Otherwise create a new brush.
        UpdateBrush(app, "AppBackgroundBrush",  useDark ? "#0F1115" : "#FAFAFA");
        UpdateBrush(app, "AppSurfaceBrush",     useDark ? "#171A21" : "#FFFFFF");
        UpdateBrush(app, "AppSurfaceAltBrush",  useDark ? "#1F2330" : "#F3F4F6");
        UpdateBrush(app, "AppBorderBrush",      useDark ? "#2C3142" : "#D1D5DB");
        UpdateBrush(app, "AppForegroundBrush",  useDark ? "#E6E8EC" : "#1F2937");
        UpdateBrush(app, "AppMutedBrush",       useDark ? "#8A93A6" : "#6B7280");
        UpdateBrush(app, "AppAccentBrush",      accentHex);
        UpdateBrush(app, "AppAccentFgBrush",    accentFgHex);
        UpdateBrush(app, "AppDangerBrush",      useDark ? "#E5484D" : "#DC2626");
        UpdateBrush(app, "AppSuccessBrush",     useDark ? "#3DD68C" : "#059669");

        // 5) Animations gate.
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window w)
        {
            if (enableAnimations)
            {
                if (!w.Classes.Contains("Anim"))
                {
                    w.Classes.Add("Anim");
                    Trace.WriteLine("[ThemeService] Added 'Anim' class to MainWindow");
                }
            }
            else
            {
                if (w.Classes.Contains("Anim"))
                {
                    w.Classes.Remove("Anim");
                    Trace.WriteLine("[ThemeService] Removed 'Anim' class from MainWindow");
                }
            }
        }
        else
        {
            Trace.WriteLine("[ThemeService] WARNING: MainWindow not found — Anim class not toggled");
        }
    }

    /// <summary>
    /// Safely update a brush resource. Uses TryGetResource (the correct
    /// Avalonia API for reading from IResourceDictionary — indexer reading
    /// is NOT supported and throws).
    /// </summary>
    private static void UpdateBrush(Application app, string brushKey, string hex)
    {
        if (!Color.TryParse(hex, out var color)) return;

        // TryGetResource is the correct way to read from IResourceDictionary.
        // Avalonia 12 signature: TryGetResource(object key, ThemeVariant? theme, out object? value)
        if (app.Resources.TryGetResource(brushKey, null, out var existing) && existing is SolidColorBrush brush)
        {
            // Mutate in place — Color is observable, all bindings update.
            brush.Color = color;
        }
        else
        {
            // First time — create new brush.
            app.Resources[brushKey] = new SolidColorBrush(color);
        }
    }

    public static void ApplyFromSettings(Settings settings)
    {
        if (settings is null) return;
        ApplyTheme(
            settings.ThemeMode ?? "Dark",
            settings.AccentColor ?? "Indigo",
            settings.EnableAnimations);
    }

    private static string NormalizeMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? "Dark" :
        string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" :
        string.Equals(mode, "System", StringComparison.OrdinalIgnoreCase) ? "System" :
        "Dark";

    private static bool ResolveIsDark(string mode)
    {
        if (mode == "System")
        {
            try
            {
                var actual = Application.Current?.ActualThemeVariant;
                if (actual == ThemeVariant.Light) return false;
            }
            catch { }
            return true;
        }
        return mode == "Dark";
    }
}
