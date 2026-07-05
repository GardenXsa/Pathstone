using System.IO;
using System.Text.Json;
using MyGame.Core.Common;
using MyGame.Core.Profile;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Core.Saves;

/// <summary>
/// Manage standalone <see cref="CharacterSheet"/> exports under
/// <c>{ProfileDirectory}/characters/</c>. Each sheet is a single JSON
/// file (<c>char_{Guid:N}.json</c>) — not a directory — because a
/// character sheet is much smaller than a full save and doesn't benefit
/// from the split-file layout.
/// </summary>
/// <remarks>
/// <b>Use case:</b> the BG3-style "your character travels with you
/// between hosts" feature. A player exports their character to a sheet,
/// sends the file to a friend, the friend imports it into their install
/// — the friend's world's <c>SpawnPlayer</c> flow materializes a live
/// Player from the sheet.
/// </remarks>
public sealed class CharacterSheetStore
{
    private readonly SaveManager _saveManager;
    private readonly string _charactersDirectory;

    /// <param name="saveManager">
    /// Used by <see cref="Export"/> to load the source save's World.
    /// </param>
    /// <param name="charactersDirectory">
    /// Optional override for the characters directory. Defaults to
    /// <c>{ProfileStore.DefaultProfileDirectory}/characters</c>.
    /// </param>
    public CharacterSheetStore(SaveManager saveManager, string? charactersDirectory = null)
    {
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _charactersDirectory = charactersDirectory
            ?? Path.Combine(ProfileStore.DefaultProfileDirectory, "characters");
    }

    /// <summary>Directory containing all character-sheet JSON files.</summary>
    public string CharactersDirectory => _charactersDirectory;

    /// <summary>
    /// Export a player from a save into a standalone
    /// <see cref="CharacterSheet"/>. Loads the save's World, finds the
    /// player by id, snapshots it, and writes the sheet to
    /// <c>{CharactersDirectory}/{charId}.json</c> atomically. Returns
    /// null if the save doesn't exist, the player doesn't exist in the
    /// save, or the write fails.
    /// </summary>
    /// <remarks>
    /// <b>Idempotency:</b> if a sheet for the same
    /// (<paramref name="saveId"/>, <paramref name="playerId"/>) pair
    /// already exists, this OVERWRITES it (refreshes the snapshot).
    /// We key sheets by <c>(sourceSaveId, sourcePlayerId)</c> rather
    /// than minting a fresh Guid each time, so a re-export doesn't
    /// duplicate. The first export mints the Guid; subsequent exports
    /// reuse it.
    /// </remarks>
    public CharacterSheet? Export(string saveId, EntityId playerId)
    {
        var world = _saveManager.LoadWorld(saveId);
        if (world is null) return null;
        var player = world.GetPlayer(playerId);
        if (player is null) return null;

        // Reuse the existing sheet id for this (saveId, playerId) pair
        // if one exists, so a re-export overwrites rather than
        // duplicating.
        var existing = FindBySource(saveId, playerId.Value);
        var id = existing?.Id ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var sheet = new CharacterSheet
        {
            Id = id,
            Name = player.Name,
            Race = player.Race,
            Class = player.Class,
            Background = player.Background,
            Attributes = new Dictionary<string, int>(player.Attributes),
            Resources = new Dictionary<string, int>(player.Resources),
            Level = player.Level ?? 1,
            Xp = player.Experience ?? 0,
            InventoryItemIds = player.Inventory.Items
                .Where(i => !string.IsNullOrEmpty(i.TemplateId))
                .Select(i => i.TemplateId!)
                .ToList(),
            EquippedItemIds = player.Equipped
                .Where(kv => !string.IsNullOrEmpty(kv.Value.TemplateId))
                .ToDictionary(kv => kv.Key, kv => kv.Value.TemplateId!),
            ProficientSkills = player.ProficientSkills ?? new List<string>(),
            Speed = player.Speed,
            SourceSaveId = saveId,
            SourcePlayerId = playerId.Value,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        try
        {
            Directory.CreateDirectory(_charactersDirectory);
            var path = PathFor(sheet.Id);
            ProfileStore.AtomicWriteJson(path, sheet);
            return sheet;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[CharacterSheetStore] Failed to export character from {saveId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Load a sheet by id. Returns null if the file doesn't exist or
    /// fails to parse.
    /// </summary>
    public CharacterSheet? Load(string charId)
    {
        var id = ParseCharId(charId);
        if (id is null) return null;
        var path = PathFor(id.Value);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CharacterSheet>(
                File.ReadAllText(path), WorldJson.Options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[CharacterSheetStore] Failed to load character {charId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Convenience overload: load by Guid id (no <c>char_</c> prefix
    /// needed).
    /// </summary>
    public CharacterSheet? Load(Guid charId) => Load(CharIdToString(charId));

    /// <summary>
    /// List all stored sheets. Returns an empty list if the directory
    /// doesn't exist. Corrupt files are silently skipped (logged to
    /// <c>Trace</c>). Sorted by <see cref="CharacterSheet.UpdatedAt"/>
    /// descending (most recently exported first).
    /// </summary>
    public IReadOnlyList<CharacterSheet> List()
    {
        var result = new List<CharacterSheet>();
        string[] files;
        try
        {
            if (!Directory.Exists(_charactersDirectory)) return result;
            files = Directory.GetFiles(_charactersDirectory, "char_*.json");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[CharacterSheetStore] Failed to list characters in {_charactersDirectory}: {ex.Message}");
            return result;
        }

        foreach (var file in files)
        {
            try
            {
                var sheet = JsonSerializer.Deserialize<CharacterSheet>(
                    File.ReadAllText(file), WorldJson.Options);
                if (sheet is not null) result.Add(sheet);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[CharacterSheetStore] Skipping corrupt character at {file}: {ex.Message}");
            }
        }

        result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    /// <summary>
    /// Delete a sheet by id. No-op (returns false) if it doesn't exist.
    /// </summary>
    public bool Delete(string charId)
    {
        var id = ParseCharId(charId);
        if (id is null) return false;
        var path = PathFor(id.Value);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[CharacterSheetStore] Failed to delete character {charId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Convenience overload: delete by Guid id.
    /// </summary>
    public bool Delete(Guid charId) => Delete(CharIdToString(charId));

    // ─── Helpers ───────────────────────────────────────────────────────

    private string PathFor(Guid id) => Path.Combine(_charactersDirectory, $"{CharIdToString(id)}.json");

    /// <summary>Format a Guid as the on-disk char id: <c>char_{Guid:N}</c>.</summary>
    public static string CharIdToString(Guid id) => $"char_{id:N}";

    /// <summary>
    /// Parse a <c>char_{Guid:N}</c> string back to a Guid. Returns null
    /// if the input doesn't match the expected format.
    /// </summary>
    public static Guid? ParseCharId(string charId)
    {
        if (string.IsNullOrEmpty(charId)) return null;
        if (!charId.StartsWith("char_", StringComparison.Ordinal)) return null;
        var hex = charId.Substring(5);
        return Guid.TryParseExact(hex, "N", out var g) ? g : null;
    }

    /// <summary>
    /// Find an existing sheet exported from the given (saveId, playerId)
    /// pair. Used by <see cref="Export"/> to make re-export idempotent.
    /// </summary>
    private CharacterSheet? FindBySource(string saveId, string playerId)
    {
        foreach (var sheet in List())
        {
            if (sheet.SourceSaveId == saveId && sheet.SourcePlayerId == playerId)
                return sheet;
        }
        return null;
    }
}
