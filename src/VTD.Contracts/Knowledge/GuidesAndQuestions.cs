using VTD.Contracts.Fixes;

namespace VTD.Contracts.Knowledge;

/// <summary>A condition on a signal.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record ConditionDef
{
    /// <summary>Signal id.</summary>
    public required string Signal { get; init; }

    /// <summary>Required value. A condition on an Unknown signal is not satisfied; the UI then shows every branch.</summary>
    public required Tri Is { get; init; }
}

/// <summary>
/// A guided manual flow (BIOS, Secure Boot, TPM, HVCI, ISP/WARP, uninstall, driver update) for what a PC tool cannot or
/// must not automate. The only automated actions are a "Restart into BIOS" button (resume armed), opening an
/// allowlisted Settings page, running a read-only check, or handing off to a normal consented fix.
/// File: <c>kb/guides/&lt;id&gt;.json</c>. Schema: <c>kb/schema/guide.schema.json</c>.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record GuideDef
{
    /// <summary>Stable id <c>guide.&lt;area&gt;.&lt;name&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Revision.</summary>
    public int Rev { get; init; } = 1;

    /// <summary>Title key.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Summary key.</summary>
    public string? SummaryKey { get; init; }

    /// <summary>Honesty bucket (usually <see cref="HonestyBucket.GuidedManual"/>; ISP flows use ExternalNetwork).</summary>
    public HonestyBucket Bucket { get; init; } = HonestyBucket.GuidedManual;

    /// <summary>Risk (firmware flows are high: guide only).</summary>
    public RiskLevel Risk { get; init; } = RiskLevel.High;

    /// <summary>Reboot need.</summary>
    public RebootNeed Reboot { get; init; } = RebootNeed.None;

    /// <summary>Hypotheses it addresses.</summary>
    public IReadOnlyList<string> AppliesTo { get; init; } = [];

    /// <summary>Pre-flight checks (for example BitLocker on means "save your recovery key first", S11).</summary>
    public IReadOnlyList<GuardDef> Preflight { get; init; } = [];

    /// <summary>Branches; the first whose conditions all hold is shown first, the rest stay reachable.</summary>
    public required IReadOnlyList<GuideBranchDef> Branches { get; init; }

    /// <summary>Page under <c>docs/</c> for the QR code (the PC screen is in BIOS, the phone shows the steps).</summary>
    public string? DocsPath { get; init; }

    /// <summary>"Did it help?" question key.</summary>
    public string? OutcomeAskKey { get; init; }
}

/// <summary>One branch of a guide.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GuideBranchDef
{
    /// <summary>Id within the guide (for example <c>mbr</c>, <c>csm</c>, <c>setup_mode</c>, <c>sb_on_still_failing</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Conditions (all must hold). Empty = default branch.</summary>
    public IReadOnlyList<ConditionDef> When { get; init; } = [];

    /// <summary>Branch explanation key.</summary>
    public string? TextKey { get; init; }

    /// <summary>Steps.</summary>
    public required IReadOnlyList<GuideStepDef> Steps { get; init; }

    /// <summary>Verification after the steps (and after the firmware restart, on resume).</summary>
    public IReadOnlyList<ConditionDef> Verify { get; init; } = [];

    /// <summary>Branch to suggest when verification fails ("not yet, here's the next thing to try").</summary>
    public string? NextOnFail { get; init; }
}

/// <summary>What a guide step can do by itself.</summary>
public enum GuideAction
{
    /// <summary>Text only; the player acts.</summary>
    None = 0,

    /// <summary>"Restart into BIOS" button (UI-click minted restart token, resume armed, AC power gate).</summary>
    RestartToFirmware = 1,

    /// <summary>Open an allowlisted <c>ms-settings:</c> page (<see cref="GuideStepDef.Target"/>).</summary>
    OpenSettings = 2,

    /// <summary>Run a read-only live check (<see cref="GuideStepDef.Target"/> = probe id) and show the result.</summary>
    RunCheck = 3,

