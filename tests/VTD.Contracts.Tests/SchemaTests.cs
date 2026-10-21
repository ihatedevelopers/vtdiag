using VTD.Contracts.Assist;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Serialization;
using VTD.Testing.Fakes;

namespace VTD.Contracts.Tests;

/// <summary>kb/schema/*.schema.json are generated from the contracts and must never drift from them.</summary>
public sealed class SchemaTests
{
    public static TheoryData<string> SchemaNames()
    {
        var data = new TheoryData<string>();
        foreach (var spec in SchemaGenerator.Specs)
        {
            data.Add(spec.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SchemaNames))]
    public void Checked_in_schema_matches_the_contract(string name)
    {
        var spec = SchemaGenerator.Specs.Single(s => s.Name == name);
        var generated = SchemaGenerator.Generate(spec);
        var path = Path.Combine(RepoPaths.SchemaDir, name + ".schema.json");

        if (RepoPaths.UpdateSchemas)
        {
            Directory.CreateDirectory(RepoPaths.SchemaDir);
            File.WriteAllText(path, generated);
        }

        Assert.True(File.Exists(path), $"{path} is missing. Run the tests once with VTD_UPDATE_SCHEMAS=1.");
        var onDisk = RepoPaths.Normalize(File.ReadAllText(path));
        Assert.True(string.Equals(generated, onDisk, StringComparison.Ordinal), $"{name}.schema.json is out of date with VTD.Contracts. Re-run with VTD_UPDATE_SCHEMAS=1 and review the diff.");
    }

    [Fact]
    public void Generated_schemas_are_self_contained()
    {
        foreach (var spec in SchemaGenerator.Specs)
        {
            var text = SchemaGenerator.Generate(spec);
            var refs = System.Text.RegularExpressions.Regex.Matches(text, "\"\\$ref\": \"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.All(refs, r => Assert.Equal("#/$defs/item", r));
        }
    }

    [Fact]
    public void Sample_kb_items_are_valid_against_their_schemas()
    {
        var kb = FakeKnowledgeBase.Sample();
        AssertAllValid("rule", kb.Rules);
        AssertAllValid("signal", kb.Signals);
        AssertAllValid("collector", kb.Collectors);
        AssertAllValid("conflict", kb.Conflicts);
        AssertAllValid("code", kb.ErrorCodes);
        AssertAllValid("fix", kb.Fixes);
        AssertAllValid("guide", kb.Guides);
        AssertAllValid("question", kb.Questions);
        AssertAllValid("symptom", kb.Symptoms);
        AssertAllValid("table", kb.Tables);

        // Array files are valid too (kb/conflicts/<kind>.json holds a list).
        Assert.Empty(SchemaValidation.Validate("conflict", VtdJson.Serialize(kb.Conflicts)));
        Assert.Empty(SchemaValidation.Validate("collector", VtdJson.Serialize(kb.Collectors)));
    }

    [Fact]
    public void Kb_schemas_reject_an_unknown_weight_and_unknown_properties()
    {
        // There is no weight for Unknown in a rule: evidence over an Unknown fact contributes nothing.
        const string BadRule = """
            {"id":"x","category":"fps_performance","titleKey":"t","prior":{"threads":1},
             "evidence":[{"id":"e","signal":"s","true":1.0,"unknown":0.5}]}
            """;
        Assert.NotEmpty(SchemaValidation.Validate("rule", BadRule));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => VtdJson.Deserialize<RuleDef>(BadRule));
    }

    [Fact]
    public void Sample_plan_is_valid_and_carries_only_kb_ids()
    {
        var plan = new FixPlan
        {
            Schema = PlanLimits.CurrentSchema,
            PlanId = "01JB7A2B3C4D5E6F7G8H9J0K1M",
            ForSnapshotId = FixtureSnapshots.RealtekId,
            KbMin = "2026.10.1",
            CreatedUtc = new DateTimeOffset(2026, 10, 21, 20, 10, 0, TimeSpan.Zero),
            ExpiresUtc = new DateTimeOffset(2026, 10, 28, 20, 10, 0, TimeSpan.Zero),
            Author = new PlanAuthor { Name = "u/ModName" },
            Message = "Hey! Looks like your spare network port. Try step 1, then play a DM.",
            Steps =
            [
                new PlanFixStep { FixId = "fix.net.disable_unused_adapter", Bind = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["adapter"] = [FixtureSnapshots.RealtekAdapterInstanceId] }, Note = "You're on Wi-Fi so this is safe." },
                new PlanMonitorStep { Minutes = 10, Note = "Play one Deathmatch." },
                new PlanQuestionStep { QuestionId = "ask.only_valorant_affected" },
                new PlanGuideStep { GuideId = "guide.secureboot.enable" },
            ],
        };
        var json = VtdJson.Serialize(plan, indented: true);
        Assert.Empty(SchemaValidation.Validate("plan", json));
        Assert.Contains("\"kind\": \"fix\"", json, StringComparison.Ordinal);

        // A step kind that is not a KB id (for example a command) is rejected by schema and by the contract.
        var withCommand = json.Replace("\"kind\": \"monitor\"", "\"kind\": \"command\"", StringComparison.Ordinal);
        Assert.NotEmpty(SchemaValidation.Validate("plan", withCommand));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => VtdJson.Deserialize<FixPlan>(withCommand));
    }

    private static void AssertAllValid<T>(string schema, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            var json = VtdJson.Serialize(item);
            var errors = SchemaValidation.Validate(schema, json);
            Assert.True(errors.Count == 0, $"{schema}: {string.Join("; ", errors)}\n{json}");
        }
    }
}
