namespace VTD.Contracts.Fixes;

/// <summary>Risk of a fix or primitive. Effective risk = max(declared, every primitive's floor).</summary>
public enum RiskLevel
{
    /// <summary>Low: quick, harmless, undoable.</summary>
    Low = 0,

    /// <summary>Medium: needs a verified restore point before the first one in a session.</summary>
    Medium = 1,

    /// <summary>High: never automated; guide only (the KB validator enforces this).</summary>
    High = 2,
}

/// <summary>How exactly a change can be undone. Effective reversibility = the weakest of the fix's steps.</summary>
public enum Reversibility
{
    /// <summary>Undo restores the captured state bit-identically.</summary>
    Exact = 0,

    /// <summary>Undo restores the important part; side state may differ.</summary>
    BestEffort = 1,

    /// <summary>The change lasts only until reboot or process restart (for example closing an overlay).</summary>
    SessionOnly = 2,

    /// <summary>Cannot be undone with a click (network reset, mbr2gpt). Needs a second consent and comes after a restore point (S12).</summary>
    OneWay = 3,
}

/// <summary>Reboot need.</summary>
public enum RebootNeed
{
    /// <summary>No reboot.</summary>
    None = 0,

    /// <summary>Works now; a reboot is recommended.</summary>
    Recommended = 1,

    /// <summary>Takes effect only after a restart; verification runs after the reboot (resume).</summary>
    Required = 2,
}

/// <summary>Lifetime of a fix's effect.</summary>
public enum FixScope
{
    /// <summary>Persists across reboots (journaled, drift-watched).</summary>
    Persistent = 0,

    /// <summary>This session only (for example a closed process).</summary>
    Session = 1,
}

/// <summary>How many entities a binding takes from its source signal.</summary>
public enum BindPick
{
    /// <summary>Exactly one entity (the UI lets the player choose when several match).</summary>
    One = 0,

    /// <summary>All matching entities.</summary>
    All = 1,
}

/// <summary>What happens when a step fails.</summary>
public enum StepFailure
{
    /// <summary>Stop and undo the steps already applied, in reverse order (all or nothing). Default.</summary>
    Abort = 0,

    /// <summary>Record the failure and continue with the next step (for example "stop service" before "disable").</summary>
    Continue = 1,
}

/// <summary>When a verify check runs.</summary>
public enum VerifyTiming
{
    /// <summary>Right after apply.</summary>
    Immediate = 0,

    /// <summary>After the reboot, on resume.</summary>
    PostReboot = 1,
}

/// <summary>How the "did it help?" outcome is measured (applied is not fixed, S14).</summary>
public enum OutcomeKind
{
    /// <summary>Ask after a playtest.</summary>
    Playtest = 0,

    /// <summary>Ask after relaunching the game.</summary>
    Relaunch = 1,

    /// <summary>The error code no longer appears.</summary>
    ErrorGone = 2,

    /// <summary>Compare monitor metrics before and after.</summary>
    MonitorCompare = 3,
}

/// <summary>Severity of a failed guard or preflight.</summary>
public enum GuardSeverity
{
    /// <summary>Blocks apply.</summary>
    Block = 0,

    /// <summary>Shown as a warning; the player may continue.</summary>
    Warn = 1,
}

/// <summary>How an undo treats state that changed since VTDiag applied the fix.</summary>
public enum UndoMode
{
    /// <summary>Default: refuse (report a conflict) if someone else changed the state since our apply.</summary>
    IfUnchanged = 0,

    /// <summary>Restore the captured "before" state anyway (after the player confirmed).</summary>
    Force = 1,
}
