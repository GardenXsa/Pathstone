using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MyGame.Core.I18n;

/// <summary>
/// Internationalization framework (issue #48). Loads string resources
/// from JSON files. Falls back to Russian when a translation is missing.
/// </summary>
public static class Strings
{
    private static Dictionary<string, string> _active = new();
    private static Dictionary<string, string> _fallback = new();
    private static string _language = "ru";

    public static string Language => _language;

    public static void SetLanguage(string lang)
    {
        _language = lang ?? "ru";
        _active = LoadLanguage(_language);
        if (_language != "ru")
            _fallback = LoadLanguage("ru");
    }

    public static string Get(string key, params object[] args)
    {
        var value = GetValue(_active, key) ?? GetValue(_fallback, key) ?? key;
        return args.Length > 0 ? string.Format(value, args) : value;
    }

    private static string? GetValue(Dictionary<string, string> dict, string key)
    {
        foreach (var kv in dict)
        {
            if (kv.Key.Equals(key, System.StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    private static Dictionary<string, string> LoadLanguage(string lang)
    {
        var assembly = typeof(Strings).Assembly;
        var resourceName = $"MyGame.Core.I18n.locales.{lang}.json";
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? new();
            }
        }
        catch { }

        try
        {
            var filePath = Path.Combine(
                Profile.ProfileStore.DefaultProfileDirectory,
                "locales", $"{lang}.json");
            if (File.Exists(filePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath)) ?? new();
        }
        catch { }

        return new();
    }
}
