using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using StreamJsonRpc;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;

namespace VTD.App.Services;

/// <summary>
/// Connects the UI to the REAL engine: starts <c>VTDiag.Engine.exe</c> next to this app (elevated through UAC when the
/// player allows changes, otherwise <c>--limited</c>: unelevated, read-only scan, analyze and export), opens the hardened
/// pipe with identification-level impersonation only, and verifies from the pipe itself that the server is the process
/// that was just started and that its image is the engine next to the UI (<see cref="PipeServerVerifier"/>) before a single
/// byte is sent. If UAC is declined or the elevated engine refuses to run (an unpacked build outside Program Files), the
/// UI falls back to the limited engine and says so. Real fixes still need the player's click: consent only ever comes from
/// this UI and the engine enforces <see cref="CommandPolicy"/> and the consent token.
/// </summary>
public sealed class RealEngineConnector : IEngineConnector
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly AppLog _log;

    /// <summary>Creates the connector.</summary>
    /// <param name="log">UI log.</param>
    public RealEngineConnector(AppLog log) => _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <inheritdoc />
    public bool IsDemo => false;

    /// <inheritdoc />
    public async Task<IEngineRpc> ConnectAsync(PrivilegeMode requested, CancellationToken cancellationToken)
    {
        if (!EngineLauncher.EngineExists)
        {
            _log.Warn("engine.missing");
            throw new InvalidOperationException("engine.missing");
        }

        // The elevated engine refuses to run outside Program Files, so a portable copy goes straight to limited
        // mode instead of showing a UAC prompt that can only fail.
        if (requested == PrivilegeMode.Elevated && !EngineLauncher.IsInstalled)
        {
            _log.Info("engine.portable", "limited");
            requested = PrivilegeMode.Limited;
        }

        if (requested == PrivilegeMode.Elevated)
        {
            try
            {
                return await StartAsync(elevated: true, cancellationToken).ConfigureAwait(true);
            }
            catch (InvalidOperationException ex)
            {
                _log.Warn("engine.elevated_failed", ex.Message);
            }
        }

        return await StartAsync(elevated: false, cancellationToken).ConfigureAwait(true);
    }

    private async Task<IEngineRpc> StartAsync(bool elevated, CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        var pipeName = EngineProtocol.PipeNamePrefix + Guid.NewGuid().ToString("N");
        var launched = EngineLauncher.Start(elevated, pipeName) ?? throw new InvalidOperationException(elevated ? "engine.uac_declined" : "engine.start_failed");
        _log.Info("engine.launched", elevated ? "elevated" : "limited");
        var pipe = await ConnectPipeAsync(pipeName, launched, ct).ConfigureAwait(true);
        try
        {
            Verify(pipe, launched);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(true);
            throw;
        }

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter { JsonSerializerOptions = VtdJson.CreateRpcOptions() }));
        var proxy = rpc.Attach<IEngineRpc>();
        rpc.StartListening();
        var adapter = new RpcEngine(proxy, rpc, pipe);
        _log.Info("engine.connected", elevated ? "elevated" : "limited");
        _log.Debug("engine.connect_time", TimeProvider.System.GetElapsedTime(started).TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
        await _log.AttachAsync(adapter).ConfigureAwait(true);
        return adapter;
    }

    private static async Task<NamedPipeClientStream> ConnectPipeAsync(string name, LaunchedEngine launched, CancellationToken ct)
    {
        var deadline = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(deadline) < StartTimeout)
        {
            ct.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(400, ct).ConfigureAwait(true);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(true);
            }
            catch (UnauthorizedAccessException)
            {
                // The pipe exists but is not ours (the DACL refused us): never talk to it.
                await pipe.DisposeAsync().ConfigureAwait(true);
                throw new InvalidOperationException("engine.unverified");
            }

            if (launched.HasExited())
            {
                // Refused to run (elevated outside Program Files), another engine already runs, or it crashed.
                throw new InvalidOperationException("engine.exited");
            }

            await Task.Delay(150, ct).ConfigureAwait(true);
        }

        throw new InvalidOperationException("engine.start_timeout");
    }

    private static void Verify(NamedPipeClientStream pipe, LaunchedEngine launched)
    {
        var serverPid = PipeServerVerifier.ServerProcessId(pipe.SafePipeHandle);
        var image = serverPid is { } pid ? PipeServerVerifier.ImagePathOf(pid) : null;
        var expected = Path.GetFullPath(EngineLauncher.EnginePath);
        if (serverPid != launched.ProcessId || image is null || !string.Equals(Path.GetFullPath(image), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("engine.unverified");
        }
    }

    /// <summary>The proxy, with the pipe's failures turned into the codes the UI already understands.</summary>
    private sealed class RpcEngine : IEngineRpc, IDisposable
    {
        private readonly IEngineRpc _inner;
        private readonly JsonRpc _rpc;
        private readonly NamedPipeClientStream _pipe;

        public RpcEngine(IEngineRpc inner, JsonRpc rpc, NamedPipeClientStream pipe)
        {
            _inner = inner;
            _rpc = rpc;
            _pipe = pipe;
            _inner.Notification += (_, n) => Notification?.Invoke(this, n);
        }

        public event EventHandler<EngineNotification>? Notification;

        public Task<EngineHello> HelloAsync(ClientHello hello, CancellationToken cancellationToken) => Call(() => _inner.HelloAsync(hello, cancellationToken));

        public Task PingAsync(CancellationToken cancellationToken) => Call(async () =>
        {
            await _inner.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        });

        public Task<SessionState> GetSessionAsync(CancellationToken cancellationToken) => Call(() => _inner.GetSessionAsync(cancellationToken));

        public Task<SessionState> SendAsync(SessionCommand command, CancellationToken cancellationToken) => Call(() => _inner.SendAsync(command, cancellationToken));

        public Task<ScanSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken) => Call(() => _inner.GetSnapshotAsync(cancellationToken));

        public Task<AnalysisResult?> GetAnalysisAsync(CancellationToken cancellationToken) => Call(() => _inner.GetAnalysisAsync(cancellationToken));

        public Task<FixRunResult?> WaitForOutcomeAsync(string previewId, int timeoutSeconds, CancellationToken cancellationToken) => Call(() => _inner.WaitForOutcomeAsync(previewId, timeoutSeconds, cancellationToken));

        public Task<FixPreview?> GetPreviewAsync(string previewId, CancellationToken cancellationToken) => Call(() => _inner.GetPreviewAsync(previewId, cancellationToken));

        public Task<MonitorSummary?> GetMonitorSummaryAsync(string summaryId, CancellationToken cancellationToken) => Call(() => _inner.GetMonitorSummaryAsync(summaryId, cancellationToken));

        public Task<FixPreview> PreviewFixAsync(string fixId, FixBinding binding, CancellationToken cancellationToken) => Call(() => _inner.PreviewFixAsync(fixId, binding, cancellationToken));

        public Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken) => Call(() => _inner.ListAttemptsAsync(cancellationToken));

        public Task<RestartPrompt> PrepareRestartAsync(RestartKind kind, CancellationToken cancellationToken) => Call(() => _inner.PrepareRestartAsync(kind, cancellationToken));

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken) => Call(() => _inner.ExportAsync(request, cancellationToken));

        public Task SubmitClientLogAsync(IReadOnlyList<ClientLogEntry> entries, CancellationToken cancellationToken) => Call(async () =>
        {
            await _inner.SubmitClientLogAsync(entries, cancellationToken).ConfigureAwait(false);
            return true;
        });

        public Task<bool> SetDebugModeAsync(bool enabled, CancellationToken cancellationToken) => Call(() => _inner.SetDebugModeAsync(enabled, cancellationToken));

        public Task<PlanValidation> ImportPlanAsync(string planText, CancellationToken cancellationToken) => Call(() => _inner.ImportPlanAsync(planText, cancellationToken));

        public void Dispose()
        {
            _rpc.Dispose();
            _pipe.Dispose();
        }

        private static async Task<T> Call<T>(Func<Task<T>> call)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (RemoteRpcException ex) when (ex is not ConnectionLostException)
            {
                // The engine's refusals travel as the machine code in the message (for example command.not_allowed).
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or IOException)
            {
                throw new InvalidOperationException("engine.connection_lost", ex);
            }
        }
    }
}
