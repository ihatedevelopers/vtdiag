using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using VTD.Fixes.Interop;

namespace VTD.Fixes.IntegrationTests;

/// <summary>The KM-TEST Loopback Adapter the machine fixture installed (never the default-route NIC).</summary>
/// <param name="InstanceId">PnP instance id.</param>
/// <param name="DriverKey">Driver key (<c>{4d36e972-…}\NNNN</c>).</param>
/// <param name="InterfaceGuid">NetCfgInstanceId.</param>
internal sealed record LoopbackAdapter(string InstanceId, string DriverKey, Guid InterfaceGuid)
{
    /// <summary>The driver key path under HKLM.</summary>
    public string ClassKeyPath => @"SYSTEM\CurrentControlSet\Control\Class\" + DriverKey;
}

/// <summary>
/// Machine-wide arrangements shared by all rows of <see cref="ApplyUndoTests"/>: the loopback adapter (installed lazily,
/// only after <see cref="Gate.RequireSystemWrites"/> passed, removed on dispose) and the VTDiag data root (deleted on
/// dispose when the tests created it). The constructor touches nothing, so a skipped run changes nothing.
/// </summary>
public sealed class MachineFixture : IDisposable
{
    private const string LoopbackHardwareId = "*MSLOOP";
    private LoopbackAdapter? _loopback;
    private string? _loopbackSkip;
    private bool? _dataRootExisted;

    /// <summary>The data root the runner, the quarantine and the journal use.</summary>
    public static string DataRoot => RealSystemPorts.DefaultDataRoot();

    /// <summary>Remembers whether the data root existed before the first write test.</summary>
    internal void NoteDataRoot() => _dataRootExisted ??= Directory.Exists(DataRoot);

    /// <summary>The loopback adapter, installed on first use; skips the row when the machine cannot have one.</summary>
    /// <param name="log">Log.</param>
    internal LoopbackAdapter Loopback(Action<string> log)
    {
        if (_loopbackSkip is not null)
        {
            Assert.Skip(_loopbackSkip);
        }

        if (_loopback is not null)
        {
            return _loopback;
        }

        var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "netloop.inf");
        if (!File.Exists(inf))
        {
            _loopbackSkip = "netloop.inf (KM-TEST Loopback Adapter) is not on this machine: no safe adapter to act on, and the default-route NIC is never touched.";
            Assert.Skip(_loopbackSkip);
        }

