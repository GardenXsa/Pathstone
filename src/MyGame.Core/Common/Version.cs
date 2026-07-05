namespace MyGame.Core.Common;

/// <summary>
/// Engine version constants and helpers.
///
/// Port of <c>engine/core/version.ts</c>. The TS file was a one-liner
/// (<c>export const ENGINE_VERSION = '0.2.0'</c>); the C# port adds a small
/// semver parse/compare helper so <see cref="SaveManager"/> (later task) can
/// decide whether a save file is loadable by the current engine.
///
/// Bump <see cref="Current"/> whenever the save file layout changes in a
/// breaking way. The save meta should persist this string verbatim.
/// </summary>
public static class Version
{
    /// <summary>Current engine version, persisted in save meta.</summary>
    public const string Current = "0.2.0";

    /// <summary>
    /// Try to parse a dotted numeric version string
    /// (<c>major.minor.patch</c>). Missing components default to 0. Returns
    /// false for null/empty/garbage input without throwing.
    /// </summary>
    public static bool TryParse(string? input, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrEmpty(input)) return false;

        var parts = input.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        if (!int.TryParse(parts[0], out var major)) return false;

        int minor = 0, patch = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minor)) return false;
        if (parts.Length > 2 && !int.TryParse(parts[2], out patch)) return false;

        version = (major, minor, patch);
        return true;
    }

    /// <summary>
    /// Compare two version strings numerically (component-wise). Unparseable
    /// components are treated as 0, so <c>Compare("1.0", "1.0.0") == 0</c>.
    /// </summary>
    public static int Compare(string a, string b)
    {
        TryParse(a, out var va);
        TryParse(b, out var vb);
        int c = va.Major.CompareTo(vb.Major);
        if (c != 0) return c;
        c = va.Minor.CompareTo(vb.Minor);
        if (c != 0) return c;
        return va.Patch.CompareTo(vb.Patch);
    }

    /// <summary>
    /// Compatibility check for save files: same major version = compatible.
    /// Minor/patch differences are allowed (forward/backward compatible
    /// additions). Caller is responsible for any required migration when the
    /// minors differ.
    /// </summary>
    public static bool IsCompatible(string savedVersion)
    {
        if (!TryParse(savedVersion, out var saved)) return false;
        TryParse(Current, out var current);
        return saved.Major == current.Major;
    }
}
