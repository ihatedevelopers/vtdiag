using System.Globalization;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Scanning;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.Engine.Cli;

/// <summary>
/// <c>VTDiag.Engine.exe --cli …</c> (DESIGN.md section 3): a pure <see cref="IEngineRpc"/> client with the verified origin
/// <see cref="EventOrigin.Cli"/>. Read-only verbs (<c>scan</c>, <c>analyze</c>, <c>export</c>, <c>status</c>) work
/// unelevated through an in-process LIMITED engine when no elevated engine runs. Write verbs can only PROPOSE
/// (<c>fix propose</c>, <c>undo propose</c>): the player decides in the VTDiag window; the CLI can wait for the outcome
/// but never consent. JSON goes to stdout, diagnostics to stderr; exit codes are <see cref="CliExitCode"/>.
/// </summary>
public static class CliApp
{
    /// <summary>Usage text.</summary>
    public const string Usage = """
        VTDiag.Engine.exe --cli <verb> [options]

          scan [--json] [--quick] [--no-network]          scan this PC (share-redacted snapshot)
          analyze [--json] [--no-network]                 scan, then rank suspects (share-redacted analysis)
          export [--kind html|md|json|ai|vtdscan] [--lang en|tr] [--out <file>] [--exclude <section>]...
          export --debug-bundle <file.vtddebug> [--exclude logs|snapshot|analysis|session|journal]...
                                                          one redacted zip (logs, last scan, analysis, journal, session,
                                                          environment) to send when something goes wrong
          --debug                                         (any verb) raise the log verbosity for this run
          status [--json]                                 the current session
          fix preview <fixId> [--bind name=Kind:Id]...    what a fix would do (needs the elevated engine)
          fix propose <fixId> [--bind name=Kind:Id]... [--plan <planId>] [--wait <seconds>] [--label <text>]
                                                          propose a fix: the player decides in the VTDiag window
          undo propose <attemptId> [--label <text>]       propose an undo: the player decides in the VTDiag window

        The CLI can never give consent, restart the PC or undo by itself (consent is collected in the UI only).
        """;

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Runs one CLI command.</summary>
    /// <param name="args">Arguments after <c>--cli</c>.</param>
    /// <param name="stdout">Standard output (JSON).</param>
    /// <param name="stderr">Diagnostics.</param>
    /// <param name="connect">Engine connector (default: the elevated engine, else an in-process limited one for read-only verbs).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, Func<bool, bool, CancellationToken, Task<EngineClient?>>? connect = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var parsed = CliArguments.Parse(args);
        if (parsed is null)
        {
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }

