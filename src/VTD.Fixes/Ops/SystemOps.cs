using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>
/// <c>time.resync</c>: <c>w32tm /resync /force</c>. The skew comes from the live <see cref="GuardIds.ClockOk"/> probe
/// (evidence arg <c>skewSeconds</c>); below 2 seconds counts as 0. Undo cannot (and must not) bring a wrong clock back:
/// best effort, it changes nothing.
/// </summary>
public sealed class TimeResyncOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.TimeResync;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.BestEffort;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["skewSeconds"];

    /// <inheritdoc />
    public override IReadOnlyList<string> VolatileKeys { get; } = ["skewSeconds"];

    /// <inheritdoc />
    public override bool AlwaysApply => true;

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before) => Captures.Of(Op, ("skewSeconds", "0"));

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken) => ValueTask.FromResult(ProtectedTargetPolicy.Allow);

    /// <inheritdoc />
    protected override async ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var skew = string.Empty;
        try
        {
            var r = await Ctx.FactProbe.ProbeAsync(GuardIds.ClockOk, System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty, cancellationToken).ConfigureAwait(false);
            var raw = r.Evidence.SelectMany(e => e.Args).Where(a => string.Equals(a.Key, "skewSeconds", StringComparison.Ordinal)).Select(a => a.Value).FirstOrDefault();
            if (raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s))
            {
                var abs = Math.Abs(s);
                skew = abs < 2 ? "0" : Math.Round(abs, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            skew = string.Empty;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal) { ["skewSeconds"] = skew };
    }

    /// <inheritdoc />
    protected override async ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var code = await Ports.Tools.RunAsync(SystemTool.TimeResync, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return code == 0 ? false : throw new PortRefusedException("tool.exit:" + code.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);
}

/// <summary>A stateless one-way primitive that runs one fixed Windows tool.</summary>
public abstract class ToolOneWayOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <summary>The tool.</summary>
    protected abstract SystemTool Tool { get; }

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.OneWay;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = [];

    /// <inheritdoc />
    public override bool AlwaysApply => true;

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before) => Captures.Of(Op, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken) => ValueTask.FromResult(ProtectedTargetPolicy.Allow);

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <inheritdoc />
    protected override async ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var code = await Ports.Tools.RunAsync(Tool, TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
        return code == 0 ? Reboot == RebootNeed.Required : throw new PortRefusedException("tool.exit:" + code.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
        => throw new PortRefusedException("undo.one_way");
}

/// <summary><c>dns.flush</c> (harmless, one-way).</summary>
public sealed class DnsFlushOp(OperationContext ctx) : ToolOneWayOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.DnsFlush;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    protected override SystemTool Tool => SystemTool.FlushDns;
}

/// <summary><c>oneway.winsockReset</c> (one-way, reboot, network).</summary>
public sealed class WinsockResetOp(OperationContext ctx) : ToolOneWayOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.OneWayWinsockReset;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Medium;

    /// <inheritdoc />
    public override RebootNeed Reboot => RebootNeed.Required;

    /// <inheritdoc />
    public override bool AffectsNetwork => true;

    /// <inheritdoc />
    protected override SystemTool Tool => SystemTool.WinsockReset;
}

/// <summary><c>oneway.ipReset</c> (one-way, reboot, network).</summary>
public sealed class IpResetOp(OperationContext ctx) : ToolOneWayOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.OneWayIpReset;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Medium;

    /// <inheritdoc />
    public override RebootNeed Reboot => RebootNeed.Required;

    /// <inheritdoc />
    public override bool AffectsNetwork => true;

    /// <inheritdoc />
    protected override SystemTool Tool => SystemTool.IpReset;
}

/// <summary><c>oneway.sfcDism</c>: declared for M4; refused by policy until it ships (streamed, AC power, long).</summary>
public sealed class SfcDismOp(OperationContext ctx) : ToolOneWayOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.OneWaySfcDism;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Medium;

    /// <inheritdoc />
    public override RebootNeed Reboot => RebootNeed.Recommended;

    /// <inheritdoc />
    protected override SystemTool Tool => throw new PortRefusedException("op.not_shipped");

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(ProtectedTargetPolicy.Deny("op.not_shipped_m4"));
}

/// <summary>
/// <c>process.closeGraceful</c>: posts WM_CLOSE to the top-level windows of every process with that image name in the
/// player's console session and waits for them to exit. Never kills, never opens a process handle. Session-only: there is
/// no undo (the app comes back when the player starts it or at the next login).
/// </summary>
public sealed class ProcessCloseGracefulOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.ProcessCloseGraceful;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.SessionOnly;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override bool RequiresAdmin => false;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("imageName", OpArgType.ImageName)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["running"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before) => Captures.Of(Op, ("running", "false"));

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(ProtectedTargetPolicy.CheckProcessClose(args.Get("imageName")));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal) { ["running"] = Captures.Bool(Matching(args).Count > 0) });

    /// <inheritdoc />
    protected override async ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var targets = Matching(args);
        var windows = targets.Sum(p => Ports.Processes.PostClose(p.ProcessId));
        if (windows == 0)
        {
            throw new PortRefusedException("process.no_window");
        }

        var deadline = Ctx.Clock.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (Matching(args).Count > 0)
        {
            if (Ctx.Clock.GetUtcNow() >= deadline)
            {
                throw new PortRefusedException("process.still_running");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Ctx.Clock, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
        => throw new PortRefusedException("undo.session_only");

    private List<ProcessEntry> Matching(OpArgs args)
    {
        var image = args.Get("imageName");
        var session = Ports.Processes.ActiveConsoleSessionId();
        return [.. Ports.Processes.Snapshot().Where(p => string.Equals(p.ImageName, image, StringComparison.OrdinalIgnoreCase) && p.SessionId == session)];
    }
}

/// <summary><c>systemProtection.enable</c>: System Protection on the system drive. Best effort undo LEAVES IT ON: turning it
/// off would delete every restore point, including the one VTDiag just made.</summary>
public sealed class SystemProtectionEnableOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.SystemProtectionEnable;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.BestEffort;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("drive", OpArgType.String, true, "^[A-Z]:$")];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["enabled", "maxSizePercent"];

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Controlled(OpArgs args, StateCapture state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new Dictionary<string, string>(StringComparer.Ordinal) { ["enabled"] = state.Get("enabled") };
    }

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return Captures.Of(Op, ("enabled", "true"), ("maxSizePercent", before.Get("maxSizePercent")));
    }

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckSystemProtection(args.Get("drive")));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = Captures.Bool(Ports.SystemProtection.IsEnabled(args.GetOrNull("drive") ?? Ports.Files.Folders.SystemDrive)),
            ["maxSizePercent"] = Ports.SystemProtection.MaxSizePercent()?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        });

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        Ports.SystemProtection.Enable(args.Get("drive"));
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);
}
