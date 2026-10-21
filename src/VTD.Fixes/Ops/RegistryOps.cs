using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>Shared capture/restore of one registry value (exact: type and bytes).</summary>
public abstract class RegistryValueOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["exists", "kind", "data"];

    /// <summary>The value's location.</summary>
    /// <param name="args">Arguments.</param>
    protected static (RegHive Hive, string SubKey, string Name) Target(OpArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!RegistryPaths.TryParse(args.Get("path"), out var hive, out var subKey))
        {
            throw new ArgumentException("args.invalid:path");
        }

        return (hive, subKey, args.Get("name"));
    }

    /// <summary>Exact comparison: existence and raw bytes.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="state">State.</param>
    public override IReadOnlyDictionary<string, string> Controlled(OpArgs args, StateCapture state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = state.Get("exists"), ["_raw"] = state.Get("_raw") };
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var (hive, subKey, name) = Target(args);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        RegCodec.Fill(Ports.Registry.GetValue(hive, subKey, name), map);
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        var (hive, subKey, name) = Target(args);
        if (before.Get("exists") == "true")
        {
            Ports.Registry.SetValue(hive, subKey, name, RegCodec.ParseRaw(before.Get("_raw")) ?? throw new PortRefusedException("undo.no_before_state"));
        }
        else
        {
            Ports.Registry.DeleteValue(hive, subKey, name);
        }

        return ValueTask.FromResult(false);
    }

    /// <summary>A capture of an absent value.</summary>
    /// <param name="op">Primitive id.</param>
    protected static StateCapture Absent(string op)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        RegCodec.Fill(null, map);
        return Captures.Of(op, map);
    }
}

/// <summary><c>registry.setValue</c> under a code allowlisted location (HKCU = the player's hive).</summary>
public sealed class RegistrySetValueOp(OperationContext ctx) : RegistryValueOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.RegistrySetValue;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } =
        [Arg("path", OpArgType.RegistryPath), Arg("name", OpArgType.String), Arg("kind", OpArgType.Enum, true, null, "String", "ExpandString", "DWord", "QWord", "MultiString"), Arg("data", OpArgType.String)];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        var value = RegCodec.Encode(args.Get("kind"), args.Get("data")) ?? throw new ArgumentException("args.invalid:data");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        RegCodec.Fill(value, map);
        return Captures.Of(Op, map);
    }

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
    {
        var (hive, subKey, name) = Target(args);
        if (RegCodec.Encode(args.Get("kind"), args.Get("data")) is null)
        {
            return ValueTask.FromResult(ProtectedTargetPolicy.Deny("registry.data"));
        }

        return ValueTask.FromResult(Policy.CheckRegistry(hive, subKey, name, args.Get("kind"), args.Get("data")));
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var (hive, subKey, name) = Target(args);
        Ports.Registry.SetValue(hive, subKey, name, RegCodec.Encode(args.Get("kind"), args.Get("data"))!);
        return ValueTask.FromResult(false);
    }
}

/// <summary><c>registry.deleteValue</c> under a code allowlisted location that allows deletion.</summary>
public sealed class RegistryDeleteValueOp(OperationContext ctx) : RegistryValueOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.RegistryDeleteValue;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("path", OpArgType.RegistryPath), Arg("name", OpArgType.String)];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before) => Absent(Op);

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
    {
        var (hive, subKey, name) = Target(args);
        return ValueTask.FromResult(Policy.CheckRegistry(hive, subKey, name, null, null));
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var (hive, subKey, name) = Target(args);
        Ports.Registry.DeleteValue(hive, subKey, name);
        return ValueTask.FromResult(false);
    }
}
