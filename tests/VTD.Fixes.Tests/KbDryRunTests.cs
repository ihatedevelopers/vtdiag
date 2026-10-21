using System.Globalization;
using VTD.Analysis;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Fixes.Ports;
using VTD.Fixes.Runner;
using VTD.Fixes.Tests.Fakes;
using VTD.Knowledge;
using VTD.Testing.Fakes;

namespace VTD.Fixes.Tests;

/// <summary>
/// Every <c>kb/fixes/*.json</c> of the bundled KB resolves to registered primitives with valid arguments and produces a
/// dry-run preview against a fake PC seeded from the REAL dev-PC snapshot (<c>tests/fixtures/snapshots/real/dev-pc.json</c>).
/// Nothing is applied. Bindings come from the analyzer's proposals on that snapshot, or from the snapshot's own entities
/// when the dev PC does not have the problem.
/// </summary>
public sealed class KbDryRunTests
{
    private static readonly string Root = FindRoot();
    private static readonly Lazy<IKnowledgeBase> Bundled = new(LoadKb);
    private static readonly Lazy<ScanSnapshot> DevPc = new(() => VtdJson.Deserialize<ScanSnapshot>(File.ReadAllText(Path.Combine(Root, "tests", "fixtures", "snapshots", "real", "dev-pc.json"))));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> FixIds => [.. KbFixFiles()];

