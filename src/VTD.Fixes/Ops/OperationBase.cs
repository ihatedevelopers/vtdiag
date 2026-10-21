using System.ComponentModel;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>Builds <see cref="StateCapture"/>s (hash = <see cref="CanonicalHash.Of"/> over the data).</summary>
public static class Captures
{
    /// <summary>Creates a capture.</summary>
    /// <param name="op">Primitive id.</param>
    /// <param name="data">State map.</param>
    public static StateCapture Of(string op, IReadOnlyDictionary<string, string> data)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in data)
        {
            copy[k] = v;
        }

        return new StateCapture { Op = op, Data = copy, Hash = CanonicalHash.Of(copy) };
    }

    /// <summary>Creates a capture from pairs.</summary>
    /// <param name="op">Primitive id.</param>
    /// <param name="pairs">Key/value pairs.</param>
    public static StateCapture Of(string op, params (string Key, string Value)[] pairs)
        => Of(op, pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    /// <summary>Invariant <c>true</c>/<c>false</c>.</summary>
    /// <param name="value">Value.</param>
    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary>A value of the capture or empty.</summary>
    /// <param name="capture">Capture.</param>
    /// <param name="key">Key.</param>
    public static string Get(this StateCapture capture, string key)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return capture.Data.TryGetValue(key, out var v) ? v : string.Empty;
    }
}

