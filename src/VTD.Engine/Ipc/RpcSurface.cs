using StreamJsonRpc;
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
/// What StreamJsonRpc exposes on the pipe: the connection's <see cref="IEngineRpc"/> and nothing else. Every refusal
/// becomes an RPC error whose message (and error data) is the machine code (for example <c>command.not_allowed</c>);
/// any other failure becomes <c>engine.error</c>, so no stack trace, path or user name ever crosses the pipe.
/// </summary>
public sealed class RpcSurface : IEngineRpc, IDisposable
{
    /// <summary>JSON-RPC error code of every engine refusal.</summary>
    public const int RefusedErrorCode = -32010;

    private readonly EngineConnection _inner;

    /// <summary>Creates the surface.</summary>
    /// <param name="inner">Connection.</param>
    public RpcSurface(EngineConnection inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.Notification += Forward;
    }

    /// <inheritdoc />
    public event EventHandler<EngineNotification>? Notification;

    /// <summary>Raised for unexpected failures (for the engine log; never sent to the client).</summary>
    public event EventHandler<Exception>? Faulted;

    /// <inheritdoc />
    public Task<EngineHello> HelloAsync(ClientHello hello, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.HelloAsync), () => _inner.HelloAsync(hello, cancellationToken));

    /// <inheritdoc />
    public Task PingAsync(CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.PingAsync), async () =>
    {
        await _inner.PingAsync(cancellationToken).ConfigureAwait(false);
        return true;
    });

    /// <inheritdoc />
    public Task<SessionState> GetSessionAsync(CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.GetSessionAsync), () => _inner.GetSessionAsync(cancellationToken));

    /// <inheritdoc />
    public Task<SessionState> SendAsync(SessionCommand command, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.SendAsync), () => _inner.SendAsync(command, cancellationToken));

    /// <inheritdoc />
    public Task<ScanSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.GetSnapshotAsync), () => _inner.GetSnapshotAsync(cancellationToken));

    /// <inheritdoc />
    public Task<AnalysisResult?> GetAnalysisAsync(CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.GetAnalysisAsync), () => _inner.GetAnalysisAsync(cancellationToken));

    /// <inheritdoc />
    public Task<FixRunResult?> WaitForOutcomeAsync(string previewId, int timeoutSeconds, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.WaitForOutcomeAsync), () => _inner.WaitForOutcomeAsync(previewId, timeoutSeconds, cancellationToken));

    /// <inheritdoc />
    public Task<FixPreview?> GetPreviewAsync(string previewId, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.GetPreviewAsync), () => _inner.GetPreviewAsync(previewId, cancellationToken));

    /// <inheritdoc />
    public Task<MonitorSummary?> GetMonitorSummaryAsync(string summaryId, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.GetMonitorSummaryAsync), () => _inner.GetMonitorSummaryAsync(summaryId, cancellationToken));

    /// <inheritdoc />
    public Task<FixPreview> PreviewFixAsync(string fixId, FixBinding binding, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.PreviewFixAsync), () => _inner.PreviewFixAsync(fixId, binding, cancellationToken));

    /// <inheritdoc />
    public Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.ListAttemptsAsync), () => _inner.ListAttemptsAsync(cancellationToken));

    /// <inheritdoc />
    public Task<RestartPrompt> PrepareRestartAsync(RestartKind kind, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.PrepareRestartAsync), () => _inner.PrepareRestartAsync(kind, cancellationToken));

    /// <inheritdoc />
    public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.ExportAsync), () => _inner.ExportAsync(request, cancellationToken));

    /// <inheritdoc />
    public Task SubmitClientLogAsync(IReadOnlyList<ClientLogEntry> entries, CancellationToken cancellationToken) => Guard(nameof(SubmitClientLogAsync), async () =>
    {
        await _inner.SubmitClientLogAsync(entries, cancellationToken).ConfigureAwait(false);
        return true;
    });

    /// <inheritdoc />
    public Task<bool> SetDebugModeAsync(bool enabled, CancellationToken cancellationToken) => Guard(nameof(SetDebugModeAsync), () => _inner.SetDebugModeAsync(enabled, cancellationToken));

    /// <inheritdoc />
    public Task<PlanValidation> ImportPlanAsync(string planText, CancellationToken cancellationToken) => Guard(nameof(IEngineRpc.ImportPlanAsync), () => _inner.ImportPlanAsync(planText, cancellationToken));

    /// <inheritdoc />
    public void Dispose()
    {
        _inner.Notification -= Forward;
        _inner.Dispose();
    }

    private void Forward(object? sender, EngineNotification n) => Notification?.Invoke(this, n);

    private async Task<T> Guard<T>(string name, Func<Task<T>> call)
    {
        var log = _inner.Log;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        string Ms() => System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        var noisy = name is nameof(IEngineRpc.PingAsync) or nameof(IEngineRpc.SubmitClientLogAsync);
        try
        {
            var result = await call().ConfigureAwait(false);
            log.Write(noisy ? ClientLogLevel.Debug : ClientLogLevel.Info, "engine", "rpc.call", name, [new("origin", _inner.Origin.ToString()), new("result", "ok"), new("ms", Ms())]);
            return result;
        }
        catch (OperationCanceledException)
        {
            log.Write(ClientLogLevel.Debug, "engine", "rpc.call", name, [new("origin", _inner.Origin.ToString()), new("result", "cancelled"), new("ms", Ms())]);
            throw;
        }
        catch (SessionRejectedException ex)
        {
            throw Refused(name, ex.Code, Ms());
        }
        catch (EngineRpcException ex)
        {
            throw Refused(name, ex.Message, Ms());
        }
        catch (ArgumentException)
        {
            throw Refused(name, "request.invalid", Ms());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Faulted?.Invoke(this, ex);
            log.Error("rpc.fault", ex, "call", name, "origin", _inner.Origin.ToString());
            throw Refused(name, "engine.error", Ms());
        }
    }

    private LocalRpcException Refused(string name, string code, string ms)
    {
        _inner.Log.Write(ClientLogLevel.Warning, "engine", "rpc.call", name, [new("origin", _inner.Origin.ToString()), new("result", code), new("ms", ms)]);
        return Refused(code);
    }

    private static LocalRpcException Refused(string code) => new(code) { ErrorCode = RefusedErrorCode, ErrorData = code };
}
