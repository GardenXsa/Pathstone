using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using MyGame.Core.World.Content;

namespace MyGame.Core.Tooling;

/// <summary>
/// Loads mod packs (.pathstone-pack zip files) from a mods/ directory.
/// Issue #59. Each pack contains a content.json (same format as data.json)
/// with item/NPC/building/recipe templates. Packs are loaded at startup
/// and merged into the ContentRegistry (later packs override earlier by
/// template id — load order is alphabetical by filename).
/// </summary>
public static class ModLoader
{
    /// <summary>
    /// Scan the mods/ directory for .pathstone-pack files and load each
    /// into the registry. Returns a list of loaded mod names (filenames
    /// without extension). Silently skips invalid packs.
    /// </summary>
    public static List<string> LoadAll(string modsDir, ContentRegistry registry)
    {
        var loaded = new List<string>();
        if (!Directory.Exists(modsDir)) return loaded;

        var packFiles = Directory.GetFiles(modsDir, "*.pathstone-pack")
            .Concat(Directory.GetFiles(modsDir, "*.zip"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in packFiles)
        {
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var contentEntry = zip.GetEntry("content.json")
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith("content.json"));
                if (contentEntry is null) continue;

                using var stream = contentEntry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();

                registry.LoadFromJson(json);
                loaded.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch
            {
                // Skip invalid packs — don't crash the app.
            }
        }

        return loaded;
    }
}