    private static IEnumerable<string> KbFixFiles()
        => Directory.EnumerateFiles(Path.Combine(Root, "kb", "fixes"), "*.json", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!;

    [Fact]
    public void The_bundled_KB_has_all_32_fixes()
    {
        var ids = KbFixFiles().ToList();
        Assert.Equal(32, ids.Count);
        Assert.Equal(32, Bundled.Value.Fixes.Count);
        foreach (var id in ids)
        {
            Assert.NotNull(Bundled.Value.FindFix(id));
        }
    }

    [Theory]
    [MemberData(nameof(FixIds))]
    public async Task Every_KB_fix_plans_and_previews_on_the_dev_PC(string fixId)
    {
        Assert.SkipUnless(File.Exists(Path.Combine(Root, "tests", "fixtures", "snapshots", "real", "dev-pc.json")), "Private dev-PC snapshot not present (excluded from the public repo).");
        var kb = Bundled.Value;
        var fix = kb.FindFix(fixId)!;
        var snapshot = DevPc.Value;
        var clock = new FakeClock(snapshot.Meta.CreatedUtc.AddMinutes(10));
        var system = Seed(snapshot, clock);
        var context = new FixSessionContext("01JB6R7X3K9M2N4P5Q6R7S8TB0", "0.2.0") { Player = FakePlayer.Default, IsElevated = true };
        var probe = FakeFactProbe.ReadyToFix();
        var registry = new OperationRegistry(system.Ports, context, clock, probe);
        using var runner = new FixRunner(kb, registry, new InMemoryJournal(), probe, new FakeRestorePointService(clock), context, clock, new SequentialIdGenerator(clock));

        var binding = BindingFor(fix, snapshot, Analysis(kb, snapshot));
        var plan = FixPlanner.Plan(fix, binding, registry);
        Assert.True(plan.Errors.Count == 0, fixId + " plan errors: " + string.Join(", ", plan.Errors));
        Assert.All(plan.Steps, s => Assert.NotNull(s.Op));
        Assert.NotEmpty(plan.Steps);

        var before = system.Fingerprint();
        var preview = await runner.PreviewAsync(fixId, binding, new Proposer { Origin = VTD.Contracts.Sessions.EventOrigin.Ui }, Ct);
        Assert.Equal(before, system.Fingerprint()); // a preview never changes anything
        string[] structural = ["binding.", "args.", "op.", "verify.", "capture.failed", "fix.no_steps", "fix.deadman_missing", "fix.high_risk"];
        var bad = preview.BlockReasonKeys.Where(k => structural.Any(s => k.StartsWith(s, StringComparison.Ordinal))).ToList();
        Assert.True(bad.Count == 0, fixId + " structural blocks: " + string.Join(", ", bad));
        Assert.Equal(plan.Steps.Count, preview.Steps.Count);
        Assert.False(string.IsNullOrEmpty(preview.PreviewHash));
        TestContext.Current.TestOutputHelper?.WriteLine(fixId + ": " + (preview.CanApply ? "would apply" : "blocked: " + string.Join(", ", preview.BlockReasonKeys)));

        if (fixId == "fix.vg.quarantine_stale_dat")
        {
            // Dev PC: Vanguard works, vgk is running and 392667600.dat was rewritten after the boot: it must be left alone.
            Assert.False(preview.CanApply);
            Assert.Contains(preview.BlockReasonKeys, k => k is "policy.file_vanguard_running" or "policy.file_vg_dat_not_stale");
            system.Services.Mutate("vgk", s => s with { State = ServiceRun.Stopped });
            var again = await runner.PreviewAsync(fixId, binding, new Proposer { Origin = VTD.Contracts.Sessions.EventOrigin.Ui }, Ct);
            Assert.Contains("policy.file_vg_dat_not_stale", again.BlockReasonKeys);
        }

        if (fixId == "fix.net.disable_unused_adapter" || fixId == "fix.net.disable_second_adapter")
        {
            var target = binding.Entities["adapter"][0].Id;
            var isDefault = snapshot.Network.Adapters.TryGetValue(out var adapters) && adapters.Any(a => a.IsDefaultRoute && a.InstanceId == target);
            if (isDefault)
            {
                Assert.Contains("policy.default_route_nic", preview.BlockReasonKeys);
            }
        }

        if (fixId == "fix.vg.vgc_automatic")
        {
            Assert.Contains("policy.service_vgc_precheck", preview.BlockReasonKeys); // the Pre-Check gate is not confirmed
        }
    }

    private static AnalysisResult Analysis(IKnowledgeBase kb, ScanSnapshot snapshot) => new Analyzer(kb).Analyze(new AnalysisInput
    {
        AnalysisId = "01JB6R7X3K9M2N4P5Q6R7S8TA0",
        Snapshot = snapshot,
        AsOfUtc = snapshot.Meta.CreatedUtc.AddMinutes(5),
    });

    /// <summary>The analyzer's binding for the fix when it proposes it; otherwise one built from the snapshot's entities.</summary>
    private static FixBinding BindingFor(FixDef fix, ScanSnapshot s, AnalysisResult analysis)
    {
        var proposed = analysis.Proposals.FirstOrDefault(p => p.FixId == fix.Id && fix.Bind.Keys.All(k => p.Binding.Entities.TryGetValue(k, out var e) && e.Count > 0));
        if (proposed is not null)
        {
            return proposed.Binding;
        }

        var entities = new Dictionary<string, IReadOnlyList<EntityRef>>(StringComparer.Ordinal);
        foreach (var (name, def) in fix.Bind)
        {
            entities[name] = [Candidate(fix, def, s)];
        }

        return new FixBinding { Entities = entities };
    }

    private static EntityRef Candidate(FixDef fix, BindDef def, ScanSnapshot s)
    {
        var adapters = s.Network.Adapters.TryGetValue(out var a) ? a : [];
        var services = s.Services.TryGetValue(out var sv) ? sv : [];
        var devices = s.Devices.TryGetValue(out var dv) ? dv : [];
        var processes = s.Processes.TryGetValue(out var pr) ? pr : [];
        var startup = s.Startup.TryGetValue(out var st) ? st : [];
        var layers = s.Riot.CompatLayers.TryGetValue(out var l) ? l : [];
        var valorant = s.Riot.Valorant.TryGetValue(out var v) ? v.Path : null;
        var client = s.Riot.Client.TryGetValue(out var c) ? c.Path : null;
        var vanguardDir = s.Riot.Vanguard.TryGetValue(out var vg) ? vg.InstallPath : null;
        EntityRef E(EntityKind kind, string id, string? name = null) => new() { Kind = kind, Id = id, DisplayName = name ?? id };
        return def.Kind switch
        {
            EntityKind.Adapter => E(EntityKind.Adapter, (adapters.FirstOrDefault(x => !x.IsDefaultRoute) ?? adapters[0]).InstanceId),
            EntityKind.Service when fix.Id == "fix.vg.vgc_automatic" => E(EntityKind.Service, "vgc"),
            EntityKind.Service => E(EntityKind.Service, services.First(x => x.Kind == ServiceKind.Win32 && !Policy.ProtectedTargetPolicy.ProtectedServices.Contains(x.Name, StringComparer.OrdinalIgnoreCase)
                                                                             && x.StartType is ServiceStartType.Automatic or ServiceStartType.Manual).Name),
            EntityKind.Process when fix.Id.StartsWith("fix.sec.", StringComparison.Ordinal) => E(EntityKind.Process, RiotProcessNames.Game),
            EntityKind.Process => E(EntityKind.Process, processes.First(x => !RiotProcessNames.IsProtected(x.Name) && Policy.ProtectedTargetPolicy.CheckProcessClose(x.Name).Allowed).Name),
            EntityKind.File when fix.Id == "fix.vg.quarantine_stale_dat" => E(EntityKind.File, @"%SystemRoot%\392667600.dat", "392667600.dat"),
            EntityKind.File when fix.Id == "fix.vg.quarantine_bootstatus" => E(EntityKind.File, (vanguardDir ?? @"C:\Program Files\Riot Vanguard") + @"\vgkbootstatus.dat", "vgkbootstatus.dat"),
            EntityKind.File when fix.Id == "fix.riot.client_runasadmin" => E(EntityKind.File, client + @"\RiotClientServices.exe", "RiotClientServices.exe"),
            EntityKind.File => E(EntityKind.File, valorant + @"\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", "VALORANT-Win64-Shipping.exe"),
            EntityKind.Device => E(EntityKind.Device, (devices.FirstOrDefault(x => x.InstanceId.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase))
                                                      ?? devices.First(x => string.Equals(x.ClassName, "MEDIA", StringComparison.OrdinalIgnoreCase))).InstanceId),
            EntityKind.StartupItem => StartupEntity(fix, startup),
            EntityKind.CompatLayer => E(EntityKind.CompatLayer, layers[0].ExePath, string.Join(' ', layers[0].Layers.Where(t => t != "~"))),
            _ => throw new InvalidOperationException("no candidate for " + def.Kind),
        };
    }

