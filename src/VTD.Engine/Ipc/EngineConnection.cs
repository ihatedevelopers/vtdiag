using System.Text;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;
using VTD.Session;

namespace VTD.Engine.Ipc;

/// <summary>
/// One client's view of the engine (<see cref="IEngineRpc"/>). The origin is the one <see cref="ClientPolicy"/> verified
/// from the pipe; every command is stamped with it and checked against <see cref="CommandPolicy"/> before the session
/// sees it. Non-UI clients (CLI, MCP) get share-redacted data, previews without the consent nonce, no restart nonce,
/// and notifications without the second-consent nonce; they can propose and wait, never consent.
/// </summary>
public sealed class EngineConnection : IEngineRpc, IDisposable
{
    /// <summary>Largest <c>.vtdplan</c> accepted.</summary>
    public const int MaxPlanChars = 512 * 1024;

    private readonly EngineCore _core;
    private readonly ClientIdentity _client;
    private bool _helloDone;
    private bool _disposed;

    /// <summary>Creates the connection and subscribes it to engine notifications.</summary>
    /// <param name="core">Core.</param>
    /// <param name="client">Verified client.</param>
    public EngineConnection(EngineCore core, ClientIdentity client)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (client.Origin is not (EventOrigin.Ui or EventOrigin.Cli or EventOrigin.Mcp))
        {
            throw new ArgumentException("A connection needs a verified client origin.", nameof(client));
        }

