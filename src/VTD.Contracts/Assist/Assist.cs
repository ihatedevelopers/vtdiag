using System.Text.Json.Serialization;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Snapshots;

namespace VTD.Contracts.Assist;

/// <summary>Plan limits (design_B.md section 9).</summary>
public static class PlanLimits
{
    /// <summary>Current plan schema.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Max steps.</summary>
    public const int MaxSteps = 10;

    /// <summary>Max plan lifetime.</summary>
    public static TimeSpan MaxLifetime { get; } = TimeSpan.FromDays(7);

    /// <summary>Max length of the plan message.</summary>
    public const int MaxMessageLength = 2000;

    /// <summary>Max length of a step note.</summary>
    public const int MaxNoteLength = 500;
}

/// <summary>Plan author as written by the helper (unverified unless the plan comes in a <see cref="SignedPlanEnvelope"/>).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PlanAuthor
{
    /// <summary>Name as given (plain text, for example <c>u/ModName</c>).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public required string Name { get; init; }
}

/// <summary>
/// A signed <c>.vtdplan</c>: a detached Ed25519 signature over the EXACT bytes of the plan JSON, so any language (Python,
/// an AI agent, System.Text.Json) can sign and verify without canonicalization rules. Signing raises trust
/// ("Verified helper" when the key is in the KB's trusted helpers) but never removes consent.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record SignedPlanEnvelope
{
    /// <summary>Format id.</summary>
    public const string CurrentFormat = "vtdplan-signed/1";

    /// <summary>Format (<see cref="CurrentFormat"/>).</summary>
    public required string Format { get; init; }

    /// <summary>Base64 of the UTF-8 plan JSON bytes (a <see cref="FixPlan"/>).</summary>
    public required string Payload { get; init; }

    /// <summary>Public key, <c>ed25519:&lt;base64&gt;</c>.</summary>
    public required string PublicKey { get; init; }

    /// <summary>Base64 Ed25519 signature over the decoded <see cref="Payload"/> bytes.</summary>
    public required string Signature { get; init; }
}

/// <summary>
/// A helper's plan (<c>.vtdplan</c>, schema <c>kb/schema/plan.schema.json</c>). The helper (person or AI) is untrusted:
/// steps can only be KB fix, guide, question or monitor ids. No commands, paths, registry keys, URLs-as-actions or
/// scripts. Notes render as plain text. Every step goes through normal per-step consent; there is no "apply all".
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record FixPlan
{
    /// <summary>Schema (<see cref="PlanLimits.CurrentSchema"/>).</summary>
    public required int Schema { get; init; }

    /// <summary>ULID.</summary>
    public required string PlanId { get; init; }

    /// <summary>The <c>.vtdscan</c> snapshot the plan was written for.</summary>
    public required string ForSnapshotId { get; init; }

    /// <summary><see cref="Snapshots.SnapshotMeta.MachineKey"/> of that snapshot (the same on every scan of one PC). When
    /// present and different from the fresh scan's, the plan is invalid (<c>snapshot_mismatch</c>).</summary>
    public string? ForMachineKey { get; init; }

    /// <summary>Minimum KB version.</summary>
    public required string KbMin { get; init; }

    /// <summary>Created.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Expires (at most 7 days after creation).</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>Author.</summary>
    public required PlanAuthor Author { get; init; }

    /// <summary>Message to the player (plain text).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? Message { get; init; }

    /// <summary>Steps (at most 10).</summary>
    public required IReadOnlyList<PlanStep> Steps { get; init; }
}

/// <summary>A plan step. JSON discriminator: <c>kind</c> (<c>fix</c>, <c>guide</c>, <c>question</c>, <c>monitor</c>).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(PlanFixStep), "fix")]
[JsonDerivedType(typeof(PlanGuideStep), "guide")]
[JsonDerivedType(typeof(PlanQuestionStep), "question")]
[JsonDerivedType(typeof(PlanMonitorStep), "monitor")]
[Sensitivity(Sensitivity.None)]
public abstract record PlanStep
{
    /// <summary>Note to the player (plain text; URLs shown as "link from helper", never auto-opened).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? Note { get; init; }
}

/// <summary>Apply a KB fix bound to entities.</summary>
public sealed record PlanFixStep : PlanStep
{
    /// <summary>KB fix id.</summary>
    public required string FixId { get; init; }

    /// <summary>Binding name to entity ids exactly as they appear in the share-level snapshot the helper received
    /// (<see cref="Reporting.IRedactor.ShareEntityId"/>); the player's app maps them back to live entities on a fresh scan.</summary>
    [Sensitivity(Sensitivity.EntityId)]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Bind { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty;
}

/// <summary>Show a KB guide.</summary>
public sealed record PlanGuideStep : PlanStep
{
    /// <summary>KB guide id.</summary>
    public required string GuideId { get; init; }

    /// <summary>Branch.</summary>
    public string? BranchId { get; init; }
}

/// <summary>Ask a KB question.</summary>
public sealed record PlanQuestionStep : PlanStep
{
    /// <summary>KB question id.</summary>
    public required string QuestionId { get; init; }
}

/// <summary>Watch a match.</summary>
public sealed record PlanMonitorStep : PlanStep
{
    /// <summary>Minutes.</summary>
    public required int Minutes { get; init; }
}

/// <summary>Trust in a plan.</summary>
public enum PlanTrust
{
    /// <summary>Signed by a key in the KB's trusted helpers ("Verified r/ValorantTechSupport helper").</summary>
    VerifiedHelper = 0,

