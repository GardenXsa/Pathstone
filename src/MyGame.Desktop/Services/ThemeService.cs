using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using MyGame.Core.Profile;

namespace MyGame.Desktop.Services;

/// <summary>
/// Runtime theme + accent + animation switching (issue #47 + #46).
///
/// <para>
/// Pathstone ships a custom dark palette (AppBackground / AppSurface /
/// AppAccent / ...) defined as <c>Color</c> + <c>SolidColorBrush</c>
/// resources in <c>App.axaml</c>. Because FluentTheme's built-in
/// <c>RequestedThemeVariant</c> only swaps Fluent's own brushes (not our
/// custom overrides), we have to mutate the application-level resources
/// ourselves to flip between dark and light. ThemeService owns that
/// mutation: it holds the dark + light palette tables + the 6 accent
/// presets, and on <see cref="ApplyTheme"/> writes the resolved values
/// into <see cref="Application.Resources"/> so every <c>DynamicResource</c>
/// binding in the XAML picks them up at runtime.
/// </para>
///
/// <para>
/// <b>Animations gate (issue #46, #47):</b> a global <c>Anim</c> style
/// class is added to / removed from the main <c>Window</c>. The
/// animation/transition styles in <c>App.axaml</c> are scoped to
/// <c>Window.Anim …</c>, so removing the class turns every transition
/// off across the whole app in one shot. No tree-walk needed.
/// </para>
/// </summary>
public static class ThemeService
{
    // ─── Theme mode palette tables ─────────────────────────────────────
    //
    // Each entry maps an App.* resource key (Color) to its hex value for
    // the given mode. The brushes (AppBackgroundBrush, …) are rebuilt
    // from these colors in ApplyTheme. "System" mode resolves to whichever
    // of dark/light the OS prefers at startup — we don't react to mid-
    // session OS theme changes (the user can re-toggle via Settings).

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

    // ─── Accent presets (issue #47 1b) ─────────────────────────────────
    //
    // Each preset pairs a friendly name with the accent + accent-foreground
    // hex colors. Accent-foreground is the text color drawn on top of the
    // accent (button labels, badges, etc.). All 6 presets use white text
    // — the accent colors are saturated enough that white is the readable
    // choice.