        _core.Runtime.Hub.Raised += OnRaised;
        _core.Connected();
    }

    /// <inheritdoc />
    public event EventHandler<EngineNotification>? Notification;

    /// <summary>Verified origin.</summary>
    public EventOrigin Origin => _client.Origin;

    private bool IsUi => _client.Origin == EventOrigin.Ui;

    /// <summary>The engine log.</summary>
    internal Diagnostics.DiagLog Log => _core.Log;

    /// <inheritdoc />
    public async Task<EngineHello> HelloAsync(ClientHello hello, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hello);
        _core.Touch();
        if (hello.ProtocolVersion != EngineProtocol.Version)
        {
            throw new EngineRpcException("protocol.mismatch");
        }

        _core.Runtime.SetLabel(_client.Origin, IsUi ? null : hello.Label);
        if (IsUi)
        {
            _core.Runtime.Language = string.Equals(hello.Language, Languages.Turkish, StringComparison.OrdinalIgnoreCase) ? Languages.Turkish : Languages.English;
            _core.RefinePlayerAdmin(_client.UserIsAdmin);
            await _core.Controller.HandleAsync(new UiConnected { EventId = _core.Runtime.Services.Ids.NewId(), Utc = _core.Runtime.Services.Clock.GetUtcNow(), Origin = EventOrigin.Engine }, cancellationToken).ConfigureAwait(false);
        }

        _helloDone = true;
        return new EngineHello
        {
            ProtocolVersion = EngineProtocol.Version,
            EngineVersion = _core.Options.EngineVersion,
            KbVersion = _core.Kb.Version.Version,
            Privilege = _core.Privilege,
            EngineProcessId = _core.Options.ProcessId,
            ConsentAllowed = IsUi && _core.Privilege == PrivilegeMode.Elevated,
            Recovery = _core.Runtime.LastRecovery,
            Session = View(_core.Controller.Current),
            Capabilities = _core.Capabilities,
        };
    }

    /// <inheritdoc />
    public Task PingAsync(CancellationToken cancellationToken)
    {
        RequireHello();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SessionState> GetSessionAsync(CancellationToken cancellationToken)
    {
        RequireHello();
        return Task.FromResult(View(_core.Controller.Current));
    }

    /// <inheritdoc />
    public async Task<SessionState> SendAsync(SessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireHello();

        // The origin comes from the pipe, never from the client (it is not even part of the JSON contract).
        var stamped = command with { Origin = _client.Origin };
        if (!CommandPolicy.IsAllowed(stamped, _client.Origin))
        {
            throw new EngineRpcException(SessionCodes.NotAllowed);
        }

        var state = await _core.Controller.HandleAsync(stamped, cancellationToken).ConfigureAwait(false);
        return View(state);
    }

    /// <inheritdoc />
    public Task<ScanSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        RequireHello();
        var snapshot = _core.Runtime.Services.Artifacts.Snapshot(_core.Controller.Current.SnapshotId);
        return Task.FromResult(snapshot is null ? null : Share(snapshot));
    }

    /// <inheritdoc />
    public Task<AnalysisResult?> GetAnalysisAsync(CancellationToken cancellationToken)
    {
        RequireHello();
        var analysis = _core.Runtime.Services.Artifacts.Analysis(_core.Controller.Current.AnalysisId);
        return Task.FromResult(analysis is null ? null : Share(analysis));
    }

    /// <inheritdoc />
    public Task<FixRunResult?> WaitForOutcomeAsync(string previewId, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previewId);
        RequireHello();
        if (timeoutSeconds <= 0 || timeoutSeconds > EngineProtocol.MaxWaitSeconds)
        {
            throw new EngineRpcException("wait.timeout_out_of_range");
        }

        return _core.Runtime.Outcomes.WaitAsync(previewId, TimeSpan.FromSeconds(timeoutSeconds), _core.Runtime.Services.Clock, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FixPreview?> GetPreviewAsync(string previewId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previewId);
        RequireHello();
        var preview = _core.Runtime.Previews.Find(previewId);
        return Task.FromResult(preview is null ? null : ViewPreview(preview));
    }

    /// <inheritdoc />
    public Task<MonitorSummary?> GetMonitorSummaryAsync(string summaryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summaryId);
        RequireHello();
        var summary = _core.Runtime.Services.Artifacts.Summary(summaryId);
        return Task.FromResult(summary is null ? null : Share(summary));
    }

    /// <inheritdoc />
    public async Task<FixPreview> PreviewFixAsync(string fixId, FixBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixId);
        ArgumentNullException.ThrowIfNull(binding);
        RequireHello();
        if (_core.Privilege != PrivilegeMode.Elevated || _core.Runtime.Services.FixRunner is not { } runner)
        {
            throw new EngineRpcException(SessionCodes.LimitedMode);
        }

        FixPreview preview;
        try
        {
            preview = await runner.PreviewAsync(fixId, binding, _core.Runtime.ProposerFor(_client.Origin), cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            throw new EngineRpcException("fix.unknown");
        }

        // Not registered: a stand-alone preview can never be consented to (consent binds to the session's preview).
        return ViewPreview(preview);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken)
    {
        RequireHello();
        return _core.Runtime.Services.FixRunner is { } runner ? await runner.ListAttemptsAsync(cancellationToken).ConfigureAwait(false) : [];
    }

    /// <inheritdoc />
    public Task<RestartPrompt> PrepareRestartAsync(RestartKind kind, CancellationToken cancellationToken)
    {
        RequireHello();
        if (!IsUi)
        {
            // Only the person at the PC can restart it: no other client ever sees a restart nonce.
            throw new EngineRpcException(SessionCodes.NotAllowed);
        }

        return _core.Runtime.PrepareRestartAsync(_core.Controller.Current, kind, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireHello();
        if (request.Kind == ExportKind.DebugBundle)
        {
            return DebugBundle(request);
        }

        var input = await _core.ExportInputAsync(cancellationToken).ConfigureAwait(false);
        if (input is null)
        {
            return new ExportResult { Status = ExportStatus.Failed };
        }

        // Share level always; the server IP only on the player's own request.
        var result = _core.Options.Exporter.Export(request with { IncludeServerIp = IsUi && request.IncludeServerIp }, input);
        if (result.Status == ExportStatus.Ok && request.SaveToReportsFolder && _core.Options.ReportsFolder is { } folder && result.FileName is { } name)
        {
            result = result with { SavedPath = SaveReport(folder, name, result) };
        }

        return result;
    }

    /// <inheritdoc />
    public Task SubmitClientLogAsync(IReadOnlyList<ClientLogEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        RequireHello();
        if (IsUi)
        {
            _core.Log.WriteClient(entries);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> SetDebugModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        RequireHello();
        if (_client.Origin is EventOrigin.Ui or EventOrigin.Cli)
        {
            _core.Log.DebugMode = enabled;
        }

        return Task.FromResult(_core.Log.DebugMode);
    }

    private ExportResult DebugBundle(ExportRequest request)
    {
        if (_core.Options.DebugBundles is not { } builder)
        {
            return new ExportResult { Status = ExportStatus.Failed };
        }

        _core.Log.Info("bundle.requested", _client.Origin.ToString(), "excluded", string.Join(',', request.ExcludedSections));
        var result = builder.Build(request, _core.DebugBundleInput());
        if (result.Status == ExportStatus.BlockedByRedaction)
        {
            _core.Log.Warn("bundle.blocked", string.Join(',', result.SweepHits.Select(h => h.Section + ":" + h.Rule).Distinct(StringComparer.Ordinal)));
        }
        else
        {
            _core.Log.Info("bundle.created", result.Status.ToString(), "bytes", (result.Base64Content?.Length / 4 * 3 ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PlanValidation> ImportPlanAsync(string planText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planText);
        RequireHello();
        if (planText.Length > MaxPlanChars)
        {
            return new PlanValidation { PlanId = "invalid", Valid = false, Trust = PlanTrust.Unsigned, Issues = [new PlanIssue { Code = "too_large" }] };
        }

        var (snapshot, analysis) = await _core.FreshScanAsync(cancellationToken).ConfigureAwait(false);
        var validation = _core.Options.PlanValidator.Validate(planText, snapshot, _core.Kb, _core.Runtime.Services.Clock.GetUtcNow(), analysis);
        if (validation.Valid)
        {
            _core.Runtime.MarkPlanValidated(validation.PlanId);
        }

        return validation;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _core.Runtime.Hub.Raised -= OnRaised;
        _core.Disconnected();
    }

    private static string? SaveReport(string folder, string fileName, ExportResult result)
    {
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safe))
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, safe);
        if (result.Base64Content is { } b64)
        {
            File.WriteAllBytes(path, Convert.FromBase64String(b64));
        }
        else
        {
            File.WriteAllText(path, result.TextContent ?? string.Empty, new UTF8Encoding(false));
        }

        return path;
    }

    private void RequireHello()
    {
        _core.Touch();
        if (!_helloDone)
        {
            throw new EngineRpcException("hello.required");
        }
    }

    private void OnRaised(object? sender, EngineNotification n)
    {
        if (!_helloDone)
        {
            return;
        }

        EngineNotification? view = n switch
        {
            SessionChangedNotification s => s with { Session = View(s.Session) },
            ConsentRequestedNotification c when !IsUi => c with { SecondConsentNonce = null },
            DriftNotification d when !IsUi => d with { Findings = [.. d.Findings.Select(f => f with { Expected = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty, Actual = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty })] },
            _ => n,
        };
        try
        {
            Notification?.Invoke(this, view);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException)
        {
            // The client went away while we were notifying it.
        }
    }

    private SessionState View(SessionState state) => IsUi ? state : state with { PlayerSid = null };

    private FixPreview ViewPreview(FixPreview preview) => IsUi ? preview : Share(preview with { ConsentNonce = null });

    private T Share<T>(T model)
        where T : class
    {
        if (IsUi)
        {
            return model;
        }

        var redacted = _core.Options.Redactor.Redact(model, RedactionPolicy.Share);
        if (redacted.Blocked)
        {
            throw new EngineRpcException("redaction.blocked");
        }

        return redacted.Value;
    }
}