        _loopback = Install(inf, log);
        return _loopback;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_loopback is not null)
        {
            try
            {
                Remove(_loopback.InstanceId);
            }
            catch (Win32Exception)
            {
                // Best effort on a throwaway runner; the log of the row that installed it has the id.
            }

            _loopback = null;
        }

        if (_dataRootExisted == false && Directory.Exists(DataRoot))
        {
            try
            {
                Directory.Delete(DataRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static LoopbackAdapter Install(string inf, Action<string> log)
    {
        var className = new char[256];
        if (!TestNative.SetupDiGetInfClass(inf, out var classGuid, className, className.Length, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetINFClass(netloop.inf)");
        }

        var set = TestNative.SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == TestNative.InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfoList");
        }

        string instanceId;
        try
        {
            var data = new TestNative.SpDevinfoData { CbSize = Marshal.SizeOf<TestNative.SpDevinfoData>() };
            var name = new string(className, 0, Array.IndexOf(className, '\0') is var n and >= 0 ? n : className.Length);
            if (!TestNative.SetupDiCreateDeviceInfo(set, name, ref classGuid, null, IntPtr.Zero, TestNative.DicdGenerateId, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfo");
            }

            var hwid = Encoding.Unicode.GetBytes(LoopbackHardwareId + "\0\0");
            if (!TestNative.SetupDiSetDeviceRegistryProperty(set, ref data, TestNative.SpdrpHardwareId, hwid, hwid.Length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiSetDeviceRegistryProperty(HardwareID)");
            }

            if (!TestNative.SetupDiCallClassInstaller(TestNative.DifRegisterDevice, set, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DIF_REGISTERDEVICE");
            }

            var id = new char[512];
            if (!TestNative.SetupDiGetDeviceInstanceId(set, ref data, id, id.Length, out var len))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInstanceId");
            }

            instanceId = new string(id, 0, Math.Max(0, len - 1));
        }
        finally
        {
            _ = TestNative.SetupDiDestroyDeviceInfoList(set);
        }

        log("loopback: registered " + instanceId + ", installing netloop.inf");
        if (!TestNative.UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, LoopbackHardwareId, inf, TestNative.InstallFlagForce, out _))
        {
            var err = Marshal.GetLastWin32Error();
            Remove(instanceId);
            throw new Win32Exception(err, "UpdateDriverForPlugAndPlayDevices(*MSLOOP)");
        }

        // The driver key, the NetCfgInstanceId and the ms_tcpip6 binding appear asynchronously.
        var clock = TimeProvider.System;
        var deadline = clock.GetUtcNow().AddSeconds(90);
        while (true)
        {
            var driver = DriverKeyOf(instanceId);
            if (driver is not null
                && Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver) is { } k)
            {
                using (k)
                {
                    if (k.GetValue("NetCfgInstanceId") is string g && Guid.TryParse(g, out var guid) && State.Ipv6Binding(guid) != State.Absent)
                    {
                        log("loopback: " + instanceId + " driver " + driver + " interface " + guid.ToString("B", CultureInfo.InvariantCulture));
                        return new LoopbackAdapter(instanceId, driver, guid);
                    }
                }
            }

            if (clock.GetUtcNow() > deadline)
            {
                Remove(instanceId);
                throw new TimeoutException("The loopback adapter " + instanceId + " never got a driver key, interface GUID and ms_tcpip6 binding.");
            }

            Thread.Sleep(1000);
        }
    }

    private static string? DriverKeyOf(string instanceId)
    {
        var classGuid = new Guid(VTD.Fixes.Policy.ProtectedTargetPolicy.NetClass);
        var set = TestNative.SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == TestNative.InvalidHandle)
        {
            return null;
        }

        try
        {
            var data = new TestNative.SpDevinfoData { CbSize = Marshal.SizeOf<TestNative.SpDevinfoData>() };
            if (!TestNative.SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref data))
            {
                return null;
            }

            var buffer = new byte[1024];
            if (!TestNative.SetupDiGetDeviceRegistryProperty(set, ref data, TestNative.SpdrpDriver, out _, buffer, buffer.Length, out var len) || len < 2)
            {
                return null;
            }

            return Encoding.Unicode.GetString(buffer, 0, len).TrimEnd('\0');
        }
        finally
        {
            _ = TestNative.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static void Remove(string instanceId)
    {
        var classGuid = new Guid(VTD.Fixes.Policy.ProtectedTargetPolicy.NetClass);
        var set = TestNative.SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == TestNative.InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfoList");
        }

        try
        {
            var data = new TestNative.SpDevinfoData { CbSize = Marshal.SizeOf<TestNative.SpDevinfoData>() };
            if (!TestNative.SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref data))
            {
                return;
            }

            if (!TestNative.SetupDiCallClassInstaller(TestNative.DifRemove, set, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DIF_REMOVE " + instanceId);
            }
        }
        finally
        {
            _ = TestNative.SetupDiDestroyDeviceInfoList(set);
        }
    }
}

/// <summary>
/// Independent read-backs of real system state (registry through Microsoft.Win32, devnodes through cfgmgr32, power through
/// powrprof, IPv6 bindings through WMI, files by SHA-256) as invariant strings, so "restored exactly" is a string compare.
/// </summary>
internal static class State
{
    /// <summary>The value for anything that does not exist.</summary>
    public const string Absent = "<absent>";

    /// <summary>A registry value as <c>Kind:data</c> (DWORD/QWORD unsigned decimal, binary lower hex, multi-string joined by \n).</summary>
    /// <param name="root">Hive.</param>
    /// <param name="subKey">Key.</param>
    /// <param name="name">Value name.</param>
    public static string Reg(RegistryKey root, string subKey, string name)
    {
        using var key = root.OpenSubKey(subKey, writable: false);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
        {
            return Absent;
        }

        var data = value switch
        {
            int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
            long l => unchecked((ulong)l).ToString(CultureInfo.InvariantCulture),
            byte[] b => Convert.ToHexStringLower(b),
            string[] s => string.Join('\n', s),
            string s => s,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        return key.GetValueKind(name).ToString() + ":" + data;
    }

    /// <summary>A service's start configuration from its registry key.</summary>
    /// <param name="name">Service name.</param>
    public static string Service(string name)
    {
        var path = @"SYSTEM\CurrentControlSet\Services\" + name;
        using var key = Registry.LocalMachine.OpenSubKey(path);
        if (key is null)
        {
            return Absent;
        }

        // The SCM writes "not delayed" as DelayedAutostart=0 once the start type is changed; to Windows that is the same
        // as no value, so compare what the service does, not how the SCM spells it.
        var delayed = Reg(Registry.LocalMachine, path, "DelayedAutostart");
        return "Start=" + Reg(Registry.LocalMachine, path, "Start") + ";Delayed=" + (delayed == "DWord:0" ? Absent : delayed);
    }

    /// <summary><c>problem=N</c> of a present devnode (0 = no problem, 22 = disabled).</summary>
    /// <param name="instanceId">Instance id.</param>
    public static string Device(string instanceId)
    {
        if (TestNative.CmLocateDevNode(out var inst, instanceId, 0) != 0)
        {
            return Absent;
        }

        if (TestNative.CmGetDevNodeStatus(out var status, out var problem, inst, 0) != 0)
        {
            return "status-unreadable";
        }

        return "problem=" + ((status & TestNative.DnHasProblem) != 0 ? problem : 0).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Re-enables a devnode left disabled by an earlier failed row.</summary>
    /// <param name="instanceId">Instance id.</param>
    public static void EnableDevice(string instanceId)
    {
        if (TestNative.CmLocateDevNode(out var inst, instanceId, 0) == 0)
        {
            _ = TestNative.CmEnableDevNode(inst, 0);
        }
    }

    /// <summary><c>enabled=true|false</c> of the ms_tcpip6 binding of an interface.</summary>
    /// <param name="interfaceGuid">Interface GUID.</param>
    public static string Ipv6Binding(Guid interfaceGuid)
    {
        var instance = "{" + interfaceGuid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "}::ms_tcpip6";
        var scope = new ManagementScope(@"root\StandardCimv2");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT InstanceID, Enabled FROM MSFT_NetAdapterBindingSettingData WHERE ComponentID = 'ms_tcpip6'"));
        using var results = searcher.Get();
        var found = Absent;
        foreach (var mo in results)
        {
            try
            {
                if (string.Equals(mo["InstanceID"] as string, instance, StringComparison.OrdinalIgnoreCase) && mo["Enabled"] is bool b)
                {
                    found = "enabled=" + (b ? "true" : "false");
                }
            }
            finally
            {
                mo.Dispose();
            }
        }

        return found;
    }

    /// <summary>The active power scheme.</summary>
    public static Guid ActiveScheme()
    {
        var rc = TestNative.PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc, "PowerGetActiveScheme");
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            _ = TestNative.LocalFree(ptr);
        }
    }

    /// <summary>Activates a scheme.</summary>
    /// <param name="scheme">Scheme.</param>
    public static void SetActiveScheme(Guid scheme)
    {
        var rc = TestNative.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc, "PowerSetActiveScheme " + scheme.ToString("D", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>An AC or DC value index, or null when the setting is not readable.</summary>
    /// <param name="scheme">Scheme.</param>
    /// <param name="subgroup">Subgroup.</param>
    /// <param name="setting">Setting.</param>
    /// <param name="ac">AC (else DC).</param>
    public static uint? PowerValue(Guid scheme, Guid subgroup, Guid setting, bool ac)
    {
        var rc = ac
            ? TestNative.PowerReadAcValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var v)
            : TestNative.PowerReadDcValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out v);
        return rc == 0 ? v : null;
    }

    /// <summary>Writes an AC or DC value index and re-applies the scheme when it is active.</summary>
    /// <param name="scheme">Scheme.</param>
    /// <param name="subgroup">Subgroup.</param>
    /// <param name="setting">Setting.</param>
    /// <param name="ac">AC (else DC).</param>
    /// <param name="value">Value.</param>
    public static void WritePowerValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value)
    {
        var rc = ac
            ? TestNative.PowerWriteAcValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
            : TestNative.PowerWriteDcValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc, "PowerWrite" + (ac ? "AC" : "DC") + "ValueIndex");
        }

        if (ActiveScheme() == scheme)
        {
            SetActiveScheme(scheme);
        }
    }

    /// <summary><c>sha256:&lt;hex&gt;</c> of a file, or <see cref="Absent"/>.</summary>
    /// <param name="path">Path.</param>
    public static string File(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return Absent;
        }

        using var stream = System.IO.File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Invariant bool word.</summary>
    /// <param name="value">Value.</param>
    public static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>
