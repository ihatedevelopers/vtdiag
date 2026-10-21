using System.Globalization;
using System.Text;
using System.Text.Json;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Reporting.Crypto;

namespace VTD.Reporting;

/// <summary>
/// Validates a <c>.vtdplan</c> (design_B.md section 9 plan safety) against a FRESH scan of this PC. The helper, and any
/// AI that wrote the plan, is untrusted: only KB fix, guide, question and monitor ids pass; bindings must resolve to
/// exactly one live entity each by recomputing <see cref="IRedactor.ShareEntityId"/> for every candidate; high-risk
/// fixes are greyed (guides only). Plan-level problems (<c>schema</c>, <c>expired</c>, <c>kb_too_old</c>,
/// <c>too_many_steps</c>, <c>bad_signature</c>, <c>snapshot_mismatch</c>) make the plan invalid; <c>unknown_id</c> only
/// greys its step. Guards and ProtectedTargetPolicy are re-checked by the engine's preview when a step runs.
/// </summary>
/// <remarks>
/// <c>snapshot_mismatch</c>: the fresh scan has a NEW snapshot id, so <see cref="FixPlan.ForSnapshotId"/> cannot be
/// compared with it. The machine key can: <see cref="FixPlan.ForMachineKey"/> (the <see cref="SnapshotMeta.MachineKey"/>
/// of the share snapshot it was written for; the same on every scan of one PC) is compared when present. Without it,
/// binding resolution against the live PC is the protection: ids from another PC's snapshot hash differently and grey
/// their steps out.
/// </remarks>
public sealed class PlanValidator : IPlanValidator
{
    /// <summary>How far in the future <see cref="FixPlan.CreatedUtc"/> may be (clocks are corrected before this runs).</summary>
    public static TimeSpan ClockTolerance { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Longest plan author name.</summary>
    public const int MaxAuthorNameLength = 100;

    /// <summary>Monitor step minutes range.</summary>
    public const int MaxMonitorMinutes = 60;

    /// <summary>Most ids one binding may list (a "pick all" binding still has a ceiling).</summary>
    public const int MaxIdsPerBinding = 32;

    private static readonly HashSet<string> StepKinds = new(["fix", "guide", "question", "monitor"], StringComparer.Ordinal);

    private readonly IRedactor _redactor;

    /// <summary>Creates the validator.</summary>
    /// <param name="redactor">The same redactor (same install salt) that produced the share ids.</param>
    public PlanValidator(IRedactor redactor) => _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

    /// <summary>Reason keys for greyed steps.</summary>
    public static class Reasons
    {
        /// <summary>The fix id is not in the KB.</summary>
        public const string UnknownFix = "plan.step.unknown_fix";

        /// <summary>The guide id is not in the KB.</summary>
        public const string UnknownGuide = "plan.step.unknown_guide";

        /// <summary>The guide branch does not exist.</summary>
        public const string UnknownBranch = "plan.step.unknown_branch";

        /// <summary>The question id is not in the KB.</summary>
        public const string UnknownQuestion = "plan.step.unknown_question";

        /// <summary>The fix is deprecated.</summary>
        public const string Deprecated = "plan.step.fix_deprecated";

        /// <summary>High-risk fixes are guides only.</summary>
        public const string HighRisk = "plan.step.high_risk_guide_only";

        /// <summary>A bind name of the fix is missing or empty.</summary>
        public const string BindingMissing = "plan.step.binding_missing";

        /// <summary>The plan binds a name the fix does not have.</summary>
        public const string BindingUnknown = "plan.step.binding_unknown";

        /// <summary>A "pick one" binding got more than one id.</summary>
        public const string BindingCount = "plan.step.binding_count";

        /// <summary>An id does not match exactly one live entity ("This step doesn't match your PC anymore").</summary>
        public const string BindingUnresolved = "plan.step.binding_unresolved";

        /// <summary>Monitor minutes outside 1..60.</summary>
        public const string MonitorMinutes = "plan.step.monitor_minutes";

        /// <summary>Step beyond the 10-step limit.</summary>
        public const string OverLimit = "plan.step.over_limit";

        /// <summary>The entity is a protected target (Riot/Vanguard, the active network adapter, the boot disk controller).</summary>
        public const string ProtectedTarget = "plan.step.protected_target";

        /// <summary>The fresh analysis does not offer this entity for this (non-template) fix.</summary>
        public const string BindingNotSuggested = "plan.step.binding_not_suggested";
    }

    /// <summary>Validates without an analysis (see the overload).</summary>
    /// <param name="planText">File content.</param>
    /// <param name="freshSnapshot">Fresh local-level snapshot.</param>
    /// <param name="kb">KB.</param>
    /// <param name="nowUtc">Trusted time.</param>
    public PlanValidation Validate(string planText, ScanSnapshot freshSnapshot, IKnowledgeBase kb, DateTimeOffset nowUtc)
        => Validate(planText, freshSnapshot, kb, nowUtc, freshAnalysis: null);

    /// <summary>
    /// Validates with the fresh analysis too: a non-template fix may then only bind entities that analysis offers for
    /// it (its proposals and hypotheses), which is what the binding's <c>FromSignal</c> means. Without an analysis that
    /// restriction cannot be checked; protected targets are refused either way and the engine's preview re-checks
    /// guards and ProtectedTargetPolicy. Never throws on plan content.
    /// </summary>
    /// <param name="planText">File content.</param>
    /// <param name="freshSnapshot">Fresh local-level snapshot.</param>
    /// <param name="kb">KB.</param>
    /// <param name="nowUtc">Trusted time.</param>
    /// <param name="freshAnalysis">Analysis of <paramref name="freshSnapshot"/>, when available.</param>
    public PlanValidation Validate(string planText, ScanSnapshot freshSnapshot, IKnowledgeBase kb, DateTimeOffset nowUtc, AnalysisResult? freshAnalysis)
    {
        ArgumentNullException.ThrowIfNull(planText);
        ArgumentNullException.ThrowIfNull(freshSnapshot);
        ArgumentNullException.ThrowIfNull(kb);
        try
        {
            return ValidateCore(planText, freshSnapshot, kb, nowUtc, freshAnalysis);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException
                                       or FormatException or OverflowException or NullReferenceException or KeyNotFoundException
                                       or System.Text.RegularExpressions.RegexMatchTimeoutException or DecoderFallbackException)
        {
            return Invalid("invalid", PlanTrust.Unsigned, [Issue("schema", string.Empty)]);
        }
    }

    private PlanValidation ValidateCore(string planText, ScanSnapshot freshSnapshot, IKnowledgeBase kb, DateTimeOffset nowUtc, AnalysisResult? freshAnalysis)
    {

        var issues = new List<PlanIssue>();
        JsonDocument outer;
        try
        {
            outer = JsonDocument.Parse(planText, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return Invalid("invalid", PlanTrust.Unsigned, [Issue("schema", string.Empty)]);
        }

        string planJson;
        var trust = PlanTrust.Unsigned;
        string? helperName = null;
        using (outer)
        {
            if (outer.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("invalid", PlanTrust.Unsigned, [Issue("schema", string.Empty)]);
            }

            if (outer.RootElement.TryGetProperty("format", out var formatElement) || outer.RootElement.TryGetProperty("payload", out _))
            {
                if (formatElement.ValueKind != JsonValueKind.String || !string.Equals(formatElement.GetString(), SignedPlanEnvelope.CurrentFormat, StringComparison.Ordinal))
                {
                    return Invalid("invalid", PlanTrust.Unsigned, [Issue("schema", "/format")]);
                }

                SignedPlanEnvelope envelope;
                try
                {
                    envelope = VtdJson.DeserializeLenient<SignedPlanEnvelope>(planText);
                }
                catch (JsonException)
                {
                    return Invalid("invalid", PlanTrust.Unsigned, [Issue("schema", string.Empty)]);
                }

                var payload = FromBase64(envelope.Payload);
                if (payload is null)
                {
                    return Invalid("invalid", PlanTrust.Unsigned, [Issue("schema", "/payload")]);
                }

                planJson = Encoding.UTF8.GetString(payload);
                var publicKey = ParsePublicKey(envelope.PublicKey);
                var signature = FromBase64(envelope.Signature);
                if (publicKey is null || signature is null || !Ed25519.Verify(publicKey, payload, signature))
                {
                    var id = TryReadPlanId(planJson);
                    return Invalid(id, PlanTrust.BadSignature, [Issue("bad_signature", "/signature")]);
                }

                var trusted = kb.TrustedHelpers.FirstOrDefault(k =>
                    ParsePublicKey(k.PublicKey) is { } key && key.AsSpan().SequenceEqual(publicKey)
                    && (k.ValidUntil is null || k.ValidUntil.Value >= DateOnly.FromDateTime(nowUtc.UtcDateTime)));
                if (trusted is not null)
                {
                    trust = PlanTrust.VerifiedHelper;
                    helperName = trusted.Name;
                }
                else
                {
                    trust = PlanTrust.SignedUnknownKey;
                }
            }
            else
            {
                planJson = planText;
            }
        }

        var plan = ParsePlan(planJson, issues);
        if (plan is null)
        {
            return Invalid(TryReadPlanId(planJson), trust, issues, helperName);
        }

        CheckLimits(plan, issues);
        CheckTime(plan, nowUtc, issues);
        CheckKb(plan, kb, issues);
        if (plan.ForMachineKey is { } forMachineKey && !string.Equals(forMachineKey, freshSnapshot.Meta.MachineKey, StringComparison.Ordinal))
        {
            issues.Add(Issue("snapshot_mismatch", "/forMachineKey"));
        }

        var ctx = new StepContext(this, freshSnapshot, kb, freshAnalysis);
        var steps = new List<PlanStepValidation>();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            steps.Add(i >= PlanLimits.MaxSteps
                ? new PlanStepValidation { Index = i, Kind = KindOf(plan.Steps[i]), Status = PlanStepStatus.Greyed, ReasonKey = Reasons.OverLimit }
                : ValidateStep(i, plan.Steps[i], ctx, issues));
        }

        var fatal = issues.Any(x => x.Code is not "unknown_id");
        return new PlanValidation
        {
            PlanId = plan.PlanId,
            Valid = !fatal,
            Trust = trust,
            HelperName = helperName,
            Steps = steps,
            Issues = issues,
        };
    }

    // ---------------------------------------------------------------- parsing

    private static FixPlan? ParsePlan(string json, List<PlanIssue> issues)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            issues.Add(Issue("schema", string.Empty));
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Issue("schema", string.Empty));
                return null;
            }

            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaNumber) || schemaNumber != PlanLimits.CurrentSchema)
            {
                issues.Add(Issue("schema", "/schema"));
                return null;
            }

            foreach (var required in (string[])["planId", "forSnapshotId", "kbMin", "createdUtc", "expiresUtc", "author", "steps"])
            {
                if (!root.TryGetProperty(required, out _))
                {
                    issues.Add(Issue("schema", "/" + required));
                }
            }

            if (root.TryGetProperty("steps", out var stepsElement))
            {
                if (stepsElement.ValueKind != JsonValueKind.Array)
                {
                    issues.Add(Issue("schema", "/steps"));
                }
                else
                {
                    var i = 0;
                    foreach (var step in stepsElement.EnumerateArray())
                    {
                        if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || !StepKinds.Contains(kind.GetString()!))
                        {
                            issues.Add(Issue("schema", "/steps/" + i.ToString(CultureInfo.InvariantCulture) + "/kind"));
                        }

                        i++;
                    }
                }
            }
        }

        if (issues.Count > 0)
        {
            return null;
        }

        FixPlan plan;
        try
        {
            plan = VtdJson.DeserializeLenient<FixPlan>(json);
        }
        catch (JsonException)
        {
            issues.Add(Issue("schema", string.Empty));
            return null;
        }
        catch (NotSupportedException)
        {
            issues.Add(Issue("schema", string.Empty));
            return null;
        }

        if (!Ulid.IsValid(plan.PlanId))
        {
            issues.Add(Issue("schema", "/planId"));
        }

        if (!Ulid.IsValid(plan.ForSnapshotId))
        {
            issues.Add(Issue("schema", "/forSnapshotId"));
        }

        if (plan.Author.Name.Length is 0 or > MaxAuthorNameLength)
        {
            issues.Add(Issue("schema", "/author/name"));
        }

        if (plan.Message is { Length: > PlanLimits.MaxMessageLength })
        {
            issues.Add(Issue("schema", "/message"));
        }

        if (plan.Steps is null || plan.Steps.Any(st => st is null))
        {
            issues.Add(Issue("schema", "/steps"));
            return null;
        }

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            if (plan.Steps[i].Note is { Length: > PlanLimits.MaxNoteLength })
            {
                issues.Add(Issue("schema", "/steps/" + i.ToString(CultureInfo.InvariantCulture) + "/note"));
            }
        }

        if (CalVer.Parse(plan.KbMin) is null)
        {
            issues.Add(Issue("schema", "/kbMin"));
        }

        return plan;
    }

    private static string TryReadPlanId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("planId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()!
                : "invalid";
        }
        catch (JsonException)
        {
            return "invalid";
        }
    }

    // ---------------------------------------------------------------- plan-level checks

    private static void CheckLimits(FixPlan plan, List<PlanIssue> issues)
    {
        if (plan.Steps.Count > PlanLimits.MaxSteps)
        {
            issues.Add(Issue("too_many_steps", "/steps"));
        }
    }

    private static void CheckTime(FixPlan plan, DateTimeOffset nowUtc, List<PlanIssue> issues)
    {
        if (nowUtc > plan.ExpiresUtc || plan.ExpiresUtc < plan.CreatedUtc || plan.ExpiresUtc - plan.CreatedUtc > PlanLimits.MaxLifetime)
        {
            issues.Add(Issue("expired", "/expiresUtc"));
        }
        else if (plan.CreatedUtc > nowUtc + ClockTolerance)
        {
            issues.Add(Issue("expired", "/createdUtc"));
        }
    }

    private static void CheckKb(FixPlan plan, IKnowledgeBase kb, List<PlanIssue> issues)
    {
        var have = CalVer.Parse(kb.Version.Version);
        var need = CalVer.Parse(plan.KbMin);
        if (need is not null && (have is null || CalVer.Compare(have, need) < 0))
        {
            issues.Add(Issue("kb_too_old", "/kbMin"));
        }
    }

    // ---------------------------------------------------------------- steps

    private PlanStepValidation ValidateStep(int index, PlanStep step, StepContext ctx, List<PlanIssue> issues)
    {
        var pointer = "/steps/" + index.ToString(CultureInfo.InvariantCulture);
        switch (step)
        {
            case PlanFixStep fixStep:
                return ValidateFix(index, fixStep, ctx, issues, pointer);
            case PlanGuideStep guideStep:
                var guide = ctx.Kb.FindGuide(guideStep.GuideId);
                if (guide is null)
                {
                    issues.Add(Issue("unknown_id", pointer + "/guideId"));
                    return Greyed(index, PlanStepKind.Guide, Reasons.UnknownGuide);
                }

                if (guideStep.BranchId is { } branch && !guide.Branches.Any(b => string.Equals(b.Id, branch, StringComparison.Ordinal)))
                {
                    return Greyed(index, PlanStepKind.Guide, Reasons.UnknownBranch);
                }

                return Ok(index, PlanStepKind.Guide);
            case PlanQuestionStep questionStep:
                if (ctx.Kb.FindQuestion(questionStep.QuestionId) is null)
                {
                    issues.Add(Issue("unknown_id", pointer + "/questionId"));
                    return Greyed(index, PlanStepKind.Question, Reasons.UnknownQuestion);
                }

                return Ok(index, PlanStepKind.Question);
            case PlanMonitorStep monitorStep:
                return monitorStep.Minutes is >= 1 and <= MaxMonitorMinutes
                    ? Ok(index, PlanStepKind.Monitor)
                    : Greyed(index, PlanStepKind.Monitor, Reasons.MonitorMinutes);
            default:
                return Greyed(index, PlanStepKind.Monitor, Reasons.UnknownFix);
        }
    }

    private PlanStepValidation ValidateFix(int index, PlanFixStep step, StepContext ctx, List<PlanIssue> issues, string pointer)
    {
        var fix = ctx.Kb.FindFix(step.FixId);
        if (fix is null)
        {
            issues.Add(Issue("unknown_id", pointer + "/fixId"));
            return Greyed(index, PlanStepKind.Fix, Reasons.UnknownFix);
        }

        if (fix.Deprecated is not null)
        {
            return Greyed(index, PlanStepKind.Fix, Reasons.Deprecated);
        }

        if (fix.Risk == RiskLevel.High)
        {
            return Greyed(index, PlanStepKind.Fix, Reasons.HighRisk);
        }

        var planBind = step.Bind ?? System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty;
        if (planBind.Keys.Any(k => !fix.Bind.ContainsKey(k)))
        {
            return Greyed(index, PlanStepKind.Fix, Reasons.BindingUnknown);
        }

        var resolved = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal);
        foreach (var (name, bind) in fix.Bind.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (!planBind.TryGetValue(name, out var ids) || ids is null || ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace))
            {
                return Greyed(index, PlanStepKind.Fix, Reasons.BindingMissing);
            }

            if ((bind.Pick == BindPick.One && ids.Count != 1) || ids.Count > MaxIdsPerBinding)
            {
                return Greyed(index, PlanStepKind.Fix, Reasons.BindingCount);
            }

            var allowed = !fix.Template && bind.FromSignal is not null && ctx.Analysis is { } analysis
                ? SuggestedEntities(analysis, fix.Id, name, bind.Kind)
                : null;
            var live = new List<EntityRef>();
            foreach (var id in ids)
            {
                var match = ctx.Resolve(bind.Kind, id);
                if (match is null || live.Any(e => string.Equals(e.Id, match.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    return Greyed(index, PlanStepKind.Fix, Reasons.BindingUnresolved);
                }

                if (PlanEntityCandidates.IsProtected(ctx.Fresh, match))
                {
                    return Greyed(index, PlanStepKind.Fix, Reasons.ProtectedTarget);
                }

                if (allowed is not null && !allowed.Contains(match.Id))
                {
                    return Greyed(index, PlanStepKind.Fix, Reasons.BindingNotSuggested);
                }

                live.Add(match);
            }

            resolved[name] = live;
        }

        return new PlanStepValidation
        {
            Index = index,
            Kind = PlanStepKind.Fix,
            Status = PlanStepStatus.Ok,
            ResolvedBinding = new FixBinding { Entities = resolved },
        };
    }

    /// <summary>Entities the fresh analysis offers for a non-template fix's binding (its proposals and the hypotheses
    /// that list the fix): the stand-in for the binding's <c>FromSignal</c>, whose signal results are not part of the
    /// analysis contract.</summary>
    private static HashSet<string> SuggestedEntities(AnalysisResult analysis, string fixId, string bindName, EntityKind kind)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proposal in analysis.Proposals.Where(p => string.Equals(p.FixId, fixId, StringComparison.Ordinal)))
        {
            if (proposal.Binding.Entities.TryGetValue(bindName, out var entities))
            {
                set.UnionWith(entities.Where(e => e.Kind == kind).Select(e => e.Id));
            }
        }

        foreach (var h in analysis.Hypotheses.Where(h => h.FixIds.Contains(fixId, StringComparer.Ordinal)))
        {
            set.UnionWith(h.Entities.Where(e => e.Kind == kind).Select(e => e.Id));
        }

        return set;
    }

    /// <summary>Per-validation lookups: live candidates per kind keyed by their share id (computed once).</summary>
    private sealed class StepContext(PlanValidator owner, ScanSnapshot fresh, IKnowledgeBase kb, AnalysisResult? analysis)
    {
        private readonly Dictionary<EntityKind, Dictionary<string, List<EntityRef>>> _byShareId = [];
        private IdentifierSet? _identifiers;

        public ScanSnapshot Fresh { get; } = fresh;

        public IKnowledgeBase Kb { get; } = kb;

        public AnalysisResult? Analysis { get; } = analysis;

        public EntityRef? Resolve(EntityKind kind, string planId)
        {
            if (kind == EntityKind.PowerSetting)
            {
                return TextPatterns.PowerSettingId().IsMatch(planId) ? new EntityRef { Kind = kind, Id = planId.ToLowerInvariant() } : null;
            }

            if (!_byShareId.TryGetValue(kind, out var map))
            {
                map = new Dictionary<string, List<EntityRef>>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in PlanEntityCandidates.Of(Fresh, kind))
                {
                    var shareId = ShareId(candidate);
                    if (!map.TryGetValue(shareId, out var list))
                    {
                        list = [];
                        map[shareId] = list;
                    }

                    list.Add(candidate);
                }

                _byShareId[kind] = map;
            }

            return map.TryGetValue(planId.Trim(), out var matches) && matches.Count == 1 ? matches[0] : null;
        }

        private string ShareId(EntityRef candidate)
        {
            if (owner._redactor is Redactor redactor)
            {
                _identifiers ??= redactor.CollectIdentifiers(Fresh);
                return redactor.ShareEntityId(candidate, _identifiers);
            }

            return owner._redactor.ShareEntityId(candidate);
        }
    }


    // ---------------------------------------------------------------- helpers

    private static PlanStepKind KindOf(PlanStep step) => step switch
    {
        PlanFixStep => PlanStepKind.Fix,
        PlanGuideStep => PlanStepKind.Guide,
        PlanQuestionStep => PlanStepKind.Question,
        _ => PlanStepKind.Monitor,
    };

    private static PlanStepValidation Ok(int index, PlanStepKind kind) => new() { Index = index, Kind = kind, Status = PlanStepStatus.Ok };

    private static PlanStepValidation Greyed(int index, PlanStepKind kind, string reason) => new() { Index = index, Kind = kind, Status = PlanStepStatus.Greyed, ReasonKey = reason };

    private static PlanIssue Issue(string code, string path) => new() { Code = code, Path = path };

    private static PlanValidation Invalid(string planId, PlanTrust trust, IReadOnlyList<PlanIssue> issues, string? helperName = null)
        => new() { PlanId = planId, Valid = false, Trust = trust, HelperName = helperName, Issues = issues };

    private static byte[]? FromBase64(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Parses <c>ed25519:&lt;base64&gt;</c> into 32 key bytes.</summary>
    /// <param name="text">Key text.</param>
    internal static byte[]? ParsePublicKey(string? text)
    {
        const string prefix = "ed25519:";
        if (text is null || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var bytes = FromBase64(text[prefix.Length..]);
        return bytes is { Length: Ed25519.PublicKeySize } ? bytes : null;
    }
}

/// <summary>CalVer versions (<c>2026.10.21.1</c>): dotted non-negative integers, compared segment by segment (missing = 0).</summary>
public static class CalVer
{
    /// <summary>Parses a version; null when malformed.</summary>
    /// <param name="text">Text.</param>
    public static IReadOnlyList<int>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length is 0 or > 6)
        {
            return null;
        }

        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]))
            {
                return null;
            }
        }

        return result;
    }

    /// <summary>Compares two parsed versions.</summary>
    /// <param name="a">A.</param>
    /// <param name="b">B.</param>
    public static int Compare(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }
}
