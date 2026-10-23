using System.Globalization;
using VTD.Collectors.Core;
using VTD.Collectors.Tests.Support;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;
using VTD.Testing.Fakes;

namespace VTD.Collectors.Tests;

public sealed class OrchestratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Full_scan_produces_a_complete_schema_valid_snapshot()
    {
        var result = await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, Ct);
        var s = result.Snapshot;
        var json = VtdJson.Serialize(s);

        Assert.Empty(TestPaths.ValidateSnapshot(json));
        Assert.Equal(ScanOrchestrator.CollectorIds, s.Runs.Select(r => r.CollectorId));
        Assert.DoesNotContain(s.Runs, r => r.Status is CollectorStatus.Failed or CollectorStatus.TimedOut);
        Assert.DoesNotContain(s.Runs.SelectMany(r => r.IssueCodes), i => i.StartsWith("exception:", StringComparison.Ordinal));
        Assert.Equal(26200, s.Os.Build.Value);
        Assert.True(s.Security.SecureBootEnabled.Value);
        Assert.Equal(TpmKind.Firmware, s.Security.Tpm.Value.Kind);
        Assert.Equal(VbsStatus.Running, s.Security.Vbs.Value.Status);
        Assert.Equal(412u, s.Meta.Boot.Value.BootId);
        Assert.Equal(PrivilegeMode.Elevated, s.Meta.Privilege);
        Assert.StartsWith("mk1:", s.Meta.MachineKey, StringComparison.Ordinal);
        Assert.Equal("tr-TR", s.Meta.OsUiCulture);
    }

    [Fact]
    public async Task Vgc_manual_and_stopped_is_reported_as_is_with_vgk_running()
    {
        var s = (await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        var v = s.Riot.Vanguard.Value;
        Assert.True(v.Installed);
        Assert.Equal(ServiceStartType.Manual, v.Vgc!.StartType);
        Assert.Equal(ServiceState.Stopped, v.Vgc.State);
        Assert.Equal(ServiceState.Running, v.Vgk!.State);
        Assert.Equal(2, v.Logs!.FileCount);
        Assert.Equal(1, v.Logs.CountLast24Hours);
        Assert.Equal("1.17.9.5", v.Version);
        Assert.Contains(v.Files, f => f.Name == "vgkbootstatus.dat" && !f.Exists);
    }

    [Fact]
    public async Task Limited_mode_reports_admin_only_facts_as_needs_admin_never_as_defaults()
    {
        var f = Pcs.Healthy();
        f.EventLogStatus["Microsoft-Windows-CodeIntegrity/Operational"] = ReadStatus.AccessDenied;
        var s = (await Pcs.Orchestrator(f, PrivilegeMode.Limited).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;

        Assert.Equal(PrivilegeMode.Limited, s.Meta.Privilege);
        foreach (var fact in new[] { s.Security.SecureBootSetupMode, s.Security.SecureBootDbHasCa2023, s.Firmware.BootToFirmwareSupported })
        {
            Assert.Equal(FactStatus.Unknown, fact.Status);
            Assert.Equal(UnknownReason.NeedsAdmin, fact.Reason);
        }

        Assert.Equal(UnknownReason.NeedsAdmin, s.Security.Bcd.Reason);
        Assert.Equal(UnknownReason.NeedsAdmin, s.Firmware.OtherOsBootEntries.Reason);
        Assert.Equal(UnknownReason.NeedsAdmin, s.Security.HvciIncompatibleDrivers.Reason);
        Assert.True(s.Security.SecureBootEnabled.Value, "the non-admin SecureBoot\\State read still works in limited mode");
        Assert.True(s.Security.Tpm.Value.Present);
        Assert.Null(s.Security.Tpm.Value.Ready);
        var security = s.Runs.Single(r => r.CollectorId == "security");
        Assert.Equal(CollectorStatus.Partial, security.Status);
        Assert.Contains("needs_admin:wmi:BcdStore", security.IssueCodes);
    }

    [Fact]
    public async Task Access_denied_in_limited_mode_is_needs_admin_and_never_false()
    {
        var f = Pcs.Healthy().Denied(@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        var s = (await Pcs.Orchestrator(f, PrivilegeMode.Limited).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        Assert.Equal(FactStatus.Unknown, s.Security.SecureBootEnabled.Status);
        Assert.Equal(UnknownReason.NeedsAdmin, s.Security.SecureBootEnabled.Reason);
        Assert.Equal(Tri.Unknown, s.Security.SecureBootEnabled.ToTri());
    }

    [Fact]
    public async Task A_hanging_collector_times_out_without_blocking_the_scan()
    {
        var f = Pcs.Healthy();
        using var release = new ManualResetEventSlim(false);
        var hang = new HangingCollector(f.ToSources(), release);
        var collectors = ScanOrchestrator.CreateDefaultCollectors(f.ToSources()).Where(c => c.Id != "power").Append(hang).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = (await Pcs.Orchestrator(f, collectors: collectors).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        sw.Stop();
        release.Set();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        var run = s.Runs.Single(r => r.CollectorId == "power");
        Assert.Equal(CollectorStatus.TimedOut, run.Status);
        Assert.Equal(UnknownReason.TimedOut, s.Power.ActiveScheme.Reason);
        Assert.Equal(UnknownReason.TimedOut, s.Power.Battery.Reason);
        Assert.Equal(CollectorStatus.Ok, s.Runs.Single(r => r.CollectorId == "input").Status);
    }

    [Fact]
    public async Task A_throwing_source_fails_one_collector_only()
    {
        var f = Pcs.Healthy();
        f.OnSystemCall = name =>
        {
            if (name == "displays")
            {
                throw new InvalidOperationException("boom");
            }
        };
        var s = (await Pcs.Orchestrator(f).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        Assert.Equal(UnknownReason.Failed, s.Display.Monitors.Reason);
        Assert.Contains(s.Runs.Single(r => r.CollectorId == "display").IssueCodes, i => i.StartsWith("exception:InvalidOperationException", StringComparison.Ordinal));
        Assert.True(s.Display.HagsEnabled.Value);
        Assert.Empty(TestPaths.ValidateSnapshot(VtdJson.Serialize(s)));
    }

    [Fact]
    public async Task Quick_and_targeted_scans_mark_the_rest_not_requested()
    {
        var quick = (await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(ScanMode.Quick), null, Ct)).Snapshot;
        Assert.Equal(CollectorStatus.NotRequested, quick.Runs.Single(r => r.CollectorId == "network").Status);
        Assert.Equal(UnknownReason.NotCollected, quick.Network.Adapters.Reason);
        Assert.True(quick.Os.Build.IsKnown);

        var targeted = (await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(ScanMode.Targeted, ["riot"]), null, Ct)).Snapshot;
        Assert.Equal(CollectorStatus.Ok, targeted.Runs.Single(r => r.CollectorId == "riot").Status);
        Assert.Equal(14, targeted.Runs.Count(r => r.Status == CollectorStatus.NotRequested));
        Assert.Equal(UnknownReason.NotCollected, targeted.Os.Build.Reason);
        Assert.Empty(TestPaths.ValidateSnapshot(VtdJson.Serialize(targeted)));
    }

    [Fact]
    public async Task Progress_reports_every_collector_and_cancellation_throws()
    {
        var reports = new List<ScanProgress>();
        await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), new SyncProgress<ScanProgress>(reports.Add), Ct);
        Assert.Equal(15, reports.Count(r => r.Status is not null));
        Assert.Contains(reports, r => r.Completed == 15 && r.Total == 15);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, cts.Token));
    }

    [Fact]
    public async Task Per_user_data_comes_from_the_player_hive_not_the_engine_account()
    {
        var f = Pcs.Healthy();
        var other = Pcs.Request() with { Player = Pcs.Player with { Sid = "S-1-5-21-9-9-9-500" } };
        var s = (await Pcs.Orchestrator(f).ScanAsync(other, null, Ct)).Snapshot;
        Assert.False(s.Input.Mouse.IsKnown);
        Assert.DoesNotContain(s.Apps.Value, a => a.Hive == AppHive.Hkcu);

        var mine = (await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        Assert.False(mine.Input.Mouse.Value.EnhancePointerPrecision);
        Assert.Contains(mine.Apps.Value, a => a.Hive == AppHive.Hkcu && a.DisplayName == "Discord");
        Assert.Contains(mine.Riot.CompatLayers.Value, c => c.Hive == AppHive.Hkcu && c.Layers.Contains("RUNASADMIN"));
    }

    [Fact]
    public async Task Matching_is_culture_invariant_tr_TR_equals_en_US()
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            var tr = VtdJson.Serialize(ReplayHarness.Normalize((await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, Ct)).Snapshot));
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            var en = VtdJson.Serialize(ReplayHarness.Normalize((await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, Ct)).Snapshot));
            Assert.Equal(Normalize(en), Normalize(tr));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous.CurrentCulture;
            CultureInfo.CurrentUICulture = previous.CurrentUICulture;
        }
    }

    [Fact]
    public async Task Power_plan_matches_on_guid_not_the_localized_name()
    {
        var s = (await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        Assert.Equal(PowerSchemeKind.Balanced, s.Power.ActiveScheme.Value.Kind);
        Assert.Equal("Dengeli", s.Power.ActiveScheme.Value.Name);
        Assert.True(s.Power.UsbSelectiveSuspendAc.Value);
    }

    [Fact]
    public async Task Capture_then_replay_reproduces_the_snapshot()
    {
        var live = await Pcs.Orchestrator(Pcs.Healthy(), deterministicTimeouts: true).ScanAsync(Pcs.Request(capture: true), null, Ct);
        Assert.NotEmpty(live.Capture);
        Assert.Contains(live.Capture, e => e.Source == "meta:scan");
        var (replayed, misses) = await ReplayHarness.ReplayAsync(live.Capture, Ct);
        Assert.Empty(misses);
        var expected = ReplayHarness.Normalize(live.Snapshot);
        Assert.Equal(
            Normalize(VtdJson.Serialize(expected with { Meta = expected.Meta with { Origin = SnapshotOrigin.Replay, KbVersion = "replay", SnapshotId = "x" } })),
            Normalize(VtdJson.Serialize(replayed with { Meta = replayed.Meta with { SnapshotId = "x" } })));
    }

    [Fact]
    public async Task Collector_runs_standalone_through_ICollector()
    {
        var f = Pcs.Healthy();
        ICollector c = new Collectors.SecurityCollector(f.ToSources());
        var ctx = new CollectContext { Request = Pcs.Request(capture: true), Privilege = PrivilegeMode.Elevated, StartedUtc = Pcs.Start, Clock = new FakeClock(Pcs.Start) };
        var output = await c.CollectAsync(ctx, Ct);
        Assert.Equal("security", output.Run.CollectorId);
        Assert.NotNull(output.Patch.Security);
        Assert.NotEmpty(output.Capture);
        Assert.All(output.Capture, e => Assert.Equal("security", e.CollectorId));
        Assert.True(c.Timeout <= TimeSpan.FromSeconds(15));
    }

    private static string Normalize(string json) => json;

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (report)
            {
                report(value);
            }
        }
    }

    private sealed class HangingCollector(SystemSources sources, ManualResetEventSlim release) : CollectorBase(sources)
    {
        public override string Id => "power";

        public override ScanArea Area => ScanArea.Hardware;

        public override TimeSpan Timeout => TimeSpan.FromMilliseconds(300);

        protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
        {
            release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
            return new SnapshotPatch();
        }
    }
}