    private static readonly Dictionary<string, (string Accent, string AccentFg)> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Indigo"]  = ("#7C5CFF", "#FFFFFF"),
        ["Emerald"] = ("#10B981", "#FFFFFF"),
        ["Amber"]   = ("#F59E0B", "#1F2937"),
        ["Rose"]    = ("#F43F5E", "#FFFFFF"),
        ["Cyan"]    = ("#06B6D4", "#FFFFFF"),
        ["Violet"]  = ("#8B5CF6", "#FFFFFF"),
    };

    /// <summary>
    /// Read-only list of accent preset names (in display order). Bound to
    /// the settings UI's color-swatch row so the user can click to pick.
    /// </summary>
    public static IReadOnlyList<string> AccentPresetNames { get; } =
        new[] { "Indigo", "Emerald", "Amber", "Rose", "Cyan", "Violet" };

    /// <summary>
    /// Resolve an accent preset name to its hex color. Returns the
    /// default Indigo accent when the name is unknown (defensive — a
    /// corrupt settings.json with a stale name shouldn't crash the UI).
    /// </summary>
    public static string GetAccentHex(string name) =>
        Accents.TryGetValue(name ?? "", out var v) ? v.Accent : Accents["Indigo"].Accent;

    /// <summary>
    /// Resolve an accent preset name to its foreground hex color (the
    /// text color drawn on top of the accent).
    /// </summary>
    public static string GetAccentFgHex(string name) =>
        Accents.TryGetValue(name ?? "", out var v) ? v.AccentFg : Accents["Indigo"].AccentFg;

    // ─── ApplyTheme ────────────────────────────────────────────────────

    /// <summary>
    /// Apply the given theme mode + accent preset + animation flag to the
    /// running <see cref="Application"/>. Idempotent — calling it again
    /// with the same values just rewrites the same resources (cheap).
    /// Safe to call from the UI thread only (mutates Avalonia resources).
    ///
    /// <para>
    /// Side effects:
    /// <list type="bullet">
    ///   <item>Sets <c>Application.Current.RequestedThemeVariant</c> to
    ///       Dark / Light / Default (System → Default lets FluentTheme
    ///       pick based on the OS).</item>
    ///   <item>Writes the 8 palette colors + 2 accent colors into
    ///       <c>Application.Current.Resources</c> as <c>Color</c>s.</item>
    ///       <item>Rebuilds the matching <c>SolidColorBrush</c>es
    ///       (AppBackgroundBrush, AppSurfaceBrush, …) so every
    ///       <c>DynamicResource</c> binding refreshes.</item>
    ///   <item>Toggles the <c>Anim</c> style class on the main window to
    ///       enable / disable every transition (issue #46 #4f).</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="themeMode">"Dark", "Light", or "System" (case-
    /// insensitive; anything else falls back to Dark).</param>
    /// <param name="accentName">One of <see cref="AccentPresetNames"/>
    /// (case-insensitive; unknown → Indigo).</param>
    /// <param name="enableAnimations">When false, the main window's
    /// <c>Anim</c> class is removed so all transition styles scoped to
    /// <c>Window.Anim</c> stop applying.</param>
    public static void ApplyTheme(string themeMode, string accentName, bool enableAnimations)
    {
        var app = Application.Current;
        if (app is null) return;

        // 1) FluentTheme variant. "System" → Default (FluentTheme falls
        //    back to the OS preference).
        app.RequestedThemeVariant = NormalizeMode(themeMode) switch
        {
            "Light"  => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _        => ThemeVariant.Dark,
        };

        // 2) Custom palette. Pick the table for the resolved mode (System
        //    resolves to the OS preference at this instant — we don't
        //    react to mid-session OS theme changes).
        var useDark = ResolveIsDark(themeMode);
        var palette = useDark ? DarkPalette : LightPalette;

        // Parse every palette color up front so we can write both the
        // Color resource and the matching brush in one pass. Skip any
        // malformed hex (defensive — palette tables are static so this
        // should never trip, but we don't want a typo to crash startup).
        var resolved = new Dictionary<string, Color>(palette.Count, StringComparer.Ordinal);
        foreach (var (key, hex) in palette)
        {
            if (TryParseColor(hex, out var color))
            {
                app.Resources[key] = color;
                resolved[key] = color;
            }
        }

        // 3) Accent.
        var accentHex   = GetAccentHex(accentName);
        var accentFgHex = GetAccentFgHex(accentName);
        Color accentColor   = TryParseColor(accentHex,   out var ac) ? ac : Color.FromRgb(0x7C, 0x5C, 0xFF);
        Color accentFgColor = TryParseColor(accentFgHex, out var af) ? af : Colors.White;
        app.Resources["AppAccent"]   = accentColor;
        app.Resources["AppAccentFg"] = accentFgColor;
        resolved["AppAccent"]   = accentColor;
        resolved["AppAccentFg"] = accentFgColor;

        // 4) Rebuild the SolidColorBrush resources. We replace the brush
        //    instances outright (rather than mutating brush.Color) so
        //    DynamicResource bindings see a fresh reference and re-read.
        //    Mutating brush.Color wouldn't propagate because brushes
        //    aren't observable on Color.
        static SolidColorBrush Brush(IReadOnlyDictionary<string, Color> map, string key, Color fallback) =>
            new SolidColorBrush(map.TryGetValue(key, out var c) ? c : fallback);

        app.Resources["AppBackgroundBrush"]  = Brush(resolved, "AppBackground",  Colors.Black);
        app.Resources["AppSurfaceBrush"]     = Brush(resolved, "AppSurface",     Colors.Black);
        app.Resources["AppSurfaceAltBrush"]  = Brush(resolved, "AppSurfaceAlt",  Colors.Black);
        app.Resources["AppBorderBrush"]      = Brush(resolved, "AppBorder",      Colors.Black);
        app.Resources["AppForegroundBrush"]  = Brush(resolved, "AppForeground",  Colors.White);
        app.Resources["AppMutedBrush"]       = Brush(resolved, "AppMuted",       Colors.Gray);
        app.Resources["AppAccentBrush"]      = new SolidColorBrush(accentColor);
        app.Resources["AppAccentFgBrush"]    = new SolidColorBrush(accentFgColor);
        app.Resources["AppDangerBrush"]      = Brush(resolved, "AppDanger",      Colors.Red);
        app.Resources["AppSuccessBrush"]     = Brush(resolved, "AppSuccess",     Colors.Green);

        // 5) Animations gate. Toggling the class on the main window
        //    enables/disables every Window.Anim-scoped transition style.
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window w)
        {
            const string animClass = "Anim";
            if (enableAnimations)
            {
                if (!w.Classes.Contains(animClass))
                    w.Classes.Add(animClass);
            }
            else
            {
                if (w.Classes.Contains(animClass))
                    w.Classes.Remove(animClass);
            }
        }
    }

    /// <summary>
    /// Convenience: read theme + accent + animations from a
    /// <see cref="Settings"/> instance and apply them. Used at app startup
    /// and on SettingsStore.Changed.
    /// </summary>
    public static void ApplyFromSettings(Settings settings)
    {
        if (settings is null) return;
        ApplyTheme(settings.ThemeMode, settings.AccentColor, settings.EnableAnimations);
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static string NormalizeMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? "Dark" :
        string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" :
        string.Equals(mode, "System", StringComparison.OrdinalIgnoreCase) ? "System" :
        "Dark";

    /// <summary>
    /// Resolve a theme mode string to a concrete dark/light choice. System
    /// resolves to the OS preference at the moment of the call (we don't
    /// subscribe to OS theme changes).
    /// </summary>
    private static bool ResolveIsDark(string? mode)
    {
        var normalized = NormalizeMode(mode);
        if (normalized == "System")
        {
            // SystemSettings doesn't expose a portable "is dark" flag in
            // Avalonia 11's public API. Use the actual theme variant
            // FluentTheme resolved to — fall back to dark (the original
            // Pathstone look) on platforms where we can't tell.
            try
            {
                var actual = Application.Current?.ActualThemeVariant;
                if (actual == ThemeVariant.Dark)  return true;
                if (actual == ThemeVariant.Light) return false;
            }
            catch { /* defensive — fall through to default */ }
            return true;
        }
        return normalized == "Dark";
    }

    /// <summary>
    /// Parse a hex color string (#RGB, #RRGGBB, or #AARRGGBB) into a
    /// <see cref="Color"/>. Returns false on any parse failure (so a
    /// typo in the palette table doesn't crash startup).
    /// </summary>
    private static bool TryParseColor(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        try
        {
            // Accept RGB (3), RRGGBB (6), or AARRGGBB (8) hex forms.
            if (s.Length == 3)
            {
                var r = (byte)(Convert.ToByte(s.Substring(0, 1), 16) * 17);
                var g = (byte)(Convert.ToByte(s.Substring(1, 1), 16) * 17);
                var b = (byte)(Convert.ToByte(s.Substring(2, 1), 16) * 17);
                color = Color.FromArgb(255, r, g, b);
                return true;
            }
            if (s.Length == 6)
            {
                var r = Convert.ToByte(s.Substring(0, 2), 16);
                var g = Convert.ToByte(s.Substring(2, 2), 16);
                var b = Convert.ToByte(s.Substring(4, 2), 16);
                color = Color.FromArgb(255, r, g, b);
                return true;
            }
            if (s.Length == 8)
            {
                var a = Convert.ToByte(s.Substring(0, 2), 16);
                var r = Convert.ToByte(s.Substring(2, 2), 16);
                var g = Convert.ToByte(s.Substring(4, 2), 16);
                var b = Convert.ToByte(s.Substring(6, 2), 16);
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
        }
        catch { /* fall through */ }
        return false;
    }
}