/// <summary>
/// Shared shape of every primitive: metadata, argument validation, policy, and the generic undo protocol (probe →
/// conflict check against the state we left → restore → probe → compare with the captured before-state).
/// Subclasses implement <see cref="PolicyCore"/>, <see cref="CaptureCore"/>, <see cref="Intended"/>, <see cref="ApplyCore"/>
/// and <see cref="RestoreCore"/>. Primitives never throw for a system failure in apply/undo: they return a failed
/// <see cref="OpResult"/> with a machine code.
/// </summary>
public abstract class OperationBase : IOperation
{
    /// <summary>Creates the primitive.</summary>
    /// <param name="ctx">Shared dependencies.</param>
    protected OperationBase(OperationContext ctx)
    {
        Ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <inheritdoc />
    public abstract string Op { get; }

    /// <inheritdoc />
    public abstract Reversibility Reversibility { get; }

    /// <inheritdoc />
    public abstract RiskLevel RiskFloor { get; }

    /// <inheritdoc />
    public virtual bool RequiresAdmin => true;

    /// <inheritdoc />
    public virtual RebootNeed Reboot => RebootNeed.None;

    /// <inheritdoc />
    public virtual bool AffectsNetwork => false;

    /// <inheritdoc />
    public abstract IReadOnlyList<OpArgSpec> Args { get; }

    /// <summary>Keys this primitive's captures carry (must equal <see cref="OpSpec.StateKeys"/>, plus private keys that start with <c>_</c>).</summary>
    public abstract IReadOnlyList<string> StateKeys { get; }

    /// <summary>Keys that may change on their own between preview and apply (left out of the preview hash).</summary>
    public virtual IReadOnlyList<string> VolatileKeys => [];

    /// <summary>Stateless one-way primitives (DNS flush, network resets) always run: "already in the intended state" never applies.</summary>
    public virtual bool AlwaysApply => false;

    /// <summary>Shared dependencies.</summary>
    protected OperationContext Ctx { get; }

    /// <summary>System ports.</summary>
    protected SystemPorts Ports => Ctx.Ports;

    /// <summary>Policy.</summary>
    protected ProtectedTargetPolicy Policy => Ctx.Policy;

    /// <inheritdoc />
    public async ValueTask<PolicyDecision> CheckPolicyAsync(OpArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();
        if (ArgValidator.Validate(Args, args.Values) is not null)
        {
            return ProtectedTargetPolicy.Deny("args");
        }

        try
        {
            return await PolicyCore(args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ProtectedTargetPolicy.Deny("error");
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateCapture> CaptureAsync(OpArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        RequireValid(args, partial: false);
        return Captures.Of(Op, await CaptureCore(args, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async ValueTask<StateCapture> ProbeAsync(OpArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        RequireValid(args, partial: true);
        return Captures.Of(Op, await CaptureCore(args, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public abstract StateCapture Intended(OpArgs args, StateCapture before);

    /// <summary>
    /// Why the change cannot be made at all on this PC, judged from the captured state (for example the service does not
    /// exist); null when it can. The runner shows it as a block reason (<c>target.*</c>).
    /// </summary>
    /// <param name="args">Arguments.</param>
    /// <param name="before">Captured state.</param>
    public virtual string? Precondition(OpArgs args, StateCapture before) => null;

    /// <summary>The part of a state that the primitive changes and undo restores (conflict and recovery comparisons).</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="state">State.</param>
    public virtual IReadOnlyDictionary<string, string> Controlled(OpArgs args, StateCapture state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Data.Where(kv => !VolatileKeys.Contains(kv.Key, StringComparer.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>True when two states agree on <see cref="Controlled"/>.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="a">State.</param>
    /// <param name="b">State.</param>
    public bool Same(OpArgs args, StateCapture a, StateCapture b)
    {
        var x = Controlled(args, a);
        var y = Controlled(args, b);
        return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async ValueTask<OpResult> ApplyAsync(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        try
        {
            RequireValid(args, partial: false);
            var policy = await CheckPolicyAsync(args, cancellationToken).ConfigureAwait(false);
            if (!policy.Allowed)
            {
                return Fail("policy.denied", policy.RuleId);
            }

            if (Precondition(args, before) is { } pre)
            {
                return Fail(pre);
            }

            var current = await ProbeAsync(args, cancellationToken).ConfigureAwait(false);
            var intended = Intended(args, before);
            if (!AlwaysApply && Same(args, current, intended))
            {
                return new OpResult { Success = true, After = current, Detail = "already" };
            }

            var reboot = await ApplyCore(args, before, cancellationToken).ConfigureAwait(false);
            var after = await ProbeAsync(args, cancellationToken).ConfigureAwait(false);
            if (!AlwaysApply && Reversibility == Reversibility.Exact && !reboot && !Same(args, after, intended))
            {
                // The write "succeeded" but the state is not what we set (Tamper Protection, a policy, a driver that
                // resets it): report it honestly so the step fails and the fix rolls back.
                return new OpResult { Success = false, ErrorCode = "apply.not_landed", After = after };
            }

            return new OpResult { Success = true, After = after, RebootRequired = reboot || Reboot == RebootNeed.Required };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FromException(ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<OpResult> UndoAsync(OpArgs args, StateCapture before, StateCapture? expectedAfter, UndoMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        if (Reversibility == Reversibility.OneWay)
        {
            return Fail("undo.one_way");
        }

        try
        {
            RequireValid(args, partial: false);
            var current = await ProbeAsync(args, cancellationToken).ConfigureAwait(false);
            if (Same(args, current, before))
            {
                return new OpResult { Success = true, After = current, Detail = "already" };
            }

            if (mode == UndoMode.IfUnchanged && expectedAfter is not null && !Same(args, current, expectedAfter))
            {
                return new OpResult { Success = false, ErrorCode = "undo.conflict", After = current };
            }

            var reboot = await RestoreCore(args, before, current, cancellationToken).ConfigureAwait(false);
            var after = await ProbeAsync(args, cancellationToken).ConfigureAwait(false);
            if (Reversibility == Reversibility.Exact && !Same(args, after, before))
            {
                return new OpResult { Success = false, ErrorCode = "undo.mismatch", After = after, RebootRequired = reboot };
            }

            return new OpResult { Success = true, After = after, RebootRequired = reboot || Reboot == RebootNeed.Required };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FromException(ex);
        }
    }

    /// <summary>Policy beyond argument validation.</summary>
    /// <param name="args">Validated arguments.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    protected abstract ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken);

    /// <summary>Reads the state map (capture and probe).</summary>
    /// <param name="args">Validated arguments (probe: possibly partial).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    protected abstract ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken);

    /// <summary>Makes the change. Returns true when Windows asked for a reboot.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="before">Captured state.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    protected abstract ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken);

    /// <summary>Restores <paramref name="before"/>. Returns true when Windows asked for a reboot.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="before">State to restore.</param>
    /// <param name="current">Current state.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    protected abstract ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken);

    /// <summary>Throws when the arguments are invalid.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="partial">Probe use.</param>
    protected void RequireValid(OpArgs args, bool partial)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (ArgValidator.Validate(Args, args.Values, partial) is { } problem)
        {
            throw new ArgumentException(problem);
        }
    }

    /// <summary>A failed result.</summary>
    /// <param name="code">Machine code.</param>
    /// <param name="detail">Detail.</param>
    protected static OpResult Fail(string code, string? detail = null) => new() { Success = false, ErrorCode = code, Detail = detail };

    /// <summary>Maps an exception to a machine error code.</summary>
    /// <param name="ex">Exception.</param>
    protected static OpResult FromException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var code = ex switch
        {
            PortRefusedException r => r.Code,
            Win32Exception w => "win32:" + w.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UnauthorizedAccessException => "win32:5",
            TimeoutException => "op.timeout",
            ArgumentException => "args.invalid",
            OperationCanceledException => "op.cancelled",
            IOException io => "io:" + (io.HResult & 0xFFFF).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "op.error:" + ex.GetType().Name,
        };
        return new OpResult { Success = false, ErrorCode = code, Detail = ex.GetType().Name };
    }

    /// <summary>Spec helper.</summary>
    /// <param name="name">Name.</param>
    /// <param name="type">Type.</param>
    /// <param name="required">Required.</param>
    /// <param name="pattern">Pattern.</param>
    /// <param name="allowed">Enum values.</param>
    protected static OpArgSpec Arg(string name, OpArgType type, bool required = true, string? pattern = null, params string[] allowed)
        => new() { Name = name, Type = type, Required = required, Pattern = pattern, AllowedValues = allowed };
}

/// <summary>Dependencies shared by all primitives.</summary>
/// <param name="Ports">System ports.</param>
/// <param name="Policy">Policy.</param>
/// <param name="Session">Session context.</param>
/// <param name="Clock">Clock.</param>
/// <param name="FactProbe">Live probes (clock skew).</param>
public sealed record OperationContext(SystemPorts Ports, ProtectedTargetPolicy Policy, IFixSessionContext Session, TimeProvider Clock, VTD.Contracts.Scanning.IFactProbe FactProbe);
