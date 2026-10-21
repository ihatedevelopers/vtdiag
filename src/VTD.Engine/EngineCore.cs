using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Ipc;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Reporting;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;
using VTD.Reporting;
using VTD.Session;

namespace VTD.Engine;

/// <summary>An engine refusal sent to the client: <see cref="Exception.Message"/> is the machine code.</summary>
public sealed class EngineRpcException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public EngineRpcException()
        : base("engine.error")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Machine code.</param>
    public EngineRpcException(string code)
        : base(code)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Machine code.</param>
    /// <param name="innerException">Cause.</param>
    public EngineRpcException(string code, Exception innerException)
        : base(code, innerException)
    {
    }
}

/// <summary>Everything the RPC surface serves, shared by all connections of one engine process.</summary>
public sealed class EngineCore : IAsyncDisposable
{
    private readonly object _gate = new();
    private Phase _lastPhase;
    private string? _loggedSnapshot;
    private string? _loggedAnalysis;
    private int _connections;
    private long _lastActivity;
    private PlayerContext _player;

    /// <summary>Creates the core and wires session changes to the notification hub.</summary>
    /// <param name="options">Parts.</param>
    public EngineCore(EngineCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _player = options.Player;
        options.Controller.Changed += (_, s) => options.Runtime.Hub.SessionChanged(s);
        _lastPhase = options.Controller.Current.Phase;
        options.Controller.Changed += (_, s) => LogSession(s);
        options.Runtime.Hub.Raised += (_, n) => LogNotification(n);
        options.Controller.EffectFaulted += (_, ex) => options.Log.Error("session.effect_faulted", ex);
        Touch();
    }

    /// <summary>Parts.</summary>
    public EngineCoreOptions Options { get; }

    /// <summary>The rolling redacted log.</summary>
    public Diagnostics.DiagLog Log => Options.Log;

    /// <summary>Controller.</summary>
    public SessionController Controller => Options.Controller;

    /// <summary>Runtime.</summary>
    public SessionRuntime Runtime => Options.Runtime;

    /// <summary>KB.</summary>
    public IKnowledgeBase Kb => Options.Runtime.Services.Kb;

    /// <summary>Privilege.</summary>
    public PrivilegeMode Privilege => Options.Runtime.Services.Privilege;

    /// <summary>The interactive player (refined when the verified UI connects).</summary>
    public PlayerContext Player
    {
        get
        {
            lock (_gate)
            {
                return _player;
            }
        }
    }

