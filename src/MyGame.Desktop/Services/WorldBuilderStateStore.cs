using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyGame.Core.AI.Agents;
using MyGame.Core.Profile;

namespace MyGame.Desktop.Services;

/// <summary>
/// On-disk persistence for the <see cref="WorldBuilderState"/> snapshot
/// (issue #19). When the user cancels a world-build mid-flight, the
/// <see cref="ViewModels.WorldBuildViewModel"/> writes the orchestrator's
/// current state to <c>%APPDATA%/MyGame/worldbuilder-state.json</c> via
/// <see cref="Save"/>. On next app launch, the main menu reads it via
/// <see cref="Load"/> and offers to resume the build.
/// </summary>
public static class WorldBuilderStateStore
{
    /// <summary>
    /// Path to the on-disk state file. Lives in the profile directory
    /// alongside <c>profile.json</c> + <c>settings.json</c> so it
    /// follows the same per-user / per-machine conventions.
    /// </summary>
    public static string StateFilePath =>
        Path.Combine(ProfileStore.DefaultProfileDirectory, "worldbuilder-state.json");

    // Reuse a single JsonSerializerOptions instance across all
    // serializations. The default System.Text.Json policy round-trips
    // the WorldBuilderState record + the WorldPlan record (which uses
    // init-only properties + JsonPropertyName attributes) cleanly.
    // PropertyNameCaseInsensitive lets us read files written by older
    // versions that might have used a different casing policy.
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Persist the given <see cref="WorldBuilderState"/> + brief to disk
    /// atomically (write to <c>.tmp</c> then rename). Safe to call from
    /// any thread. Non-fatal on any I/O error — the caller catches and
    /// logs, since a failed state-save shouldn't break the cancel flow.
    /// </summary>
    /// <param name="state">The orchestrator's saved state.</param>
    /// <param name="brief">The original world brief (so a resumed run
    /// uses the same brief as the original). Stored alongside the state
    /// in a wrapper record.</param>
    public static void Save(WorldBuilderState state, string? brief)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        var wrapper = new WorldBuilderStateFile
        {
            State = state,
            Brief = brief ?? string.Empty,
            SavedAt = DateTimeOffset.UtcNow,
        };
        var dir = Path.GetDirectoryName(StateFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(wrapper, s_options);
        var tmp = StateFilePath + ".tmp";
        File.WriteAllText(tmp, json);
        // File.Move with overwrite=true is atomic on the same volume
        // (NTFS / ext4). This matches the SaveManager's atomic-write
        // pattern so a crash mid-write leaves either the old file or
        // the new file — never a half-written one.
        File.Move(tmp, StateFilePath, overwrite: true);
    }

    /// <summary>
    /// Load the saved state from disk. Returns null if the file doesn't
    /// exist or is unparseable (defensive — a corrupt state file
    /// shouldn't crash the main menu).
    /// </summary>
    public static WorldBuilderStateFile? Load()
    {
        if (!File.Exists(StateFilePath)) return null;
        try
        {
            var json = File.ReadAllText(StateFilePath);
            return JsonSerializer.Deserialize<WorldBuilderStateFile>(json, s_options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[WorldBuilderStateStore] failed to load state file: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Delete the saved state file (called after a successful build so
    /// the next launch doesn't prompt to resume a finished build). Safe
    /// to call when the file doesn't exist.
    /// </summary>
    public static void Delete()
    {
        try { if (File.Exists(StateFilePath)) File.Delete(StateFilePath); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[WorldBuilderStateStore] failed to delete state file: {ex.Message}");
        }
    }

    /// <summary>
    /// True when a saved state file exists on disk. Used by the main
    /// menu to decide whether to show the «Незавершённая генерация мира.
    /// Продолжить?» prompt.
    /// </summary>
    public static bool Exists() => File.Exists(StateFilePath);
}

/// <summary>
/// On-disk wrapper for <see cref="WorldBuilderState"/>: carries the
/// state itself + the original brief + a timestamp so the resume UI can
/// show "saved N minutes ago".
/// </summary>
public sealed class WorldBuilderStateFile
{
    /// <summary>The orchestrator's saved progress.</summary>
    public WorldBuilderState? State { get; set; }

    /// <summary>
    /// The original world brief (so a resumed run uses the same brief
    /// as the original — the planner needs it).
    /// </summary>
    public string Brief { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the state was saved.</summary>
    public DateTimeOffset SavedAt { get; set; }
}