    /// <summary>Hand off to a normal, consented KB fix (<see cref="GuideStepDef.Target"/> = fix id).</summary>
    RunFix = 4,

    /// <summary>Copy a text block (for example the WinRE fallback command) to the clipboard.</summary>
    CopyText = 5,
}

/// <summary>One guide step.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GuideStepDef
{
    /// <summary>Step text key (may use vendor-table placeholders).</summary>
    public required string TextKey { get; init; }

    /// <summary>Action.</summary>
    public GuideAction Action { get; init; } = GuideAction.None;

    /// <summary>Action target (settings URI, probe id or fix id).</summary>
    public string? Target { get; init; }

    /// <summary>Vendor table id (<see cref="TableDef"/>) selected by board/system manufacturer.</summary>
    public string? VendorTable { get; init; }

    /// <summary>Image key (SVG with string slots; no text baked into images).</summary>
    public string? ImageKey { get; init; }

    /// <summary>Anchor in <see cref="GuideDef.DocsPath"/>.</summary>
    public string? DocsAnchor { get; init; }
}

/// <summary>Question kind.</summary>
public enum QuestionKind
{
    /// <summary>Yes / No / Not sure.</summary>
    YesNo = 0,

    /// <summary>One of several options.</summary>
    SingleChoice = 1,
}

/// <summary>
/// An intake question. Answering sets signal <c>ask.&lt;name&gt;</c> (= <see cref="Id"/>) to the chosen option's
/// <see cref="QuestionOption.Sets"/>, and for choices also <c>&lt;id&gt;.&lt;optionId&gt;</c> True (others False).
/// "Not sure" sets Unknown. At most <see cref="RankingConstants.MaxQuestions"/> per session.
/// File: <c>kb/questions/*.json</c>. Schema: <c>kb/schema/question.schema.json</c>.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record QuestionDef
{
    /// <summary>Id, also the signal id: <c>ask.&lt;name&gt;</c> (for example <c>ask.league_also_broken</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Question text key.</summary>
    public required string TextKey { get; init; }

    /// <summary>Help text key.</summary>
    public string? HelpKey { get; init; }

    /// <summary>Kind.</summary>
    public QuestionKind Kind { get; init; } = QuestionKind.YesNo;

    /// <summary>Options (YesNo questions get yes/no/unsure by default when empty).</summary>
    public IReadOnlyList<QuestionOption> Options { get; init; } = [];

    /// <summary>Asked up front on the "What's wrong?" screen (for example "is League also broken?").</summary>
    public bool Intake { get; init; }

    /// <summary>Only relevant for these symptom ids (empty = any).</summary>
    public IReadOnlyList<string> Symptoms { get; init; } = [];

    /// <summary>Only ask when these conditions hold.</summary>
    public IReadOnlyList<ConditionDef> AskIf { get; init; } = [];
}

/// <summary>One answer option.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record QuestionOption
{
    /// <summary>Option id (snake_case), for example <c>yes</c>, <c>periodic</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Option text key.</summary>
    public required string TextKey { get; init; }

    /// <summary>Value the question's signal takes when chosen.</summary>
    public required Tri Sets { get; init; }
}

/// <summary>A symptom chip on the "What's wrong?" screen. Chips set priors; they never veto a hypothesis.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record SymptomDef
{
    /// <summary>Id, for example <c>stutter</c>, <c>launch_black_screen</c>, <c>error_code</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Chip text key.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Categories whose priors this symptom selects.</summary>
    public IReadOnlyList<ProblemCategory> Categories { get; init; } = [];

    /// <summary>Display order.</summary>
    public int Order { get; init; }
}

/// <summary>A lookup table (BIOS vendor key/menu paths, known-bad driver versions). Version-specific facts live in data.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record TableDef
{
    /// <summary>Table id, for example <c>bios_secureboot_paths</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Column used to select a row (for example <c>vendor</c>), matched ordinal case-insensitive.</summary>
    public string? KeyColumn { get; init; }

    /// <summary>Rows.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; }

    /// <summary>Last date the table is valid.</summary>
    public DateOnly? ValidUntil { get; init; }
}
