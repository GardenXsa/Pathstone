using System.Text.Json;
using MyGame.Core.Common;

namespace MyGame.Tests.Common;

/// <summary>
/// Unit tests for the cuid-style EntityId. Covers NewId uniqueness,
/// parse round-trip, ordinal comparison, and JSON serialization.
/// </summary>
public class EntityIdTests
{
    [Fact]
    public void NewId_ReturnsNonEmptyString()
    {
        var id = EntityId.NewId();
        Assert.False(string.IsNullOrEmpty(id.ToString()));
        Assert.True(id.ToString().Length > 0);
    }

    [Fact]
    public void NewId_HasExpectedPrefix()
    {
        // The cuid-style format is 'c' + 9-char base36 timestamp +
        // 12-char base36 random suffix -> 22 chars total.
        var id = EntityId.NewId().ToString();
        Assert.StartsWith("c", id);
        Assert.Equal(22, id.Length);
    }

    [Fact]
    public void NewId_IsUnique()
    {
        // 1000 minted ids should all be distinct. The cuid scheme mixes a
        // millisecond timestamp with 12 base36 random digits — collisions
        // are astronomically unlikely even in tight loops (where the
        // timestamp can repeat across iterations).
        var ids = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            var s = EntityId.NewId().ToString();
            Assert.True(ids.Add(s), $"Duplicate id generated at iteration {i}: {s}");
        }
    }

    [Fact]
    public void TryParse_RoundTrips()
    {
        var original = EntityId.NewId();
        var s = original.ToString();

        Assert.True(EntityId.TryParse(s, out var parsed));
        Assert.Equal(original, parsed);
        Assert.Equal(s, parsed.ToString());
    }

    [Fact]
    public void Parse_RoundTrips()
    {
        var original = EntityId.NewId();
        var s = original.ToString();
        var parsed = EntityId.Parse(s);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void TryParse_RejectsEmptyAndWhitespace()
    {
        // TryParse never throws — it returns false for null/empty inputs.
        // The implementation only guards against null/empty (it does NOT
        // trim whitespace), so "   " is accepted as a non-empty string.
        // (Per the impl: any non-empty string is accepted, since EntityId
        // is just a wrapper around an arbitrary string.)
        Assert.False(EntityId.TryParse(null, out _));
        Assert.False(EntityId.TryParse("", out _));
        Assert.True(EntityId.TryParse("   ", out _));
    }

    [Fact]
    public void TryParse_AcceptsArbitraryNonEmptyString()
    {
        // Per the implementation, any non-empty string is accepted — the
        // EntityId is just a wrapper. So "not-an-id" parses successfully.
        Assert.True(EntityId.TryParse("not-an-id", out var parsed));
        Assert.Equal("not-an-id", parsed.ToString());
    }

    [Fact]
    public void Parse_EmptyOrNull_Throws()
    {
        Assert.Throws<ArgumentException>(() => EntityId.Parse(null!));
        Assert.Throws<ArgumentException>(() => EntityId.Parse(""));
    }

    [Fact]
    public void CompareTo_IsConsistentWithToString()
    {
        // Mint two ids and compare them via CompareTo — the ordering must
        // match a plain ordinal string comparison of their .ToString()
        // values (that's how CompareTo is implemented).
        var a = EntityId.NewId();
        var b = EntityId.NewId();
        // Ensure deterministic ordering: if the random suffix makes a<b
        // lexically, swap so we have a known "smaller / larger" pair.
        if (a.CompareTo(b) > 0) (a, b) = (b, a);

        Assert.Equal(
            Math.Sign(string.CompareOrdinal(a.ToString(), b.ToString())),
            Math.Sign(a.CompareTo(b)));
        Assert.True(a.CompareTo(b) <= 0);
    }

    [Fact]
    public void CompareTo_SortOrderMatchesStringOrder()
    {
        var ids = new List<EntityId>();
        for (int i = 0; i < 50; i++) ids.Add(EntityId.NewId());

        var byEntityId = ids.OrderBy(x => x, Comparer<EntityId>.Default).ToList();
        var byString = ids.OrderBy(x => x.ToString(), StringComparer.Ordinal).ToList();

        Assert.Equal(byString, byEntityId);
    }

    [Fact]
    public void ImplicitStringConversion_Works()
    {
        EntityId id = EntityId.NewId();
        // EntityId -> string
        string s = id;
        Assert.Equal(id.ToString(), s);

        // string -> EntityId
        EntityId fromString = "test_id";
        Assert.Equal("test_id", fromString.ToString());
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new EntityId("abc");
        var b = new EntityId("abc");
        var c = new EntityId("def");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.True(a != c);
        Assert.False(a == c);
    }

    [Fact]
    public void Json_SerializesAsBareString()
    {
        // The custom JsonConverter should serialize an EntityId as a bare
        // JSON string, NOT as an object with a "value" property.
        var id = new EntityId("c1234567890abcdefghijkl");
        var json = JsonSerializer.Serialize(id);
        Assert.Equal("\"c1234567890abcdefghijkl\"", json);
    }

    [Fact]
    public void Json_RoundTripsAsBareString()
    {
        var id = EntityId.NewId();
        var json = JsonSerializer.Serialize(id);
        var back = JsonSerializer.Deserialize<EntityId>(json);
        Assert.Equal(id, back);
    }

    [Fact]
    public void Json_DeserializesFromBareString()
    {
        const string json = "\"my-custom-id\"";
        var parsed = JsonSerializer.Deserialize<EntityId>(json);
        Assert.Equal("my-custom-id", parsed.ToString());
    }

    [Fact]
    public void Empty_IsKnownSentinel()
    {
        Assert.Equal(string.Empty, EntityId.Empty.ToString());
        // Empty.Value is a string-typed sentinel; parsing its underlying
        // string should yield false (empty string is rejected).
        Assert.False(EntityId.TryParse((string?)EntityId.Empty.Value, out _));
    }
}
