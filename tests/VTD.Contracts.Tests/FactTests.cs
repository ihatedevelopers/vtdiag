using System.Text.Json;
using VTD.Contracts.Serialization;

namespace VTD.Contracts.Tests;

/// <summary>Three-valued facts: Unknown is never False, on the API and on the wire.</summary>
public sealed class FactTests
{
    [Fact]
    public void Reading_the_value_of_an_unknown_fact_throws()
    {
        var f = Fact<bool>.Unknown(UnknownReason.NeedsAdmin, "not elevated", "security/firmware:SetupMode");
        Assert.False(f.IsKnown);
        Assert.Throws<InvalidOperationException>(() => f.Value);
        Assert.False(f.TryGetValue(out _));
        Assert.Equal(Tri.Unknown, f.ToTri());
    }

    [Fact]
    public void Known_false_and_unknown_serialize_differently()
    {
        var known = VtdJson.Serialize(Fact<bool>.Known(false, "s"));
        var unknown = VtdJson.Serialize(Fact<bool>.Unknown(UnknownReason.TimedOut));
        var na = VtdJson.Serialize(Fact<bool>.NotApplicable());
        Assert.Equal("""{"status":"known","value":false,"source":"s"}""", known);
        Assert.Equal("""{"status":"unknown","reason":"timedOut"}""", unknown);
        Assert.Equal("""{"status":"notApplicable"}""", na);
    }

    [Theory]
    [InlineData("""{"status":"unknown","reason":"failed","value":false}""")]
    [InlineData("""{"status":"known"}""")]
    [InlineData("""{"status":"unknown"}""")]
    [InlineData("""{"status":"known","value":null}""")]
    [InlineData("""{"status":"maybe","value":true}""")]
    [InlineData("""{"status":"known","value":true,"extra":1}""")]
    [InlineData("""{"value":true}""")]
    [InlineData("""{"status":"unknown","reason":"failed","complete":false}""")]
    [InlineData("""{"status":"known","value":true,"complete":"no"}""")]
    public void Contradictory_fact_documents_are_rejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() => VtdJson.Deserialize<Fact<bool>>(json));
    }

    [Fact]
    public void Facts_round_trip_with_record_payloads()
    {
        var f = Fact.Known(new BootIdentity { BootId = 7, LastBootUpTimeUtc = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), Kind = BootKind.Cold }, "os");
        var back = VtdJson.Deserialize<Fact<BootIdentity>>(VtdJson.Serialize(f));
        Assert.Equal(f, back);
    }

    [Fact]
    public void Partial_lists_round_trip_and_differ_from_complete_ones()
    {
        IReadOnlyList<string> items = ["a.sys"];
        var partial = Fact<IReadOnlyList<string>>.Partial(items, "needs_admin:2", "devices/drivers");
        var json = VtdJson.Serialize(partial);
        Assert.Equal("""{"status":"known","value":["a.sys"],"complete":false,"detail":"needs_admin:2","source":"devices/drivers"}""", json);
        var back = VtdJson.Deserialize<Fact<IReadOnlyList<string>>>(json);
        Assert.True(back.IsKnown);
        Assert.False(back.IsComplete);
        Assert.Equal("needs_admin:2", back.Detail);
        Assert.False(partial.Map(l => l.Count).IsComplete);
        var complete = VtdJson.Deserialize<Fact<IReadOnlyList<string>>>("""{"status":"known","value":["a.sys"]}""");
        Assert.True(complete.IsComplete);
        Assert.True(Fact<int>.Unknown(UnknownReason.Failed).IsComplete);
        Assert.DoesNotContain("complete", VtdJson.Serialize(Fact.Known(1)), StringComparison.Ordinal);
    }

    [Fact]
    public void Map_keeps_the_unknown_reason()
    {
        var f = Fact<int>.Unknown(UnknownReason.NeedsAdmin).Map(v => v > 0);
        Assert.Equal(UnknownReason.NeedsAdmin, f.Reason);
        Assert.Equal(7, Fact.Known(6).Map(v => v + 1).Value);
    }

    [Fact]
    public void Known_requires_a_value()
    {
        Assert.Throws<ArgumentNullException>(() => Fact<string>.Known(null!));
    }

    [Fact]
    public void Kleene_not_leaves_unknown_alone()
    {
        Assert.Equal(Tri.False, Tri.True.Not());
        Assert.Equal(Tri.Unknown, Tri.Unknown.Not());
        Assert.Equal(Tri.NotApplicable, Tri.NotApplicable.Not());
        Assert.Equal(Tri.Unknown, ((bool?)null).ToTri());
    }

    [Fact]
    public void Ulids_are_valid_sortable_and_culture_free()
    {
        var clock = new VTD.Testing.Fakes.FakeClock();
        var gen = new UlidGenerator(clock);
        var a = gen.NewId();
        clock.Advance(TimeSpan.FromMilliseconds(5));
        var b = gen.NewId();
        Assert.True(Ulid.IsValid(a));
        Assert.True(string.CompareOrdinal(a, b) < 0);
        Assert.False(Ulid.IsValid("01JB6R7X3K9M2N4P5Q6R7S8TIL"));
    }

    [Fact]
    public void Canonical_hash_is_order_independent_and_escapes_separators()
    {
        var a = CanonicalHash.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["b"] = "2", ["a"] = "1" });
        var b = CanonicalHash.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2" });
        Assert.Equal(a, b);
        Assert.NotEqual(
            CanonicalHash.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1\nb=2" }),
            CanonicalHash.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2" }));
        Assert.Equal(64, a.Length);
    }
}