        if (parsed.Verb == "help")
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return (int)CliExitCode.Ok;
        }

        connect ??= DefaultConnectAsync;
        var needsElevated = parsed.Verb is "fix" or "undo";
        EngineClient? client;
        try
        {
            client = await connect(needsElevated, !parsed.NoNetwork, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(stderr, CodeOf(ex)).ConfigureAwait(false);
        }

        if (client is null)
        {
            await stderr.WriteLineAsync("cli.needs_elevated_engine: open VTDiag and allow it to make changes, then try again.").ConfigureAwait(false);
            return (int)CliExitCode.NotElevated;
        }

        await using (client.ConfigureAwait(false))
        {
            try
            {
                var engine = client.Engine;
                var hello = await engine.HelloAsync(new ClientHello { ProtocolVersion = EngineProtocol.Version, AppVersion = EngineComposition.Version(), Language = parsed.Language, Kind = ClientKind.Cli, Label = parsed.Label }, cancellationToken).ConfigureAwait(false);
                if (parsed.Debug)
                {
                    await engine.SetDebugModeAsync(true, cancellationToken).ConfigureAwait(false);
                }

                if (client.InProcess)
                {
                    await stderr.WriteLineAsync("cli.limited: no elevated VTDiag engine is running; read-only scan in limited mode (admin-only checks are reported as 'could not check').").ConfigureAwait(false);
                }

                return parsed.Verb switch
                {
                    "scan" => await ScanAsync(engine, parsed, stdout, stderr, analyze: false, cancellationToken).ConfigureAwait(false),
                    "analyze" => await ScanAsync(engine, parsed, stdout, stderr, analyze: true, cancellationToken).ConfigureAwait(false),
                    "export" => await ExportAsync(engine, parsed, stdout, stderr, cancellationToken).ConfigureAwait(false),
                    "status" => await StatusAsync(engine, hello, parsed, stdout, cancellationToken).ConfigureAwait(false),
                    "fix" when parsed.Sub == "preview" => await PreviewAsync(engine, parsed, stdout, cancellationToken).ConfigureAwait(false),
                    "fix" when parsed.Sub == "propose" => await ProposeAsync(engine, parsed, stdout, stderr, cancellationToken).ConfigureAwait(false),
                    "undo" when parsed.Sub == "propose" => await ProposeUndoAsync(engine, parsed, stdout, stderr, cancellationToken).ConfigureAwait(false),
                    _ => (int)CliExitCode.Usage,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await FailAsync(stderr, CodeOf(ex)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Maps a refusal code to an exit code.</summary>
    /// <param name="code">Machine code.</param>
    public static CliExitCode ExitFor(string code) => code switch
    {
        "command.not_allowed" or "redaction.blocked" or "consent.blocked" => CliExitCode.Blocked,
        "fixes.limited_mode" or "consent.limited_mode" or "cli.needs_elevated_engine" => CliExitCode.NotElevated,
        "session.needs_attention" => CliExitCode.NeedsAttention,
        "consent.pending" => CliExitCode.ConsentRequired,
        _ => CliExitCode.Error,
    };

    private static async Task<EngineClient?> DefaultConnectAsync(bool needsElevated, bool allowNetwork, CancellationToken ct)
    {
        var elevated = await EngineClient.TryConnectElevatedAsync(TextWriter.Null, ct).ConfigureAwait(false);
        if (elevated is not null || needsElevated)
        {
            return elevated;
        }

        return await EngineClient.StartInProcessAsync(allowNetwork, ct).ConfigureAwait(false);
    }

    private static async Task<int> ScanAsync(IEngineRpc engine, CliArguments a, TextWriter stdout, TextWriter stderr, bool analyze, CancellationToken ct)
    {
        var started = await engine.SendAsync(new ScanRequested { EventId = NewId(), Utc = Now(), Mode = a.Quick ? ScanMode.Quick : ScanMode.Full }, ct).ConfigureAwait(false);
        var state = await WaitWhileAsync(engine, started, s => s.Phase == Phase.Scanning, ScanTimeout, ct).ConfigureAwait(false);
        if (state.Phase == Phase.Scanning)
        {
            return await FailAsync(stderr, "scan.timeout").ConfigureAwait(false);
        }

        if (state.SnapshotId is null || state.LastErrorCode is "scan.failed" or "scan.interrupted")
        {
            return await FailAsync(stderr, state.LastErrorCode ?? "scan.failed").ConfigureAwait(false);
        }

        if (analyze)
        {
            var analysis = await engine.GetAnalysisAsync(ct).ConfigureAwait(false);
            if (analysis is null)
            {
                return await FailAsync(stderr, "analysis.missing").ConfigureAwait(false);
            }

            await stdout.WriteLineAsync(a.Json ? VtdJson.Serialize(analysis, indented: true) : Summarize(analysis)).ConfigureAwait(false);
            return (int)CliExitCode.Ok;
        }

        var snapshot = await engine.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return await FailAsync(stderr, "scan.missing").ConfigureAwait(false);
        }

        await stdout.WriteLineAsync(a.Json ? VtdJson.Serialize(snapshot, indented: true) : Summarize(snapshot)).ConfigureAwait(false);
        return (int)CliExitCode.Ok;
    }

    private static async Task<int> ExportAsync(IEngineRpc engine, CliArguments a, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var state = await engine.GetSessionAsync(ct).ConfigureAwait(false);
        if (state.SnapshotId is null)
        {
            var started = await engine.SendAsync(new ScanRequested { EventId = NewId(), Utc = Now() }, ct).ConfigureAwait(false);
            state = await WaitWhileAsync(engine, started, s => s.Phase == Phase.Scanning, ScanTimeout, ct).ConfigureAwait(false);

            // A debug bundle is most needed when the scan failed: it goes on without a snapshot.
            if (state.SnapshotId is null && a.Kind != ExportKind.DebugBundle)
            {
                return await FailAsync(stderr, state.LastErrorCode ?? "scan.failed").ConfigureAwait(false);
            }
        }

        var result = await engine.ExportAsync(new ExportRequest { Kind = a.Kind, Language = a.Language, ExcludedSections = a.Excluded }, ct).ConfigureAwait(false);
        if (result.Status == ExportStatus.BlockedByRedaction)
        {
            await stderr.WriteLineAsync("export.blocked_by_redaction: " + string.Join(", ", result.SweepHits.Select(h => (h.Section ?? "?") + ":" + h.Rule))).ConfigureAwait(false);
            return (int)CliExitCode.Blocked;
        }

        if (result.Status != ExportStatus.Ok)
        {
            return await FailAsync(stderr, "export.failed").ConfigureAwait(false);
        }

        if (a.Out is { } path)
        {
            if (result.Base64Content is { } b64)
            {
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(b64), ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(path, result.TextContent ?? string.Empty, new UTF8Encoding(false), ct).ConfigureAwait(false);
            }

            await stderr.WriteLineAsync("export.saved " + path + " sha256=" + result.Sha256).ConfigureAwait(false);
        }
        else
        {
            await stdout.WriteLineAsync(result.TextContent ?? result.Base64Content ?? string.Empty).ConfigureAwait(false);
        }

        return (int)CliExitCode.Ok;
    }

    private static async Task<int> StatusAsync(IEngineRpc engine, EngineHello hello, CliArguments a, TextWriter stdout, CancellationToken ct)
    {
        var state = await engine.GetSessionAsync(ct).ConfigureAwait(false);
        await stdout.WriteLineAsync(a.Json
            ? VtdJson.Serialize(state, indented: true)
            : string.Create(CultureInfo.InvariantCulture, $"engine {hello.EngineVersion} ({hello.Privilege}), KB {hello.KbVersion}\nsession {state.SessionId} r{state.Revision}: {state.Phase}, {state.Attempts.Count} change(s)")).ConfigureAwait(false);
        return state.Phase == Phase.NeedsAttention ? (int)CliExitCode.NeedsAttention : (int)CliExitCode.Ok;
    }

    private static async Task<int> PreviewAsync(IEngineRpc engine, CliArguments a, TextWriter stdout, CancellationToken ct)
    {
        var preview = await engine.PreviewFixAsync(a.Target!, a.Binding, ct).ConfigureAwait(false);
        await stdout.WriteLineAsync(VtdJson.Serialize(preview, indented: true)).ConfigureAwait(false);
        return preview.CanApply ? (int)CliExitCode.Ok : (int)CliExitCode.Blocked;
    }

    private static async Task<int> ProposeAsync(IEngineRpc engine, CliArguments a, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var state = await engine.SendAsync(new FixSelected { EventId = NewId(), Utc = Now(), FixId = a.Target!, Binding = a.Binding, PlanId = a.PlanId }, ct).ConfigureAwait(false);
        var previewId = state.CurrentPreviewId ?? throw new EngineRpcException("fix.preview_failed");
        await stderr.WriteLineAsync("consent.requested: the player decides in the VTDiag window (the CLI cannot consent).").ConfigureAwait(false);
        if (a.WaitSeconds is not { } wait)
        {
            await stdout.WriteLineAsync(VtdJson.Serialize(state, indented: true)).ConfigureAwait(false);
            return (int)CliExitCode.ConsentRequired;
        }

        var outcome = await engine.WaitForOutcomeAsync(previewId, wait, ct).ConfigureAwait(false);
        if (outcome is null)
        {
            await stderr.WriteLineAsync("consent.not_given (declined, skipped or timed out)").ConfigureAwait(false);
            return (int)CliExitCode.ConsentRequired;
        }

        await stdout.WriteLineAsync(VtdJson.Serialize(outcome, indented: true)).ConfigureAwait(false);
        return outcome.Status is AttemptStatus.Failed or AttemptStatus.VerifyFailed or AttemptStatus.RolledBack or AttemptStatus.InDoubt ? (int)CliExitCode.Blocked : (int)CliExitCode.Ok;
    }

    private static async Task<int> ProposeUndoAsync(IEngineRpc engine, CliArguments a, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var state = await engine.SendAsync(new UndoProposed { EventId = NewId(), Utc = Now(), AttemptId = a.Target! }, ct).ConfigureAwait(false);
        await stderr.WriteLineAsync("consent.requested: the player confirms the undo in the VTDiag window.").ConfigureAwait(false);
        await stdout.WriteLineAsync(VtdJson.Serialize(state, indented: true)).ConfigureAwait(false);
        return (int)CliExitCode.ConsentRequired;
    }

    private static async Task<SessionState> WaitWhileAsync(IEngineRpc engine, SessionState state, Func<SessionState, bool> waiting, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = TimeProvider.System.GetTimestamp() + (long)(timeout.TotalSeconds * TimeProvider.System.TimestampFrequency);
        while (waiting(state) && TimeProvider.System.GetTimestamp() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System, ct).ConfigureAwait(false);
            state = await engine.GetSessionAsync(ct).ConfigureAwait(false);
        }

        return state;
    }

    private static string Summarize(ScanSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"snapshot {s.Meta.SnapshotId} ({s.Meta.Privilege}), {s.Runs.Count} collectors:");
        foreach (var run in s.Runs)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n  {run.CollectorId,-10} {run.Status}");
        }

        return sb.ToString();
    }

    private static string Summarize(AnalysisResult a)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"analysis {a.AnalysisId}: {a.Hypotheses.Count} hypotheses, unknown cause {a.UnknownCause.Probability:0.00}");
        foreach (var h in a.Hypotheses.Take(5))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n  {h.Band,-11} {h.Posterior:0.00} {h.Id}");
        }

        foreach (var p in a.Proposals.Take(5))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n  fix {p.FixId}");
        }

        return sb.ToString();
    }

    private static async Task<int> FailAsync(TextWriter stderr, string code)
    {
        await stderr.WriteLineAsync("error: " + code).ConfigureAwait(false);
        return (int)ExitFor(code);
    }

    private static string CodeOf(Exception ex)
    {
        var message = ex.Message;
        return message.Length is > 0 and < 64 && message.Contains('.', StringComparison.Ordinal) && !message.Contains(' ', StringComparison.Ordinal) ? message : "engine.error";
    }

    private static string NewId() => new UlidGenerator(TimeProvider.System).NewId();

    private static DateTimeOffset Now() => TimeProvider.System.GetUtcNow();
}