/// One row's arrangements on the real machine. Every change is registered with an undo BEFORE it is made, and
/// <see cref="Cleanup"/> runs them in reverse order (also after a failure), so the runner is left as it was found.
/// </summary>
internal sealed class Lab
{
    private readonly List<(string What, Action Undo)> _cleanups = [];

    /// <summary>Creates the lab.</summary>
    /// <param name="machine">Machine fixture.</param>
    /// <param name="session">Session context of the row's runner.</param>
    /// <param name="probe">Live probe of the row's runner.</param>
    /// <param name="log">Log.</param>
    public Lab(MachineFixture machine, FixSessionContext session, VTD.Testing.Fakes.FakeFactProbe probe, Action<string> log)
    {
        Machine = machine;
        Session = session;
        Probe = probe;
        Log = log;
    }

    /// <summary>Machine fixture.</summary>
    public MachineFixture Machine { get; }

    /// <summary>Session context (vgc gate).</summary>
    public FixSessionContext Session { get; }

    /// <summary>Live probe (clock skew).</summary>
    public VTD.Testing.Fakes.FakeFactProbe Probe { get; }

    /// <summary>Log.</summary>
    public Action<string> Log { get; }

    /// <summary>The loopback adapter, enabled.</summary>
    public LoopbackAdapter Loopback()
    {
        var lb = Machine.Loopback(Log);
        if (State.Device(lb.InstanceId) != "problem=0")
        {
            State.EnableDevice(lb.InstanceId);
        }

        return lb;
    }

