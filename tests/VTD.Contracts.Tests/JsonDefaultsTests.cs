using System.Text.Json;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Serialization;

namespace VTD.Contracts.Tests;

/// <summary>
/// Regression: absent init-only members keep their C# initializer defaults through <see cref="VtdJson"/> (the
/// source-generated object initializer used to turn them into default(T)), in strict, lenient, indented and RPC options.
/// </summary>
public sealed class JsonDefaultsTests
{
    private const string Guide = "{\"id\":\"g\",\"titleKey\":\"t\",\"branches\":[]}";
    private const string Rule = "{\"id\":\"x\",\"category\":\"other\",\"titleKey\":\"t\",\"prior\":{\"threads\":1}}";
    private const string Fix = "{\"id\":\"f\",\"titleKey\":\"t\",\"explainKey\":\"e\",\"undoExplainKey\":\"u\",\"risk\":\"low\",\"reversibility\":\"exact\",\"steps\":[]}";

    public static TheoryData<string> OptionSets => new() { "strict", "lenient", "rpc" };

    private static T Read<T>(string json, string options) => options switch
    {
        "strict" => VtdJson.Deserialize<T>(json),
        "lenient" => VtdJson.DeserializeLenient<T>(json),
        _ => JsonSerializer.Deserialize<T>(json, VtdJson.CreateRpcOptions())!,
    };

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void Guide_keeps_risk_and_bucket_defaults(string options)
    {
        var g = Read<GuideDef>(Guide, options);
        var expected = new GuideDef { Id = "g", TitleKey = "t", Branches = [] };
        Assert.Equal(expected.Risk, g.Risk);
        Assert.Equal(expected.Bucket, g.Bucket);
        Assert.Equal(RiskLevel.High, g.Risk);
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void Rule_keeps_rev_and_empty_lists(string options)
    {
        var r = Read<RuleDef>(Rule, options);
        Assert.Equal(1, r.Rev);
        Assert.NotNull(r.Symptoms);
        Assert.Empty(r.Symptoms);
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void Fix_keeps_admin_and_rev(string options)
    {
        var f = Read<FixDef>(Fix, options);
        Assert.True(f.Admin);
        Assert.Equal(1, f.Rev);
    }

    [Fact]
    public void Analysis_lists_absent_from_json_are_empty_not_null()
    {
        var full = Fixtures_Analysis();
        var json = VtdJson.Serialize(full);
        using var doc = JsonDocument.Parse(json);
        var trimmed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() != 0)
            {
                trimmed[p.Name] = p.Value.Clone();
            }
        }

        var back = VtdJson.Deserialize<AnalysisResult>(JsonSerializer.Serialize(trimmed));
        Assert.NotNull(back.Proposals);
        Assert.NotNull(back.CouldNotCheck);
        Assert.NotNull(back.Anomalies);
    }

    [Fact]
    public void Strictness_is_unchanged()
    {
        Assert.ThrowsAny<JsonException>(() => VtdJson.Deserialize<GuideDef>("{\"id\":\"g\",\"titleKey\":\"t\",\"branches\":[],\"bogus\":1}"));
        Assert.ThrowsAny<JsonException>(() => VtdJson.Deserialize<GuideDef>("{\"id\":\"g\",\"branches\":[]}"));
        Assert.ThrowsAny<JsonException>(() => VtdJson.Deserialize<FixDef>(Fix.Replace("\"low\"", "0", StringComparison.Ordinal)));
        Assert.NotNull(VtdJson.DeserializeLenient<GuideDef>("{\"id\":\"g\",\"titleKey\":\"t\",\"branches\":[],\"bogus\":1}"));
    }

    [Fact]
    public void Types_outside_the_context_are_still_rejected()
    {
        Assert.Throws<NotSupportedException>(() => VtdJson.Serialize(new Unregistered(1)));
    }

    private static AnalysisResult Fixtures_Analysis() => VTD.Testing.Fakes.CannedAnalyses.For(VTD.Testing.Fakes.FixtureSnapshots.RealtekStutter());

    private sealed record Unregistered(int X);
}
