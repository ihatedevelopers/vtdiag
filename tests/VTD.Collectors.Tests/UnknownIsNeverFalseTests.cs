using VTD.Collectors.Capture;
using VTD.Collectors.Core;
using VTD.Collectors.Tests.Support;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;
using VTD.Testing.Fakes;

namespace VTD.Collectors.Tests;

/// <summary>Unreadable sources must surface as Unknown(reason), never as Known(false) or a Known empty list.</summary>
public sealed class UnknownIsNeverFalseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unreadable_riot_metadata_and_logs_are_unknown()
    {
        var f = Pcs.Healthy();
        f.Stats[@"C:\ProgramData\Riot Games\RiotClientInstalls.json"] = new FileStat { Status = ReadStatus.AccessDenied };
        f.Texts.Remove(@"C:\ProgramData\Riot Games\RiotClientInstalls.json");
        f.Texts.Remove(@"C:\ProgramData\Riot Games\Metadata\valorant.live\valorant.live.product_settings.yaml");
        f.Stats.Remove(@"C:\ProgramData\Riot Games\Metadata\valorant.live\valorant.live.product_settings.yaml");
        var denied = new DeniedReads(f, @"\RiotClientInstalls.json", @"\ShooterGame");
        var s = (await Pcs.Orchestrator(denied.Fake).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        Assert.Equal(FactStatus.Unknown, s.Riot.Client.Status);
        Assert.Equal(FactStatus.Unknown, s.Riot.Valorant.Status);
        Assert.Equal(FactStatus.Unknown, s.Riot.ShooterLog.Status);
    }

    [Fact]
    public async Task Unreadable_player_hive_makes_proxy_and_compat_layers_unknown()
    {
        var f = Pcs.Healthy()
            .Denied(Pcs.Hku(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            .Denied(Pcs.Hku(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"));
        var s = (await Pcs.Orchestrator(f, PrivilegeMode.Limited).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        Assert.Equal(UnknownReason.NeedsAdmin, s.Network.Proxy.Reason);
        Assert.Equal(UnknownReason.NeedsAdmin, s.Riot.CompatLayers.Reason);
    }

    [Fact]
    public async Task One_wmi_timeout_trips_the_breaker_and_the_scan_still_completes()
    {
        var f = Pcs.Healthy();
        foreach (var cls in f.WmiByClass.Keys.ToList())
        {
            f.WmiByClass[cls] = new WmiResult { Status = ReadStatus.TimedOut };
        }

        var s = (await Pcs.Orchestrator(f).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        Assert.Equal(UnknownReason.TimedOut, s.Security.Vbs.Reason);
        Assert.Equal(UnknownReason.TimedOut, s.Storage.Disks.Reason);
        Assert.True(s.Security.SecureBootEnabled.Value, "registry facts survive a hung WMI");
        Assert.DoesNotContain(s.Runs, r => r.Status is CollectorStatus.TimedOut or CollectorStatus.Failed);
        Assert.Empty(TestPaths.ValidateSnapshot(VtdJson.Serialize(s)));

        var breaker = new WmiBreaker(f, f);
        Assert.Equal(ReadStatus.TimedOut, breaker.Query("root", "SELECT * FROM Win32_DeviceGuard", TimeSpan.FromSeconds(1)).Status);
        Assert.True(breaker.Tripped);
        Assert.Equal("wmi_unhealthy", breaker.Query("root", "SELECT * FROM Win32_Processor", TimeSpan.FromSeconds(1)).Error);
        Assert.Equal(ReadStatus.TimedOut, breaker.ReadBcd(TimeSpan.FromSeconds(1)).Status);
    }

    [Fact]
    public async Task Default_audio_endpoints_are_not_taken_from_another_account()
    {
        var f = Pcs.Healthy();
        f.Env = f.Env with { UserSid = "S-1-5-21-1-2-3-500" };
        f.AudioDefaults = new AudioDefaultsRaw { Status = ReadStatus.Ok, RenderConsole = "{0.0.0.00000000}.{x}" };
        f.Reg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{x}", ("DeviceState", "DWord", "1"));
        var s = (await Pcs.Orchestrator(f).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        Assert.DoesNotContain(s.Audio.Endpoints.Value, e => e.IsDefault == true);
        Assert.All(s.Audio.Endpoints.Value, e => Assert.Null(e.IsDefault)); // not read, never "not default"
        Assert.Contains("skipped:default_endpoints_engine_is_not_the_player", s.Runs.Single(r => r.CollectorId == "audio").IssueCodes);
    }

    [Fact]
    public void Redactor_keeps_only_safe_environment_values_and_executables_of_command_lines()
    {
        var entries = new List<RawCaptureEntry>
        {
            new() { CollectorId = "os", Source = @"registry:HKU\" + CanaryTokens.Sid + @"\Environment", Payload = "{\"status\":\"ok\",\"values\":[{\"name\":\"TEMP\",\"kind\":\"ExpandString\",\"data\":\"%USERPROFILE%\\\\AppData\\\\Local\\\\Temp\"},{\"name\":\"OPENAI_API_KEY\",\"kind\":\"String\",\"data\":\"sk-secret\"},{\"name\":\"Path\",\"kind\":\"String\",\"data\":\"C:\\\\secret\\\\dir\"}]}" },
            new() { CollectorId = "software", Source = @"registry:HKLM\SYSTEM\CurrentControlSet\Services\x", Payload = "{\"status\":\"ok\",\"values\":[{\"name\":\"ImagePath\",\"kind\":\"ExpandString\",\"data\":\"\\\"C:\\\\Program Files\\\\X\\\\svc.exe\\\" --token=abc\"}]}" },
            new() { CollectorId = "software", Source = @"registry:HKU\" + CanaryTokens.Sid + @"\Software\Microsoft\Windows\CurrentVersion\Run", Payload = "{\"status\":\"ok\",\"values\":[{\"name\":\"Discord\",\"kind\":\"String\",\"data\":\"C:\\\\D\\\\Update.exe --processStart " + CanaryTokens.Jwt + "\"}]}" },
        };
        var redactor = new CaptureRedactor(new CaptureIdentity { Sid = CanaryTokens.Sid, ProfilePath = @"C:\Users\" + CanaryTokens.UserName, UserName = CanaryTokens.UserName, MachineName = CanaryTokens.ComputerName });
        var text = string.Join("\n", redactor.Redact(entries).Select(e => e.Source + " " + e.Payload));
        Assert.DoesNotContain("OPENAI_API_KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret\\\\dir", text, StringComparison.Ordinal);
        Assert.Matches("\"name\":\"Path\",\"kind\":\"String\",\"data\":\"\\.{13}\"", text);
        Assert.DoesNotContain("--token", text, StringComparison.Ordinal);
        Assert.Contains("svc.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("processStart", text, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
    }

    /// <summary>Makes file reads whose path contains one of the needles fail with AccessDenied.</summary>
    private sealed class DeniedReads
    {
        public DeniedReads(FakeSources fake, params string[] needles)
        {
            Fake = fake;
            foreach (var key in fake.Texts.Keys.Where(k => needles.Any(n => k.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                fake.Texts.Remove(key);
                fake.Stats[key] = new FileStat { Status = ReadStatus.AccessDenied };
            }

            fake.DeniedTextNeedles.AddRange(needles);
        }

        public FakeSources Fake { get; }
    }
}
