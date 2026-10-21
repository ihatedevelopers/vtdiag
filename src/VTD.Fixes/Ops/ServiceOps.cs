using System.Globalization;
using VTD.Contracts.Fixes;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>Shared service state helpers.</summary>
internal static class ServiceState
{
    public static TimeSpan Timeout(OpArgs args, int fallbackSeconds)
        => TimeSpan.FromSeconds(Math.Clamp(args.GetOrNull("timeoutSec") is { } s ? int.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : fallbackSeconds, 1, 120));

    public static string RunWord(ServiceRun state) => state switch
    {
        ServiceRun.Running or ServiceRun.StartPending or ServiceRun.ContinuePending => "Running",
        ServiceRun.Stopped or ServiceRun.StopPending => "Stopped",
        _ => state.ToString(),
    };
}

/// <summary><c>service.setStartType</c>: start type and delayed-auto flag; undo restores both exactly (Boot/System included).</summary>
public sealed class ServiceSetStartTypeOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.ServiceSetStartType;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } =
        [Arg("name", OpArgType.ServiceName), Arg("startType", OpArgType.Enum, true, null, "Automatic", "Manual", "Disabled"), Arg("delayed", OpArgType.Bool, false)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["exists", "startType", "delayed"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        if (before.Get("exists") != "true")
        {
            return before;
        }

        var start = args.Get("startType");
        var delayed = args.GetOrNull("delayed") ?? before.Get("delayed");
        return Captures.Of(Op, ("exists", "true"), ("startType", start), ("delayed", delayed));
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => before?.Get("exists") == "true" ? null : "target.service_missing";

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckService(Op, args.Get("name"), args.Get("startType")));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var info = Ports.Services.Query(args.Get("name"));
        IReadOnlyDictionary<string, string> map = info is null
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = "false", ["startType"] = string.Empty, ["delayed"] = string.Empty }
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = "true", ["startType"] = info.StartType.ToString(), ["delayed"] = Captures.Bool(info.Delayed) };
        return ValueTask.FromResult(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var target = Intended(args, before);
        Ports.Services.SetStartType(args.Get("name"), Enum.Parse<ServiceStart>(target.Get("startType")), target.Get("delayed") == "true");
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        if (before.Get("exists") != "true" || !Enum.TryParse<ServiceStart>(before.Get("startType"), out var start))
        {
            throw new PortRefusedException("undo.no_before_state");
        }

        Ports.Services.SetStartType(args.Get("name"), start, before.Get("delayed") == "true");
        return ValueTask.FromResult(false);
    }
}

/// <summary><c>service.stop</c> / <c>service.start</c>: run state; undo puts the previous run state back.</summary>
public abstract class ServiceRunOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <summary>The state this primitive moves to.</summary>
    protected abstract bool StartIt { get; }

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("name", OpArgType.ServiceName), Arg("timeoutSec", OpArgType.Int, false)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["exists", "state"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return before.Get("exists") != "true" ? before : Captures.Of(Op, ("exists", "true"), ("state", StartIt ? "Running" : "Stopped"));
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => before?.Get("exists") == "true" ? null : "target.service_missing";

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckService(Op, args.Get("name"), null));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var info = Ports.Services.Query(args.Get("name"));
        IReadOnlyDictionary<string, string> map = info is null
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = "false", ["state"] = string.Empty }
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = "true", ["state"] = ServiceState.RunWord(info.State) };
        return ValueTask.FromResult(map);
    }

    /// <inheritdoc />
    protected override async ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        await Move(args, StartIt, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <inheritdoc />
    protected override async ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        var wasRunning = before.Get("state") == "Running";
        await Move(args, wasRunning, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private Task Move(OpArgs args, bool start, CancellationToken cancellationToken)
    {
        var timeout = ServiceState.Timeout(args, start ? 30 : 30);
        return start
            ? Ports.Services.StartAsync(args.Get("name"), timeout, cancellationToken)
            : Ports.Services.StopAsync(args.Get("name"), timeout, cancellationToken);
    }
}

/// <summary><c>service.stop</c>.</summary>
public sealed class ServiceStopOp(OperationContext ctx) : ServiceRunOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.ServiceStop;

    /// <inheritdoc />
    protected override bool StartIt => false;
}

/// <summary><c>service.start</c>.</summary>
public sealed class ServiceStartOp(OperationContext ctx) : ServiceRunOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.ServiceStart;

    /// <inheritdoc />
    protected override bool StartIt => true;
}