    /// <summary>Registers an undo (run in reverse order by <see cref="Cleanup"/>).</summary>
    /// <param name="what">Description.</param>
    /// <param name="undo">Undo.</param>
    public void Defer(string what, Action undo) => _cleanups.Add((what, undo));

    /// <summary>Runs every undo, newest first; returns the ones that failed.</summary>
    public IReadOnlyList<string> Cleanup()
    {
        var failures = new List<string>();
        for (var i = _cleanups.Count - 1; i >= 0; i--)
        {
            var (what, undo) = _cleanups[i];
            try
            {
                undo();
                Log("cleanup: " + what);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(what + ": " + ex.GetType().Name + ": " + ex.Message);
                Log("cleanup FAILED: " + what + ": " + ex.Message);
            }
        }

        _cleanups.Clear();
        return failures;
    }

    // ------------------------------------------------------------------ registry

    /// <summary>Registers the restore of one value (or the deletion of the keys that did not exist yet).</summary>
    /// <param name="root">Hive.</param>
    /// <param name="subKey">Key.</param>
    /// <param name="name">Value name.</param>
    public void PreserveValue(RegistryKey root, string subKey, string name)
    {
        if (FirstMissingKey(root, subKey) is { } missing)
        {
            Defer("delete key " + root.Name + "\\" + missing, () => root.DeleteSubKeyTree(missing, throwOnMissingSubKey: false));
            return;
        }

        using var key = root.OpenSubKey(subKey)!;
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = value is null ? RegistryValueKind.Unknown : key.GetValueKind(name);
        Defer("restore " + root.Name + "\\" + subKey + "\\" + name + " = " + (value is null ? State.Absent : kind.ToString()), () =>
        {
            using var w = root.OpenSubKey(subKey, writable: true);
            if (w is null)
            {
                return;
            }

            if (value is null)
            {
                w.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                w.SetValue(name, value, kind);
            }
        });
    }