    private static EntityRef StartupEntity(FixDef fix, IReadOnlyList<StartupItem> startup)
    {
        var location = fix.Id.EndsWith("hkcu", StringComparison.Ordinal) ? StartupLocation.HkcuRun : StartupLocation.HklmRun;
        var item = startup.FirstOrDefault(x => x.Location == location && !Policy.ProtectedTargetPolicy.IsProtectedStartup(x.Name, x.ImageName))
                   ?? new StartupItem { Name = "SampleApp", Location = location, ImageName = "sample.exe", TargetPath = @"C:\Sample\sample.exe", Enabled = true };
        return new EntityRef { Kind = EntityKind.StartupItem, Id = EntityIds.StartupItem(location, item.Name), DisplayName = item.Name };
    }

    /// <summary>A fake PC with the dev PC's services, devices, adapters, processes, startup entries, compat layers,
    /// Vanguard files, power scheme and boot time.</summary>
    private static FakeSystem Seed(ScanSnapshot s, TimeProvider clock)
    {
        var system = new FakeSystem(clock);
        if (s.Meta.Boot.TryGetValue(out var boot))
        {
            system.Boot.BootUtc = boot.LastBootUpTimeUtc;
        }

        foreach (var x in s.Services.TryGetValue(out var services) ? services : [])
        {
            system.Services.Add(new Ports.ServiceInfo
            {
                Name = x.Name,
                StartType = (ServiceStart)(int)x.StartType,
                Delayed = x.DelayedAutoStart,
                State = (ServiceRun)((int)x.State + 1),
                IsDriver = x.Kind is ServiceKind.KernelDriver or ServiceKind.FileSystemDriver,
            });
        }

        foreach (var d in s.Devices.TryGetValue(out var devices) ? devices : [])
        {
            system.Devices.Add(d.InstanceId, d.ClassGuid.ToLowerInvariant(), null);
            system.Devices.Mutate(d.InstanceId, x => x with { Present = d.Present, Enabled = d.Enabled, ProblemCode = d.ProblemCode });
        }

        var n = 0;
        foreach (var a in s.Network.Adapters.TryGetValue(out var adapters) ? adapters : [])
        {
            var driverKey = "{4d36e972-e325-11ce-bfc1-08002be10318}\\" + (n++).ToString("0000", CultureInfo.InvariantCulture);
            system.Devices.Add(a.InstanceId, FakeSystem.ProtectedClassNet, null, driverKey);
            var guid = Guid.TryParse(a.InterfaceGuid, out var g) ? g : Guid.NewGuid();
            system.Network.Map(a.InstanceId, guid, a.IsDefaultRoute, new InterfaceMetric(a.Ipv4Metric ?? 25, a.AutomaticMetric ?? true), a.Ipv6Bound ?? true);
            var key = @"SYSTEM\CurrentControlSet\Control\Class\" + driverKey;
            system.Registry.SetString(RegHive.Machine, key, "NetCfgInstanceId", "{" + guid.ToString("D").ToUpperInvariant() + "}");
            foreach (var (kw, value) in a.AdvancedProperties)
            {
                system.Registry.SetString(RegHive.Machine, key, kw, value);
                system.Registry.SetString(RegHive.Machine, key + @"\Ndi\params\" + kw, "type", "edit");
            }
        }

        foreach (var p in s.Processes.TryGetValue(out var processes) ? processes : [])
        {
            system.Processes.Add(p.ProcessId, p.Name, 1, windows: 1);
        }

        foreach (var item in s.Startup.TryGetValue(out var startup) ? startup : [])
        {
            var hive = item.Location == StartupLocation.HkcuRun ? RegHive.User : RegHive.Machine;
            if (item.Location is StartupLocation.HkcuRun or StartupLocation.HklmRun)
            {
                system.Registry.SetString(hive, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", item.Name, "\"" + (item.TargetPath ?? item.ImageName) + "\"");
            }
        }

        foreach (var layer in s.Riot.CompatLayers.TryGetValue(out var layers) ? layers : [])
        {
            system.Registry.SetString(layer.Hive == AppHive.Hkcu ? RegHive.User : RegHive.Machine, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", layer.ExePath, string.Join(' ', layer.Layers));
        }

        if (s.Riot.Vanguard.TryGetValue(out var vg))
        {
            foreach (var f in vg.Files.Where(f => f.Exists))
            {
                var path = f.Location switch
                {
                    "windows" => @"C:\Windows\" + f.Name,
                    "system32" => @"C:\Windows\System32\" + f.Name,
                    _ => (vg.InstallPath ?? @"C:\Program Files\Riot Vanguard") + "\\" + f.Name,
                };
                system.Files.Add(path, [1, 2, 3], f.ModifiedUtc ?? clock.GetUtcNow());
            }
        }

        if (s.Riot.Valorant.TryGetValue(out var valorant) && valorant.Path is { } vp)
        {
            system.Files.AddDirectory(vp);
        }

        system.Power.Schemes.AddRange([FakeSystem.Balanced, FakeSystem.HighPerformance]);
        system.Power.Active = s.Power.ActiveScheme.TryGetValue(out var scheme) && Guid.TryParse(scheme.Guid, out var sg) ? sg : FakeSystem.Balanced;
        if (!system.Power.Schemes.Contains(system.Power.Active))
        {
            system.Power.Schemes.Add(system.Power.Active);
        }

        foreach (var schemeId in system.Power.Schemes)
        {
            foreach (var ac in new[] { true, false })
            {
                system.Power.Values[(schemeId, FakeSystem.ProcessorSubgroup, FakeSystem.ProcessorMax, ac)] = (uint)(s.Power.ProcessorMaxPercentAc.TryGetValue(out var max) ? max : 100);
                system.Power.Values[(schemeId, FakeSystem.UsbSubgroup, FakeSystem.UsbSuspend, ac)] = s.Power.UsbSelectiveSuspendAc.TryGetValue(out var usb) && usb ? 1u : 0u;
            }
        }

        return system;
    }

    private static IKnowledgeBase LoadKb()
    {
        var result = new KnowledgeBaseLoader([]).LoadAsync(new KbLoadOptions
        {
            BundledPath = Path.Combine(Root, "kb"),
            NowUtc = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
            AppVersion = "0.2.0",
        }, CancellationToken.None).GetAwaiter().GetResult();
        var errors = result.Issues.Where(i => i.Severity == KbIssueSeverity.Error).ToList();
        return errors.Count == 0 ? result.Kb : throw new InvalidOperationException("kb/ does not load: " + string.Join("; ", errors.Select(i => i.Code + " " + i.Path)));
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VTDiag.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("VTDiag.sln not found");
    }
}
