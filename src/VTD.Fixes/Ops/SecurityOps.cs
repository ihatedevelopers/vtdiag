using System.Globalization;
using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>Shared firewall rule capture (the full definition is kept so a removed VTDiag rule can be recreated exactly).</summary>
public abstract class FirewallRuleOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["exists", "enabled"];

    /// <summary>State map of a rule.</summary>
    /// <param name="rule">Rule or null.</param>
    protected static Dictionary<string, string> Map(FirewallRuleInfo? rule) => new(StringComparer.Ordinal)
    {
        ["exists"] = Captures.Bool(rule is not null),
        ["enabled"] = rule is null ? string.Empty : Captures.Bool(rule.Enabled),
        ["_grouping"] = rule?.Grouping ?? string.Empty,
        ["_program"] = rule?.Program ?? string.Empty,
        ["_direction"] = rule?.Direction ?? string.Empty,
        ["_action"] = rule?.Action ?? string.Empty,
        ["_profiles"] = rule is null ? string.Empty : rule.Profiles.ToString(CultureInfo.InvariantCulture),
        ["_protocol"] = rule is null ? string.Empty : rule.Protocol.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Rule from a state map.</summary>
    /// <param name="name">Rule name.</param>
    /// <param name="s">State.</param>
    protected static FirewallRuleInfo FromState(string name, StateCapture s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new FirewallRuleInfo
        {
            Name = name,
            Grouping = s.Get("_grouping").Length == 0 ? null : s.Get("_grouping"),
            Program = s.Get("_program").Length == 0 ? null : s.Get("_program"),
            Direction = s.Get("_direction"),
            Action = s.Get("_action"),
            Enabled = s.Get("enabled") == "true",
            Profiles = int.Parse(s.Get("_profiles"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            Protocol = int.Parse(s.Get("_protocol"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        };
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(Map(Ports.Firewall.GetRule(args.Get("name"))));

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        var name = args.Get("name");
        if (current.Get("exists") == "true")
        {
            if (!string.Equals(current.Get("_grouping"), ProtectedTargetPolicy.FirewallGroup, StringComparison.Ordinal))
            {
                throw new PortRefusedException("firewall.not_ours");
            }

            Ports.Firewall.RemoveRule(name);
        }

        if (before.Get("exists") == "true")
        {
            Ports.Firewall.AddRule(FromState(name, before));
        }

        return ValueTask.FromResult(false);
    }
}

/// <summary><c>firewall.addRule</c>: an allow rule in group VTDiag for a Riot executable.</summary>
public sealed class FirewallAddRuleOp(OperationContext ctx) : FirewallRuleOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.FirewallAddRule;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } =
        [Arg("name", OpArgType.String, true, "^VTDiag [A-Za-z0-9 ._-]{1,64}$"), Arg("program", OpArgType.FilePath), Arg("direction", OpArgType.Enum, true, null, "In", "Out")];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Captures.Of(Op, Map(Rule(args)));
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => before?.Get("exists") == "true" && !string.Equals(before.Get("_grouping"), ProtectedTargetPolicy.FirewallGroup, StringComparison.Ordinal) ? "target.firewall_name_taken" : null;

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckFirewallAdd(Program(args)));

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        if (before.Get("exists") == "true")
        {
            Ports.Firewall.RemoveRule(args.Get("name")); // our own rule with another definition: replace it
        }

        Ports.Firewall.AddRule(Rule(args));
        return ValueTask.FromResult(false);
    }

    private FirewallRuleInfo Rule(OpArgs args) => new()
    {
        Name = args.Get("name"),
        Grouping = ProtectedTargetPolicy.FirewallGroup,
        Program = Program(args),
        Direction = args.Get("direction"),
        Action = "Allow",
        Enabled = true,
    };

    private string Program(OpArgs args) => ArgValidator.ExpandPath(args.Get("program"), Ports.Files.Folders) ?? throw new ArgumentException("args.invalid:program");
}

/// <summary><c>firewall.removeRule</c>: removes a rule of group VTDiag; undo recreates it from the capture.</summary>
public sealed class FirewallRemoveRuleOp(OperationContext ctx) : FirewallRuleOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.FirewallRemoveRule;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("name", OpArgType.String, true, "^VTDiag [A-Za-z0-9 ._-]{1,64}$")];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before) => Captures.Of(Op, Map(null));

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckFirewallRemove(args.Get("name")));

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        if (!string.Equals(before.Get("_grouping"), ProtectedTargetPolicy.FirewallGroup, StringComparison.Ordinal))
        {
            throw new PortRefusedException("firewall.not_ours");
        }

        Ports.Firewall.RemoveRule(args.Get("name"));
        return ValueTask.FromResult(false);
    }
}

/// <summary><c>defender.addExclusion</c>: a Riot folder exclusion (medium risk, off by default, listed as a security change).</summary>
public sealed class DefenderAddExclusionOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.DefenderAddExclusion;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Medium;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("path", OpArgType.FilePath)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["excluded"];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before) => Captures.Of(Op, ("excluded", "true"));

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckDefenderExclusion(PathOf(args)));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal) { ["excluded"] = Captures.Bool(Ports.Defender.IsExcluded(PathOf(args))) });

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        Ports.Defender.AddExclusion(PathOf(args));
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        if (before.Get("excluded") != "true")
        {
            Ports.Defender.RemoveExclusion(PathOf(args));
        }

        return ValueTask.FromResult(false);
    }

    private string PathOf(OpArgs args) => ArgValidator.ExpandPath(args.Get("path"), Ports.Files.Folders) ?? throw new ArgumentException("args.invalid:path");
}

/// <summary><c>bcd.setValue</c>: one allowlisted element of <c>{current}</c>, only towards the secure value; reboot required.</summary>
public sealed class BcdSetValueOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.BcdSetValue;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Medium;

    /// <inheritdoc />
    public override RebootNeed Reboot => RebootNeed.Required;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } =
        [Arg("element", OpArgType.Enum, true, null, "testsigning", "nointegritychecks", "nx", "hypervisorlaunchtype"), Arg("value", OpArgType.String, true, "^(on|off|OptIn|OptOut|AlwaysOn|Auto|Off)$")];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["value"];

    /// <summary>Maps the element word.</summary>
    /// <param name="word">bcdedit word.</param>
    public static BcdElement ElementOf(string word) => word switch
    {
        "testsigning" => BcdElement.TestSigning,
        "nointegritychecks" => BcdElement.NoIntegrityChecks,
        "nx" => BcdElement.Nx,
        "hypervisorlaunchtype" => BcdElement.HypervisorLaunchType,
        _ => throw new ArgumentException("args.invalid:element"),
    };

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Captures.Of(Op, ("value", args.Get("value")));
    }

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(ProtectedTargetPolicy.CheckBcd(args.Get("element"), args.Get("value")));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = Ports.Bcd.Get(ElementOf(args.Get("element"))) ?? string.Empty });

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        Ports.Bcd.Set(ElementOf(args.Get("element")), args.Get("value"));
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        var element = ElementOf(args.Get("element"));
        if (before.Get("value").Length == 0)
        {
            Ports.Bcd.Delete(element);
        }
        else
        {
            Ports.Bcd.Set(element, before.Get("value"));
        }

        return ValueTask.FromResult(true);
    }
}
