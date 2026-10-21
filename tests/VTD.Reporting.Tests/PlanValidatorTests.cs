using System.Text;
using VTD.Contracts;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Serialization;
using VTD.Reporting.Crypto;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary>Plan safety (design_B.md section 9) against a fresh scan of the Realtek and Nahimic fixture PCs.</summary>
public sealed class PlanValidatorTests
{
    private static readonly DateTimeOffset Now = FakeClock.DefaultStart.AddHours(2);
    private static readonly byte[] HelperSeed = [.. Enumerable.Range(0, 32).Select(i => (byte)(i + 100))];

    private static string AdapterShareId => TestSupport.Redactor().ShareEntityId(new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId });

    private static FixPlan Plan(params PlanStep[] steps) => new()
    {
        Schema = PlanLimits.CurrentSchema,
        PlanId = "01JB7Z6Q4N8V2X3Y5W7T9R1M0K",
        ForSnapshotId = FixtureSnapshots.RealtekId,
        KbMin = "2026.10.1",
        CreatedUtc = FakeClock.DefaultStart,
        ExpiresUtc = FakeClock.DefaultStart.AddDays(3),
        Author = new PlanAuthor { Name = "u/ModName" },
        Message = "Looks like your spare network port. Try step 1, then play a Deathmatch. https://example.com/why",
        Steps = steps,
    };

    private static PlanFixStep DisableAdapter(params string[] ids) => new()
    {
        FixId = "fix.net.disable_unused_adapter",
        Bind = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["adapter"] = ids },
        Note = "You're on Wi-Fi so this is safe.",
    };

    private static PlanValidation Validate(string text, string fixture = Fixtures.RealtekStutter, IKnowledgeBase? kb = null, DateTimeOffset? now = null)
        => new PlanValidator(TestSupport.Redactor()).Validate(text, TestSupport.Snapshot(fixture), kb ?? FakeKnowledgeBase.Sample(), now ?? Now);

    private static PlanValidation Validate(FixPlan plan, string fixture = Fixtures.RealtekStutter, IKnowledgeBase? kb = null, DateTimeOffset? now = null)
        => Validate(VtdJson.Serialize(plan), fixture, kb, now);

    private static FakeKnowledgeBase Kb(string version = FixtureSnapshots.KbVersion, IReadOnlyList<TrustedHelperKey>? helpers = null, IReadOnlyList<FixDef>? extraFixes = null)
    {
        var sample = FakeKnowledgeBase.Sample();
        return new FakeKnowledgeBase(new KbVersion { Version = version, Format = KbManifest.CurrentFormat, Source = KbSource.Bundled })
        {
            Fixes = [.. sample.Fixes, .. extraFixes ?? []],
            Guides = sample.Guides,
            Questions = sample.Questions,
            Signals = sample.Signals,
            TrustedHelpers = helpers ?? [],
        };
    }

    private static string Sign(FixPlan plan, byte[] seed, Func<byte[], byte[]>? tamperPayload = null)
    {
        var payload = Encoding.UTF8.GetBytes(VtdJson.Serialize(plan));
        var signature = Ed25519.Sign(seed, payload);
        var sent = tamperPayload?.Invoke(payload) ?? payload;
        return VtdJson.Serialize(new SignedPlanEnvelope
        {
            Format = SignedPlanEnvelope.CurrentFormat,
            Payload = Convert.ToBase64String(sent),
            PublicKey = "ed25519:" + Convert.ToBase64String(Ed25519.GetPublicKey(seed)),
            Signature = Convert.ToBase64String(signature),
        });
    }

    [Fact]
    public void Valid_plain_plan_resolves_the_realtek_adapter_to_the_live_entity()
    {
        var plan = Plan(
            DisableAdapter(AdapterShareId),
            new PlanMonitorStep { Minutes = 10, Note = "Play one Deathmatch." },
            new PlanQuestionStep { QuestionId = "ask.only_valorant_affected" },
            new PlanGuideStep { GuideId = "guide.secureboot.enable", BranchId = "uefi_sb_off" });
        var v = Validate(plan);
        Assert.True(v.Valid, string.Join(", ", v.Issues.Select(i => i.Code + i.Path)));
        Assert.Equal(PlanTrust.Unsigned, v.Trust);
        Assert.Null(v.HelperName);
        Assert.Equal(plan.PlanId, v.PlanId);
        Assert.Empty(v.Issues);
        Assert.Equal(4, v.Steps.Count);
        Assert.All(v.Steps, s => Assert.Equal(PlanStepStatus.Ok, s.Status));
        var bound = v.Steps[0].ResolvedBinding!.Entities["adapter"].Single();
        Assert.Equal(EntityKind.Adapter, bound.Kind);
        Assert.Equal(FixtureSnapshots.RealtekAdapterInstanceId, bound.Id);
        Assert.Equal([PlanStepKind.Fix, PlanStepKind.Monitor, PlanStepKind.Question, PlanStepKind.Guide], v.Steps.Select(s => s.Kind));
    }

    [Fact]
    public void Share_ids_are_matched_case_insensitively()
        => Assert.Equal(PlanStepStatus.Ok, Validate(Plan(DisableAdapter(AdapterShareId.ToLowerInvariant()))).Steps[0].Status);

    [Fact]
    public void Service_bindings_resolve_by_name_on_the_nahimic_pc()
    {
        var step = new PlanFixStep { FixId = "fix.svc.disable_conflict_service", Bind = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["svc"] = ["NahimicService"] } };
        var v = Validate(Plan(step) with { ForSnapshotId = FixtureSnapshots.NahimicId }, Fixtures.NahimicBlackScreen);
        Assert.True(v.Valid);
        Assert.Equal("NahimicService", v.Steps[0].ResolvedBinding!.Entities["svc"].Single().Id);
    }

    [Theory]
    [InlineData(FixtureSnapshots.RealtekAdapterInstanceId)] // the raw live id is not a share id
    [InlineData("PCI\\VEN_10EC&DEV_8125&SUBSYS_87D71043&REV_05\\h:0000000000000000")] // another PC / tampered hash
    [InlineData("PCI\\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A\\h:ffffffffffffffff")]
    public void Ids_that_do_not_hash_to_a_live_entity_grey_the_step(string id)
    {
        var v = Validate(Plan(DisableAdapter(id)));
        Assert.True(v.Valid);
        Assert.Equal(PlanStepStatus.Greyed, v.Steps[0].Status);
        Assert.Equal("plan.step.binding_unresolved", v.Steps[0].ReasonKey);
        Assert.Null(v.Steps[0].ResolvedBinding);
    }

    [Fact]
    public void A_plan_written_against_another_install_salt_does_not_bind()
    {
        var foreign = new Redactor(TestSupport.OtherSalt).ShareEntityId(new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId });
        Assert.Equal("plan.step.binding_unresolved", Validate(Plan(DisableAdapter(foreign))).Steps[0].ReasonKey);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("count")]
    [InlineData("unknown")]
    public void Bindings_must_match_the_fix_definition(string what)
    {
        var step = what switch
        {
            "missing" => new PlanFixStep { FixId = "fix.net.disable_unused_adapter" },
            "count" => DisableAdapter(AdapterShareId, AdapterShareId),
            _ => DisableAdapter(AdapterShareId) with { Bind = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["adapter"] = [AdapterShareId], ["extra"] = ["x"] } },
        };
        var v = Validate(Plan(step));
        Assert.Equal(PlanStepStatus.Greyed, v.Steps[0].Status);
        Assert.Equal("plan.step.binding_" + what, v.Steps[0].ReasonKey);
    }

    [Fact]
    public void Expired_plans_are_invalid()
    {
        Assert.Contains(Validate(Plan(DisableAdapter(AdapterShareId)), now: FakeClock.DefaultStart.AddDays(4)).Issues, i => i.Code == "expired");
        var tooLong = Plan(DisableAdapter(AdapterShareId)) with { ExpiresUtc = FakeClock.DefaultStart.AddDays(8) };
        var v = Validate(tooLong);
        Assert.False(v.Valid);
        Assert.Contains(v.Issues, i => i.Code == "expired" && i.Path == "/expiresUtc");
        var future = Plan(DisableAdapter(AdapterShareId)) with { CreatedUtc = Now.AddHours(1), ExpiresUtc = Now.AddDays(2) };
        Assert.Contains(Validate(future).Issues, i => i.Code == "expired" && i.Path == "/createdUtc");
        var withinTolerance = Plan(DisableAdapter(AdapterShareId)) with { CreatedUtc = Now.AddMinutes(5), ExpiresUtc = Now.AddDays(2) };
        Assert.True(Validate(withinTolerance).Valid);
    }

    [Fact]
    public void More_than_ten_steps_is_invalid_and_the_extra_steps_are_greyed()
    {
        var steps = Enumerable.Range(0, 11).Select(_ => (PlanStep)new PlanMonitorStep { Minutes = 5 }).ToArray();
        var v = Validate(Plan(steps));
        Assert.False(v.Valid);
        Assert.Contains(v.Issues, i => i.Code == "too_many_steps");
        Assert.Equal("plan.step.over_limit", v.Steps[10].ReasonKey);
        Assert.Equal(PlanStepStatus.Ok, v.Steps[9].Status);
    }

    [Fact]
    public void Unknown_ids_grey_their_step_but_keep_the_plan()
    {
        var v = Validate(Plan(
            new PlanFixStep { FixId = "fix.made.up" },
            new PlanGuideStep { GuideId = "guide.made.up" },
            new PlanQuestionStep { QuestionId = "ask.made_up" },
            new PlanGuideStep { GuideId = "guide.secureboot.enable", BranchId = "no_such_branch" },
            DisableAdapter(AdapterShareId)));
        Assert.True(v.Valid);
        Assert.Equal(3, v.Issues.Count(i => i.Code == "unknown_id"));
        Assert.Equal(["plan.step.unknown_fix", "plan.step.unknown_guide", "plan.step.unknown_question", "plan.step.unknown_branch"], v.Steps.Take(4).Select(s => s.ReasonKey!));
        Assert.Equal(PlanStepStatus.Ok, v.Steps[4].Status);
        Assert.Contains(v.Issues, i => i.Path == "/steps/0/fixId");
    }

    [Fact]
    public void High_risk_fixes_are_greyed_guides_only()
    {
        var risky = new FixDef { Id = "fix.fw.flash_bios", TitleKey = "x", ExplainKey = "x", UndoExplainKey = "x", Risk = RiskLevel.High, Reversibility = Reversibility.OneWay };
        var v = Validate(Plan(new PlanFixStep { FixId = risky.Id }), kb: Kb(extraFixes: [risky]));
        Assert.True(v.Valid);
        Assert.Equal("plan.step.high_risk_guide_only", v.Steps[0].ReasonKey);
    }

    [Theory]
    [InlineData("2026.10.1", "2026.10.21.1", true)]
    [InlineData("2026.10.21.1", "2026.10.21.1", true)]
    [InlineData("2026.10.21", "2026.10.21.1", true)]
    [InlineData("2026.10.22", "2026.10.21.1", false)]
    [InlineData("2027.1.1", "2026.10.21.1", false)]
    [InlineData("2026.10.21.1", "2026.10.9", false)] // numeric, not lexicographic: 9 < 21
    public void Kb_must_be_at_least_kbMin(string kbMin, string kbVersion, bool ok)
    {
        var v = Validate(Plan(DisableAdapter(AdapterShareId)) with { KbMin = kbMin }, kb: Kb(kbVersion));
        Assert.Equal(ok, v.Valid);
        Assert.Equal(!ok, v.Issues.Any(i => i.Code == "kb_too_old"));
    }

    [Fact]
    public void Monitor_minutes_must_be_1_to_60()
    {
        var v = Validate(Plan(new PlanMonitorStep { Minutes = 0 }, new PlanMonitorStep { Minutes = 61 }, new PlanMonitorStep { Minutes = 60 }));
        Assert.Equal(["plan.step.monitor_minutes", "plan.step.monitor_minutes", null], v.Steps.Select(s => s.ReasonKey));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{\"schema\":2,\"planId\":\"01JB7Z6Q4N8V2X3Y5W7T9R1M0K\"}")]
    [InlineData("{\"schema\":1,\"planId\":\"01JB7Z6Q4N8V2X3Y5W7T9R1M0K\"}")]
    public void Malformed_plans_are_schema_errors(string text)
    {
        var v = Validate(text);
        Assert.False(v.Valid);
        Assert.Contains(v.Issues, i => i.Code == "schema");
    }

    [Fact]
    public void Unknown_step_kinds_and_scripts_are_schema_errors()
    {
        var json = VtdJson.Serialize(Plan(new PlanMonitorStep { Minutes = 5 }))
            .Replace("\"kind\":\"monitor\",\"minutes\":5", "\"kind\":\"script\",\"command\":\"powershell -c Remove-Item C:\\\\\"", StringComparison.Ordinal);
        Assert.Contains("\"script\"", json, StringComparison.Ordinal);
        var v = Validate(json);
        Assert.False(v.Valid);
        Assert.Contains(v.Issues, i => i.Code == "schema" && i.Path == "/steps/0/kind");
    }

    [Fact]
    public void Lengths_and_ulids_are_checked()
    {
        var v = Validate(Plan(new PlanMonitorStep { Minutes = 5, Note = new string('x', PlanLimits.MaxNoteLength + 1) }) with
        {
            PlanId = "not-a-ulid",
            Message = new string('m', PlanLimits.MaxMessageLength + 1),
        });
        Assert.False(v.Valid);
        Assert.Contains(v.Issues, i => i.Path == "/planId");
        Assert.Contains(v.Issues, i => i.Path == "/message");
        Assert.Contains(v.Issues, i => i.Path == "/steps/0/note");
    }

    [Fact]
    public void A_machine_key_mismatch_is_a_snapshot_mismatch()
    {
        var json = VtdJson.Serialize(Plan(DisableAdapter(AdapterShareId)));
        var other = json.Insert(1, "\"forMachineKey\":\"mk1:0000000000000000\",");
        var same = json.Insert(1, "\"forMachineKey\":\"mk1:7f3c2a9e41d05b6c\",");
        Assert.Contains(Validate(other).Issues, i => i.Code == "snapshot_mismatch");
        Assert.True(Validate(same).Valid);
    }

    [Fact]
    public void A_verified_helper_signature_is_trusted()
    {
        var publicKey = "ed25519:" + Convert.ToBase64String(Ed25519.GetPublicKey(HelperSeed));
        var kb = Kb(helpers: [new TrustedHelperKey { Name = "u/ModName", PublicKey = publicKey }]);
        var v = Validate(Sign(Plan(DisableAdapter(AdapterShareId)), HelperSeed), kb: kb);
        Assert.True(v.Valid, string.Join(", ", v.Issues.Select(i => i.Code)));
        Assert.Equal(PlanTrust.VerifiedHelper, v.Trust);
        Assert.Equal("u/ModName", v.HelperName);
        Assert.Equal(PlanStepStatus.Ok, v.Steps[0].Status);
    }

    [Fact]
    public void An_expired_helper_key_is_only_a_signed_unknown_key()
    {
        var publicKey = "ed25519:" + Convert.ToBase64String(Ed25519.GetPublicKey(HelperSeed));
        var kb = Kb(helpers: [new TrustedHelperKey { Name = "u/ModName", PublicKey = publicKey, ValidUntil = new DateOnly(2026, 1, 1) }]);
        Assert.Equal(PlanTrust.SignedUnknownKey, Validate(Sign(Plan(DisableAdapter(AdapterShareId)), HelperSeed), kb: kb).Trust);
    }

    [Fact]
    public void A_valid_signature_from_an_unknown_key_is_signed_unknown()
    {
        var v = Validate(Sign(Plan(DisableAdapter(AdapterShareId)), [.. Enumerable.Range(0, 32).Select(i => (byte)i)]));
        Assert.True(v.Valid);
        Assert.Equal(PlanTrust.SignedUnknownKey, v.Trust);
        Assert.Null(v.HelperName);
    }

    [Fact]
    public void A_tampered_payload_is_a_bad_signature()
    {
        var publicKey = "ed25519:" + Convert.ToBase64String(Ed25519.GetPublicKey(HelperSeed));
        var kb = Kb(helpers: [new TrustedHelperKey { Name = "u/ModName", PublicKey = publicKey }]);
        var text = Sign(Plan(new PlanMonitorStep { Minutes = 5 }), HelperSeed, payload =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("\"minutes\":5", "\"minutes\":6", StringComparison.Ordinal)));
        var v = Validate(text, kb: kb);
        Assert.False(v.Valid);
        Assert.Equal(PlanTrust.BadSignature, v.Trust);
        Assert.Contains(v.Issues, i => i.Code == "bad_signature");
        Assert.Equal("01JB7Z6Q4N8V2X3Y5W7T9R1M0K", v.PlanId);
        Assert.Empty(v.Steps);
    }

    [Fact]
    public void A_garbage_signature_or_key_is_a_bad_signature()
    {
        var good = VtdJson.Deserialize<SignedPlanEnvelope>(Sign(Plan(new PlanMonitorStep { Minutes = 5 }), HelperSeed));
        var badSig = good with { Signature = Convert.ToBase64String(new byte[64]) };
        var badKey = good with { PublicKey = "ed25519:AAAA" };
        var notBase64 = good with { Signature = "%%%" };
        foreach (var envelope in new[] { badSig, badKey, notBase64 })
        {
            var v = Validate(VtdJson.Serialize(envelope));
            Assert.Equal(PlanTrust.BadSignature, v.Trust);
            Assert.False(v.Valid);
        }
    }

    [Fact]
    public void An_unknown_envelope_format_is_a_schema_error()
    {
        var good = VtdJson.Deserialize<SignedPlanEnvelope>(Sign(Plan(new PlanMonitorStep { Minutes = 5 }), HelperSeed));
        var v = Validate(VtdJson.Serialize(good with { Format = "vtdplan-signed/2" }));
        Assert.False(v.Valid);
        Assert.Contains(v.Issues, i => i.Code == "schema" && i.Path == "/format");
    }

    [Fact]
    public void Notes_and_message_with_urls_are_fine_but_never_actions()
    {
        var v = Validate(Plan(new PlanMonitorStep { Minutes = 5, Note = "see https://example.com and C:\\Windows\\System32 — just text" }));
        Assert.True(v.Valid);
        Assert.Equal(PlanStepStatus.Ok, v.Steps[0].Status);
    }
}