    /// <summary>Connected clients.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Time of the last RPC or connection change (engine clock ticks).</summary>
    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivity), TimeSpan.Zero);

    /// <summary>Capabilities announced in the hello.</summary>
    public IReadOnlyList<string> Capabilities
    {
        get
        {
            var caps = new List<string> { "plans", "export" };
            if (Privilege == PrivilegeMode.Elevated && Runtime.Services.FixRunner is not null)
            {
                caps.Add("fixes");
            }

            if (Runtime.Services.Monitor is not null)
            {
                caps.Add("monitor");
            }

            return caps;
        }
    }

    /// <summary>Records activity (idle exit).</summary>
    public void Touch() => Interlocked.Exchange(ref _lastActivity, Runtime.Services.Clock.GetUtcNow().UtcTicks);

    /// <summary>A verified client connected.</summary>
    public void Connected()
    {
        Interlocked.Increment(ref _connections);
        Touch();
    }

    /// <summary>A client went away.</summary>
    public void Disconnected()
    {
        Interlocked.Decrement(ref _connections);
        Touch();
    }

    /// <summary>The verified UI told us whether the player's token carries Administrators (standard user + separate admin).</summary>
    /// <param name="isAdmin">Player is an administrator.</param>
    public void RefinePlayerAdmin(bool isAdmin)
    {
        lock (_gate)
        {
            _player = _player with { IsAdmin = isAdmin };
        }
    }

    /// <summary>What the debug bundle is made from: everything the engine has, nothing redacted yet.</summary>
    public Diagnostics.DebugBundleInput DebugBundleInput()
    {
        var s = Controller.Current;
        var artifacts = Runtime.Services.Artifacts;
        var journal = new List<(string, string)>();
        if (Options.JournalFolder is { } folder && Directory.Exists(folder))
        {
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(3).OrderBy(f => f.LastWriteTimeUtc))
                {
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    journal.Add((file.Name, reader.ReadToEnd()));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("bundle.journal_unreadable", ex.GetType().Name);
            }
        }

        return new Diagnostics.DebugBundleInput
        {
            Session = s,
            Snapshot = artifacts.Snapshot(s.SnapshotId),
            Analysis = artifacts.Analysis(s.AnalysisId),
            Journal = journal,
            Logs = Log.ReadAll(),
            EngineVersion = Options.EngineVersion,
            KbVersion = Kb.Version.Version,
            KbIssueCodes = [.. Options.KbIssues.Select(i => i.Severity + ":" + i.Code)],
            Privilege = Privilege,
            DebugMode = Log.DebugMode,
            ProcessStartUtc = Options.StartedUtc == default ? null : Options.StartedUtc,
        };
    }

    /// <summary>The share exporter input for the current session.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<ShareExportInput?> ExportInputAsync(CancellationToken cancellationToken)
    {
        var s = Controller.Current;
        var artifacts = Runtime.Services.Artifacts;
        if (artifacts.Snapshot(s.SnapshotId) is not { } snapshot)
        {
            return null;
        }

        var attempts = Runtime.Services.FixRunner is { } runner
            ? (await runner.ListAttemptsAsync(cancellationToken).ConfigureAwait(false)).Where(a => string.Equals(a.SessionId, s.SessionId, StringComparison.Ordinal)).ToList()
            : [];
        return new ShareExportInput
        {
            Snapshot = snapshot,
            Analysis = artifacts.Analysis(s.AnalysisId),
            Attempts = attempts,
            Monitor = artifacts.Summary(s.Monitor?.SummaryId),
            Baseline = artifacts.Summary(s.Monitor?.BaselineSummaryId),
            AppVersion = Options.AppVersion,
            KbVersion = Kb.Version.Version,
            SessionId = s.SessionId,
        };
    }

    /// <summary>A snapshot fresh enough to validate a plan against (the latest if under 10 minutes old, else a new scan
    /// outside the session), with its analysis.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<(ScanSnapshot Snapshot, AnalysisResult Analysis)> FreshScanAsync(CancellationToken cancellationToken)
    {
        var s = Controller.Current;
        var artifacts = Runtime.Services.Artifacts;
        var now = Runtime.Services.Clock.GetUtcNow();
        if (artifacts.Snapshot(s.SnapshotId) is { } latest && now - latest.Meta.CreatedUtc < TimeSpan.FromMinutes(10))
        {
            var known = artifacts.Analysis(s.AnalysisId) ?? Runtime.Services.Analyzer.Analyze(Runtime.AnalysisInputFor(s, latest));
            return (latest, known);
        }

        var result = await Runtime.Services.Scanner.ScanAsync(Runtime.ScanRequestFor(ScanMode.Full, Runtime.Language), null, cancellationToken).ConfigureAwait(false);
        var analysis = Runtime.Services.Analyzer.Analyze(Runtime.AnalysisInputFor(s, result.Snapshot));
        return (result.Snapshot, analysis);
    }

    private void LogSession(SessionState s)
    {
        var log = Log;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (s.Phase != _lastPhase)
        {
            log.Info("session.phase", _lastPhase + " -> " + s.Phase, "revision", s.Revision.ToString(inv), "lastError", s.LastErrorCode, "fix", s.CurrentFixId, "attempts", s.Attempts.Count.ToString(inv));
            _lastPhase = s.Phase;
        }

        if (s.SnapshotId is { } snapshotId && !string.Equals(snapshotId, _loggedSnapshot, StringComparison.Ordinal))
        {
            _loggedSnapshot = snapshotId;
            LogSnapshot(snapshotId);
        }

        if (s.AnalysisId is { } analysisId && !string.Equals(analysisId, _loggedAnalysis, StringComparison.Ordinal))
        {
            _loggedAnalysis = analysisId;
            LogAnalysis(analysisId);
        }
    }

    private void LogSnapshot(string snapshotId)
    {
        try
        {
            if (Runtime.Services.Artifacts.Snapshot(snapshotId) is not { } snapshot)
            {
                return;
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Log.Info("scan.done", snapshot.Meta.Mode.ToString(), "durationMs", snapshot.Meta.DurationMs.ToString(inv), "privilege", snapshot.Meta.Privilege.ToString(), "collectors", snapshot.Runs.Count.ToString(inv));
            foreach (var run in snapshot.Runs)
            {
                var failed = run.Status is CollectorStatus.Failed or CollectorStatus.TimedOut;
                Log.Write(
                    failed ? ClientLogLevel.Warning : ClientLogLevel.Debug,
                    "engine",
                    "scan.collector",
                    run.CollectorId,
                    [new("status", run.Status.ToString()), new("elapsedMs", run.ElapsedMs.ToString(inv)), new("issues", string.Join(',', run.IssueCodes))]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Warn("scan.log_failed", ex.GetType().Name);
        }
    }

    private void LogAnalysis(string analysisId)
    {
        try
        {
            if (Runtime.Services.Artifacts.Analysis(analysisId) is not { } analysis)
            {
                return;
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var top = string.Join(',', analysis.Hypotheses.Take(3).Select(h => h.Id + "=" + h.Posterior.ToString("0.00", inv)));
            Log.Info("analysis.done", analysis.PrimaryBucket.ToString(), "top", top, "hypotheses", analysis.Hypotheses.Count.ToString(inv), "proposals", analysis.Proposals.Count.ToString(inv), "couldNotCheck", analysis.CouldNotCheck.Count.ToString(inv), "kb", analysis.KbVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Warn("analysis.log_failed", ex.GetType().Name);
        }
    }

    private void LogNotification(EngineNotification n)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        switch (n)
        {
            case FixProgressNotification f:
                Log.Info("fix.progress", f.Progress.Phase.ToString(), "attempt", f.Progress.AttemptId, "step", f.Progress.StepIndex.ToString(inv) + "/" + f.Progress.StepCount.ToString(inv), "key", f.Progress.MessageKey);
                break;
            case ConsentRequestedNotification c:
                Log.Info("fix.consent_requested", c.Kind.ToString(), "preview", c.PreviewId);
                break;
            case DriftNotification:
                Log.Info("drift.detected");
                break;
            case DeadmanNotification:
                Log.Warn("deadman.tick");
                break;
            case ScanProgressNotification:
                Log.Debug("scan.progress");
                break;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await Controller.DisposeAsync().ConfigureAwait(false);
}

/// <summary>The parts of an <see cref="EngineCore"/>.</summary>
public sealed record EngineCoreOptions
{
    /// <summary>Controller.</summary>
    public required SessionController Controller { get; init; }

    /// <summary>Runtime.</summary>
    public required SessionRuntime Runtime { get; init; }

    /// <summary>Redactor (share level for non-UI clients and exports).</summary>
    public required IRedactor Redactor { get; init; }

    /// <summary>Share exporter.</summary>
    public required ShareExporter Exporter { get; init; }

    /// <summary>Plan validator.</summary>
    public required IPlanValidator PlanValidator { get; init; }

    /// <summary>The interactive player.</summary>
    public required PlayerContext Player { get; init; }

    /// <summary>Engine version.</summary>
    public required string EngineVersion { get; init; }

    /// <summary>App version (reports).</summary>
    public required string AppVersion { get; init; }

    /// <summary>Engine PID.</summary>
    public required int ProcessId { get; init; }

    /// <summary>KB load issues (errors mean the bundled KB was rejected and an empty KB is in use).</summary>
    public IReadOnlyList<KbIssue> KbIssues { get; init; } = [];

    /// <summary>Folder for "also save under reports" (null = not available).</summary>
    public string? ReportsFolder { get; init; }

    /// <summary>The rolling redacted log (a discarding log when absent).</summary>
    public Diagnostics.DiagLog Log { get; init; } = Diagnostics.DiagLog.Disabled;

    /// <summary>Builds the <c>.vtddebug</c> bundle (null = not available).</summary>
    public Diagnostics.DebugBundleBuilder? DebugBundles { get; init; }

    /// <summary>Journal folder (elevated engine) whose files go into the debug bundle.</summary>
    public string? JournalFolder { get; init; }

    /// <summary>Engine start time (uptime in the debug bundle).</summary>
    public DateTimeOffset StartedUtc { get; init; }
}
