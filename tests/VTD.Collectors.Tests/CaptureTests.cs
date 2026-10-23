using System.Globalization;
using VTD.Collectors.Capture;
using VTD.Collectors.Tests.Support;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Collectors.Tests;

public sealed class CaptureRedactorTests
{
    private static readonly CaptureIdentity Identity = new()
    {
        Sid = CanaryTokens.Sid,
        ProfilePath = @"C:\Users\" + CanaryTokens.UserName,
        UserName = CanaryTokens.UserName,
        MachineName = CanaryTokens.ComputerName,
        Salt = ReplayHarness.Salt,
    };

    [Fact]
    public void Identity_is_removed_consistently_from_keys_and_payloads()
    {
        var path = CanaryTokens.UserProfilePath + @"\Saved\Logs\ShooterGame.log";
        var entries = new List<RawCaptureEntry>
        {
            new() { CollectorId = "riot", Source = "file.stat:" + path, Payload = "{\"status\":\"ok\",\"size\":1}" },
            new() { CollectorId = "input", Source = @"registry:HKU\" + CanaryTokens.Sid + @"\Control Panel\Mouse", Payload = "{\"status\":\"ok\",\"values\":[{\"name\":\"MouseSpeed\",\"kind\":\"String\",\"data\":\"1\"}]}" },
            new() { CollectorId = "os", Source = @"registry:HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", Payload = "{\"status\":\"ok\",\"values\":[{\"name\":\"RegisteredOwner\",\"kind\":\"String\",\"data\":\"" + CanaryTokens.UserName + "\"},{\"name\":\"DigitalProductId\",\"kind\":\"Binary\",\"data\":\"00ff\"},{\"name\":\"CurrentBuild\",\"kind\":\"String\",\"data\":\"26200\"}]}" },
            new() { CollectorId = "devices", Source = "sys:devices:x", Payload = "{\"status\":\"ok\",\"items\":[{\"instanceId\":\"USBSTOR\\\\DISK&VEN_SANDISK&PROD_ULTRA\\\\4C530001231020115302&0\",\"parent\":\"PCI\\\\VEN_8086&DEV_7A60\\\\3&11583659&0&A0\"},{\"instanceId\":\"PCI\\\\VEN_10EC&DEV_8125\\\\01000000684CE00000\"}]}" },
            new() { CollectorId = "network", Source = "sys:network", Payload = "{\"status\":\"ok\",\"interfaces\":[{\"guid\":\"{X}\",\"mac\":\"D8:F2:CA:4F:8C:46\",\"ipv4\":[\"192.168.1.34\"],\"dns\":[\"192.168.1.1\",\"1.1.1.1\"],\"gateways\":[\"192.168.1.1\"]}]}" },
            new() { CollectorId = "network", Source = "probe:ping:192.168.1.1|10", Payload = "{\"status\":\"ok\",\"sent\":10}" },
            new() { CollectorId = "network", Source = "sys:wlan", Payload = "{\"status\":\"ok\",\"interfaces\":[{\"guid\":\"{W}\",\"ssid\":\"HomeNet-5G\"}]}" },
            new() { CollectorId = "riot", Source = "file.list:" + Pcs.LocalAppData + @"\VALORANT\Saved\Config|*|n|500", Payload = "{\"status\":\"ok\",\"entries\":[{\"name\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0-eu\",\"isDirectory\":true},{\"name\":\"" + CanaryTokens.ComputerName + "-1234\",\"isDirectory\":true}]}" },
        };
        var redacted = new CaptureRedactor(Identity).Redact(entries);
        var text = VtdJson.Serialize(redacted, indented: true);

        Assert.Empty(LeakScan.Find(text, new PlayerContext { Sid = Identity.Sid, ProfilePath = Identity.ProfilePath, LocalAppData = "x", RoamingAppData = "x", IsAdmin = true }, Identity.UserName, Identity.MachineName));
        Assert.Contains(redacted, e => e.Source.StartsWith(@"registry:HKU\" + CaptureRedactor.FakeSid, StringComparison.Ordinal));
        Assert.Contains(redacted, e => e.Source.Contains(@"C:\Users\<user>\AppData", StringComparison.Ordinal));
        Assert.DoesNotContain("DigitalProductId", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisteredOwner", text, StringComparison.Ordinal);
        Assert.Contains("26200", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4C530001231020115302", text, StringComparison.Ordinal);
        Assert.DoesNotContain("01000000684CE00000", text, StringComparison.Ordinal);
        Assert.Contains("3&11583659&0&A0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.", text, StringComparison.Ordinal);
        Assert.Contains("1.1.1.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HomeNet", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0f1e2d3c-4b5a", text, StringComparison.Ordinal);
        var gateway = redacted.Single(e => e.Source.StartsWith("probe:ping:", StringComparison.Ordinal)).Source;
        var network = redacted.Single(e => e.Source == "sys:network").Payload;
        Assert.Contains(gateway["probe:ping:".Length..gateway.IndexOf('|', StringComparison.Ordinal)], network, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redacted_synthetic_capture_still_replays()
    {
        var live = await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(capture: true), null, TestContext.Current.CancellationToken);
        var redacted = new CaptureRedactor(new CaptureIdentity { Sid = Pcs.Player.Sid, ProfilePath = @"C:\Users\<user>", UserName = "nobody-here", MachineName = "nomachine", Salt = ReplayHarness.Salt }).Redact(live.Capture);
        var (snapshot, misses) = await ReplayHarness.ReplayAsync(redacted, TestContext.Current.CancellationToken);
        Assert.Empty(misses);
        Assert.True(snapshot.Riot.ShooterLog.IsKnown);
        Assert.True(snapshot.Input.Mouse.IsKnown);
        Assert.Empty(TestPaths.ValidateSnapshot(VtdJson.Serialize(snapshot)));
    }
}

/// <summary>Replay tests on the real, redacted, Turkish-locale capture of the development PC.</summary>
public sealed class RealCaptureTests
{
    private static string CapturePath => Path.Combine(TestPaths.CaptureDir, "dev-pc.capture.json");

    private static string SnapshotPath => Path.Combine(TestPaths.RealSnapshotDir, "dev-pc.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Replay_of_the_real_capture_matches_the_checked_in_snapshot()
    {
        Assert.SkipUnless(File.Exists(CapturePath), "Private dev-PC capture not present (excluded from the public repo).");
        var (snapshot, misses) = await ReplayHarness.ReplayAsync(ReplayHarness.LoadCapture(CapturePath), Ct);
        Assert.Empty(misses);
        var json = TestPaths.Normalize(VtdJson.Serialize(snapshot, indented: true));
        if (TestPaths.UpdateCapture && !TestPaths.LiveScan)
        {
            // VTD_UPDATE_CAPTURE=1 alone: re-derive dev-pc.json from the checked-in capture after a collector or contract
            // change (no new scan of this PC). With VTD_LIVE_SCAN=1 too, LiveScanTests re-captures instead.
            await File.WriteAllTextAsync(SnapshotPath, json, Ct);
        }

        Assert.Equal(TestPaths.Normalize(File.ReadAllText(SnapshotPath)), json);
        Assert.Empty(TestPaths.ValidateSnapshot(VtdJson.Serialize(snapshot)));
    }

    [Fact]
    public async Task Replay_is_identical_under_tr_TR_and_en_US_and_invariant_cultures()
    {
        Assert.SkipUnless(File.Exists(CapturePath), "Private dev-PC capture not present (excluded from the public repo).");
        var entries = ReplayHarness.LoadCapture(CapturePath);
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var outputs = new List<string>();
        try
        {
            foreach (var name in new[] { "tr-TR", "en-US", string.Empty })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = name.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(name);
                outputs.Add(VtdJson.Serialize((await ReplayHarness.ReplayAsync(entries, Ct)).Snapshot));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous.CurrentCulture;
            CultureInfo.CurrentUICulture = previous.CurrentUICulture;
        }

        Assert.Equal(outputs[0], outputs[1]);
        Assert.Equal(outputs[0], outputs[2]);
    }

    [Fact]
    public async Task Real_turkish_pc_facts_are_locale_free()
    {
        Assert.SkipUnless(File.Exists(CapturePath), "Private dev-PC capture not present (excluded from the public repo).");
        var s = (await ReplayHarness.ReplayAsync(ReplayHarness.LoadCapture(CapturePath), Ct)).Snapshot;
        Assert.Equal("tr-TR", s.Meta.OsUiCulture);
        Assert.Equal(PrivilegeMode.Limited, s.Meta.Privilege);
        Assert.True(s.Os.Build.Value >= 19041);
        Assert.Equal(FirmwareType.Uefi, s.Firmware.Type.Value);
        Assert.Equal(UnknownReason.NeedsAdmin, s.Security.Bcd.Reason);
        Assert.Equal(UnknownReason.NeedsAdmin, s.Security.SecureBootSetupMode.Reason);
        Assert.True(s.Security.SecureBootEnabled.IsKnown, "SecureBoot\\State is readable without admin");
        Assert.True(s.Security.Tpm.Value.Present);
        Assert.StartsWith(@"ACPI\MSFT0101", s.Security.Tpm.Value.InstanceId, StringComparison.OrdinalIgnoreCase);
        Assert.True(s.Power.ActiveScheme.IsKnown);
        Assert.Equal(PowerScheme(s.Power.ActiveScheme.Value.Guid), s.Power.ActiveScheme.Value.Kind);
        Assert.Contains(s.Services.Value, x => x.Name == "vgc");
        Assert.True(s.Riot.Vanguard.Value.Installed);
        Assert.Contains(s.Runs, r => r.CollectorId == "devices" && r.IssueCodes.Contains("fallback:scm_active_drivers"));
        Assert.DoesNotContain(s.Runs, r => r.Status is CollectorStatus.Failed or CollectorStatus.TimedOut);
    }

    [Fact]
    public void Checked_in_capture_and_snapshot_hold_no_identifiers_or_secrets()
    {
        Assert.SkipUnless(File.Exists(CapturePath), "Private dev-PC capture not present (excluded from the public repo).");
        foreach (var path in new[] { CapturePath, SnapshotPath })
        {
            var text = File.ReadAllText(path);
            var leaks = LeakScan.Find(text);
            Assert.True(leaks.Count == 0, Path.GetFileName(path) + ": " + string.Join(", ", leaks));
            Assert.DoesNotContain("remoting-auth-token", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DigitalProductId", text, StringComparison.Ordinal);
        }
    }

    private static PowerSchemeKind PowerScheme(string guid) => Collectors.PowerCollector.SchemeKind(guid);
}
