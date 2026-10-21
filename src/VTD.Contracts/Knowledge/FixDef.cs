using VTD.Contracts.Fixes;

namespace VTD.Contracts.Knowledge;

/// <summary>
/// A fix: JSON that only composes audited C# primitives (design_B.md section 4.3, about 25 <see cref="IOperation"/>s).
/// It contains no commands, scripts, URLs-as-actions or paths outside primitive allowlists. Argument values are
/// invariant strings; <c>$&lt;bind&gt;.id</c> and <c>$&lt;bind&gt;.name</c> reference bound entities.
/// Effective risk, reversibility, admin and reboot are computed from the primitives and may be raised, never lowered;
/// a high-risk fix has no steps (guide only); a network-affecting fix must declare a <see cref="Deadman"/>.
/// File: <c>kb/fixes/&lt;area&gt;/&lt;id&gt;.json</c>. Schema: <c>kb/schema/fix.schema.json</c>.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record FixDef
{
    /// <summary>Stable id <c>fix.&lt;area&gt;.&lt;name&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Revision.</summary>
    public int Rev { get; init; } = 1;

    /// <summary>Title key.</summary>
    public required string TitleKey { get; init; }

    /// <summary>What and why, at most 3 sentences, 6th-grade reading level.</summary>
    public required string ExplainKey { get; init; }

    /// <summary>How to undo, in plain words.</summary>
    public required string UndoExplainKey { get; init; }

    /// <summary>What the player will notice ("You'll stay on Wi-Fi, internet keeps working").</summary>
    public string? NoticeKey { get; init; }

    /// <summary>Side effects ("headset mic will stop working in Hands-Free mode").</summary>
    public IReadOnlyList<string> SideEffectKeys { get; init; } = [];

    /// <summary>Declared risk.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>Declared reversibility.</summary>
    public required Reversibility Reversibility { get; init; }

    /// <summary>Needs the elevated engine.</summary>
    public bool Admin { get; init; } = true;

    /// <summary>Reboot need.</summary>
    public RebootNeed Reboot { get; init; } = RebootNeed.None;

    /// <summary>Persistent or session-only.</summary>
    public FixScope Scope { get; init; } = FixScope.Persistent;

    /// <summary>Estimated minutes (for the cost term and the badge).</summary>
    public double? EstMinutes { get; init; }

    /// <summary>Hypotheses this fix addresses.</summary>
    public IReadOnlyList<string> AppliesTo { get; init; } = [];

    /// <summary>Generic entity-bound template (DESIGN.md section 3.1: close process, disable service, disable device,
    /// remove compat layer, unbind IPv6, disable startup entry): bindings may come from a helper plan or an agent,
    /// still subject to ProtectedTargetPolicy and per-step consent.</summary>
    public bool Template { get; init; }

    /// <summary>Entity bindings by name (for example <c>adapter</c>, <c>svc</c>).</summary>
    public IReadOnlyDictionary<string, BindDef> Bind { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, BindDef>.Empty;

    /// <summary>Guards, re-evaluated with FRESH probes right before apply. The standard set (game not running, no pending
    /// reboot or WU install, disk free, not monitoring) is added by the FixRunner to every persistent fix.</summary>
    public IReadOnlyList<GuardDef> Guards { get; init; } = [];

    /// <summary>Primitive steps, applied in order; undone in reverse.</summary>
    public IReadOnlyList<StepDef> Steps { get; init; } = [];

    /// <summary>Mechanical verification ("did the change land?").</summary>
    public IReadOnlyList<VerifyDef> Verify { get; init; } = [];

    /// <summary>Auto-revert unless confirmed (required for network-cutting changes).</summary>
    public DeadmanDef? Deadman { get; init; }

    /// <summary>"Did it help?" measurement.</summary>
    public OutcomeDef? Outcome { get; init; }

    /// <summary>Re-probe on every rescan; offer to re-apply if Windows Update or an OEM tool reverted it.</summary>
    public DriftDef? Drift { get; init; }

    /// <summary>Seed for P(fix works | cause), from the catalog's fix evidence counts.</summary>
    public SuccessPriorDef? SuccessPrior { get; init; }

    /// <summary>Minimum app version that has every primitive used.</summary>
    public string? MinApp { get; init; }

    /// <summary>Windows builds this fix was tested on (for example <c>win11-25H2</c>).</summary>
    public IReadOnlyList<string> Tested { get; init; } = [];

    /// <summary>Set when retired.</summary>
    public DeprecationDef? Deprecated { get; init; }
}

/// <summary>A binding: which entities a fix acts on.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record BindDef
{
    /// <summary>Entity kind.</summary>
    public required EntityKind Kind { get; init; }

    /// <summary>Signal whose <c>Entities</c> supply candidates (supports a <c>*</c> wildcard segment, for example
    /// <c>conflict.*.service_present</c>). Null for template bindings that come only from a plan.</summary>
    public string? FromSignal { get; init; }

    /// <summary>One or all.</summary>
    public BindPick Pick { get; init; } = BindPick.One;
}

/// <summary>A guard or preflight check.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GuardDef
{
    /// <summary>A <see cref="GuardIds"/> id or a live-probeable signal id.</summary>
    public required string Probe { get; init; }

    /// <summary>Arguments (may reference <c>$bind.id</c>).</summary>
    public IReadOnlyDictionary<string, string> Args { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Required value. An Unknown probe result never passes a Block guard.</summary>
    public Tri Must { get; init; } = Tri.True;

    /// <summary>Block or warn.</summary>
    public GuardSeverity Severity { get; init; } = GuardSeverity.Block;

    /// <summary>String key shown when the guard fails ("Close VALORANT first").</summary>
    public required string FailKey { get; init; }
}

/// <summary>One primitive step.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record StepDef
{
    /// <summary>Primitive id, for example <c>device.disable</c>, <c>service.setStartType</c>.</summary>
    public required string Op { get; init; }

    /// <summary>Arguments (invariant strings; validated against the primitive's <see cref="IOperation.Args"/>).</summary>
    public IReadOnlyDictionary<string, string> Args { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Failure handling.</summary>
    public StepFailure OnFail { get; init; } = StepFailure.Abort;
}

/// <summary>One verification check.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record VerifyDef
{
    /// <summary>Primitive id whose <see cref="IOperation.ProbeAsync"/> reads the state (for example <c>device.disable</c>
    /// reads device state), or a live probe id.</summary>
    public required string Probe { get; init; }

    /// <summary>Arguments.</summary>
    public IReadOnlyDictionary<string, string> Args { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Expected values of the probed state map (invariant strings, for example <c>enabled</c> = <c>false</c>).</summary>
    public required IReadOnlyDictionary<string, string> Expect { get; init; }

    /// <summary>When it runs.</summary>
    public VerifyTiming When { get; init; } = VerifyTiming.Immediate;
}

/// <summary>Deadman auto-revert: "Is your internet still working? [Yes, keep] [No, undo]" with a countdown; the engine
/// also probes; no confirmation or a failed probe means automatic undo.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record DeadmanDef
{
    /// <summary>Countdown (seconds, default 60).</summary>
    public int Seconds { get; init; } = 60;

    /// <summary>Live probe that must stay True (default <see cref="GuardIds.InternetReachable"/>).</summary>
    public string Check { get; init; } = GuardIds.InternetReachable;
}

/// <summary>Outcome measurement.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record OutcomeDef
{
    /// <summary>Kind.</summary>
    public required OutcomeKind Kind { get; init; }

    /// <summary>Question key ("Did the stutter stop?").</summary>
    public string? AskKey { get; init; }

    /// <summary>Monitor signals to compare before/after.</summary>
    public IReadOnlyList<string> MonitorSignals { get; init; } = [];
}

/// <summary>Drift watch.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record DriftDef
{
    /// <summary>Watch for reverts.</summary>
    public required bool Watch { get; init; }

    /// <summary>Message key ("Nahimic came back after a Windows update").</summary>
    public string? ReasonKey { get; init; }
}

/// <summary>Beta prior for fix success.</summary>
public sealed record SuccessPriorDef
{
    /// <summary>Successes.</summary>
    public required int Success { get; init; }

    /// <summary>Trials.</summary>
    public required int Trials { get; init; }
}
