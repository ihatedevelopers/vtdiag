using System.Security.Cryptography;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Reporting;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;
using VTD.Reporting;
using VTD.Testing.Fakes;

namespace VTD.App.Services;

/// <summary>Creates <see cref="DemoEngine"/>s (M1, until A4's engine and pipe client exist).</summary>
/// <param name="fixture">Fixture the demo scan returns.</param>
/// <param name="pacing">Delay between replayed progress steps (zero in tests).</param>
public sealed class DemoEngineConnector(string fixture, TimeSpan pacing) : IEngineConnector
{
    /// <summary>The last engine created.</summary>
    public DemoEngine? Current { get; private set; }

    /// <inheritdoc />
    public bool IsDemo => true;

    /// <inheritdoc />
    public Task<IEngineRpc> ConnectAsync(PrivilegeMode requested, CancellationToken cancellationToken)
    {
        Current = new DemoEngine(new FakeEngineRpc(fixture, requested), pacing);
        return Task.FromResult<IEngineRpc>(Current);
    }
}

/// <summary>
/// The demo engine: <see cref="FakeEngineRpc"/> (A6's development fake) plus what the fake leaves out so every screen
/// can be clicked through: real share-level exports through <see cref="ShareExporter"/> (redaction, sweep, all report
/// formats, <c>.vtdscan</c>), plan validation through <see cref="PlanValidator"/>, paced progress, and a few
/// "simulate" hooks for things only a real PC does (an AI agent proposing a fix, the PC coming back after a restart,
/// the deadman running out).
/// </summary>
public sealed class DemoEngine : IEngineRpc
{
    private readonly TimeSpan _pacing;
    private readonly List<EngineNotification> _buffer = [];
    private readonly Redactor _redactor;
    private readonly ShareExporter _exporter;
    private bool _buffering;

    /// <summary>Creates the demo engine.</summary>
    /// <param name="fake">Fake engine.</param>
    /// <param name="pacing">Delay between replayed progress notifications.</param>
    public DemoEngine(FakeEngineRpc fake, TimeSpan pacing)
    {
        Fake = fake ?? throw new ArgumentNullException(nameof(fake));
        _pacing = pacing;
        _redactor = new Redactor(RandomNumberGenerator.GetBytes(32));
        var builder = new ReportBuilder(fake.Kb, _redactor);
        _exporter = new ShareExporter(_redactor, builder, new ScanBundleCodec(_redactor), fake.Ids, fake.Clock);
        Fake.Notification += OnFakeNotification;
    }

    /// <inheritdoc />
    public event EventHandler<EngineNotification>? Notification;

    /// <summary>The wrapped fake.</summary>
    public FakeEngineRpc Fake { get; }

    /// <summary>The share-level redactor (its <see cref="IRedactor.ShareEntityId"/> is what a helper plan binds to).</summary>
    public IRedactor Redactor => _redactor;

    /// <inheritdoc />
    public Task<EngineHello> HelloAsync(ClientHello hello, CancellationToken cancellationToken) => Fake.HelloAsync(hello, cancellationToken);

    /// <inheritdoc />
    public Task PingAsync(CancellationToken cancellationToken) => Fake.PingAsync(cancellationToken);

