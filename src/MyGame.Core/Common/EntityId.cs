using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyGame.Core.Common;

/// <summary>
/// Strong-typed identifier for game entities.
///
/// Port of <c>engine/core/entityId.ts</c>. The TS version built IDs by
/// prefixing a UUID with the entity type (<c>plr_&lt;uuid&gt;</c>,
/// <c>npc_&lt;uuid&gt;</c>, ...). For the C# rewrite we use a single
/// cuid-style value (timestamp base36 + random) so that IDs are:
///  - globally unique without coordination,
///  - lexicographically sortable by creation time (handy for log scanning),
///  - cheap to compare and store.
///
/// The type is a <see cref="readonly record struct"/> wrapping a string, so
/// value-equality, hashing, and <c>==</c>/<c>!=</c> come for free. We add
/// ordinal <see cref="IComparable{T}"/> for sortability, implicit string
/// conversions for ergonomic call sites, and a custom
/// <see cref="JsonConverter{T}"/> so IDs serialise as bare strings on the wire
/// and in save files (no <c>{ "value": "..." }</c> wrapper).
/// </summary>
[JsonConverter(typeof(EntityIdJsonConverter))]
public readonly record struct EntityId(string Value) : IComparable<EntityId>
{
    /// <summary>An empty ID, used as a sentinel "unassigned" value.</summary>
    public static readonly EntityId Empty = new(string.Empty);

    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const int TimestampWidth = 9;   // base36 digits for ms timestamp
    private const int RandomWidth = 12;     // base36 digits of entropy

    /// <summary>
    /// Mint a new unique ID. Format: <c>c</c> + 9-char base36 timestamp
    /// (ms since epoch, zero-padded) + 12-char base36 random suffix.
    /// The fixed-width timestamp prefix makes IDs generated in time order
    /// sort lexicographically.
    /// </summary>
    public static EntityId NewId()
    {
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tsPart = ToBase36Padded(timestamp, TimestampWidth);
        var randPart = RandomBase36(RandomWidth);
        return new EntityId($"c{tsPart}{randPart}");
    }

    /// <summary>Parse a string into an <see cref="EntityId"/>.</summary>
    /// <exception cref="ArgumentException">input is null or empty.</exception>
    public static EntityId Parse(string input)
    {
        if (string.IsNullOrEmpty(input))
            throw new ArgumentException("EntityId value cannot be null or empty.", nameof(input));
        return new EntityId(input);
    }

    /// <summary>Try-parse variant — never throws.</summary>
    public static bool TryParse(string? input, out EntityId result)
    {
        if (string.IsNullOrEmpty(input))
        {
            result = default;
            return false;
        }
        result = new EntityId(input);
        return true;
    }

    /// <summary>
    /// Ordinal comparison so that IDs sort by their raw bytes — which, given
    /// the fixed-width timestamp prefix, is also creation-time order.
    /// </summary>
    public int CompareTo(EntityId other) =>
        string.CompareOrdinal(Value, other.Value);

    /// <summary>Return the raw string value.</summary>
    public override string ToString() => Value;

    /// <summary>Allow <c>string s = entityId;</c> for ergonomic API calls.</summary>
    public static implicit operator string(EntityId id) => id.Value;

    /// <summary>Allow <c>EntityId id = "abc";</c> for ergonomic literals.</summary>
    public static implicit operator EntityId(string s) => new(s);

    // ── base36 helpers ────────────────────────────────────────────────────

    private static string ToBase36(ulong value)
    {
        if (value == 0) return "0";
        Span<char> buffer = stackalloc char[16];
        int pos = 16;
        while (value > 0)
        {
            buffer[--pos] = Alphabet[(int)(value % 36)];
            value /= 36;
        }
        return new string(buffer.Slice(pos));
    }

    private static string ToBase36Padded(ulong value, int width)
    {
        var s = ToBase36(value);
        return s.Length >= width ? s : s.PadLeft(width, '0');
    }

    private static string RandomBase36(int length)
    {
        Span<char> buffer = stackalloc char[length];
        for (int i = 0; i < length; i++)
            buffer[i] = Alphabet[Random.Shared.Next(36)];
        return new string(buffer);
    }
}

/// <summary>
/// Serialises <see cref="EntityId"/> as a bare JSON string — never as an
/// object. Applied automatically via the <c>[JsonConverter]</c> attribute on
/// the type, so callers don't need to register anything with
/// <see cref="JsonSerializerOptions"/>.
/// </summary>
public sealed class EntityIdJsonConverter : JsonConverter<EntityId>
{
    /// <inheritdoc />
    public override EntityId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        new EntityId(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        EntityId value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
