using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyGame.Core.Multiplayer.Protocol;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for all Multiplayer-layer JSON.
///
/// Convention (from the worklog): camelCase on the wire, raw UTF-8 (no
/// \uXXXX escapes for Cyrillic), null fields omitted. Matches the World /
/// Saves / Profile layers' JSON flavor so a single mental model applies
/// across the codebase.
///
/// <para>
/// <b>Polymorphism:</b> the options pick up the <c>[JsonPolymorphic]</c>
/// and <c>[JsonDerivedType]</c> attributes on <see cref="NetMessage"/>
/// automatically — no converter registration needed. To serialise a
/// message polymorphically, use
/// <c>JsonSerializer.Serialize&lt;NetMessage&gt;(msg, MultiplayerJson.Options)</c>
/// (the explicit type parameter is required). To deserialise:
/// <c>JsonSerializer.Deserialize&lt;NetMessage&gt;(json, MultiplayerJson.Options)</c>.
/// </para>
/// </summary>
public static class MultiplayerJson
{
    /// <summary>
    /// Default options for the multiplayer wire format. Instance is
    /// immutable and shared across all callers (JsonSerializerOptions is
    /// thread-safe for reads after the first serialization).
    /// </summary>
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Match WorldJson.Options: write Cyrillic (and all non-ASCII)
            // as raw UTF-8 so wire traffic is human-readable when sniffed.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Compact wire format — no pretty-printing (saves bytes per
            // message, which matters for streaming narrative deltas).
            WriteIndented = false,
            IncludeFields = false,
        };
        return opts;
    }

    /// <summary>
    /// Serialize a <see cref="NetMessage"/> polymorphically to UTF-8 bytes
    /// (the form <see cref="System.Net.WebSockets.WebSocket.SendAsync"/>
    /// wants). Use this in the host / client send loops.
    /// </summary>
    public static byte[] SerializeToUtf8Bytes(NetMessage msg) =>
        JsonSerializer.SerializeToUtf8Bytes<NetMessage>(msg, Options);

    /// <summary>
    /// Serialize a <see cref="NetMessage"/> polymorphically to a string.
    /// Mostly for tests / debugging — the wire path uses
    /// <see cref="SerializeToUtf8Bytes"/> directly to avoid an extra
    /// UTF-8 → string → UTF-8 round-trip.
    /// </summary>
    public static string Serialize(NetMessage msg) =>
        JsonSerializer.Serialize<NetMessage>(msg, Options);

    /// <summary>
    /// Deserialize a UTF-8 string into a <see cref="NetMessage"/> of the
    /// appropriate derived type. Returns null if the JSON is empty or
    /// the discriminator is unknown (the caller should treat null as a
    /// protocol error and close the connection).
    /// </summary>
    public static NetMessage? Deserialize(string json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<NetMessage>(json, Options);

    /// <summary>
    /// Deserialize from a UTF-8 byte buffer (the form
    /// <see cref="System.Net.WebSockets.WebSocket.ReceiveAsync"/> produces).
    /// Avoids an extra byte → string → byte round-trip on the receive path.
    /// </summary>
    public static NetMessage? Deserialize(ReadOnlySpan<byte> utf8Bytes) =>
        utf8Bytes.IsEmpty ? null : JsonSerializer.Deserialize<NetMessage>(utf8Bytes, Options);
}
