using System.Globalization;
using System.Management;
using VTD.Fixes.Ops;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Interop;

/// <summary>Shared WMI helpers.</summary>
internal static class Wmi
{
    public static long ReturnValue(ManagementBaseObject? result) => result?["ReturnValue"] switch
    {
        uint u => u,
        int i => i,
        ulong ul => (long)ul,
        long l => l,
        bool b => b ? 0 : 1, // BcdStore methods return a boolean: true = success
        _ => -1,
    };

    public static void Require(ManagementBaseObject? result, string what)
    {
        if (ReturnValue(result) != 0)
        {
            throw new IOException(what + " returned " + ReturnValue(result).ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}

/// <summary>Defender path exclusions through MSFT_MpPreference (the engine is elevated; Tamper Protection may still refuse).</summary>
public sealed class MpPreferenceDefenderPort : IDefenderPort
{
    private const string Namespace = @"root\Microsoft\Windows\Defender";

    /// <inheritdoc />
    public bool IsExcluded(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var scope = new ManagementScope(Namespace);
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ExclusionPath FROM MSFT_MpPreference"));
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                var paths = mo["ExclusionPath"] as string[] ?? [];
                if (paths.Any(p => p.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new UnauthorizedAccessException("Defender exclusions cannot be read.");
                }

                return paths.Any(p => string.Equals(Path.TrimEndingDirectorySeparator(p), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase));
            }
        }

        throw new IOException("MSFT_MpPreference returned nothing.");
    }

    /// <inheritdoc />
    public void AddExclusion(string path) => Call("Add", path);

    /// <inheritdoc />
    public void RemoveExclusion(string path) => Call("Remove", path);

    private static void Call(string method, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var scope = new ManagementScope(Namespace);
        scope.Connect();
        using var cls = new ManagementClass(scope, new ManagementPath("MSFT_MpPreference"), null);
        using var input = cls.GetMethodParameters(method);
        input["ExclusionPath"] = new[] { path };
        using var result = cls.InvokeMethod(method, input, null);
        Wmi.Require(result, "MSFT_MpPreference." + method);
    }
}

/// <summary>The allowlisted BCD elements of <c>{current}</c> through WMI BcdStore.</summary>
public sealed class WmiBcdPort : IBcdPort
{
    private const string CurrentBootEntry = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";

    /// <inheritdoc />
    public string? Get(BcdElement element)
    {
        using var obj = Current(out var scope);
        using var input = obj.GetMethodParameters("GetElement");
        input["Type"] = TypeOf(element);
        using var output = obj.InvokeMethod("GetElement", input, null);
        if (Wmi.ReturnValue(output) != 0 || output?["Element"] is not ManagementBaseObject e)
        {
            return null;
        }

        using (e)
        {
            return element switch
            {
                BcdElement.TestSigning or BcdElement.NoIntegrityChecks => e["Boolean"] is bool b ? (b ? "on" : "off") : null,
                BcdElement.Nx => Integer(e) switch { 0 => "OptIn", 1 => "OptOut", 2 => "AlwaysOff", 3 => "AlwaysOn", _ => null },
                _ => Integer(e) switch { 0 => "Off", 1 => "Auto", _ => null },
            };
        }
    }

    /// <inheritdoc />
    public void Set(BcdElement element, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var obj = Current(out _);
        if (element is BcdElement.TestSigning or BcdElement.NoIntegrityChecks)
        {
            using var input = obj.GetMethodParameters("SetBooleanElement");
            input["Type"] = TypeOf(element);
            input["Boolean"] = value switch { "on" => true, "off" => false, _ => throw new ArgumentException("Invalid BCD boolean.", nameof(value)) };
            using var r = obj.InvokeMethod("SetBooleanElement", input, null);
            Wmi.Require(r, "BcdObject.SetBooleanElement");
        }
        else
        {
            ulong n = element == BcdElement.Nx
                ? value switch { "OptIn" => 0UL, "OptOut" => 1UL, "AlwaysOff" => 2UL, "AlwaysOn" => 3UL, _ => throw new ArgumentException("Invalid nx value.", nameof(value)) }
                : value switch { "Off" => 0UL, "Auto" => 1UL, _ => throw new ArgumentException("Invalid hypervisorlaunchtype value.", nameof(value)) };
            using var input = obj.GetMethodParameters("SetIntegerElement");
            input["Type"] = TypeOf(element);
            input["Integer"] = n;
            using var r = obj.InvokeMethod("SetIntegerElement", input, null);
            Wmi.Require(r, "BcdObject.SetIntegerElement");
        }
    }

    /// <inheritdoc />
    public void Delete(BcdElement element)
    {
        using var obj = Current(out _);
        using var input = obj.GetMethodParameters("DeleteElement");
        input["Type"] = TypeOf(element);
        using var r = obj.InvokeMethod("DeleteElement", input, null);
        Wmi.Require(r, "BcdObject.DeleteElement");
    }

    private static uint TypeOf(BcdElement element) => element switch
    {
        BcdElement.TestSigning => 0x16000049,
        BcdElement.NoIntegrityChecks => 0x16000048,
        BcdElement.Nx => 0x25000020,
        BcdElement.HypervisorLaunchType => 0x250000F0,
        _ => throw new ArgumentOutOfRangeException(nameof(element)),
    };

    private static long? Integer(ManagementBaseObject e) => e["Integer"] switch
    {
        ulong u => unchecked((long)u),
        long l => l,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    private static ManagementObject Current(out ManagementScope scope)
    {
        scope = new ManagementScope(@"root\WMI");
        scope.Connect();
        using var storeClass = new ManagementClass(scope, new ManagementPath("BcdStore"), null);
        using var openIn = storeClass.GetMethodParameters("OpenStore");
        openIn["File"] = string.Empty;
        using var openOut = storeClass.InvokeMethod("OpenStore", openIn, null);
        Wmi.Require(openOut, "BcdStore.OpenStore");
        using var store = new ManagementObject(scope, new ManagementPath("BcdStore.FilePath=''"), null);
        using var objIn = store.GetMethodParameters("OpenObject");
        objIn["Id"] = CurrentBootEntry;
        using var objOut = store.InvokeMethod("OpenObject", objIn, null);
        Wmi.Require(objOut, "BcdStore.OpenObject");
        return new ManagementObject(scope, new ManagementPath(string.Create(CultureInfo.InvariantCulture, $"BcdObject.Id=\"{CurrentBootEntry}\",StoreFilePath=\"\"")), null);
    }
}

/// <summary>System Protection: the SPP client list in the registry (read) and SystemRestore.Enable (write).</summary>
public sealed class SystemRestorePort : ISystemProtectionPort
{
    private const string ClientsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    private const string ClientValue = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";
    private readonly IRegistryPort _registry;

    /// <summary>Creates the port.</summary>
    /// <param name="registry">Registry port.</param>
    public SystemRestorePort(IRegistryPort registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public bool IsEnabled(string drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        var v = _registry.GetValue(RegHive.Machine, ClientsKey, ClientValue);
        if (v is null)
        {
            return false;
        }

        var token = "(" + drive.TrimEnd(':').ToUpperInvariant() + "%3A)";
        return RegCodec.Display(v).Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public int? MaxSizePercent()
    {
        try
        {
            var scope = new ManagementScope(@"root\default");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskPercent FROM SystemRestoreConfig"));
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    return mo["DiskPercent"] is uint u ? (int)u : null;
                }
            }
        }
        catch (ManagementException)
        {
            return null;
        }

        return null;
    }

    /// <inheritdoc />
    public void Enable(string drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        var scope = new ManagementScope(@"root\default");
        scope.Connect();
        using var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
        using var input = cls.GetMethodParameters("Enable");
        input["Drive"] = drive.TrimEnd('\\') + "\\";
        using var result = cls.InvokeMethod("Enable", input, null);
        Wmi.Require(result, "SystemRestore.Enable");
    }
}

/// <summary>Windows Firewall rules through INetFwPolicy2 (late-bound COM, HNetCfg.FwPolicy2).</summary>
public sealed class ComFirewallPort : IFirewallPort
{
    private const int DirIn = 1;
    private const int DirOut = 2;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;

    /// <inheritdoc />
    public FirewallRuleInfo? GetRule(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        dynamic policy = Policy();
        foreach (dynamic rule in policy.Rules)
        {
            if (string.Equals((string)rule.Name, name, StringComparison.Ordinal))
            {
                return new FirewallRuleInfo
                {
                    Name = (string)rule.Name,
                    Grouping = (string?)rule.Grouping,
                    Program = (string?)rule.ApplicationName,
                    Direction = (int)rule.Direction == DirOut ? "Out" : "In",
                    Action = (int)rule.Action == ActionAllow ? "Allow" : "Block",
                    Enabled = (bool)rule.Enabled,
                    Profiles = (int)rule.Profiles,
                    Protocol = (int)rule.Protocol,
                };
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void AddRule(FirewallRuleInfo rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var type = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!;
        dynamic r = Activator.CreateInstance(type)!;
        r.Name = rule.Name;
        r.Grouping = rule.Grouping ?? string.Empty;
        r.ApplicationName = rule.Program ?? string.Empty;
        r.Protocol = rule.Protocol;
        r.Direction = string.Equals(rule.Direction, "Out", StringComparison.Ordinal) ? DirOut : DirIn;
        r.Action = string.Equals(rule.Action, "Allow", StringComparison.Ordinal) ? ActionAllow : ActionBlock;
        r.Profiles = rule.Profiles;
        r.Enabled = rule.Enabled;
        dynamic policy = Policy();
        policy.Rules.Add(r);
    }

    /// <inheritdoc />
    public void RemoveRule(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        dynamic policy = Policy();
        policy.Rules.Remove(name);
    }

    private static object Policy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }
}