/// <summary>Parsed CLI arguments.</summary>
public sealed record CliArguments
{
    /// <summary>Verb.</summary>
    public required string Verb { get; init; }

    /// <summary>Sub-verb (<c>preview</c>, <c>propose</c>).</summary>
    public string? Sub { get; init; }

    /// <summary>Fix id or attempt id.</summary>
    public string? Target { get; init; }

    /// <summary>JSON output.</summary>
    public bool Json { get; init; }

    /// <summary>Quick scan.</summary>
    public bool Quick { get; init; }

    /// <summary>No network probes.</summary>
    public bool NoNetwork { get; init; }

    /// <summary><c>--debug</c>: raise the log verbosity for this run.</summary>
    public bool Debug { get; init; }

    /// <summary>Language.</summary>
    public string Language { get; init; } = Languages.English;

    /// <summary>Export kind.</summary>
    public ExportKind Kind { get; init; } = ExportKind.ReportMarkdown;

    /// <summary>Output file.</summary>
    public string? Out { get; init; }

    /// <summary>Excluded sections.</summary>
    public IReadOnlyList<string> Excluded { get; init; } = [];

    /// <summary>Binding.</summary>
    public FixBinding Binding { get; init; } = FixBinding.Empty;

    /// <summary>Imported plan id.</summary>
    public string? PlanId { get; init; }

