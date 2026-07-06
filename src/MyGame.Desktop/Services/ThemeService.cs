using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MyGame.Core.Profile;

namespace MyGame.Desktop.Services;

/// <summary>
/// Runtime theme + accent + animation switching (issue #47 + #46).
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["AppBackground"] = "#110E0C", // Deep obsidian shadow
        ["AppSurface"]    = "#181411", // Weathered leather grimoire
        ["AppSurfaceAlt"] = "#231E1B", // Dark parchment backing
        ["AppBorder"]     = "#332A25", // Aged brass border
        ["AppForeground"] = "#F4EFE6", // Soft creamy manuscript text
        ["AppMuted"]      = "#938574", // Ancient ink dust
        ["AppDanger"]     = "#E65C4F", // Alchemical blood crimson
        ["AppSuccess"]    = "#5FB26B", // Forest leaf moss green
    };

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["AppBackground"] = "#FAF6EE", // Warm aged papyrus
        ["AppSurface"]    = "#FCFAF5", // Bleached parchment
        ["AppSurfaceAlt"] = "#EFEAE0", // Shadowed paper folds
        ["AppBorder"]     = "#D2C5B9", // Vintage leather bindings
        ["AppForeground"] = "#1C1A17", // Charcoal charcoal ink
        ["AppMuted"]      = "#7D7264", // Faded sepia text
        ["AppDanger"]     = "#C23B22", // Searing cinnabar red
        ["AppSuccess"]    = "#2A7B4C", // Rich pine needle green
    };

    private static readonly Dictionary<string, (string Accent, string AccentFg)> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Amber"]   = ("#D97706", "#110E0C"), // Glowing warm torchlight
        ["Indigo"]  = ("#6366F1", "#FFFFFF"), // Royal purple-blue
        ["Emerald"] = ("#059669", "#FFFFFF"), // Deep emerald sage
        ["Rose"]    = ("#E11D48", "#FFFFFF"), // Blood rose crimson
        ["Cyan"]    = ("#0891B2", "#110E0C"), // Spectral cyan
        ["Violet"]  = ("#7C3AED", "#FFFFFF"), // Eldritch violet
    };

    public static IReadOnlyList<string> AccentPresetNames { get; } =
        new[] { "Amber", "Indigo", "Emerald", "Rose", "Cyan", "Violet" };

    public static string GetAccentHex(string name) =>
        Accents.TryGetValue(name ?? "", out var v) ? v.Accent : Accents["Amber"].Accent;

    public static string GetAccentFgHex(string name) =>
        Accents.TryGetValue(name ?? "", out var v) ? v.AccentFg : Accents["Amber"].AccentFg;

    /// <summary>
    /// Apply theme + accent + animation flag. Never throws — all errors
    /// are caught and logged via Trace. Thread-safe: marshals to the UI thread
    /// if called from a background thread.
    /// </summary>
    public static void ApplyTheme(string themeMode, string accentName, bool enableAnimations)
    {
        if (Dispatcher.UIThread.CheckAccess())
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
        else
        {
            Dispatcher.UIThread.Post(() => ApplyTheme(themeMode, accentName, enableAnimations));
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
        UpdateBrush(app, "AppBackgroundBrush",  useDark ? "#1C1813" : "#FAFAFA");
        UpdateBrush(app, "AppSurfaceBrush",     useDark ? "#25201A" : "#FFFFFF");
        UpdateBrush(app, "AppSurfaceAltBrush",  useDark ? "#2E2A22" : "#F3F4F6");
        UpdateBrush(app, "AppBorderBrush",      useDark ? "#3D362C" : "#D1D5DB");
        UpdateBrush(app, "AppForegroundBrush",  useDark ? "#EBE5D8" : "#1F2937");
        UpdateBrush(app, "AppMutedBrush",       useDark ? "#A89B82" : "#6B7280");
        UpdateBrush(app, "AppAccentBrush",      accentHex);
        UpdateBrush(app, "AppAccentFgBrush",    accentFgHex);
        UpdateBrush(app, "AppDangerBrush",      useDark ? "#D4574B" : "#DC2626");
        UpdateBrush(app, "AppSuccessBrush",     useDark ? "#7BAE6B" : "#059669");

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