    /// <summary>Validly signed by an unknown key.</summary>
    SignedUnknownKey = 1,

    /// <summary>Unsigned ("From someone you know? Only continue if you trust them.").</summary>
    Unsigned = 2,

    /// <summary>Signature present but invalid: the plan is rejected.</summary>
    BadSignature = 3,
}

/// <summary>Plan step kind.</summary>
public enum PlanStepKind
{
    /// <summary>Fix.</summary>
    Fix = 0,

    /// <summary>Guide.</summary>
    Guide = 1,

    /// <summary>Question.</summary>
    Question = 2,

    /// <summary>Monitor.</summary>
    Monitor = 3,
}

/// <summary>Plan step status after validation.</summary>
public enum PlanStepStatus
{
    /// <summary>Usable.</summary>
    Ok = 0,

    /// <summary>Shown greyed out with the reason.</summary>
    Greyed = 1,
}

/// <summary>Validation of one step.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PlanStepValidation
{
    /// <summary>Index.</summary>
    public required int Index { get; init; }

    /// <summary>Kind.</summary>
    public required PlanStepKind Kind { get; init; }

    /// <summary>Status.</summary>
    public required PlanStepStatus Status { get; init; }

    /// <summary>Why greyed ("This step doesn't match your PC anymore").</summary>
    public string? ReasonKey { get; init; }

    /// <summary>Binding resolved against the fresh snapshot.</summary>
    public FixBinding? ResolvedBinding { get; init; }
}

/// <summary>A plan-level issue.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PlanIssue
{
    /// <summary>Code: <c>schema</c>, <c>expired</c>, <c>kb_too_old</c>, <c>snapshot_mismatch</c>, <c>too_many_steps</c>,
    /// <c>unknown_id</c>, <c>bad_signature</c>.</summary>
    public required string Code { get; init; }

    /// <summary>JSON pointer.</summary>
    public string? Path { get; init; }
}

/// <summary>Plan validation result.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PlanValidation
{
    /// <summary>Plan.</summary>
    public required string PlanId { get; init; }

    /// <summary>Usable at all (some steps may still be greyed).</summary>
    public required bool Valid { get; init; }

    /// <summary>Trust.</summary>
    public required PlanTrust Trust { get; init; }

    /// <summary>Verified helper name.</summary>
    public string? HelperName { get; init; }

    /// <summary>Steps.</summary>
    public IReadOnlyList<PlanStepValidation> Steps { get; init; } = [];

    /// <summary>Issues.</summary>
    public IReadOnlyList<PlanIssue> Issues { get; init; } = [];
}

/// <summary>File layout inside a <c>.vtdscan</c> zip.</summary>
public static class ScanBundleLayout
{
    /// <summary>Format id.</summary>
    public const string Format = "vtdscan/1";

    /// <summary>Manifest.</summary>
    public const string Manifest = "manifest.json";

    /// <summary>Redacted snapshot.</summary>
    public const string Snapshot = "snapshot.redacted.json";

    /// <summary>Analysis.</summary>
    public const string Analysis = "analysis.json";

    /// <summary>Journal summary (v2 bundles, after fixes).</summary>
    public const string Journal = "journal.summary.json";

    /// <summary>Monitor summaries folder.</summary>
    public const string MonitorFolder = "monitor/";

    /// <summary>HTML report.</summary>
    public const string Report = "report.html";
}

/// <summary>One part of a bundle.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record BundlePart
{
    /// <summary>Path in the zip.</summary>
    public required string Path { get; init; }

    /// <summary>SHA-256.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size.</summary>
    public required long Size { get; init; }

    /// <summary>Content type.</summary>
    public required string ContentType { get; init; }
}

/// <summary>Bundle manifest.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ScanBundleManifest
{
    /// <summary>Format (<see cref="ScanBundleLayout.Format"/>).</summary>
    public required string Format { get; init; }

    /// <summary>ULID.</summary>
    public required string BundleId { get; init; }

    /// <summary>Revision: 1 = scan only, 2 = with journal summary and outcomes.</summary>
    public int Revision { get; init; } = 1;

    /// <summary>Created.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>App version.</summary>
    public required string AppVersion { get; init; }

    /// <summary>KB version.</summary>
    public required string KbVersion { get; init; }

    /// <summary>Snapshot.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>Redaction level (always Share).</summary>
    public required RedactionLevel Redaction { get; init; }

    /// <summary>Parts with hashes.</summary>
    public required IReadOnlyList<BundlePart> Parts { get; init; }
}

/// <summary>An in-memory <c>.vtdscan</c>. It always holds the share-redacted snapshot, and the player saw the preview first.</summary>
public sealed record ScanBundle
{
    /// <summary>Manifest.</summary>
    public required ScanBundleManifest Manifest { get; init; }

    /// <summary>Redacted snapshot.</summary>
    public required ScanSnapshot Snapshot { get; init; }

    /// <summary>Analysis.</summary>
    public AnalysisResult? Analysis { get; init; }

    /// <summary>Journal summary.</summary>
    public IReadOnlyList<AttemptSummary> Journal { get; init; } = [];

    /// <summary>Monitor summaries.</summary>
    public IReadOnlyList<MonitorSummary> Monitor { get; init; } = [];

    /// <summary>HTML report.</summary>
    [Sensitivity(Sensitivity.None)]
    public string? ReportHtml { get; init; }
}
