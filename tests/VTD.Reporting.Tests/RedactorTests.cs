using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary>Attribute-driven share redaction on the three fixture PCs.</summary>
public sealed class RedactorTests
{
    public static TheoryData<string> FixtureNames => [.. Fixtures.Names];

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Share_output_matches_the_golden_file(string fixture)
    {
        var redacted = TestSupport.Redactor().Redact(TestSupport.Snapshot(fixture), RedactionPolicy.Share);
        var actual = TestSupport.Normalize(VtdJson.Serialize(redacted.Value, indented: true));
        var path = Path.Combine(TestSupport.GoldenDir, fixture + ".share.json");
        if (TestSupport.UpdateGolden || !File.Exists(path))
        {
            Directory.CreateDirectory(TestSupport.GoldenDir);
            File.WriteAllText(path, actual);
        }

        Assert.Equal(TestSupport.Normalize(File.ReadAllText(path)), actual);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Share_output_is_stable_valid_and_clean(string fixture)
    {
        var snapshot = TestSupport.Snapshot(fixture);
        var a = TestSupport.Redactor().Redact(snapshot, RedactionPolicy.Share);
        var b = new Redactor(TestSupport.Salt).Redact(snapshot, RedactionPolicy.Share);
        var json = VtdJson.Serialize(a.Value);
        Assert.Equal(json, VtdJson.Serialize(b.Value));
        Assert.Empty(a.SweepHits);
        Assert.False(a.Blocked);
        _ = VtdJson.Deserialize<ScanSnapshot>(json); // strict round trip
        Assert.Equal(RedactionLevel.Share, a.Value.Meta.Redaction.Level);
        Assert.Contains("device.instance_tail", a.Value.Meta.Redaction.AppliedRules);
        Assert.Equal(RedactionLevel.Share, a.Manifest.Level);
        Assert.NotEmpty(a.Manifest.Entries);
        Assert.All(a.Manifest.Entries, e => Assert.StartsWith("/", e.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Realtek_share_keeps_what_helpers_need_and_rewrites_identifiers()
    {
        var s = TestSupport.Redactor().Redact(TestSupport.Snapshot(Fixtures.RealtekStutter), RedactionPolicy.Share).Value;
        var adapters = s.Network.Adapters.Value;
        var realtek = adapters[0];

        // Kept: driver names, versions, VEN/DEV, event ids, KB numbers, board, CPU.
        Assert.StartsWith("PCI\\VEN_10EC&DEV_8125&SUBSYS_87D71043&REV_05\\h:", realtek.InstanceId, StringComparison.Ordinal);
        Assert.DoesNotContain("01000000684CE00000", realtek.InstanceId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("rt25cx21x64.sys", realtek.DriverFile);
        Assert.Equal("10.68.815.2023", realtek.DriverVersion);
        Assert.Equal("Realtek Gaming 2.5GbE Family Controller", realtek.Description);
        Assert.Equal("1", realtek.AdvancedProperties["*EEE"]);
        Assert.Contains(s.Drivers.Value, d => d.Name == "rt25cx21x64.sys" && d.Version == "10.68.815.2023");
        Assert.Contains(s.Events.Hits.Value, h => h.Provider == "rt25cx21" && h.EventId > 0);
        Assert.Equal("KB5066835", s.Os.Hotfixes.Value[0].Id);
        Assert.Equal("AMD Ryzen 7 9800X3D 8-Core Processor", s.Hardware.Cpu.Value.Model);

        // Rewritten: MAC (OUI kept), IPs (class), SSID and audio names (hashed), interface GUID (dropped), profile paths.
        Assert.StartsWith("04:42:1A:h:", realtek.Mac, StringComparison.Ordinal);
        Assert.DoesNotContain("5C:E0:7F", realtek.Mac, StringComparison.OrdinalIgnoreCase);
        Assert.Null(realtek.InterfaceGuid);
        var wifi = adapters[1];
        Assert.Equal(["private/24"], wifi.Ipv4Addresses);
        Assert.Equal(["private/24"], wifi.DnsServers);
        Assert.StartsWith("h:", s.Network.Wlan.Value.Ssid, StringComparison.Ordinal);
        Assert.StartsWith("h:", s.Audio.Endpoints.Value[0].Id, StringComparison.Ordinal);
        Assert.StartsWith("h:", s.Audio.Endpoints.Value[0].Name, StringComparison.Ordinal);
        var discord = s.Apps.Value.Single(a => a.KeyName == "Discord");
        Assert.Equal("%USERPROFILE%\\AppData\\Local\\Discord", discord.InstallLocation);
        Assert.Equal("%USERPROFILE%\\AppData\\Local\\Discord\\Update.exe", s.Startup.Value[0].TargetPath);
        Assert.Equal("HKLM:{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver", EntityIds.App(AppHive.Hklm, s.Apps.Value[2].KeyName));
    }

    [Fact]
    public void Different_salt_gives_different_hashes_same_salt_same_hashes()
    {
        var snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var a = new Redactor(TestSupport.Salt).Redact(snapshot, RedactionPolicy.Share).Value.Network.Adapters.Value[0].InstanceId;
        var b = new Redactor(TestSupport.Salt).Redact(snapshot, RedactionPolicy.Share).Value.Network.Adapters.Value[0].InstanceId;
        var c = new Redactor(TestSupport.OtherSalt).Redact(snapshot, RedactionPolicy.Share).Value.Network.Adapters.Value[0].InstanceId;
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ShareEntityId_matches_the_id_in_the_share_snapshot()
    {
        var redactor = TestSupport.Redactor();
        var snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var shared = redactor.Redact(snapshot, RedactionPolicy.Share).Value;
        var live = new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId };
        Assert.Equal(shared.Network.Adapters.Value[0].InstanceId, redactor.ShareEntityId(live));
        Assert.Equal(redactor.ShareEntityId(live), redactor.ShareEntityId(live with { Id = live.Id.ToLowerInvariant() }), ignoreCase: true);
        Assert.Equal("NahimicService", redactor.ShareEntityId(new EntityRef { Kind = EntityKind.Service, Id = "NahimicService" }));
        Assert.Equal("rt25cx21x64.sys", redactor.ShareEntityId(new EntityRef { Kind = EntityKind.Driver, Id = "rt25cx21x64.sys" }));
        Assert.Equal("%USERPROFILE%\\AppData\\x.exe", redactor.ShareEntityId(new EntityRef { Kind = EntityKind.File, Id = "C:\\Users\\Ayse\\AppData\\x.exe" }));
        Assert.Equal("%USERPROFILE%\\AppData\\Local\\Discord\\Update.exe", redactor.ShareEntityId(new EntityRef { Kind = EntityKind.CompatLayer, Id = "C:\\Users\\<user>\\AppData\\Local\\Discord\\Update.exe" }));
        Assert.StartsWith("h:", redactor.ShareEntityId(new EntityRef { Kind = EntityKind.AudioEndpoint, Id = "{0.0.0.00000000}.{6a1f}" }), StringComparison.Ordinal);
        Assert.Equal("HKLM:{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver", redactor.ShareEntityId(new EntityRef { Kind = EntityKind.App, Id = "HKLM:{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver" }));
        Assert.Equal("HKCU\\Software\\[sid]|x", redactor.ShareEntityId(new EntityRef { Kind = EntityKind.RegistryValue, Id = "HKCU\\Software\\" + CanaryTokens.Sid + "|x" }));
    }

    [Fact]
    public void Device_ids_keep_enumerator_and_safe_device_segment_only()
    {
        var r = TestSupport.Redactor();
        string Share(string id) => r.ShareEntityId(new EntityRef { Kind = EntityKind.Device, Id = id });
        Assert.StartsWith("USB\\VID_046D&PID_C539\\h:", Share("USB\\VID_046D&PID_C539\\5&2A3B4C5D&0&2"), StringComparison.Ordinal);
        Assert.StartsWith("ACPI\\MSFT0101\\h:", Share("ACPI\\MSFT0101\\1"), StringComparison.Ordinal);
        var bt = Share("BTHENUM\\{0000110b-0000-1000-8000-00805f9b34fb}_VID&0001005d_PID&a1b2\\7&2F3A&0&001A7DDA7113_C00000000");
        Assert.StartsWith("BTHENUM\\h:", bt, StringComparison.Ordinal);
        Assert.DoesNotContain("001A7DDA7113", bt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PCI\\VEN_10DE&DEV_2783", Share("PCI\\VEN_10DE&DEV_2783"));
    }

    [Fact]
    public void Analysis_entity_ids_and_free_text_args_are_redacted()
    {
        var snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var analysis = CannedAnalyses.For(snapshot);
        var redactor = TestSupport.Redactor();
        var red = redactor.Redact(analysis, RedactionPolicy.Share).Value;
        var shareId = redactor.ShareEntityId(new EntityRef { Kind = EntityKind.Adapter, Id = FixtureSnapshots.RealtekAdapterInstanceId });
        Assert.Equal(shareId, red.Hypotheses[0].Entities[0].Id);
        Assert.Equal(shareId, red.Proposals[0].Binding.Entities["adapter"][0].Id);
        Assert.Equal("14", red.Hypotheses[0].Contributions.Single(c => c.EvidenceId == "resets").Evidence[0].Args["count"]);
    }

    [Fact]
    public void Excluded_sections_become_unknown_redacted_or_empty()
    {
        var policy = new RedactionPolicy { Level = RedactionLevel.Share, ExcludedSections = ["apps", "network", "runs", "meta"] };
        var red = TestSupport.Redactor().Redact(TestSupport.Snapshot(Fixtures.RealtekStutter), policy);
        var s = red.Value;
        Assert.Equal(FactStatus.Unknown, s.Apps.Status);
        Assert.Equal(UnknownReason.Redacted, s.Apps.Reason);
        Assert.Equal(UnknownReason.Redacted, s.Network.Adapters.Reason);
        Assert.Equal(UnknownReason.Redacted, s.Network.Wlan.Reason);
        Assert.Empty(s.Runs);
        Assert.Equal(FixtureSnapshots.RealtekId, s.Meta.SnapshotId); // meta can't be excluded
        Assert.Equal(["apps", "network", "runs", "meta"], red.Manifest.ExcludedSections);
        Assert.Contains(red.Manifest.Entries, e => e.Path == "/apps" && e.Rule == "section.excluded");
    }

    [Fact]
    public void Server_ip_is_dropped_unless_the_player_ticks_include()
    {
        var monitor = TestSupport.FakeMonitorSummary();
        var red = TestSupport.Redactor().Redact(monitor, RedactionPolicy.Share).Value;
        Assert.Null(red.Network!.EndpointIp);
        Assert.Equal("AWS Frankfurt", red.Network.EndpointRegion);
        var kept = TestSupport.Redactor().Redact(monitor, RedactionPolicy.Share with { IncludeServerIp = true }).Value;
        Assert.Equal("203.0.113.77", kept.Network!.EndpointIp);
    }

    [Fact]
    public void Local_level_only_scrubs_secrets()
    {
        var snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var planted = snapshot with
        {
            Apps = Fact.Known<IReadOnlyList<InstalledApp>>([.. snapshot.Apps.Value.Select((a, i) => i == 0 ? a with { DisplayName = "x " + CanaryTokens.RemotingAuthToken, InstallLocation = "C:\\Users\\Ayse\\x" } : a)]),
        };
        var local = TestSupport.Redactor().Redact(planted, new RedactionPolicy { Level = RedactionLevel.Local }).Value;
        Assert.Equal("x --remoting-auth-token=[redacted]", local.Apps.Value[0].DisplayName);
        Assert.Equal("C:\\Users\\Ayse\\x", local.Apps.Value[0].InstallLocation);
        Assert.Equal(FixtureSnapshots.RealtekAdapterInstanceId, local.Network.Adapters.Value[0].InstanceId);
        Assert.Equal(RedactionLevel.Local, local.Meta.Redaction.Level);
    }

    [Fact]
    public void RedactText_scrubs_log_lines()
    {
        var r = new Redactor(TestSupport.Salt, ["DESKTOP-ROOM2"]);
        var line = "LogInit: CommandLine=VALORANT.exe -subject=abc " + CanaryTokens.RemotingAuthToken + " on DESKTOP-ROOM2 as " + CanaryTokens.Email + " sid " + CanaryTokens.Sid + " in " + CanaryTokens.UserProfilePath;
        var share = r.RedactText(line, RedactionPolicy.Share);
        Assert.Empty(RedactionSweep.Detect(share));
        Assert.DoesNotContain("DESKTOP-ROOM2", share, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("--remoting-auth-token=[redacted] x", r.RedactText(CanaryTokens.RemotingAuthToken + " x", new RedactionPolicy { Level = RedactionLevel.Local }));
        Assert.Equal("ok DESKTOP-4F2K9QH", r.RedactText("ok DESKTOP-4F2K9QH", new RedactionPolicy { Level = RedactionLevel.Local }));
        Assert.Equal("ok [host]", r.RedactText("ok DESKTOP-4F2K9QH", RedactionPolicy.Share));
    }

    [Fact]
    public void Identifiers_from_the_model_are_removed_everywhere_but_not_inside_words()
    {
        var snapshot = TestSupport.Snapshot(Fixtures.RealtekStutter);
        var apps = snapshot.Apps.Value.ToList();
        apps[0] = apps[0] with { InstallLocation = "C:\\Users\\Ali\\Games" };
        apps[1] = apps[1] with { DisplayName = "Discord (Ali)" };
        apps[2] = apps[2] with { DisplayName = "Alienware Command Center" };
        var red = TestSupport.Redactor().Redact(snapshot with { Apps = Fact.Known<IReadOnlyList<InstalledApp>>(apps) }, RedactionPolicy.Share).Value;
        Assert.Equal("%USERPROFILE%\\Games", red.Apps.Value[0].InstallLocation);
        Assert.Equal("Discord ([id])", red.Apps.Value[1].DisplayName);
        Assert.Equal("Alienware Command Center", red.Apps.Value[2].DisplayName);
    }

    [Fact]
    public void Report_model_redaction_stamps_snapshot_and_manifest()
    {
        var snapshot = TestSupport.Snapshot(Fixtures.NahimicBlackScreen);
        var model = new ReportModel
        {
            SchemaVersion = ReportModel.CurrentSchemaVersion,
            Meta = new ReportMeta { ReportId = "01JB6R7X3K9M2N4P5Q6R7S8TB1", CreatedUtc = FakeClock.DefaultStart, AppVersion = "0.1.0", KbVersion = "2026.10.21.1", Language = "en", MachineKey = snapshot.Meta.MachineKey },
            Snapshot = snapshot,
            Analysis = CannedAnalyses.For(snapshot),
            Redaction = new RedactionManifest { Level = RedactionLevel.None, RulesVersion = 0 },
        };
        var red = TestSupport.Redactor().Redact(model, RedactionPolicy.Share);
        Assert.Equal(RedactionLevel.Share, red.Value.Snapshot.Meta.Redaction.Level);
        Assert.Equal(RedactionLevel.Share, red.Value.Redaction.Level);
        Assert.Equal(red.Manifest.Entries.Count, red.Value.Redaction.Entries.Count);
        Assert.All(red.Manifest.Entries.Where(e => e.Rule == "device.instance_tail"), e => Assert.StartsWith("/snapshot/", e.Path, StringComparison.Ordinal));
    }
}
