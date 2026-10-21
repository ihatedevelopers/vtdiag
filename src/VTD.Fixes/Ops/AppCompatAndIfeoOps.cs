using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>
/// AppCompat layers (<c>…\AppCompatFlags\Layers</c>, value name = executable path, REG_SZ like <c>~ RUNASADMIN</c>). The
/// primitives merge or remove tokens and keep the rest; undo restores the exact previous value (or its absence).
/// </summary>
public abstract class AppCompatLayersOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <summary>Layers key path relative to the hive.</summary>
    public const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } =
        [Arg("exePath", OpArgType.FilePath), Arg("hive", OpArgType.Enum, true, null, "HKLM", "HKCU"), Arg("layers", OpArgType.String, true, "^[A-Z0-9_~ ]{1,256}$")];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["exists", "layers"];

    /// <summary>Tokens of a layer string (upper-case, de-duplicated, order kept).</summary>
    /// <param name="layers">Layer string.</param>
    public static List<string> Tokens(string layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var list = new List<string>();
        foreach (var t in layers.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var u = t.ToUpperInvariant();
            if (!list.Contains(u, StringComparer.Ordinal))
            {
                list.Add(u);
            }
        }

        return list;
    }

    /// <summary>Windows 8+ layer format: <c>~</c> first, then the tokens.</summary>
    /// <param name="tokens">Tokens (without <c>~</c>).</param>
    public static string Format(IEnumerable<string> tokens) => string.Join(' ', new[] { "~" }.Concat(tokens.Where(t => t != "~")));

    /// <summary>Exact comparison: existence and raw bytes.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="state">State.</param>
    public override IReadOnlyDictionary<string, string> Controlled(OpArgs args, StateCapture state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new Dictionary<string, string>(StringComparer.Ordinal) { ["exists"] = state.Get("exists"), ["_raw"] = state.Get("_raw") };
    }

    /// <summary>Hive from the argument.</summary>
    /// <param name="args">Arguments.</param>
    protected static RegHive HiveOf(OpArgs args) => string.Equals(args?.Get("hive"), "HKLM", StringComparison.Ordinal) ? RegHive.Machine : RegHive.User;

    /// <summary>Expanded executable path.</summary>
    /// <param name="args">Arguments.</param>
    protected string ExePath(OpArgs args) => ArgValidator.ExpandPath(args?.Get("exePath") ?? string.Empty, Ports.Files.Folders) ?? throw new ArgumentException("args.invalid:exePath");

    /// <summary>The state map for a layer value.</summary>
    /// <param name="value">Value.</param>
    protected static Dictionary<string, string> Map(RegValue? value)
    {
        var text = value is null ? string.Empty : RegCodec.Display(value);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exists"] = Captures.Bool(value is not null),
            ["layers"] = text,
            ["_raw"] = value is null ? string.Empty : RegCodec.Raw(value),
        };
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(Map(Ports.Registry.GetValue(HiveOf(args), LayersKey, ExePath(args))));

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var target = Intended(args, before);
        if (target.Get("exists") == "true")
        {
            Ports.Registry.SetValue(HiveOf(args), LayersKey, ExePath(args), RegCodec.ParseRaw(target.Get("_raw"))!);
        }
        else
        {
            Ports.Registry.DeleteValue(HiveOf(args), LayersKey, ExePath(args));
        }

        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        if (before.Get("exists") == "true")
        {
            Ports.Registry.SetValue(HiveOf(args), LayersKey, ExePath(args), RegCodec.ParseRaw(before.Get("_raw")) ?? throw new PortRefusedException("undo.no_before_state"));
        }
        else
        {
            Ports.Registry.DeleteValue(HiveOf(args), LayersKey, ExePath(args));
        }

        return ValueTask.FromResult(false);
    }

    /// <summary>A capture for a layer string, or for an absent value.</summary>
    /// <param name="layers">Layer string, or null.</param>
    protected StateCapture CaptureOfLayers(string? layers)
        => Captures.Of(Op, Map(layers is null ? null : RegCodec.Encode("String", layers)));
}

/// <summary><c>appcompat.setLayers</c>: adds tokens (Riot executables and allowlisted tokens only).</summary>
public sealed class AppCompatSetLayersOp(OperationContext ctx) : AppCompatLayersOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.AppCompatSetLayers;

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        var current = before.Get("exists") == "true" ? Tokens(before.Get("layers")) : [];
        var wanted = Tokens(args.Get("layers")).Where(t => t != "~").ToList();
        if (before.Get("exists") == "true" && wanted.All(current.Contains))
        {
            return Captures.Of(Op, before.Data);
        }

        var merged = current.Where(t => t != "~").ToList();
        merged.AddRange(wanted.Where(t => !merged.Contains(t, StringComparer.Ordinal)));
        return CaptureOfLayers(Format(merged));
    }

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckAddLayers(HiveOf(args), ExePath(args), Tokens(args.Get("layers"))));
}

