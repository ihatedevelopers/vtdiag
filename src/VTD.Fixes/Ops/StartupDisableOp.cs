using VTD.Contracts.Fixes;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Ops;

/// <summary>
/// <c>startup.disable</c>: marks a startup entry disabled in <c>Explorer\StartupApproved</c> exactly like Task Manager
/// (first byte odd = disabled); the entry itself is kept. Undo restores the previous StartupApproved value bytes (or
/// removes the value). RunOnce entries have no StartupApproved state and are refused.
/// </summary>
public sealed class StartupDisableOp(OperationContext ctx) : OperationBase(ctx)
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <inheritdoc />
    public override string Op => OpIds.StartupDisable;

    /// <inheritdoc />
    public override Reversibility Reversibility => Reversibility.Exact;

    /// <inheritdoc />
    public override RiskLevel RiskFloor => RiskLevel.Low;

    /// <inheritdoc />
    public override IReadOnlyList<OpArgSpec> Args { get; } =
        [Arg("location", OpArgType.Enum, true, null, "HklmRun", "HkcuRun", "HklmRunOnce", "HkcuRunOnce", "StartupFolderUser", "StartupFolderCommon"), Arg("name", OpArgType.String)];

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; } = ["exists", "enabled"];

    /// <summary>True when a StartupApproved value says enabled (absent = enabled; first byte even = enabled).</summary>
    /// <param name="raw">Value or null.</param>
    public static bool IsEnabled(RegValue? raw) => raw is null || raw.Data.Length == 0 || (raw.Data[0] & 1) == 0;

    /// <inheritdoc />
    public override StateCapture Intended(OpArgs args, StateCapture before)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (before.Get("exists") != "true" || before.Get("enabled") == "false")
        {
            return Captures.Of(Op, before.Data);
        }

        var current = RegCodec.ParseRaw(before.Get("_raw"));
        byte[] data;
        if (current is { Data.Length: > 0 })
        {
            data = (byte[])current.Data.Clone();
            data[0] |= 1;
        }
        else
        {
            data = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        }

        var map = before.Data.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        map["enabled"] = "false";
        map["_raw"] = RegCodec.Raw(new RegValue(current?.Type ?? RegValue.Binary, data));
        return Captures.Of(Op, map);
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Controlled(OpArgs args, StateCapture state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exists"] = state.Get("exists"),
            ["enabled"] = state.Get("enabled"),
            ["_raw"] = state.Get("_raw"),
            ["_approvedKey"] = state.Get("_approvedKey"),
        };
    }

    /// <inheritdoc />
    public override string? Precondition(OpArgs args, StateCapture before)
        => before?.Get("exists") == "true" ? null : "target.startup_missing";

    /// <inheritdoc />
    protected override ValueTask<PolicyDecision> PolicyCore(OpArgs args, CancellationToken cancellationToken)
    {
        var location = args.Get("location");
        var name = args.Get("name");
        if (location.EndsWith("RunOnce", StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ProtectedTargetPolicy.Deny("startup.runonce"));
        }

        if (location is "HkcuRun" or "StartupFolderUser" && Ctx.Session.Player is null)
        {
            return ValueTask.FromResult(ProtectedTargetPolicy.Deny("player_unknown"));
        }

        if (location.StartsWith("StartupFolder", StringComparison.Ordinal)
            && (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name is "." or ".."))
        {
            return ValueTask.FromResult(ProtectedTargetPolicy.Deny("args"));
        }

        var target = Locate(location, name);
        return ValueTask.FromResult(ProtectedTargetPolicy.IsProtectedStartup(name, target.TargetImage)
            ? ProtectedTargetPolicy.Deny("startup.protected_entry")
            : ProtectedTargetPolicy.Allow);
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyDictionary<string, string>> CaptureCore(OpArgs args, CancellationToken cancellationToken)
    {
        var location = args.Get("location");
        var name = args.Get("name");
        var target = Locate(location, name);
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exists"] = Captures.Bool(target.Exists),
            ["enabled"] = string.Empty,
            ["_raw"] = string.Empty,
            ["_approvedKey"] = target.ApprovedKey ?? string.Empty,
        };
        if (target.Exists && target.ApprovedKey is { } key)
        {
            var raw = Ports.Registry.GetValue(target.ApprovedHive, key, name);
            map["enabled"] = Captures.Bool(IsEnabled(raw));
            map["_raw"] = raw is null ? string.Empty : RegCodec.Raw(raw);
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> ApplyCore(OpArgs args, StateCapture before, CancellationToken cancellationToken)
    {
        var target = Locate(args.Get("location"), args.Get("name"));
        if (target.ApprovedKey is null || !string.Equals(target.ApprovedKey, before.Get("_approvedKey"), StringComparison.OrdinalIgnoreCase))
        {
            throw new PortRefusedException("startup.moved");
        }

        var intended = Intended(args, before);
        Ports.Registry.SetValue(target.ApprovedHive, target.ApprovedKey, args.Get("name"), RegCodec.ParseRaw(intended.Get("_raw"))!);
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> RestoreCore(OpArgs args, StateCapture before, StateCapture current, CancellationToken cancellationToken)
    {
        var location = args.Get("location");
        var key = before.Get("_approvedKey");
        if (key.Length == 0)
        {
            throw new PortRefusedException("undo.no_before_state");
        }

        var hive = location is "HkcuRun" or "StartupFolderUser" ? RegHive.User : RegHive.Machine;
        if (before.Get("_raw").Length == 0)
        {
            Ports.Registry.DeleteValue(hive, key, args.Get("name"));
        }
        else
        {
            Ports.Registry.SetValue(hive, key, args.Get("name"), RegCodec.ParseRaw(before.Get("_raw")) ?? throw new PortRefusedException("undo.no_before_state"));
        }

        return ValueTask.FromResult(false);
    }

    private (bool Exists, RegHive ApprovedHive, string? ApprovedKey, string? TargetImage) Locate(string location, string name)
    {
        switch (location)
        {
            case "HklmRun":
                if (Ports.Registry.GetValue(RegHive.Machine, RunKey, name) is { } v64)
                {
                    return (true, RegHive.Machine, ApprovedRoot + "Run", ImageOf(v64));
                }

                if (Ports.Registry.GetValue(RegHive.Machine, RunKey32, name) is { } v32)
                {
                    return (true, RegHive.Machine, ApprovedRoot + "Run32", ImageOf(v32));
                }

                return (false, RegHive.Machine, null, null);
            case "HkcuRun":
                return Ports.Registry.GetValue(RegHive.User, RunKey, name) is { } u
                    ? (true, RegHive.User, ApprovedRoot + "Run", ImageOf(u))
                    : (false, RegHive.User, null, null);
            case "StartupFolderUser":
                var player = Ctx.Session.Player ?? throw new PortRefusedException("player_unknown");
                var userFile = Path.Combine(player.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup", name);
                return (Ports.Files.Probe(userFile).Exists, RegHive.User, ApprovedRoot + "StartupFolder", null);
            case "StartupFolderCommon":
                var commonFile = Path.Combine(Ports.Files.Folders.ProgramData, "Microsoft", "Windows", "Start Menu", "Programs", "StartUp", name);
                return (Ports.Files.Probe(commonFile).Exists, RegHive.Machine, ApprovedRoot + "StartupFolder", null);
            default:
                return (false, RegHive.Machine, null, null);
        }
    }

    /// <summary>Executable file name of a Run command line (quoted or bare).</summary>
    /// <param name="value">Run value.</param>
    public static string? ImageOf(RegValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Type is not (RegValue.String or RegValue.ExpandString))
        {
            return null;
        }

        var cmd = RegCodec.Display(value).Trim();
        string path;
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            path = end > 1 ? cmd[1..end] : cmd.Trim('"');
        }
        else
        {
            var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe >= 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
