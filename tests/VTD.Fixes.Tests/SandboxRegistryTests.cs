using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using VTD.Contracts.Fixes;
using VTD.Fixes.Interop;
using VTD.Fixes.Ports;
using VTD.Fixes.Tests.Fakes;
using VTD.Testing.Fakes;

namespace VTD.Fixes.Tests;

/// <summary>
/// The REAL registry port (no-follow opens, raw values) under a throw-away key <c>HKCU\Software\VTDiag.Tests\&lt;guid&gt;</c>,
/// where both hives are mapped. This is the only registry these tests write, and the key is deleted afterwards.
/// </summary>
public sealed class SandboxRegistryTests : IDisposable
{
    private readonly string _root = @"Software\VTDiag.Tests\" + Guid.NewGuid().ToString("N");
    private readonly Win32RegistryPort _port;
    private readonly List<string> _links = [];

    public SandboxRegistryTests()
    {
        _port = Win32RegistryPort.ForSandbox(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Registry-backed primitives (args as in <see cref="PrimitiveTests"/>).</summary>
    public static TheoryData<string, string> Cases => new()
    {
        { OpIds.RegistrySetValue, @"path=HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers;name=HwSchMode;kind=DWord;data=1" },
        { OpIds.RegistrySetValue, @"path=HKCU\Control Panel\Mouse;name=MouseSpeed;kind=String;data=0" },
        { OpIds.RegistrySetValue, @"path=HKCU\Software\Microsoft\DirectX\UserGpuPreferences;name=C:\Riot Games\VALORANT\live\VALORANT.exe;kind=String;data=GpuPreference=2;" },
        { OpIds.RegistryDeleteValue, @"path=HKLM\SOFTWARE\Microsoft\Windows\Dwm;name=OverlayTestMode" },
        { OpIds.AppCompatSetLayers, @"exePath=C:\Riot Games\VALORANT\live\VALORANT.exe;hive=HKCU;layers=~ RUNASADMIN" },
        { OpIds.AppCompatRemoveLayers, @"exePath=C:\Riot Games\Riot Client\RiotClientServices.exe;hive=HKCU;layers=WIN7RTM" },
        { OpIds.IfeoResetMitigations, "imageName=VALORANT-Win64-Shipping.exe" },
        { OpIds.IfeoRemovePerfOptions, "imageName=VALORANT-Win64-Shipping.exe" },
        { OpIds.StartupDisable, "location=HkcuRun;name=Discord" },
        { OpIds.StartupDisable, "location=HklmRun;name=Steam32" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Registry_primitives_round_trip_bit_identically_through_the_real_port(string opId, string argText)
    {
        Seed();
        var clock = new FakeClock();
        var system = FakeSystem.Typical(clock);
        var context = new FixSessionContext("01JB6R7X3K9M2N4P5Q6R7S8TB0", "t") { Player = FakePlayer.Default, IsElevated = true };
        var registry = new OperationRegistry(system.Ports with { Registry = _port }, context, clock, FakeFactProbe.ReadyToFix());
        var op = registry.FindBase(opId)!;
        var args = PrimitiveTests.Args(argText);
        Assert.True((await op.CheckPolicyAsync(args, Ct)).Allowed);
        var before = await op.CaptureAsync(args, Ct);
        var dump = Dump();
        var applied = await op.ApplyAsync(args, before, Ct);
        Assert.True(applied.Success, applied.ErrorCode);
        var after = await op.ProbeAsync(args, Ct);
        Assert.True(op.Same(args, after, op.Intended(args, before)));
        Assert.NotEqual(dump, Dump());
        var undone = await op.UndoAsync(args, before, after, UndoMode.IfUnchanged, Ct);
        Assert.True(undone.Success, undone.ErrorCode);
        Assert.Equal(before.Hash, (await op.ProbeAsync(args, Ct)).Hash);
        Assert.Equal(dump, Dump());
    }

    [Fact]
    public void Raw_values_of_every_type_round_trip_exactly()
    {
        RegValue[] values =
        [
            new(RegValue.String, Ops.RegCodec.Encode("String", "çğüİı tr")!.Data),
            new(RegValue.ExpandString, Ops.RegCodec.Encode("ExpandString", "%SystemRoot%\\x")!.Data),
            new(RegValue.DWord, [1, 2, 3, 4]),
            new(RegValue.QWord, [1, 2, 3, 4, 5, 6, 7, 8]),
            new(RegValue.MultiString, Ops.RegCodec.Encode("MultiString", "a\nb\nc")!.Data),
            new(RegValue.Binary, [0, 255, 0, 128]),
            new(RegValue.Binary, []),
            new(RegValue.String, [0x41, 0, 0, 0, 0x42, 0, 0, 0]), // embedded NUL: kept bit for bit
        ];
        for (var i = 0; i < values.Length; i++)
        {
            _port.SetValue(RegHive.User, @"Software\Raw", "v" + i, values[i]);
            var back = _port.GetValue(RegHive.User, @"Software\Raw", "v" + i)!;
            Assert.Equal(values[i].Type, back.Type);
            Assert.Equal(values[i].Data, back.Data);
        }

        Assert.Null(_port.GetValue(RegHive.User, @"Software\Raw", "missing"));
        Assert.Null(_port.GetValue(RegHive.User, @"Software\Nope", "v0"));
        Assert.True(_port.DeleteValue(RegHive.User, @"Software\Raw", "v0"));
        Assert.False(_port.DeleteValue(RegHive.User, @"Software\Raw", "v0"));
        Assert.Contains("v1", _port.GetValueNames(RegHive.User, @"Software\Raw"));
        Assert.Throws<PortRefusedException>(() => _port.SetValue(RegHive.User, @"Software\Raw", "link", new RegValue(6, [1, 0])));
    }

    [Fact]
    public void A_registry_symbolic_link_on_the_path_is_refused()
    {
        _port.SetValue(RegHive.User, @"Target\Inner", "x", new RegValue(RegValue.DWord, [1, 0, 0, 0]));
        CreateLink(_root + @"\HKU\Software\Linked", _root + @"\HKU\Target");
        Assert.Throws<PortRefusedException>(() => _port.GetValue(RegHive.User, @"Software\Linked\Inner", "x"));
        Assert.Throws<PortRefusedException>(() => _port.SetValue(RegHive.User, @"Software\Linked\Inner", "y", new RegValue(RegValue.DWord, [2, 0, 0, 0])));
        Assert.Throws<PortRefusedException>(() => _port.SetValue(RegHive.User, @"Software\Linked", "y", new RegValue(RegValue.DWord, [2, 0, 0, 0])));
        Assert.Null(_port.GetValue(RegHive.User, @"Target", "y"));
        Assert.Null(_port.GetValue(RegHive.User, @"Target\Inner", "y"));
    }

    [Fact]
    public void The_production_port_never_uses_the_engine_HKCU_without_a_player()
    {
        var port = new Win32RegistryPort(() => null);
        Assert.Throws<PortRefusedException>(() => port.GetValue(RegHive.User, @"Control Panel\Mouse", "MouseSpeed"));
        var bad = new Win32RegistryPort(() => FakePlayer.Default with { Sid = "S-1-5-18" });
        Assert.Throws<PortRefusedException>(() => bad.GetValue(RegHive.User, @"Control Panel\Mouse", "MouseSpeed"));
        Assert.Throws<ArgumentException>(() => Win32RegistryPort.ForSandbox(@"Software\Microsoft"));
    }

    public void Dispose()
    {
        foreach (var link in _links)
        {
            DeleteLink(link);
        }

        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\VTDiag.Tests", writable: true);
        if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
        {
            Registry.CurrentUser.DeleteSubKey(@"Software\VTDiag.Tests", throwOnMissingSubKey: false);
        }
    }

    private void Seed()
    {
        var dw = (uint v) => new RegValue(RegValue.DWord, BitConverter.GetBytes(v));
        _port.SetValue(RegHive.Machine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", dw(2));
        _port.SetValue(RegHive.Machine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", dw(5));
        _port.SetValue(RegHive.User, @"Control Panel\Mouse", "MouseSpeed", Ops.RegCodec.Encode("String", "1")!);
        _port.SetValue(RegHive.User, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", @"C:\Riot Games\Riot Client\RiotClientServices.exe", Ops.RegCodec.Encode("String", "~ WIN7RTM HIGHDPIAWARE")!);
        _port.SetValue(RegHive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\VALORANT-Win64-Shipping.exe", "MitigationOptions", new RegValue(RegValue.Binary, [0, 1, 2, 3, 0, 0, 0, 0, 0x10, 0, 0, 0, 0, 0, 0, 0]));
        _port.SetValue(RegHive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\VALORANT-Win64-Shipping.exe", "MitigationAuditOptions", new RegValue(RegValue.QWord, [0, 0, 0, 0, 0, 0, 0, 1]));
        _port.SetValue(RegHive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\VALORANT-Win64-Shipping.exe\PerfOptions", "CpuPriorityClass", dw(3));
        _port.SetValue(RegHive.User, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Discord", Ops.RegCodec.Encode("String", "\"C:\\x\\Update.exe\" --processStart Discord.exe")!);
        _port.SetValue(RegHive.User, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Discord", new RegValue(RegValue.Binary, [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        _port.SetValue(RegHive.Machine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Steam32", Ops.RegCodec.Encode("String", "C:\\Steam\\steam.exe -silent")!);
    }

    private string Dump()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_root);
        return key is null ? string.Empty : DumpKey(key, string.Empty);
    }

    private static string DumpKey(RegistryKey key, string path)
    {
        var parts = new List<string>();
        foreach (var name in key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            var kind = key.GetValueKind(name);
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            parts.Add(path + "|" + name + "=" + kind + ":" + (data is byte[] b ? Convert.ToHexString(b) : data is string[] m ? string.Join("\\0", m) : Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (var sub in key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            // Values only: a set may leave behind an empty key it had to create (documented; the value state is exact).
            using var child = key.OpenSubKey(sub)!;
            parts.Add(DumpKey(child, path + "\\" + sub));
        }

        return string.Join("\n", parts.Where(p => p.Length > 0));
    }

    // ---- test-only: a volatile registry symbolic link inside the sandbox (HKCU → HKCU), removed as a link ----
    private void CreateLink(string linkPath, string targetPath)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var parentPath = linkPath[..linkPath.LastIndexOf('\\')];
        using var parent = Registry.CurrentUser.CreateSubKey(parentPath, writable: true);
        var rc = RegCreateKeyEx(parent.Handle, linkPath[(linkPath.LastIndexOf('\\') + 1)..], 0, null, 0x2 | 0x1, 0x0002 | 0x0020 | 0x00010000, IntPtr.Zero, out var h, out _);
        Assert.Equal(0, rc);
        using (h)
        {
            var target = System.Text.Encoding.Unicode.GetBytes(@"\REGISTRY\USER\" + sid + "\\" + targetPath);
            Assert.Equal(0, RegSetValueEx(h, "SymbolicLinkValue", 0, 6, target, target.Length));
        }

        _links.Add(linkPath);
    }

    private static void DeleteLink(string linkPath)
    {
        if (RegOpenKeyEx(new SafeRegistryHandle(new IntPtr(unchecked((int)0x80000001)), false), linkPath, 0x8, 0x00010000, out var h) == 0)
        {
            using (h)
            {
                _ = NtDeleteKey(h);
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, uint reserved, string? cls, uint options, int sam, IntPtr security, out SafeRegistryHandle result, out uint disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, uint options, int sam, out SafeRegistryHandle result);

    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(SafeRegistryHandle key);
}
