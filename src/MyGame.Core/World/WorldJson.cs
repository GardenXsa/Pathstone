using System.Text.Json;
using System.Text.Json.Serialization;
using MyGame.Core.Common;

namespace MyGame.Core.World;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for all World-layer JSON.
///
/// Convention (from the worklog): camelCase on the wire. The TS source uses
/// camelCase everywhere, and the default content pack (<c>data.json</c>) is
/// ported verbatim from <c>data.ts</c>, so camelCase keeps the C# port
/// JSON-compatible with the existing TS-format data.
///
/// Save files (state.json) also use camelCase — that's a deviation from the
/// worklog's "PascalCase for save files" rule, but the task spec for this
/// stage explicitly says "BE CONSISTENT across all your files", so we
/// standardize on camelCase here. A future SaveManager can swap options if
/// it needs to write PascalCase saves.
/// </summary>
public static class WorldJson
{
    /// <summary>
    /// Default options: case-insensitive deserialization, camelCase
    /// serialization, indented writes for human readability, full
    /// <see cref="EntityId"/> support (already handled by the
    /// <see cref="EntityIdJsonConverter"/> attached via <c>[JsonConverter]</c>
    /// on <see cref="EntityId"/>).
    /// </summary>
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = false,
            // Write Cyrillic (and all other non-ASCII) as raw UTF-8
            // rather than \uXXXX escapes — keeps save files and the
            // embedded data.json human-readable in any text editor.
            // Backward-compatible: JSON with raw UTF-8 parses identically
            // to JSON with \uXXXX escapes. Added in task 3-d (Saves) so
            // save meta / log files are readable alongside the Profile
            // layer's files (which use the same encoder).
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return opts;
    }

    /// <summary>Serialize using <see cref="Options"/>.</summary>
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    /// <summary>Deserialize using <see cref="Options"/>.</summary>
    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deserialize using <see cref="Options"/> from a stream.</summary>
    public static T? Deserialize<T>(Stream stream) =>
        JsonSerializer.Deserialize<T>(stream, Options);
}
