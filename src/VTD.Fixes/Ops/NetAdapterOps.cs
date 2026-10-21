using System.Globalization;
using System.Text.RegularExpressions;
using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>
/// <c>netadapter.setAdvancedProperty</c>: writes a keyword under the adapter's own driver key
/// (<c>Control\Class\{4d36e972…}\NNNN</c>, located from the device, never from an argument), only for a keyword the driver
/// declares under <c>Ndi\params</c> and a value that driver accepts, then restarts the adapter. A keyword the driver does
/// not declare is skipped (no change).
/// </summary>
public sealed partial class NetAdapterSetAdvancedPropertyOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.NetAdapterSetAdvancedProperty;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override bool AffectsNetwork => true;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } =
    [
        Arg("instanceId", OpArgType.InstanceId),
        Arg("keyword", OpArgType.String, true, "^\\*?[A-Za-z0-9]{1,64}$"),
        Arg("value", OpArgType.String, true, "^[A-Za-z0-9._-]{0,64}$"),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["exists", "value"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        if (before.Get("_declared") != "true")
        {
            return before;
        }

        var value = args.Get("value");
        var raw = RegCodec.Raw(RegCodec.Encode("String", value)!);
        return Captures.Of(Op, ("exists", "true"), ("value", value), ("_declared", "true"), ("_raw", raw), ("_driverKey", before.Get("_driverKey")));
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => string.IsNullOrEmpty(before?.Get("_driverKey")) ? "target.adapter_missing" : null;

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
    {
        if (!ProtectedTargetPolicy.AdapterKeywords.Contains(args.Get("keyword"), StringComparer.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(ProtectedTargetPolicy.Deny("netadapter.keyword"));
        }

        return ValueTask.FromResult(Policy.CheckNetAdapter(args.Get("instanceId")));
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = "false", ["value"] = string.Empty, ["_declared"] = "false", ["_raw"] = string.Empty, ["_driverKey"] = string.Empty };
        var key = DriverKeyPath(args.Get("instanceId"));
        if (key is null)
        {
            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
        }

        map["_driverKey"] = key;
        if (args.GetOrNull("keyword") is { } keyword)
        {
            var v = Ports.Registry.GetValue(RegHive.Machine, key, keyword);
            map["exists"] = Captures.Bool(v is not null);
            map["value"] = v is null ? string.Empty : RegCodec.Display(v);
            map["_raw"] = v is null ? string.Empty : RegCodec.Raw(v);
            map["_declared"] = Captures.Bool(Ports.Registry.KeyExists(RegHive.Machine, key + "\\Ndi\\params\\" + keyword));
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var key = before.Get("_driverKey");
        var keyword = args.Get("keyword");
        var value = args.Get("value");
        if (!ValueAllowed(key, keyword, value))
        {
            throw new PortRefusedException("netadapter.value_not_allowed");
        }

        Ports.Registry.SetValue(RegHive.Machine, key, keyword, RegCodec.Encode("String", value)!);
        return ValueTask.FromResult(Ports.Devices.Restart(args.Get("instanceId")).RebootRequired);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        var key = before.Get("_driverKey");
        if (string.IsNullOrEmpty(key) || !string.Equals(key, current.Get("_driverKey"), StringComparison.OrdinalIgnoreCase))
        {
            throw new PortRefusedException("undo.target_moved");
        }

        var keyword = args.Get("keyword");
        if (before.Get("exists") == "true")
        {
            Ports.Registry.SetValue(RegHive.Machine, key, keyword, RegCodec.ParseRaw(before.Get("_raw")) ?? throw new PortRefusedException("undo.no_before_state"));
        }
        else
        {
            Ports.Registry.DeleteValue(RegHive.Machine, key, keyword);
        }

        return ValueTask.FromResult(Ports.Devices.Restart(args.Get("instanceId")).RebootRequired);
    }

    /// <summary>The adapter's own driver key path, or null when it is not a present network adapter.</summary>
    private string? DriverKeyPath(string instanceId)
    {
        var d = Ports.Devices.Query(instanceId);
        if (d?.DriverKey is not { } driverKey || !DriverKeyPattern().IsMatch(driverKey))
        {
            return null;
        }

        return "SYSTEM\\CurrentControlSet\\Control\\Class\\" + driverKey;
    }

    private bool ValueAllowed(string key, string keyword, string value)
    {
        var p = key + "\\Ndi\\params\\" + keyword;
        if (!Ports.Registry.KeyExists(RegHive.Machine, p))
        {
            return false;
        }

        var type = Text(p, "type")?.ToUpperInvariant() ?? "EDIT";
        switch (type)
        {
            case "ENUM":
                return Ports.Registry.GetValueNames(RegHive.Machine, p + "\\enum").Contains(value, StringComparer.OrdinalIgnoreCase);
            case "INT" or "LONG" or "WORD" or "DWORD":
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                {
                    return false;
                }

                var min = long.TryParse(Text(p, "min"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mn) ? mn : long.MinValue;
                var max = long.TryParse(Text(p, "max"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mx) ? mx : long.MaxValue;
                return n >= min && n <= max;
            default:
                var limit = int.TryParse(Text(p, "limitText"), NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : 64;
                return value.Length <= limit;
        }
    }

    private string? Text(string key, string name)
        => Ports.Registry.GetValue(RegHive.Machine, key, name) is { } v ? RegCodec.Display(v) : null;

    [GeneratedRegex(@"^\{4d36e972-e325-11ce-bfc1-08002be10318\}\\[0-9]{4}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DriverKeyPattern();
}

/// <summary><c>netadapter.setMetric</c>: IPv4 interface metric (fixed or automatic); undo restores both.</summary>
public sealed class NetAdapterSetMetricOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.NetAdapterSetMetric;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override bool AffectsNetwork => true;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("instanceId", OpArgType.InstanceId), Arg("metric", OpArgType.String, true, "^(automatic|[0-9]{1,4})$")];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["metric", "automaticMetric"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        var m = args.Get("metric");
        return string.Equals(m, "automatic", StringComparison.Ordinal)
            ? Captures.Of(Op, ("metric", before.Get("metric")), ("automaticMetric", "true"), ("_guid", before.Get("_guid")))
            : Captures.Of(Op, ("metric", int.Parse(m, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)), ("automaticMetric", "false"), ("_guid", before.Get("_guid")));
    }

    /// <summary>With an automatic metric the number is Windows' choice: only the flag is ours.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="state">State.</param>
    public override IReadOnlyDictionary<string, string> Controlled(OpArgs args, StateCapture state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["automaticMetric"] = state.Get("automaticMetric"), ["_guid"] = state.Get("_guid") };
        if (state.Get("automaticMetric") != "true")
        {
            map["metric"] = state.Get("metric");
        }

        return map;
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => string.IsNullOrEmpty(before?.Get("_guid")) ? "target.adapter_missing" : string.IsNullOrEmpty(before.Get("automaticMetric")) ? "target.no_ipv4" : null;

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckNetAdapter(args.Get("instanceId")));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["metric"] = string.Empty, ["automaticMetric"] = string.Empty, ["_guid"] = string.Empty };
        if (Ports.Network.InterfaceGuidOf(args.Get("instanceId")) is { } guid)
        {
            map["_guid"] = guid.ToString("D", CultureInfo.InvariantCulture);
            if (Ports.Network.GetIpv4Metric(guid) is { } m)
            {
                map["metric"] = m.Metric.ToString(CultureInfo.InvariantCulture);
                map["automaticMetric"] = Captures.Bool(m.Automatic);
            }
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var target = Intended(args, before);
        Ports.Network.SetIpv4Metric(Guid.Parse(before.Get("_guid")), target.Get("automaticMetric") == "true", int.Parse(target.Get("metric"), NumberStyles.None, CultureInfo.InvariantCulture));
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(before.Get("_guid"), out var guid) || !int.TryParse(before.Get("metric"), NumberStyles.None, CultureInfo.InvariantCulture, out var metric))
        {
            throw new PortRefusedException("undo.no_before_state");
        }

        Ports.Network.SetIpv4Metric(guid, before.Get("automaticMetric") == "true", metric);
        return ValueTask.FromResult(false);
    }
}

/// <summary><c>netadapter.unbindIpv6</c>: the ms_tcpip6 binding of one adapter; undo binds it again.</summary>
public sealed class NetAdapterUnbindIpv6Op(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.NetAdapterUnbindIpv6;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override bool AffectsNetwork => true;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("instanceId", OpArgType.InstanceId)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["ipv6Bound"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(before);
        return string.IsNullOrEmpty(before.Get("ipv6Bound")) ? before : Captures.Of(Op, ("ipv6Bound", "false"), ("_guid", before.Get("_guid")));
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => string.IsNullOrEmpty(before?.Get("_guid")) ? "target.adapter_missing" : string.IsNullOrEmpty(before.Get("ipv6Bound")) ? "target.no_ipv6_binding" : null;

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckNetAdapter(args.Get("instanceId")));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["ipv6Bound"] = string.Empty, ["_guid"] = string.Empty };
        if (Ports.Network.InterfaceGuidOf(args.Get("instanceId")) is { } guid)
        {
            map["_guid"] = guid.ToString("D", CultureInfo.InvariantCulture);
            if (Ports.Network.GetIpv6Bound(guid) is { } bound)
            {
                map["ipv6Bound"] = Captures.Bool(bound);
            }
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        Ports.Network.SetIpv6Bound(Guid.Parse(before.Get("_guid")), false);
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(before.Get("_guid"), out var guid) || string.IsNullOrEmpty(before.Get("ipv6Bound")))
        {
            throw new PortRefusedException("undo.no_before_state");
        }

        Ports.Network.SetIpv6Bound(guid, before.Get("ipv6Bound") == "true");
        return ValueTask.FromResult(false);
    }
}
