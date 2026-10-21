using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using StreamJsonRpc;
using VTD.Contracts;
using VTD.Contracts.Ipc;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Engine.Ipc;
using VTD.Engine.Platform;

namespace VTD.Engine.Cli;

/// <summary>
/// How the CLI reaches an engine. A running ELEVATED engine is found through <c>%ProgramData%\VTDiag\engine.json</c>
/// (trusted only when an administrator or SYSTEM owns it), the pipe is opened with identification-level impersonation
/// only, and the server is verified from the pipe (its PID equals the discovery PID and its image is
/// <c>%ProgramFiles%\VTDiag\VTDiag.Engine.exe</c>) before anything is sent. Without one, the CLI runs a LIMITED engine
/// in-process: read-only (scan, analyze, export), no fix runner, the same <see cref="IEngineRpc"/> surface and the same
/// <see cref="CommandPolicy"/> for origin <see cref="EventOrigin.Cli"/>.
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private readonly IAsyncDisposable? _owned;
    private readonly IDisposable? _rpc;

    private EngineClient(IEngineRpc engine, bool inProcess, IAsyncDisposable? owned, IDisposable? rpc)
    {
        Engine = engine;
        InProcess = inProcess;
        _owned = owned;
        _rpc = rpc;
    }

    /// <summary>The engine.</summary>
    public IEngineRpc Engine { get; }

    /// <summary>True when this is the in-process limited engine.</summary>
    public bool InProcess { get; }

    /// <summary>Connects to the running elevated engine, or null when there is none (or it fails verification).</summary>
    /// <param name="log">Diagnostics (stderr).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<EngineClient?> TryConnectElevatedAsync(TextWriter log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        var path = Path.Combine(EngineStorage.ElevatedRoot(), EngineProtocol.DiscoveryFileName);
        EngineDiscovery discovery;
        try
        {
            if (!File.Exists(path) || !OwnedByAdmins(path))
            {
                return null;
            }

            discovery = VtdJson.Deserialize<EngineDiscovery>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }

        if (discovery.ProtocolVersion != EngineProtocol.Version || !discovery.PipeName.StartsWith(EngineProtocol.PipeNamePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var pipe = new NamedPipeClientStream(".", discovery.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            var serverPid = PipeClientVerifier.ServerProcessId(pipe.SafePipeHandle);
            var image = serverPid is { } pid ? PipeClientVerifier.ImagePathOf(pid) : null;
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VTDiag", ClientPolicy.EngineImage);
            if (serverPid != discovery.ProcessId || image is null || !string.Equals(Path.GetFullPath(image), expected, StringComparison.OrdinalIgnoreCase))
            {
                await log.WriteLineAsync("cli.engine_unverified").ConfigureAwait(false);
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter { JsonSerializerOptions = VtdJson.CreateRpcOptions() }));
        var proxy = rpc.Attach<IEngineRpc>();
        rpc.StartListening();
        return new EngineClient(proxy, inProcess: false, pipe, rpc);
    }

    /// <summary>Runs a limited, read-only engine in this process.</summary>
    /// <param name="allowNetwork">Allow network probes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<EngineClient> StartInProcessAsync(bool allowNetwork, CancellationToken cancellationToken)
    {
        var kb = EngineHost.FindKb(null, elevated: false) ?? throw new EngineRpcException("engine.kb_missing");
        var core = await EngineComposition.BuildAsync(new EngineStartOptions { Privilege = PrivilegeMode.Limited, KbPath = kb, AllowNetwork = allowNetwork }, cancellationToken).ConfigureAwait(false);
        foreach (var issue in core.Options.KbIssues.Where(i => i.Severity == Contracts.Knowledge.KbIssueSeverity.Error))
        {
            await Console.Error.WriteLineAsync("kb." + issue.Code + " " + issue.Path + " " + issue.Message).ConfigureAwait(false);
        }

        EngineHost.InstallLastChanceLogging(core.Log);
        var outcome = await Session.SessionStartup.RunAsync(core.Controller, core.Runtime, null, cancellationToken).ConfigureAwait(false);
        EngineHost.LogRecovery(core, outcome);
        var identity = new ClientIdentity
        {
            ProcessId = Environment.ProcessId,
            ImagePath = Environment.ProcessPath,
            UserSid = core.Player.Sid,
            UserIsAdmin = core.Player.IsAdmin,
            Elevated = false,
            Signature = new SignatureCheck(SignatureState.Unknown, null),
            Origin = EventOrigin.Cli,
        };
        return new EngineClient(new EngineConnection(core, identity), inProcess: true, core, null);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        (Engine as IDisposable)?.Dispose();
        _rpc?.Dispose();
        if (_owned is not null)
        {
            await _owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool OwnedByAdmins(string path)
    {
        var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
    }
}
