using VTD.Collectors.Core;
using VTD.Collectors.Pack;
using VTD.Collectors.Tests.Support;
using VTD.Contracts;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;
using VTD.Testing.Fakes;

namespace VTD.Collectors.Tests;

public sealed class PackCollectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScanEnvironment Env(FakeSources f, PrivilegeMode privilege = PrivilegeMode.Elevated, IReadOnlyList<CollectorDef>? defs = null)
        => new(f.ToSources(), new CollectContext { Request = Pcs.Request() with { PackCollectors = defs ?? [] }, Privilege = privilege, StartedUtc = Pcs.Start, Clock = new FakeClock(Pcs.Start) });

    [Theory]
    [InlineData(RegistryHiveScope.Hklm, @"SYSTEM\ControlSet001\Services\vgc", true)]
    [InlineData(RegistryHiveScope.Hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\vgc.exe", true)]
    [InlineData(RegistryHiveScope.Hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", false)]
    [InlineData(RegistryHiveScope.Hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", false)]
    [InlineData(RegistryHiveScope.Hklm, @"SAM\SAM", false)]
    [InlineData(RegistryHiveScope.Hklm, @"SYSTEM\CurrentControlSet\Control\Lsa", false)]
    [InlineData(RegistryHiveScope.Hklm, @"SOFTWARE\Riot Games\Riot Vanguard\Keys", false)]
    [InlineData(RegistryHiveScope.Hklm, @"SOFTWARE\Classes\CLSID", false)]
    [InlineData(RegistryHiveScope.HkcuPlayer, @"Software\Microsoft\GameBar", true)]
    [InlineData(RegistryHiveScope.HkcuPlayer, @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Passport", false)]
    [InlineData(RegistryHiveScope.HkcuPlayer, @"SYSTEM\CurrentControlSet\Services", false)]
    public void Registry_roots_are_allowlisted_and_denylisted(RegistryHiveScope hive, string key, bool allowed)
    {
        var normalized = PackCollectorExecutor.NormalizeKey(key);
        Assert.NotNull(normalized);
        Assert.Equal(allowed, PackCollectorExecutor.IsAllowedKey(hive, normalized));
    }

    [Fact]
    public void Registry_collector_reads_player_hive_and_reports_entities()
    {
        var f = Pcs.Healthy().Reg(Pcs.Hku(@"Software\Microsoft\GameBar"), ("AutoGameModeEnabled", "DWord", "0"));
        var r = PackCollectorExecutor.Run(Env(f), new RegistryValueCollectorDef { Id = "gamemode_off", Hive = RegistryHiveScope.HkcuPlayer, Key = @"Software\Microsoft\GameBar", Values = ["AutoGameModeEnabled"] }, Ct);
        Assert.Equal(1, r.Data.Value.Count);
        Assert.Equal("0", r.Data.Value.Items[0]["data"]);
        Assert.Equal(@"HKCU\Software\Microsoft\GameBar|AutoGameModeEnabled", Assert.Single(r.Data.Value.Entities).Id);

        var absent = PackCollectorExecutor.Run(Env(f), new RegistryValueCollectorDef { Id = "absent", Hive = RegistryHiveScope.Hklm, Key = @"SOFTWARE\Riot Games\Nothing" }, Ct);
        Assert.Equal(0, absent.Data.Value.Count);

        var denied = PackCollectorExecutor.Run(Env(f), new RegistryValueCollectorDef { Id = "lsa", Hive = RegistryHiveScope.Hklm, Key = @"SYSTEM\CurrentControlSet\Control\Lsa" }, Ct);
        Assert.Equal(FactStatus.Unknown, denied.Data.Status);
        Assert.Equal("invalid:registry_root", denied.Data.Detail);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\drivers\ene.sys", true)]
    [InlineData(@"%VanguardDir%\vgk.sys", true)]
    [InlineData(@"%PlayerLocalAppData%\VALORANT\Saved", true)]
    [InlineData(@"%SystemRoot%\..\Users", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"\\server\share\x", false)]
    [InlineData(@"%SystemRoot%\*.sys", false)]
    [InlineData(@"%TEMP%\x", false)]
    [InlineData(@"%SystemRoot%x", false)]
    public void File_paths_must_start_with_an_allowed_token(string template, bool valid)
        => Assert.Equal(valid, PackCollectorExecutor.ExpandPath(Env(Pcs.Healthy()), template) is not null);

    [Fact]
    public void File_exists_collector_reports_metadata_only()
    {
        var r = PackCollectorExecutor.Run(Env(Pcs.Healthy()), new FileExistsCollectorDef { Id = "vgk", Path = @"%VanguardDir%\vgk.sys" }, Ct);
        Assert.Equal(1, r.Data.Value.Count);
        Assert.Equal("true", r.Data.Value.Items[0]["exists"]);
        Assert.Equal(EntityKind.File, r.Data.Value.Entities[0].Kind);
        var missing = PackCollectorExecutor.Run(Env(Pcs.Healthy()), new FileExistsCollectorDef { Id = "dat", Path = @"%SystemRoot%\392667600.dat" }, Ct);
        Assert.Equal(0, missing.Data.Value.Count);
    }

    [Fact]
    public void Uninstall_driver_and_faulting_module_kinds_match()
    {
        var env = Env(Pcs.Healthy());
        var apps = PackCollectorExecutor.Run(env, new UninstallRegexCollectorDef { Id = "nahimic", DisplayNameRegex = "^nahimic" }, Ct);
        Assert.Equal(1, apps.Data.Value.Count);
        Assert.Equal("HKLM32:{NAHIMIC}", apps.Data.Value.Entities[0].Id);
        var drivers = PackCollectorExecutor.Run(env, new LoadedDriverCollectorDef { Id = "realtek", NameRegex = "^rt25cx" }, Ct);
        Assert.Equal("rt25cx21x64.sys", drivers.Data.Value.Items[0]["name"]);
        var module = PackCollectorExecutor.Run(env, new FaultingModuleCollectorDef { Id = "nahimic_crash", Modules = ["productinfo.dll"] }, Ct);
        Assert.Equal(1, module.Data.Value.Count);
        Assert.Equal("VALORANT-Win64-Shipping.exe", module.Data.Value.Items[0]["exe"]);
    }

    [Fact]
    public void Event_collector_enforces_limits_and_caps_items()
    {
        var f = Pcs.Healthy();
        for (var i = 0; i < 80; i++)
        {
            f.Event("System", "rt25cx21", 27, Pcs.Start.AddHours(-i - 1), ("Name", "x"));
        }

        var ok = PackCollectorExecutor.Run(Env(f), new EventLogCollectorDef { Id = "nic", Log = "System", Providers = ["rt25cx21"], Project = ["Name"], Max = 1000 }, Ct);
        Assert.Equal(82, ok.Data.Value.Count);
        Assert.Equal(PackCollectorLimits.MaxItems, ok.Data.Value.Items.Count);
        Assert.True(ok.Data.Value.Truncated);
        var tooLong = PackCollectorExecutor.Run(Env(f), new EventLogCollectorDef { Id = "x", Log = "System", Providers = ["a"], SinceDays = 90 }, Ct);
        Assert.Equal("invalid:limits", tooLong.Data.Detail);
        var unbounded = PackCollectorExecutor.Run(Env(f), new EventLogCollectorDef { Id = "y", Log = "System" }, Ct);
        Assert.Equal("invalid:unbounded_query", unbounded.Data.Detail);
    }

    [Fact]
    public void Limited_mode_access_denied_becomes_needs_admin()
    {
        var f = Pcs.Healthy();
        f.EventLogStatus["Security"] = ReadStatus.AccessDenied;
        var r = PackCollectorExecutor.Run(Env(f, PrivilegeMode.Limited), new EventLogCollectorDef { Id = "sec", Log = "Security", EventIds = [4625] }, Ct);
        Assert.Equal(UnknownReason.NeedsAdmin, r.Data.Reason);
    }

    [Fact]
    public void Invalid_regex_is_reported_not_thrown()
    {
        var r = PackCollectorExecutor.Run(Env(Pcs.Healthy()), new UninstallRegexCollectorDef { Id = "bad", DisplayNameRegex = "(unclosed" }, Ct);
        Assert.Equal("invalid:regex", r.Data.Detail);
    }

    [Fact]
    public async Task Pack_collector_runs_every_definition_in_the_scan()
    {
        var request = Pcs.Request() with { PackCollectors = [new FileExistsCollectorDef { Id = "vgk", Path = @"%VanguardDir%\vgk.sys" }, new UninstallRegexCollectorDef { Id = "bad", DisplayNameRegex = "(" }] };
        var s = (await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(request, null, Ct)).Snapshot;
        Assert.Equal(["vgk", "bad"], s.Pack.Select(p => p.CollectorId));
        Assert.Equal(CollectorStatus.Partial, s.Runs.Single(r => r.CollectorId == "pack").Status);
    }
}

public sealed class FactProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FactProbe Probe(FakeSources f) => new(f.ToSources(), Pcs.Player, new FakeClock(Pcs.Start), PrivilegeMode.Elevated);

    [Fact]
    public async Task Every_guard_id_is_supported_and_answers_without_throwing()
    {
        var probe = Probe(Pcs.Healthy());
        Assert.Equal(GuardIds.All.Order(StringComparer.Ordinal), probe.SupportedIds.Order(StringComparer.Ordinal));
        foreach (var id in GuardIds.All)
        {
            var r = await probe.ProbeAsync(id, new Dictionary<string, string>(StringComparer.Ordinal) { ["instanceId"] = Pcs.RealtekId, ["name"] = "vgc" }, Ct);
            Assert.Equal(id, r.Id);
        }
    }

    [Fact]
    public async Task Probes_read_fresh_state()
    {
        var f = Pcs.Healthy();
        var probe = Probe(f);
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.Equal(Tri.False, (await probe.ProbeAsync(GuardIds.GameRunning, none, Ct)).State);
        f.Processes = f.Processes with { Items = f.Processes.Items.Append(new ProcessRec { Name = "VALORANT-Win64-Shipping.exe", Pid = 999 }).ToList() };
        Assert.Equal(Tri.True, (await probe.ProbeAsync(GuardIds.GameRunning, none, Ct)).State);
        Assert.Equal(Tri.True, (await probe.ProbeAsync(GuardIds.ClientRunning, none, Ct)).State);
        Assert.Equal(Tri.False, (await probe.ProbeAsync(GuardIds.PendingReboot, none, Ct)).State);
        Assert.Equal(Tri.False, (await probe.ProbeAsync(GuardIds.WindowsUpdateInstalling, none, Ct)).State);
        Assert.Equal(Tri.True, (await probe.ProbeAsync(GuardIds.OnAcPower, none, Ct)).State);
        Assert.Equal(Tri.True, (await probe.ProbeAsync(GuardIds.DiskFreeAbove, new Dictionary<string, string>(StringComparer.Ordinal) { ["mb"] = "200" }, Ct)).State);
        Assert.Equal(Tri.False, (await probe.ProbeAsync(GuardIds.BitLockerOn, none, Ct)).State);
        Assert.Equal(Tri.False, (await probe.ProbeAsync(GuardIds.ModifiedWindows, none, Ct)).State);
        Assert.Equal(Tri.True, (await probe.ProbeAsync(GuardIds.InternetReachable, none, Ct)).State);
        Assert.Equal(Tri.False, (await probe.ProbeAsync(GuardIds.MonitorActive, none, Ct)).State);
        var clock = await probe.ProbeAsync(GuardIds.ClockOk, none, Ct);
        Assert.Equal(Tri.True, clock.State);
        Assert.Equal("2", clock.Evidence[0].Args["skewSeconds"]);
    }

    [Fact]
    public async Task Adapter_and_service_guards_bind_entities()
    {
        var probe = Probe(Pcs.Healthy());
        var defaultRoute = await probe.ProbeAsync(GuardIds.AdapterNotDefaultRoute, new Dictionary<string, string>(StringComparer.Ordinal) { ["instanceId"] = Pcs.RealtekId }, Ct);
        Assert.Equal(Tri.False, defaultRoute.State);
        var svc = await probe.ProbeAsync(GuardIds.ServiceExists, new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "vgc" }, Ct);
        Assert.Equal(Tri.True, svc.State);
        Assert.Equal(EntityKind.Service, svc.Entities[0].Kind);
        var missing = await probe.ProbeAsync(GuardIds.ServiceExists, new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "NahimicService" }, Ct);
        Assert.Equal(Tri.False, missing.State);
        var bad = await probe.ProbeAsync(GuardIds.ServiceExists, new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = @"..\x" }, Ct);
        Assert.Equal(Tri.Unknown, bad.State);
    }

    [Fact]
    public async Task Failures_are_unknown_never_false()
    {
        var f = Pcs.Healthy();
        f.Processes = new ProcessList { Status = ReadStatus.Failed };
        f.Clock = new ClockRaw { Status = ReadStatus.TimedOut };
        f.Power = f.Power with { PowerStatusOk = false };
        var probe = Probe(f);
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.Equal(Tri.Unknown, (await probe.ProbeAsync(GuardIds.GameRunning, none, Ct)).State);
        Assert.Equal(UnknownReason.TimedOut, (await probe.ProbeAsync(GuardIds.ClockOk, none, Ct)).UnknownReason);
        Assert.Equal(Tri.Unknown, (await probe.ProbeAsync(GuardIds.OnAcPower, none, Ct)).State);
        var unsupported = await probe.ProbeAsync("sec.something_else", none, Ct);
        Assert.Equal((Tri.Unknown, UnknownReason.NotCollected), (unsupported.State, unsupported.UnknownReason!.Value));
    }
}
