using VTD.Collectors.Logs;
using VTD.Collectors.Tests.Support;
using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Collectors.Tests;

public sealed class LogExtractorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Shooter_filter_drops_every_secret_line_and_emits_only_records()
    {
        var out1 = Pcs.ShooterLines().Select(ShooterLogExtractor.Map).Where(x => x is not null).Select(x => x!).ToList();
        var text = string.Join("\n", out1);
        foreach (var canary in CanaryTokens.All)
        {
            Assert.DoesNotContain(canary, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Command Line", text, StringComparison.Ordinal);
        Assert.All(out1, l => Assert.StartsWith("shooter.", l, StringComparison.Ordinal));
        Assert.All(out1, l => Assert.NotNull(LogRecord.Decode(l)));
    }

    [Fact]
    public void Shooter_digest_parses_the_allowlisted_patterns()
    {
        var records = Pcs.ShooterLines().Select(ShooterLogExtractor.Map).Where(x => x is not null).Select(x => (LogRecord.Decode(x!)!, true)).ToList();
        var codes = new List<DetectedCode>();
        var d = ShooterLogExtractor.Build(records, Pcs.Start, 3, codes);
        Assert.Equal("release-11.08-shipping-14-3988147", d.GameVersion);
        Assert.Equal("Windows 11 (25H2) [10.0.26200.6899] ()", d.Hardware!.Os);
        Assert.Equal("NVIDIA GeForce RTX 4070 SUPER", d.Hardware.Gpu);
        Assert.Equal("SSD", d.Hardware.Disk);
        var failure = Assert.Single(d.PlatformInitFailures);
        Assert.Equal(("ContractsManager", "RecruitmentInitConfigurationFailure", 2), (failure.Manager, failure.Error, failure.Count));
        Assert.Equal(1, d.D3dDeviceLostCount);
        Assert.Equal(1, d.NetDisconnectCount);
        Assert.Equal(1, d.FatalErrorCount);
        Assert.Equal(3, d.BackupLogCount);
        var code = Assert.Single(codes);
        Assert.Equal(("VAN 9003", CodeSource.ShooterLog, 1), (code.Code, code.Source, code.Count));
    }

    [Fact]
    public void Riot_client_filter_counts_codes_and_drops_tokens()
    {
        var out1 = Pcs.ClientLines().Select(RiotClientLogExtractor.Map).Where(x => x is not null).Select(x => LogRecord.Decode(x!)!).ToList();
        Assert.Equal(2, out1.Count(r => r.PatternId == "client.van_error_dialog"));
        Assert.Single(out1, r => r.PatternId == "client.login_error");
        Assert.Single(out1, r => r.PatternId == "client.patch_failed");
        Assert.DoesNotContain(out1, r => r.PatternId == "client.crash");
        Assert.DoesNotContain(out1, r => r.Groups.Any(g => g.Contains("eyJ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Crash_context_keeps_only_type_pattern_and_drive()
    {
        var out1 = Pcs.CrashLines().Select(CrashContextExtractor.Map).Where(x => x is not null).Select(x => x!).ToList();
        Assert.Equal(["type|GPUCrash", "error|crash.d3d_device_lost", "drive|C:"], out1);
    }

    [Fact]
    public void Game_settings_filter_is_a_whitelist()
    {
        string[] lines = ["[/Script/ShooterGame.ShooterGameUserSettings]", "LastConfirmedDefaultMonitorDeviceID=\"MONITOR\\\\MSI\\\\{x}\"", "FullscreenMode=0", "UserName=" + CanaryTokens.UserName, "ResolutionSizeX=2560"];
        var out1 = lines.Select(GameConfigExtractor.Map).Where(x => x is not null).ToList();
        Assert.Equal(["section|", "kv|FullscreenMode|0", "kv|ResolutionSizeX|2560"], out1);
        Assert.Equal("corrupt|nul", GameConfigExtractor.Map("Fullscreen\0\0"));
    }

    [Theory]
    [InlineData("Error VAN 9003 occurred", "VAN 9003")]
    [InlineData("errorCode\":\"VAN_9001\"", "VAN 9001")]
    [InlineData("VAN: RESTRICTION: 1 Secure Boot", "VAN: RESTRICTION: 1")]
    [InlineData("VAN: RESTRICTION", "VAN: RESTRICTION")]
    [InlineData("code VAN -81 seen", "VAN -81")]
    [InlineData("Login failed VAL 43", "VAL 43")]
    public void Codes_are_normalized(string line, string expected)
        => Assert.Contains(expected, CodeNormalizer.Find(line));

    [Theory]
    [InlineData("vanguard: Discovered vanguard version 1.17 at startup")]
    [InlineData("VALORANT-Win64-Shipping.exe started")]
    [InlineData("VAN9003X")]
    public void Non_codes_are_ignored(string line) => Assert.Empty(CodeNormalizer.Find(line));

    [Fact]
    public void Scrub_removes_profile_paths_guids_ips_and_tokens()
    {
        var s = LogSafety.Scrub(CanaryTokens.UserProfilePath + " 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0 10.1.2.3 " + new string('A', 40));
        Assert.DoesNotContain("VtdCanaryUser", s, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", s, StringComparison.Ordinal);
        Assert.Contains("<guid>", s, StringComparison.Ordinal);
        Assert.Contains("<ip>", s, StringComparison.Ordinal);
        Assert.Contains("<token>", s, StringComparison.Ordinal);
        Assert.Contains("10.0.26200.1", LogSafety.Scrub("10.0.26200.1", keepDottedNumbers: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Riot_section_end_to_end_contains_no_canary()
    {
        var s = (await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        var json = VtdJson.Serialize(s.Riot);
        foreach (var canary in CanaryTokens.All)
        {
            Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(s.Riot.DetectedCodes.Value, c => c.Code == "VAN 9003" && c.Source == CodeSource.RiotClientLog && c.Count == 2);
        Assert.Equal("crash.d3d_device_lost", Assert.Single(s.Riot.Crashes.Value).ErrorPattern);
        Assert.Equal(1, s.Riot.GameConfig.Value.AccountConfigCount);
        Assert.Equal("0", s.Riot.GameConfig.Value.Settings["FullscreenMode"]);
        Assert.False(s.Riot.GameConfig.Value.Settings.ContainsKey("UserName"));
        Assert.Contains(s.Riot.ShooterLog.Value.PlatformInitFailures, p => p.Manager == "VanguardManager");
        Assert.Equal("release-11.08-shipping-14-3988147", s.Riot.Valorant.Value.Version);
        Assert.True(s.Riot.ClientRunning.Value);
        Assert.False(s.Riot.GameRunning.Value);
    }

    [Fact]
    public async Task Startup_and_apps_never_keep_arguments_or_uninstall_commands()
    {
        var s = (await Pcs.Orchestrator(Pcs.Healthy()).ScanAsync(Pcs.Request(), null, Ct)).Snapshot;
        var json = VtdJson.Serialize(s);
        Assert.DoesNotContain(CanaryTokens.Jwt, json, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryTokens.RemotingAuthToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain("--processStart", json, StringComparison.Ordinal);
        var discord = Assert.Single(s.Startup.Value, x => x.Name == "Discord");
        Assert.Equal("Update.exe", discord.ImageName);
    }
}
