using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;
using VTD.Fixes.Interop;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;
using VTD.Fixes.Runner;
using VTD.Knowledge;
using VTD.Testing.Fakes;

namespace VTD.Fixes.IntegrationTests;

/// <summary>
/// READ-ONLY checks of the real Win32 ports on whatever machine runs the tests (no gate: nothing here writes). Queries
/// only: service config, device tree, routes, power scheme, process snapshot, registry values, file metadata. Then every
/// KB fix is PREVIEWED on the real machine (policy + capture are reads); nothing is ever applied.
/// </summary>
public sealed class ReadOnlyPortTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Services_devices_power_processes_and_registry_read_through_the_real_ports()
    {
        var clock = TimeProvider.System;
        var services = new Win32ServicePort(clock);
        Assert.NotNull(services.Query("Dnscache"));
        Assert.True(services.Query("Schedule")?.IsSharedProcess);
        Assert.Null(services.Query("VTDiag_NoSuchService_" + Guid.NewGuid().ToString("N")));

        var devices = new CfgMgrDevicePort();
        var all = devices.EnumeratePresent();
        Assert.NotEmpty(all);
        Assert.Contains(all, d => d.ClassGuid is not null && d.ParentInstanceId is not null);
        var first = devices.Query(all[0].InstanceId);
        Assert.NotNull(first);
        Assert.True(first.Present);

        var power = new PowrProfPowerPort();
        var active = power.GetActiveScheme();
        Assert.Contains(active, power.EnumerateSchemes());
        Assert.NotNull(power.ReadValue(active, new Guid("54533251-82be-4824-96c1-47b60b740d00"), new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), ac: true));

        var processes = new ToolHelpProcessPort().Snapshot();
        Assert.Contains(processes, p => p.ProcessId == Environment.ProcessId);

        var boot = new TickBootClock(clock).LastBootUtc()!.Value;
        Assert.InRange(boot, clock.GetUtcNow().AddDays(-400), clock.GetUtcNow());

        var registry = new Win32RegistryPort(Gate.CurrentPlayer);
        Assert.NotNull(registry.GetValue(RegHive.Machine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild"));
        Assert.NotEmpty(registry.GetSubKeyNames(RegHive.User, "Software"));
        Assert.Null(registry.GetValue(RegHive.Machine, @"SOFTWARE\VTDiag.NoSuchKey." + Guid.NewGuid().ToString("N"), "x"));

        var files = new ReparseSafeFilePort(ReparseSafeFilePort.SystemFolders(), RealSystemPorts.DefaultDataRoot(), adminOnly: true);
        Assert.True(files.DirectoryExists(files.Folders.Windows));
        Assert.True(files.Probe(Path.Combine(files.Folders.Windows, "explorer.exe")).Exists);
        try
        {
            _ = new SystemRestorePort(registry).IsEnabled(files.Folders.SystemDrive);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The System Protection key is readable only elevated; the engine runs elevated when it matters.
        }

        Assert.Null(new ComFirewallPort().GetRule("VTDiag NoSuchRule " + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void The_default_route_adapter_of_this_PC_is_protected()
    {
        var registry = new Win32RegistryPort(Gate.CurrentPlayer);
        var devices = new CfgMgrDevicePort();
        var network = new NetworkPort(devices, registry);
        var nics = devices.EnumeratePresent().Where(d => string.Equals(d.ClassGuid, ProtectedTargetPolicy.NetClass, StringComparison.OrdinalIgnoreCase)).ToList();
        var routed = nics.Where(n => network.InterfaceGuidOf(n.InstanceId) is { } g && network.CarriesDefaultRoute(g) == Tri.True).ToList();
        if (routed.Count == 0)
        {
            Assert.Skip("this machine has no default route");
        }

        var ports = Real();
        var policy = new ProtectedTargetPolicy(ports, Context());
        foreach (var nic in routed)
        {
            var decision = policy.CheckDeviceDisable(nic.InstanceId);
            Assert.False(decision.Allowed);
            Assert.Equal("protected.default_route_nic", decision.RuleId);
            Assert.NotNull(network.GetIpv4Metric(network.InterfaceGuidOf(nic.InstanceId)!.Value));
        }

        // Display adapters and disk controllers of this PC are protected too.
        foreach (var d in devices.EnumeratePresent().Where(d => d.ClassGuid is "{4d36e968-e325-11ce-bfc1-08002be10318}" or "{4d36e97b-e325-11ce-bfc1-08002be10318}" or "{4d36e96a-e325-11ce-bfc1-08002be10318}"))
        {
            Assert.False(policy.CheckDeviceDisable(d.InstanceId).Allowed, d.InstanceId);
        }
    }

    [Fact]
    public async Task Every_KB_fix_previews_on_this_PC_without_changing_anything()
    {
        var kb = LoadKb();
        var ports = Real();
        var context = Context();
        var clock = TimeProvider.System;
        var probe = FakeFactProbe.ReadyToFix();
        var registry = new OperationRegistry(ports, context, clock, probe);
        using var runner = new FixRunner(kb, registry, new InMemoryJournal(), probe, new FakeRestorePointService(clock), context, clock, new UlidGenerator(clock));
        var nic = new CfgMgrDevicePort().EnumeratePresent().FirstOrDefault(d => string.Equals(d.ClassGuid, ProtectedTargetPolicy.NetClass, StringComparison.OrdinalIgnoreCase));
        foreach (var fix in kb.Fixes)
        {
            var binding = new FixBinding
            {
                Entities = fix.Bind.ToDictionary(b => b.Key, b => (IReadOnlyList<EntityRef>)[Entity(b.Value.Kind, fix.Id, nic?.InstanceId)], StringComparer.Ordinal),
            };
            var preview = await runner.PreviewAsync(fix.Id, binding, new Proposer { Origin = EventOrigin.Ui }, Ct);
            Assert.NotNull(preview.PreviewHash);
            Assert.DoesNotContain(preview.BlockReasonKeys, k => k.StartsWith("args.", StringComparison.Ordinal) || k.StartsWith("op.", StringComparison.Ordinal) || k.StartsWith("binding.", StringComparison.Ordinal));
            TestContext.Current.TestOutputHelper?.WriteLine(fix.Id + ": " + (preview.CanApply ? "would apply" : string.Join(", ", preview.BlockReasonKeys)));
        }

        Assert.Empty(await ToList(runner.ListAttemptsAsync(Ct)));
    }

    private static EntityRef Entity(EntityKind kind, string fixId, string? nic) => kind switch
    {
        EntityKind.Adapter => new EntityRef { Kind = kind, Id = nic ?? @"PCI\VEN_0000&DEV_0000\0" },
        EntityKind.Service => new EntityRef { Kind = kind, Id = fixId == "fix.vg.vgc_automatic" ? "vgc" : "VTDiagNoSuchService" },
        EntityKind.Process => new EntityRef { Kind = kind, Id = fixId.StartsWith("fix.sec.", StringComparison.Ordinal) ? RiotProcessNames.Game : "VTDiagNoSuchProcess.exe" },
        EntityKind.File when fixId == "fix.vg.quarantine_stale_dat" => new EntityRef { Kind = kind, Id = @"%SystemRoot%\392667600.dat" },
        EntityKind.File when fixId == "fix.vg.quarantine_bootstatus" => new EntityRef { Kind = kind, Id = @"%ProgramFiles%\Riot Vanguard\vgkbootstatus.dat" },
        EntityKind.File => new EntityRef { Kind = kind, Id = @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe" },
        EntityKind.Device => new EntityRef { Kind = kind, Id = @"BTHHFENUM\VTDIAG\NOSUCH" },
        EntityKind.StartupItem => new EntityRef { Kind = kind, Id = (fixId.EndsWith("hkcu", StringComparison.Ordinal) ? "HkcuRun" : "HklmRun") + "|VTDiagNoSuchEntry" },
        EntityKind.CompatLayer => new EntityRef { Kind = kind, Id = @"C:\Riot Games\VALORANT\live\VALORANT.exe", DisplayName = "WIN7RTM" },
        _ => new EntityRef { Kind = kind, Id = "x" },
    };

    private static SystemPorts Real() => RealSystemPorts.Create(Context(), RealSystemPorts.DefaultDataRoot(), TimeProvider.System);

    private static FixSessionContext Context() => new("01JB6R7X3K9M2N4P5Q6R7S8TB0", "0.2.0-it") { Player = Gate.CurrentPlayer(), IsElevated = Gate.Elevated };

    private static IKnowledgeBase LoadKb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VTDiag.sln")))
        {
            dir = dir.Parent;
        }

        var result = new KnowledgeBaseLoader([]).LoadAsync(new KbLoadOptions
        {
            BundledPath = Path.Combine(dir!.FullName, "kb"),
            NowUtc = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
            AppVersion = "0.2.0",
        }, CancellationToken.None).GetAwaiter().GetResult();
        return result.Kb;
    }

    private static async Task<List<T>> ToList<T>(Task<IReadOnlyList<T>> task) => [.. await task];
}