    /// <summary>Wait for the player's decision.</summary>
    public int? WaitSeconds { get; init; }

    /// <summary>Self-declared label ("Suggested by …").</summary>
    public string? Label { get; init; }

    /// <summary>Parses; null on a usage error.</summary>
    /// <param name="args">Arguments.</param>
    public static CliArguments? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return null;
        }

        var verb = args[0];
        var i = 1;
        string? sub = null;
        string? target = null;
        switch (verb)
        {
            case "help" or "scan" or "analyze" or "export" or "status":
                break;
            case "fix" when args.Count >= 3 && args[1] is "preview" or "propose":
            case "undo" when args.Count >= 3 && args[1] == "propose":
                sub = args[1];
                target = args[2];
                i = 3;
                break;
            default:
                return null;
        }

        var result = new CliArguments { Verb = verb, Sub = sub, Target = target };
        var bindings = new Dictionary<string, List<EntityRef>>(StringComparer.Ordinal);
        var excluded = new List<string>();
        for (; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i])
            {
                case "--json":
                    result = result with { Json = true };
                    break;
                case "--quick":
                    result = result with { Quick = true };
                    break;
                case "--debug":
                    result = result with { Debug = true };
                    break;
                case "--debug-bundle":
                    var bundlePath = Next();
                    if (bundlePath is null || verb != "export")
                    {
                        return null;
                    }

                    result = result with { Kind = ExportKind.DebugBundle, Out = bundlePath };
                    break;
                case "--no-network":
                    result = result with { NoNetwork = true };
                    break;
                case "--lang":
                    var lang = Next();
                    if (lang is not (Languages.English or Languages.Turkish))
                    {
                        return null;
                    }

                    result = result with { Language = lang };
                    break;
                case "--kind":
                    ExportKind? kind = Next() switch
                    {
                        "html" => ExportKind.ReportHtml,
                        "md" => ExportKind.ReportMarkdown,
                        "json" => ExportKind.ReportJson,
                        "ai" => ExportKind.AiDigest,
                        "vtdscan" => ExportKind.ScanBundle,
                        _ => null,
                    };
                    if (kind is null)
                    {
                        return null;
                    }

                    result = result with { Kind = kind.Value };
                    break;
                case "--out":
                    var path = Next();
                    if (path is null)
                    {
                        return null;
                    }

                    result = result with { Out = path };
                    break;
                case "--exclude":
                    var section = Next();
                    if (section is null)
                    {
                        return null;
                    }

                    excluded.Add(section);
                    break;
                case "--bind":
                    // name=Kind:Id (the id may itself contain ':' or '=')
                    var spec = Next();
                    var eq = spec?.IndexOf('=', StringComparison.Ordinal) ?? -1;
                    var colon = eq < 0 ? -1 : spec!.IndexOf(':', eq + 1);
                    if (spec is null || eq <= 0 || colon < 0 || !Enum.TryParse<EntityKind>(spec[(eq + 1)..colon], ignoreCase: false, out var entityKind) || colon + 1 >= spec.Length)
                    {
                        return null;
                    }

                    var name = spec[..eq];
                    if (!bindings.TryGetValue(name, out var list))
                    {
                        bindings[name] = list = [];
                    }

                    list.Add(new EntityRef { Kind = entityKind, Id = spec[(colon + 1)..] });
                    break;
                case "--plan":
                    var plan = Next();
                    if (!Ulid.IsValid(plan))
                    {
                        return null;
                    }

                    result = result with { PlanId = plan };
                    break;
                case "--wait":
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wait) || wait <= 0 || wait > EngineProtocol.MaxWaitSeconds)
                    {
                        return null;
                    }

                    result = result with { WaitSeconds = wait };
                    break;
                case "--label":
                    var label = Next();
                    if (label is null)
                    {
                        return null;
                    }

                    result = result with { Label = label };
                    break;
                default:
                    return null;
            }
        }

        return result with
        {
            Excluded = excluded,
            Binding = bindings.Count == 0 ? FixBinding.Empty : new FixBinding { Entities = bindings.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EntityRef>)kv.Value, StringComparer.Ordinal) },
        };
    }
}
