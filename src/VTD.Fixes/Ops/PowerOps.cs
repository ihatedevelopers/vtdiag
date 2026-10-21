using System.Globalization;
using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>
/// <c>powercfg.setValue</c>: one AC and/or DC value index of a setting in a scheme (<c>active</c> resolves at capture and is
/// remembered, so undo writes back to the same scheme even if the active one changed). The active scheme is re-applied
/// after a write so the change takes effect now.
/// </summary>
public sealed class PowerCfgSetValueOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.PowerCfgSetValue;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } =
    [
        Arg("scheme", OpArgType.String, true, "^(active|[0-9a-fA-F-]{36})$"), Arg("subgroup", OpArgType.Guid), Arg("setting", OpArgType.Guid),
        Arg("ac", OpArgType.Int, false), Arg("dc", OpArgType.Int, false),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["ac", "dc"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        return Captures.Of(Op, ("ac", Norm(args.GetOrNull("ac")) ?? before.Get("ac")), ("dc", Norm(args.GetOrNull("dc")) ?? before.Get("dc")), ("_scheme", before.Get("_scheme")));
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        if (args.GetOrNull("ac") is null && args.GetOrNull("dc") is null)
        {
            return "target.power_nothing_to_set";
        }

        return (args.GetOrNull("ac") is not null && before.Get("ac").Length == 0) || (args.GetOrNull("dc") is not null && before.Get("dc").Length == 0)
            ? "target.power_setting_missing"
            : null;
    }

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
    {
        var values = new List<int>();
        foreach (var key in new[] { "ac", "dc" })
        {
            if (args.GetOrNull(key) is { } v)
            {
                values.Add(int.Parse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            }
        }

        var scheme = args.Get("scheme");
        if (!string.Equals(scheme, "active", StringComparison.Ordinal))
        {
            if (!Guid.TryParseExact(scheme, "D", out var g))
            {
                return ValueTask.FromResult(ProtectedTargetPolicy.Deny("power.scheme"));
            }

            try
            {
                if (!Ports.Power.EnumerateSchemes().Contains(g))
                {
                    return ValueTask.FromResult(ProtectedTargetPolicy.Deny("power.scheme_missing"));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return ValueTask.FromResult(ProtectedTargetPolicy.Deny("power.unverifiable"));
            }
        }

        return ValueTask.FromResult(ProtectedTargetPolicy.CheckPowerValue(Guid.Parse(args.Get("subgroup")), Guid.Parse(args.Get("setting")), values));
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var scheme = ResolveScheme(args.GetOrNull("scheme") ?? "active");
        var sub = Guid.Parse(args.Get("subgroup"));
        var setting = Guid.Parse(args.Get("setting"));
        var ac = Ports.Power.ReadValue(scheme, sub, setting, ac: true);
        var dc = Ports.Power.ReadValue(scheme, sub, setting, ac: false);
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ac"] = ac?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["dc"] = dc?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["_scheme"] = scheme.ToString("D", CultureInfo.InvariantCulture),
        });
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var scheme = Guid.Parse(before.Get("_scheme"));
        Write(scheme, args, args.GetOrNull("ac"), args.GetOrNull("dc"));
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(before.Get("_scheme"), out var scheme))
        {
            throw new PortRefusedException("undo.no_before_state");
        }

        Write(scheme, args, before.Get("ac").Length > 0 ? before.Get("ac") : null, before.Get("dc").Length > 0 ? before.Get("dc") : null);
        return ValueTask.FromResult(false);
    }

    private static string? Norm(string? v)
        => v is null ? null : uint.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private void Write(Guid scheme, OpArgs args, string? ac, string? dc)
    {
        var sub = Guid.Parse(args.Get("subgroup"));
        var setting = Guid.Parse(args.Get("setting"));
        if (ac is not null)
        {
            Ports.Power.WriteValue(scheme, sub, setting, ac: true, uint.Parse(ac, NumberStyles.None, CultureInfo.InvariantCulture));
        }

        if (dc is not null)
        {
            Ports.Power.WriteValue(scheme, sub, setting, ac: false, uint.Parse(dc, NumberStyles.None, CultureInfo.InvariantCulture));
        }

        var active = Ports.Power.GetActiveScheme();
        if (active == scheme)
        {
            Ports.Power.SetActiveScheme(active);
        }
    }

    private Guid ResolveScheme(string scheme)
        => string.Equals(scheme, "active", StringComparison.Ordinal) ? Ports.Power.GetActiveScheme() : Guid.Parse(scheme);
}

/// <summary><c>powercfg.setActiveScheme</c>; undo re-activates the previous scheme.</summary>
public sealed class PowerCfgSetActiveSchemeOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.PowerCfgSetActiveScheme;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("scheme", OpArgType.Guid)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["activeScheme"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Captures.Of(Op, ("activeScheme", Guid.Parse(args.Get("scheme")).ToString("D", CultureInfo.InvariantCulture)));
    }

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckActiveScheme(Guid.Parse(args.Get("scheme"))));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["activeScheme"] = Ports.Power.GetActiveScheme().ToString("D", CultureInfo.InvariantCulture),
        });

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        Ports.Power.SetActiveScheme(Guid.Parse(args.Get("scheme")));
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(before.Get("activeScheme"), out var scheme))
        {
            throw new PortRefusedException("undo.no_before_state");
        }

        Ports.Power.SetActiveScheme(scheme);
        return ValueTask.FromResult(false);
    }
}
