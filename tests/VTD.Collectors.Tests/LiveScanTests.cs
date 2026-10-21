using System.Diagnostics;
using VTD.Collectors.Capture;
using VTD.Collectors.Tests.Support;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Platform;

namespace VTD.Collectors.Tests;

/// <summary>
/// The real scan of THIS PC (opt-in: <c>VTD_LIVE_SCAN=1</c>). Read-only and unelevated. With <c>VTD_UPDATE_CAPTURE=1</c> it
/// rewrites the checked-in redacted capture (<c>tests/fixtures/captures/dev-pc.capture.json</c>) and the snapshot replayed
/// from it (<c>tests/fixtures/snapshots/real/dev-pc.json</c>). Unredacted output goes only to <c>VTD_LIVE_OUT</c>.
/// </summary>
public sealed class LiveScanTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_scan_of_this_pc_is_under_60_seconds_with_no_crash()
    {
        Assert.SkipUnless(TestPaths.LiveScan, "Set VTD_LIVE_SCAN=1 to scan this PC.");
        var clock = TimeProvider.System;
        var player = PlayerContexts.FromCurrentProcess();
        var orchestrator = new ScanOrchestrator(LiveSources.Create(clock), clock, new UlidGenerator(clock));
        var request = new ScanRequest { KbVersion = "live", Player = player, CaptureRaw = true, AllowNetworkProbes = true };
        var sw = Stopwatch.StartNew();
        var result = await orchestrator.ScanAsync(request, null, TestContext.Current.CancellationToken);
        sw.Stop();
        var snapshot = result.Snapshot;
        var json = VtdJson.Serialize(snapshot, indented: true);

        output.WriteLine($"scan: {sw.ElapsedMilliseconds} ms, privilege {snapshot.Meta.Privilege}, capture entries {result.Capture.Count}");
        foreach (var run in snapshot.Runs)
        {
            output.WriteLine($"  {run.CollectorId,-9} {run.Status,-14} {run.ElapsedMs,6} ms  {string.Join(", ", run.IssueCodes)}");
        }

        var stats = FactStats.Of(json);
        output.WriteLine($"facts: {stats}");

        if (TestPaths.LiveOut is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, "live-snapshot.json"), json, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outDir, "live-capture.json"), VtdJson.Serialize(result.Capture, indented: true), TestContext.Current.CancellationToken);
        }

        var redactor = new CaptureRedactor(new CaptureIdentity
        {
            Sid = player.Sid,
            ProfilePath = player.ProfilePath,
            UserName = Environment.UserName,
            MachineName = Environment.MachineName,
        });
        var redacted = redactor.Redact(result.Capture);
        if (TestPaths.LiveOut is { Length: > 0 } outDir2)
        {
            await File.WriteAllTextAsync(Path.Combine(outDir2, "redacted-capture.json"), VtdJson.Serialize(redacted, indented: true), TestContext.Current.CancellationToken);
        }

        var leaks = LeakScan.Find(VtdJson.Serialize(redacted, indented: true), player, Environment.UserName, Environment.MachineName);
        Assert.True(leaks.Count == 0, "redacted capture leaks:\n" + string.Join("\n", leaks));

        if (TestPaths.UpdateCapture)
        {
            Directory.CreateDirectory(TestPaths.CaptureDir);
            Directory.CreateDirectory(TestPaths.RealSnapshotDir);
            await File.WriteAllTextAsync(Path.Combine(TestPaths.CaptureDir, "dev-pc.capture.json"), TestPaths.Normalize(VtdJson.Serialize(redacted, indented: true)), TestContext.Current.CancellationToken);
            var (replayed, misses) = await ReplayHarness.ReplayAsync(redacted, TestContext.Current.CancellationToken);
            output.WriteLine($"replay misses: {misses.Count}");
            await File.WriteAllTextAsync(Path.Combine(TestPaths.RealSnapshotDir, "dev-pc.json"), TestPaths.Normalize(VtdJson.Serialize(replayed, indented: true)), TestContext.Current.CancellationToken);
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"scan took {sw.Elapsed}");
        Assert.DoesNotContain(snapshot.Runs, r => r.Status == CollectorStatus.Failed || r.IssueCodes.Any(i => i.StartsWith("exception:", StringComparison.Ordinal)));
        Assert.Empty(TestPaths.ValidateSnapshot(VtdJson.Serialize(snapshot)));
    }
}
