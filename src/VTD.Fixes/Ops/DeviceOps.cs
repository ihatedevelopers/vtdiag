using System.Globalization;
using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary><c>device.disable</c> / <c>device.enable</c>: persistent device state; undo restores the previous enabled state.</summary>
public abstract class DeviceStateOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <summary>Problem code of a disabled device (CM_PROB_DISABLED).</summary>
    public const int ProblemDisabled = 22;

    /// <summary>The state this primitive moves to.</summary>
    protected abstract bool EnableIt { get; }

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("instanceId", OpArgType.InstanceId)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["present", "enabled", "problemCode"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (before.Get("present") != "true")
        {
            return before;
        }

        var problem = EnableIt
            ? (before.Get("problemCode") == ProblemDisabled.ToString(CultureInfo.InvariantCulture) ? "0" : before.Get("problemCode"))
            : ProblemDisabled.ToString(CultureInfo.InvariantCulture);
        return Captures.Of(Op, ("present", "true"), ("enabled", Captures.Bool(EnableIt)), ("problemCode", problem));
    }

    /// <summary>Only presence and the enabled flag are ours; the problem code is the driver's business.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="state">State.</param>
    public override IReadOnlyDictionary<string, string> Controlled(OpArgs args, StateCapture state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new Dictionary<string, string>(StringComparer.Ordinal) { ["present"] = state.Get("present"), ["enabled"] = state.Get("enabled") };
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => before?.Get("present") == "true" ? null : "target.device_missing";

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var d = Ports.Devices.Query(args.Get("instanceId"));
        IReadOnlyDictionary<string, string> map = d is null || !d.Present
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["present"] = "false",
                ["enabled"] = d is null ? string.Empty : Captures.Bool(d.Enabled),
                ["problemCode"] = d is null ? string.Empty : d.ProblemCode.ToString(CultureInfo.InvariantCulture),
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["present"] = "true",
                ["enabled"] = Captures.Bool(d.Enabled),
                ["problemCode"] = d.ProblemCode.ToString(CultureInfo.InvariantCulture),
            };
        return ValueTask.FromResult(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var id = args.Get("instanceId");
        var r = EnableIt ? Ports.Devices.Enable(id) : Ports.Devices.Disable(id);
        return ValueTask.FromResult(r.RebootRequired);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        var id = args.Get("instanceId");
        var r = before.Get("enabled") == "true" ? Ports.Devices.Enable(id) : Ports.Devices.Disable(id);
        return ValueTask.FromResult(r.RebootRequired);
    }
}

/// <summary><c>device.disable</c> (protected targets refused by policy).</summary>
public sealed class DeviceDisableOp(OperationContext ctx) : DeviceStateOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.DeviceDisable;

    /// <inheritdoc />
    protected override bool EnableIt => false;

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckDeviceDisable(args.Get("instanceId")));
}

/// <summary><c>device.enable</c>.</summary>
public sealed class DeviceEnableOp(OperationContext ctx) : DeviceStateOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.DeviceEnable;

    /// <inheritdoc />
    protected override bool EnableIt => true;

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(ProtectedTargetPolicy.Allow);
}