    /// <summary>Registers the deletion of a key when it does not exist yet (or of its first missing ancestor).</summary>
    /// <param name="root">Hive.</param>
    /// <param name="subKey">Key.</param>
    public void PreserveKey(RegistryKey root, string subKey)
    {
        if (FirstMissingKey(root, subKey) is { } missing)
        {
            Defer("delete key " + root.Name + "\\" + missing, () => root.DeleteSubKeyTree(missing, throwOnMissingSubKey: false));
        }
    }

    /// <summary>Preserves, then sets a value.</summary>
    /// <param name="root">Hive.</param>
    /// <param name="subKey">Key.</param>
    /// <param name="name">Value name.</param>
    /// <param name="value">Value.</param>
    /// <param name="kind">Kind.</param>
    public void Seed(RegistryKey root, string subKey, string name, object value, RegistryValueKind kind)
    {
        PreserveValue(root, subKey, name);
        using var key = root.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, kind);
    }

    /// <summary>Preserves, then removes a value.</summary>
    /// <param name="root">Hive.</param>
    /// <param name="subKey">Key.</param>
    /// <param name="name">Value name.</param>
    public void SeedAbsent(RegistryKey root, string subKey, string name)
    {
        PreserveValue(root, subKey, name);
        using var key = root.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static string? FirstMissingKey(RegistryKey root, string subKey)
    {
        var path = string.Empty;
        foreach (var part in subKey.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? part : path + "\\" + part;
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                return path;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ services

    /// <summary>Creates a demand-start, own-process service whose binary does not exist (it can be configured, never run).</summary>
    /// <param name="name">Service name.</param>
    public void CreateDummyService(string name)
    {
        if (State.Service(name) != State.Absent)
        {
            Assert.Skip("A service named '" + name + "' already exists on this machine; the test only acts on a dummy it created.");
        }

        Defer("delete service " + name, () => Run("sc.exe", "delete", name));
        Run("sc.exe", "create", name, "binPath=", @"C:\VTDiagIT\does-not-exist\vtdiag-it-dummy.exe", "start=", "demand", "type=", "own", "DisplayName=", "VTDiag integration test dummy (" + name + ")");
    }

    /// <summary>Runs a System32 tool and returns its output; throws on a non-zero exit code.</summary>
    /// <param name="exe">Tool.</param>
    /// <param name="args">Arguments.</param>
    public string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException(exe + " did not start.");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000))
        {
            p.Kill();
            throw new TimeoutException(exe + " did not finish.");
        }

        Log("run: " + exe + " " + string.Join(' ', args) + " -> " + p.ExitCode.ToString(CultureInfo.InvariantCulture));
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(exe + " " + string.Join(' ', args) + " exited " + p.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + output.Trim());
        }

        return output;
    }

    // ------------------------------------------------------------------ files

    /// <summary>Creates a file owned by Administrators (the quarantine refuses untrusted owners), with its folders.</summary>
    /// <param name="path">Full path.</param>
    /// <param name="content">Content.</param>
    /// <param name="lastWriteUtc">Last write time to set.</param>
    public void CreateAdminFile(string path, byte[] content, DateTime? lastWriteUtc)
    {
        if (System.IO.File.Exists(path))
        {
            Assert.Skip(path + " already exists on this machine; the test only acts on a file it created.");
        }

        EnsureDirectory(Path.GetDirectoryName(path)!);
        Defer("delete " + path, () =>
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        });
        System.IO.File.WriteAllBytes(path, content);
        var info = new FileInfo(path);
        var security = info.GetAccessControl(AccessControlSections.Owner);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        info.SetAccessControl(security);
        if (lastWriteUtc is { } t)
        {
            System.IO.File.SetLastWriteTimeUtc(path, t);
        }
    }

    /// <summary>Creates a folder chain (owned by Administrators) and registers the removal of what was created.</summary>
    /// <param name="directory">Folder.</param>
    public void EnsureDirectory(string directory)
    {
        var missing = new List<string>();
        for (var d = new DirectoryInfo(directory); d is not null && !d.Exists; d = d.Parent)
        {
            missing.Insert(0, d.FullName);
        }

        if (missing.Count == 0)
        {
            return;
        }

        var top = missing[0];
        Defer("delete folder " + top, () =>
        {
            if (Directory.Exists(top))
            {
                Directory.Delete(top, recursive: true);
            }
        });
        foreach (var d in missing)
        {
            var info = Directory.CreateDirectory(d);
            var security = info.GetAccessControl(AccessControlSections.Owner);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            info.SetAccessControl(security);
        }
    }

    /// <summary>Registers the removal of a folder this row's product code will create.</summary>
    /// <param name="directory">Folder.</param>
    public void DeleteLater(string directory) => Defer("delete folder " + directory, () =>
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    // ------------------------------------------------------------------ processes

    /// <summary>
    /// Starts a harmless windowed process with the given image name (a copy of notepad.exe and its MUI in a temp folder),
    /// in this session; skips when that session is not the active console session (the primitive only closes there).
    /// </summary>
    /// <param name="imageName">Image name.</param>
    public Process StartWindowedDummy(string imageName)
    {
        using (var self = Process.GetCurrentProcess())
        {
            var console = TestNative.WtsGetActiveConsoleSessionId();
            if ((uint)self.SessionId != console)
            {
                Assert.Skip("The test runs in session " + self.SessionId.ToString(CultureInfo.InvariantCulture) + " but the active console session is "
                    + console.ToString(CultureInfo.InvariantCulture) + ": process.closeGraceful only closes console-session processes, so no valid target can be started here.");
            }
        }

        var source = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!System.IO.File.Exists(source))
        {
            Assert.Skip("notepad.exe is not on this machine: no harmless windowed program to start.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "vtdiag-it", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        DeleteLater(dir);
        var exe = Path.Combine(dir, imageName);
        System.IO.File.Copy(source, exe);
        foreach (var lang in new[] { CultureInfo.InstalledUICulture.Name, "en-US" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mui = Path.Combine(Environment.SystemDirectory, lang, "notepad.exe.mui");
            if (lang.Length > 0 && System.IO.File.Exists(mui))
            {
                Directory.CreateDirectory(Path.Combine(dir, lang));
                System.IO.File.Copy(mui, Path.Combine(dir, lang, imageName + ".mui"), overwrite: true);
            }
        }

        var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir })
            ?? throw new InvalidOperationException(imageName + " did not start.");
        Defer("stop " + imageName, () =>
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(10_000);
            }

            process.Dispose();
        });
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(20);
        while (true)
        {
            process.Refresh();
            if (process.HasExited)
            {
                Assert.Skip("The notepad copy '" + imageName + "' exited at once (exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "): no windowed target on this machine.");
            }

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                Log("started " + imageName + " pid " + process.Id.ToString(CultureInfo.InvariantCulture) + " with a window");
                return process;
            }

            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                Assert.Skip("The notepad copy '" + imageName + "' never showed a window (no interactive desktop?): no windowed target on this machine.");
            }

            Thread.Sleep(250);
        }
    }
}