    /// <inheritdoc />
    public Task<SessionState> GetSessionAsync(CancellationToken cancellationToken) => Fake.GetSessionAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ScanSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken) => Fake.GetSnapshotAsync(cancellationToken);

    /// <inheritdoc />
    public Task<AnalysisResult?> GetAnalysisAsync(CancellationToken cancellationToken) => Fake.GetAnalysisAsync(cancellationToken);

    /// <inheritdoc />
    public Task<FixRunResult?> WaitForOutcomeAsync(string previewId, int timeoutSeconds, CancellationToken cancellationToken)
        => Fake.WaitForOutcomeAsync(previewId, timeoutSeconds, cancellationToken);

    /// <inheritdoc />
    public async Task<FixPreview> PreviewFixAsync(string fixId, FixBinding binding, CancellationToken cancellationToken)
    {
        // This client is always the UI. (While a simulated agent proposal is in flight, the fake's origin is Mcp.)
        var origin = Fake.ClientOrigin;
        Fake.ClientOrigin = EventOrigin.Ui;
        try
        {
            return await Fake.PreviewFixAsync(fixId, binding, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Fake.ClientOrigin = origin;
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<FixPreview?> GetPreviewAsync(string previewId, CancellationToken cancellationToken)
    {
        // This client is always the UI (it gets the consent nonce), even while a simulated agent proposal is in flight.
        var origin = Fake.ClientOrigin;
        Fake.ClientOrigin = EventOrigin.Ui;
        try
        {
            return await Fake.GetPreviewAsync(previewId, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Fake.ClientOrigin = origin;
        }
    }

    /// <inheritdoc />
    public Task<Contracts.Monitoring.MonitorSummary?> GetMonitorSummaryAsync(string summaryId, CancellationToken cancellationToken)
        => Fake.GetMonitorSummaryAsync(summaryId, cancellationToken);

    public Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken) => Fake.ListAttemptsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<RestartPrompt> PrepareRestartAsync(RestartKind kind, CancellationToken cancellationToken) => Fake.PrepareRestartAsync(kind, cancellationToken);

    /// <inheritdoc />
    public async Task<SessionState> SendAsync(SessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var paced = _pacing > TimeSpan.Zero && command is ScanRequested or RescanRequested or ConsentGiven or MonitorRequested;
        if (!paced)
        {
            return await Fake.SendAsync(command, cancellationToken).ConfigureAwait(true);
        }

        _buffering = true;
        SessionState state;
        try
        {
            state = await Fake.SendAsync(command, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _buffering = false;
        }

        // Replay what the synchronous fake produced at a human pace, so the progress screens are visible.
        var replay = _buffer.ToList();
        _buffer.Clear();
        foreach (var n in replay)
        {
            if (n is ScanProgressNotification or FixProgressNotification or MonitorTickNotification)
            {
                await Task.Delay(_pacing, cancellationToken).ConfigureAwait(true);
            }

            Notification?.Invoke(this, n);
        }

        return state;
    }

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == ExportKind.DebugBundle)
        {
            return await Fake.ExportAsync(request, cancellationToken).ConfigureAwait(true);
        }

        var snapshot = await Fake.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
        if (snapshot is null)
        {
            return new ExportResult { Status = ExportStatus.Failed };
        }

        var session = await Fake.GetSessionAsync(cancellationToken).ConfigureAwait(true);
        var attempts = await Fake.ListAttemptsAsync(cancellationToken).ConfigureAwait(true);
        return _exporter.Export(request, new ShareExportInput
        {
            Snapshot = snapshot,
            Analysis = await Fake.GetAnalysisAsync(cancellationToken).ConfigureAwait(true),
            Attempts = attempts,
            AppVersion = FixtureSnapshots.AppVersion,
            KbVersion = Fake.Kb.Version.Version,
            SessionId = session.SessionId,
        });
    }

    /// <inheritdoc />
    public Task SubmitClientLogAsync(IReadOnlyList<ClientLogEntry> entries, CancellationToken cancellationToken) => Fake.SubmitClientLogAsync(entries, cancellationToken);

    /// <inheritdoc />
    public Task<bool> SetDebugModeAsync(bool enabled, CancellationToken cancellationToken) => Fake.SetDebugModeAsync(enabled, cancellationToken);

    /// <inheritdoc />
    public async Task<PlanValidation> ImportPlanAsync(string planText, CancellationToken cancellationToken)
    {
        // The real engine rescans first; the demo validates against the demo scan.
        var snapshot = await Fake.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
        if (snapshot is null)
        {
            return await Fake.ImportPlanAsync(planText, cancellationToken).ConfigureAwait(true);
        }

        var analysis = await Fake.GetAnalysisAsync(cancellationToken).ConfigureAwait(true);
        return new PlanValidator(_redactor).Validate(planText, snapshot, Fake.Kb, Fake.Clock.GetUtcNow(), analysis);
    }

    /// <summary>Simulates an MCP agent proposing a fix (the consent dialog shows its badge; it cannot consent itself).</summary>
    /// <param name="fixId">Fix id (default the one-way DNS flush, to show the second click).</param>
    /// <param name="label">The agent's self-declared label.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task SimulateAgentProposalAsync(string fixId = "fix.net.dns_flush", string label = "Claude Code (MCP)", CancellationToken cancellationToken = default)
    {
        Fake.ClientOrigin = EventOrigin.Mcp;
        try
        {
            await Fake.HelloAsync(new ClientHello { ProtocolVersion = EngineProtocol.Version, AppVersion = "mcp", Language = Languages.English, Kind = ClientKind.Mcp, Label = label }, cancellationToken).ConfigureAwait(true);
            await Fake.SendAsync(new FixSelected { EventId = Fake.Ids.NewId(), Utc = Fake.Clock.GetUtcNow(), FixId = fixId, Binding = FixBinding.Empty }, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Fake.ClientOrigin = EventOrigin.Ui;
        }
    }

    /// <summary>Builds a share-level <c>.vtdscan</c> from a fixture (Helper mode's "open the demo scan").</summary>
    /// <param name="fixture">Fixture.</param>
    public static async Task<byte[]> BuildSampleBundleAsync(string fixture)
    {
        var demo = new DemoEngine(new FakeEngineRpc(fixture), TimeSpan.Zero);
        await demo.HelloAsync(new ClientHello { ProtocolVersion = EngineProtocol.Version, AppVersion = FixtureSnapshots.AppVersion, Language = Languages.English }, CancellationToken.None).ConfigureAwait(true);
        await demo.SendAsync(new ScanRequested { EventId = demo.Fake.Ids.NewId(), Utc = demo.Fake.Clock.GetUtcNow() }, CancellationToken.None).ConfigureAwait(true);
        var export = await demo.ExportAsync(new ExportRequest { Kind = ExportKind.ScanBundle }, CancellationToken.None).ConfigureAwait(true);
        return export.Base64Content is null ? throw new InvalidOperationException("export." + export.Status) : Convert.FromBase64String(export.Base64Content);
    }

    /// <summary>Simulates the PC coming back after "Restart now".</summary>
    public SessionState SimulateResume() => Fake.SimulateResumeAfterReboot();

    /// <summary>Simulates the engine's deadman running out (the engine, not the UI, reverts).</summary>
    /// <param name="attemptId">Attempt.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task SimulateDeadmanExpiredAsync(string attemptId, CancellationToken cancellationToken = default)
        => Fake.SendAsync(new DeadmanAnswered { EventId = Fake.Ids.NewId(), Utc = Fake.Clock.GetUtcNow(), AttemptId = attemptId, Keep = false }, cancellationToken);

    private void OnFakeNotification(object? sender, EngineNotification e)
    {
        if (_buffering)
        {
            _buffer.Add(e);
            return;
        }

        Notification?.Invoke(this, e);
    }
}
