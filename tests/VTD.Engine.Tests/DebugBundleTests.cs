using System.IO.Compression;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Ipc;
using VTD.Contracts.Reporting;
using VTD.Contracts.Sessions;
using VTD.Engine.Cli;
using VTD.Engine.Diagnostics;
using VTD.Reporting;
using VTD.Session;
using VTD.Testing.Fakes;

namespace VTD.Engine.Tests;

/// <summary>The rolling redacted log and the .vtddebug bundle: canary tokens must never reach the disk or the zip.</summary>
public sealed class DebugBundleTests
{
    private static readonly RedactionPolicy Share = new() { Level = RedactionLevel.Share };

    private static Redactor NewRedactor() => new([.. Enumerable.Range(1, 32).Select(i => (byte)i)], [CanaryTokens.UserName, CanaryTokens.ComputerName]);

    private static string Folder() => Path.Combine(Path.GetTempPath(), "vtd-log-tests-" + Guid.NewGuid().ToString("N"));

    private static string AllText(DiagLog log) => string.Concat(log.ReadAll().Select(f => f.Text));

    private static Dictionary<string, string> Unzip(string base64)
    {
        using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(base64)), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void Log_lines_are_redacted_at_write_time()
    {
        var redactor = NewRedactor();
        var folder = Folder();
        try
        {
            var log = new DiagLog(folder, new FakeClock(), t => redactor.RedactText(t, Share));
            log.Write(ClientLogLevel.Info, "engine", "test.canary", string.Join(' ', CanaryTokens.All), [new("path", CanaryTokens.UserProfilePath)], new InvalidOperationException(CanaryTokens.UserName + " on " + CanaryTokens.ComputerName));
            var text = AllText(log);
            Assert.Contains("test.canary", text, StringComparison.Ordinal);
            foreach (var canary in CanaryTokens.All)
            {
                Assert.DoesNotContain(canary, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("VTDCANARY", text, StringComparison.OrdinalIgnoreCase);
            Assert.True(System.Text.Json.JsonDocument.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement.TryGetProperty("t", out _));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Log_rolls_and_stays_under_its_size_cap_and_debug_entries_need_debug_mode()
    {
        var folder = Folder();
        try
        {
            var log = new DiagLog(folder, new FakeClock(), t => t, maxFileBytes: 2048, maxFiles: 3);
            log.Debug("test.debug", "hidden");
            Assert.Empty(log.ReadAll());
            for (var i = 0; i < 400; i++)
            {
                log.Info("test.fill", new string('x', 60));
            }

            var files = log.ReadAll();
            Assert.InRange(files.Count, 2, 3);
            Assert.True(files.Sum(f => Encoding.UTF8.GetByteCount(f.Text)) < 3 * (2048 + 200));
            Assert.DoesNotContain("hidden", AllText(log), StringComparison.Ordinal);

            log.DebugMode = true;
            log.Debug("test.debug", "shown");
            Assert.Contains("shown", AllText(log), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task UI_log_entries_reach_the_engine_log_redacted_and_only_from_the_UI()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = await TestEngine.StartAsync();
        var entry = new ClientLogEntry
        {
            Utc = engine.Clock.GetUtcNow(),
            Level = ClientLogLevel.Error,
            Event = "app.unhandled",
            Message = "boom " + CanaryTokens.Email,
            Detail = "at X in " + CanaryTokens.UserProfilePath + " " + CanaryTokens.RemotingAuthToken + " " + CanaryTokens.UserName,
        };
        using (var cli = engine.Connect(EventOrigin.Cli))
        {
            await cli.HelloAsync(TestEngine.Hello(ClientKind.Cli), ct);
            await cli.SubmitClientLogAsync([entry], ct);
        }

        Assert.DoesNotContain("app.unhandled", AllText(engine.Core.Log), StringComparison.Ordinal);
        using (var ui = engine.Connect(EventOrigin.Ui))
        {
            await ui.HelloAsync(TestEngine.Hello(), ct);
            Assert.True(await ui.SetDebugModeAsync(true, ct));
            await ui.SubmitClientLogAsync([entry, entry with { Event = "bad event name" }], ct);
        }

        var text = AllText(engine.Core.Log);
        Assert.Contains("app.unhandled", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bad event name", text, StringComparison.Ordinal);
        Assert.Contains("\"src\":\"app\"", text, StringComparison.Ordinal);
        foreach (var canary in CanaryTokens.All)
        {
            Assert.DoesNotContain(canary, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_bundle_after_a_scan_has_every_part_and_no_canary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = await TestEngine.StartAsync();
        using var ui = new VTD.Engine.Ipc.RpcSurface(engine.Connect(EventOrigin.Ui));
        await ui.HelloAsync(TestEngine.Hello(), ct);
        engine.Core.Log.Info("test.canary", string.Join(' ', CanaryTokens.All));
        var scanned = await ui.SendAsync(new ScanRequested { EventId = engine.Ids.NewId(), Utc = engine.Clock.GetUtcNow() }, ct);
        Assert.Equal(Phase.Analyzed, scanned.Phase);

        var result = await ui.ExportAsync(new ExportRequest { Kind = ExportKind.DebugBundle }, ct);
        Assert.Equal(ExportStatus.Ok, result.Status);
        Assert.EndsWith(".vtddebug", result.FileName, StringComparison.Ordinal);
        var parts = Unzip(result.Base64Content!);
        Assert.Contains("manifest.json", parts.Keys);
        Assert.Contains("session.json", parts.Keys);
        Assert.Contains("snapshot.json", parts.Keys);
        Assert.Contains("analysis.json", parts.Keys);
        Assert.Contains("environment.json", parts.Keys);
        Assert.Contains("logs/" + DiagLog.CurrentFileName, parts.Keys);
        Assert.Contains("\"format\": \"vtd-debug/1\"", parts["manifest.json"], StringComparison.Ordinal);
        Assert.Contains("session.phase", parts["logs/" + DiagLog.CurrentFileName], StringComparison.Ordinal);
        Assert.Contains("scan.done", parts["logs/" + DiagLog.CurrentFileName], StringComparison.Ordinal);
        Assert.Contains("analysis.done", parts["logs/" + DiagLog.CurrentFileName], StringComparison.Ordinal);
        Assert.Contains("rpc.call", parts["logs/" + DiagLog.CurrentFileName], StringComparison.Ordinal);
        foreach (var (name, text) in parts)
        {
            foreach (var canary in CanaryTokens.All)
            {
                Assert.False(text.Contains(canary, StringComparison.OrdinalIgnoreCase), name + " leaks " + canary);
            }
        }
    }

    [Fact]
    public async Task A_bundle_without_any_scan_still_builds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = await TestEngine.StartAsync();
        using var ui = engine.Connect(EventOrigin.Ui);
        await ui.HelloAsync(TestEngine.Hello(), ct);
        var result = await ui.ExportAsync(new ExportRequest { Kind = ExportKind.DebugBundle }, ct);
        Assert.Equal(ExportStatus.Ok, result.Status);
        var parts = Unzip(result.Base64Content!);
        Assert.DoesNotContain("snapshot.json", parts.Keys);
        Assert.Contains("\"reason\": \"not_available\"", parts["manifest.json"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_sweep_hit_or_a_left_over_identifier_blocks_the_bundle_and_names_the_section()
    {
        var redactor = NewRedactor();
        var clock = new FakeClock();
        var builder = new DebugBundleBuilder(redactor, redactor.ContainsKnownIdentifier, new SequentialIdGenerator(clock), clock);
        var session = SessionMachine.NewSession("01JB6R7X3K9M2N4P5Q6R7S8TA0", clock.GetUtcNow(), PrivilegeMode.Limited, "0.1.0", "S-1-5-21-1-2-3-1001");

        DebugBundleInput Input(string logText) => new()
        {
            Session = session,
            Logs = [("vtd.log.jsonl", logText)],
            EngineVersion = "0.1.0",
            KbVersion = "0.1.0",
            Privilege = PrivilegeMode.Limited,
        };

        var jwt = builder.Build(new ExportRequest { Kind = ExportKind.DebugBundle }, Input("{\"msg\":\"" + CanaryTokens.Jwt + "\"}\n"));
        Assert.Equal(ExportStatus.BlockedByRedaction, jwt.Status);
        Assert.Contains(jwt.SweepHits, h => h.Section == "logs" && h.Rule == RedactionSweep.Rules.Jwt);

        var name = builder.Build(new ExportRequest { Kind = ExportKind.DebugBundle }, Input("{\"msg\":\"played by " + CanaryTokens.UserName + "\"}\n"));
        Assert.Equal(ExportStatus.BlockedByRedaction, name.Status);
        Assert.Contains(name.SweepHits, h => h.Section == "logs" && h.Rule == "identifier");

        // The player leaves the section out and the bundle goes through.
        var left = builder.Build(new ExportRequest { Kind = ExportKind.DebugBundle, ExcludedSections = ["logs"] }, Input("{\"msg\":\"" + CanaryTokens.Jwt + "\"}\n"));
        Assert.Equal(ExportStatus.Ok, left.Status);
        Assert.Contains("left_out_by_player", Unzip(left.Base64Content!)["manifest.json"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_CLI_takes_a_debug_bundle_path_and_the_debug_flag()
    {
        var parsed = CliArguments.Parse(["export", "--debug-bundle", "out.vtddebug", "--debug"])!;
        Assert.Equal(ExportKind.DebugBundle, parsed.Kind);
        Assert.Equal("out.vtddebug", parsed.Out);
        Assert.True(parsed.Debug);
        Assert.Null(CliArguments.Parse(["scan", "--debug-bundle", "x"]));
        Assert.Null(CliArguments.Parse(["export", "--debug-bundle"]));
    }
}