/// <summary><c>appcompat.removeLayers</c>: removes tokens; when only <c>~</c> would remain the value is deleted.</summary>
public sealed class AppCompatRemoveLayersOp(OperationContext ctx) : AppCompatLayersOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.AppCompatRemoveLayers;

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(before);
        if (before.Get("exists") != "true")
        {
            return Captures.Of(Op, before.Data);
        }

        var current = Tokens(before.Get("layers"));
        var remove = Tokens(args.Get("layers")).Where(t => t != "~").ToList();
        if (!remove.Any(current.Contains))
        {
            return Captures.Of(Op, before.Data);
        }

        var rest = current.Where(t => t != "~" && !remove.Contains(t, StringComparer.Ordinal)).ToList();
        return CaptureOfLayers(rest.Count == 0 ? null : Format(rest));
    }

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(Policy.CheckRemoveLayers(HiveOf(args), ExePath(args)));
}

/// <summary>Shared Image File Execution Options value removal (Riot images only); undo restores type and bytes.</summary>
public abstract class IfeoValuesOp(OperationContext ctx) : OperationBase(ctx)
{
    /// <summary>IFEO root.</summary>
    public const string IfeoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>State key → (relative subkey, value name).</summary>
    protected abstract IReadOnlyList<(string StateKey, string SubKey, string ValueName)> Values { get; }

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } = [Arg("imageName", OpArgType.ImageName)];

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
        => Captures.Of(Op, Values.ToDictionary(v => v.StateKey, _ => string.Empty, StringComparer.Ordinal));

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
        => ValueTask.FromResult(ProtectedTargetPolicy.CheckIfeo(args.Get("imageName")));

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (stateKey, subKey, valueName) in Values)
        {
            var v = Ports.Registry.GetValue(RegHive.Machine, KeyOf(args, subKey), valueName);
            map[stateKey] = v is null ? string.Empty : RegCodec.Raw(v);
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        foreach (var (_, subKey, valueName) in Values)
        {
            Ports.Registry.DeleteValue(RegHive.Machine, KeyOf(args, subKey), valueName);
        }

        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        foreach (var (stateKey, subKey, valueName) in Values)
        {
            var raw = before.Get(stateKey);
            if (raw.Length == 0)
            {
                Ports.Registry.DeleteValue(RegHive.Machine, KeyOf(args, subKey), valueName);
            }
            else
            {
                Ports.Registry.SetValue(RegHive.Machine, KeyOf(args, subKey), valueName, RegCodec.ParseRaw(raw) ?? throw new PortRefusedException("undo.no_before_state"));
            }
        }

        return ValueTask.FromResult(false);
    }

    private static string KeyOf(OpArgs args, string subKey)
    {
        var image = args.Get("imageName");
        return subKey.Length == 0 ? IfeoKey + "\\" + image : IfeoKey + "\\" + image + "\\" + subKey;
    }
}

/// <summary><c>ifeo.resetMitigations</c>: removes Exploit Protection overrides (MitigationOptions, MitigationAuditOptions).</summary>
public sealed class IfeoResetMitigationsOp(OperationContext ctx) : IfeoValuesOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.IfeoResetMitigations;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["mitigationOptions", "mitigationAuditOptions"];

    /// <inheritdoc />
    protected override IReadOnlyList<(string StateKey, string SubKey, string ValueName)> Values { get; } =
        [("mitigationOptions", string.Empty, "MitigationOptions"), ("mitigationAuditOptions", string.Empty, "MitigationAuditOptions")];
}

/// <summary><c>ifeo.removePerfOptions</c>: removes PerfOptions CpuPriorityClass and IoPriority.</summary>
public sealed class IfeoRemovePerfOptionsOp(OperationContext ctx) : IfeoValuesOp(ctx)
{
    /// <inheritdoc />
    public override string Op => OpIds.IfeoRemovePerfOptions;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["cpuPriorityClass", "ioPriority"];

    /// <inheritdoc />
    protected override IReadOnlyList<(string StateKey, string SubKey, string ValueName)> Values { get; } =
        [("cpuPriorityClass", "PerfOptions", "CpuPriorityClass"), ("ioPriority", "PerfOptions", "IoPriority")];
}
