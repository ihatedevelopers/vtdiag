using System.Globalization;

namespace VTD.Contracts.Fixes;

/// <summary>Argument type of a primitive (validation and UI rendering).</summary>
public enum OpArgType
{
    /// <summary>Free string (validated by <see cref="OpArgSpec.Pattern"/>).</summary>
    String = 0,

    /// <summary>Invariant integer.</summary>
    Int = 1,

    /// <summary><c>true</c>/<c>false</c>.</summary>
    Bool = 2,

    /// <summary>One of <see cref="OpArgSpec.AllowedValues"/>.</summary>
    Enum = 3,

    /// <summary>PnP instance id.</summary>
    InstanceId = 4,

    /// <summary>Service name.</summary>
    ServiceName = 5,

    /// <summary>Registry path under an allowlisted root.</summary>
    RegistryPath = 6,

    /// <summary>File path (reparse-safe handling, S-a).</summary>
    FilePath = 7,

    /// <summary>Executable image name.</summary>
    ImageName = 8,

    /// <summary>GUID.</summary>
    Guid = 9,
}

/// <summary>Declares one argument of a primitive.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record OpArgSpec
{
    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Type.</summary>
    public required OpArgType Type { get; init; }

    /// <summary>Required.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Allowed values for <see cref="OpArgType.Enum"/>.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    /// <summary>Regex the value must match.</summary>
    public string? Pattern { get; init; }
}

/// <summary>Resolved arguments of a step (no <c>$bind</c> references left). Values are invariant strings.</summary>
public sealed record OpArgs
{
    /// <summary>Values by name.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    /// <summary>Gets a required string.</summary>
    /// <param name="name">Name.</param>
    public string Get(string name)
        => Values.TryGetValue(name, out var v) ? v : throw new KeyNotFoundException($"Missing primitive argument '{name}'.");

    /// <summary>Gets an optional string.</summary>
    /// <param name="name">Name.</param>
    public string? GetOrNull(string name) => Values.TryGetValue(name, out var v) ? v : null;

    /// <summary>Gets a required invariant integer.</summary>
    /// <param name="name">Name.</param>
    public int GetInt(string name) => int.Parse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>Gets a required boolean (<c>true</c>/<c>false</c>, ordinal ignore-case).</summary>
    /// <param name="name">Name.</param>
    public bool GetBool(string name) => bool.Parse(Get(name));
}

/// <summary>
/// A captured state, the unit of backup, verification, drift and recovery. <see cref="Hash"/> is
/// <see cref="CanonicalHash.Of"/> over <see cref="Data"/>, so "undo is bit-identical" is testable: the capture hash
/// before apply equals the probe hash after undo. Large captures (reg export, BCD export) go to the backup store and
/// are referenced by <see cref="BlobRef"/>.
/// </summary>
public sealed record StateCapture
{
    /// <summary>Primitive id.</summary>
    [Sensitivity(Sensitivity.None)]
    public required string Op { get; init; }

    /// <summary>State as an invariant string map (for example <c>startType</c> = <c>Automatic</c>, <c>state</c> = <c>Running</c>).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public required IReadOnlyDictionary<string, string> Data { get; init; }

    /// <summary>Reference into <c>%ProgramData%\VTDiag\backups\&lt;attempt&gt;\</c> with its SHA-256, for large captures.</summary>
    [Sensitivity(Sensitivity.Path)]
    public string? BlobRef { get; init; }

    /// <summary>Canonical hash of <see cref="Data"/> (and of the blob, when present).</summary>
    [Sensitivity(Sensitivity.None)]
    public required string Hash { get; init; }
}

/// <summary>Result of an apply or undo.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record OpResult
{
    /// <summary>Succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Machine error code (for example <c>win32:5</c>, <c>policy.denied</c>, <c>undo.conflict</c>).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Short machine detail.</summary>
    public string? Detail { get; init; }

    /// <summary>State after the operation.</summary>
    public StateCapture? After { get; init; }

    /// <summary>The change only takes effect after a reboot.</summary>
    public bool RebootRequired { get; init; }
}

/// <summary>
/// One audited primitive (A3; the full list with arguments and state keys is <see cref="OpCatalog"/>). Each does
/// policy → capture → apply → probe → undo. Elevated file operations are reparse-safe; nothing here runs an HKCU
/// uninstall string as admin; restore points are A4's <c>IRestorePointService</c>, not a primitive.
/// </summary>
public interface IOperation
{
    /// <summary>Primitive id.</summary>
    string Op { get; }

    /// <summary>Reversibility.</summary>
    Reversibility Reversibility { get; }

    /// <summary>Minimum risk of any fix that uses it.</summary>
    RiskLevel RiskFloor { get; }

    /// <summary>Needs elevation.</summary>
    bool RequiresAdmin { get; }

    /// <summary>Reboot need.</summary>
    RebootNeed Reboot { get; }

    /// <summary>Can cut connectivity: the fix must declare a deadman (S15).</summary>
    bool AffectsNetwork { get; }

    /// <summary>Argument specification.</summary>
    IReadOnlyList<OpArgSpec> Args { get; }

    /// <summary>ProtectedTargetPolicy check (S4/S5); not overridable by the KB or a plan.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<PolicyDecision> CheckPolicyAsync(OpArgs args, CancellationToken cancellationToken);

    /// <summary>Captures the exact prior state (backup). Failure means no change (S2).</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<StateCapture> CaptureAsync(OpArgs args, CancellationToken cancellationToken);

    /// <summary>The state apply should produce from <paramref name="before"/> (pure; used for preview and recovery).</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="before">Captured state.</param>
    StateCapture Intended(OpArgs args, StateCapture before);

    /// <summary>Applies. Idempotent.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="before">Captured state (already journaled and fsynced).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<OpResult> ApplyAsync(OpArgs args, StateCapture before, CancellationToken cancellationToken);

    /// <summary>Reads the current state (verify, recovery, drift).</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<StateCapture> ProbeAsync(OpArgs args, CancellationToken cancellationToken);

    /// <summary>Restores <paramref name="before"/>. With <see cref="UndoMode.IfUnchanged"/> it refuses when the current state
    /// differs from <paramref name="expectedAfter"/>. Idempotent.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="before">State to restore.</param>
    /// <param name="expectedAfter">State we left behind.</param>
    /// <param name="mode">Mode.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<OpResult> UndoAsync(OpArgs args, StateCapture before, StateCapture? expectedAfter, UndoMode mode, CancellationToken cancellationToken);
}

/// <summary>Registry of primitives (A3).</summary>
public interface IOperationRegistry
{
    /// <summary>All primitives.</summary>
    IReadOnlyCollection<IOperation> All { get; }

    /// <summary>Finds a primitive by id.</summary>
    /// <param name="op">Primitive id.</param>
    IOperation? Find(string op);
}
