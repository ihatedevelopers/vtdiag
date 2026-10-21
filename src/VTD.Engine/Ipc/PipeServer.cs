using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using StreamJsonRpc;
using VTD.Contracts.Ipc;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;

namespace VTD.Engine.Ipc;

/// <summary>
/// The engine's named-pipe server (DESIGN.md section 2): <c>\\.\pipe\VTDiag.Engine.&lt;nonce&gt;</c>, created with an
/// explicit DACL (SYSTEM and Administrators full; the interactive player read/write only — never "create instance";
/// NETWORK denied), <c>PIPE_REJECT_REMOTE_CLIENTS</c>, and <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> on the first instance so
/// nobody can pre-create the name. At least one instance exists at all times, so the name can never be taken over
/// between connections. Every client is identified from the pipe (PID → image, token SID, signer) by
/// <see cref="ClientPolicy"/> BEFORE a single RPC is read; refused clients are disconnected.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    /// <summary>At most this many simultaneous clients (UI, CLI, MCP and a spare).</summary>
    public const int MaxInstances = 4;

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadmodeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const int BufferSize = 64 * 1024;

    private readonly Func<ClientIdentity, IEngineRpc> _connect;
    private readonly Func<NamedPipeServerStream, ClientIdentity> _identify;
    private readonly byte[] _securityDescriptor;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private Task? _accept;
    private int _active;

    /// <summary>Creates the server (nothing is listening yet).</summary>
    /// <param name="pipeName">Pipe name without <c>\\.\pipe\</c>.</param>
    /// <param name="security">DACL (see <see cref="CreateSecurity"/>).</param>
    /// <param name="identify">Identifies the client of a connected server handle (<see cref="IdentifyClient"/>).</param>
    /// <param name="connect">Builds the RPC object for a verified client.</param>
    public PipeServer(string pipeName, PipeSecurity security, Func<NamedPipeServerStream, ClientIdentity> identify, Func<ClientIdentity, IEngineRpc> connect)
    {
        PipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        ArgumentNullException.ThrowIfNull(security);
        _securityDescriptor = security.GetSecurityDescriptorBinaryForm();
        _identify = identify ?? throw new ArgumentNullException(nameof(identify));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
    }

    /// <summary>Pipe name.</summary>
    public string PipeName { get; }

    /// <summary>Connected, verified clients right now.</summary>
    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>Raised for every client decision (accepted or refused), for the log.</summary>
    public event EventHandler<ClientIdentity>? ClientDecided;

    /// <summary>
    /// The pipe DACL: SYSTEM and Administrators full control; <paramref name="playerSid"/> read/write (plus "create
    /// instance" only for a limited engine, which runs as the player itself); NETWORK denied.
    /// </summary>
    /// <param name="playerSid">Interactive player.</param>
    /// <param name="limitedEngine">The engine runs unelevated as the player.</param>
    public static PipeSecurity CreateSecurity(string playerSid, bool limitedEngine)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        var player = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        if (limitedEngine)
        {
            player |= PipeAccessRights.CreateNewInstance;
        }

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(playerSid), player, AccessControlType.Allow));
        return security;
    }

    /// <summary>Identifies the client of a connected server handle: PID and image from the kernel, token SID by
    /// identification-level impersonation of the pipe client, then the policy decides.</summary>
    /// <param name="pipe">Connected server stream.</param>
    /// <param name="policy">Policy.</param>
    public static ClientIdentity IdentifyClient(NamedPipeServerStream pipe, ClientPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(policy);
        var pid = PipeClientVerifier.ClientProcessId(pipe.SafePipeHandle) ?? 0;
        var image = pid == 0 ? null : PipeClientVerifier.ImagePathOf(pid);
        string? sid = null;
        var admin = false;
        var elevated = false;
        // Impersonate on a dedicated, short-lived thread: an identification-level impersonation must never touch a
        // thread-pool thread the RPC loop later runs on.
        var thread = new Thread(() =>
        {
            try
            {
                pipe.RunAsClient(() =>
                {
                    using var token = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                    sid = token.User?.Value;
                    (admin, elevated) = ClientPolicy.AdminState(token);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            {
                sid = null;
            }
        })
        {
            IsBackground = true,
            Name = "VTDiag pipe client identification",
        };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            sid = null;
        }

        return policy.Decide(pid, image, sid, admin, elevated, PipeClientVerifier.VerifySignature);
    }

    /// <summary>Creates the first instance (FIRST_PIPE_INSTANCE: fails if the name exists) and starts accepting.</summary>
    public void Start()
    {
        var first = CreateInstance(first: true);
        _accept = Task.Run(() => AcceptLoopAsync(first, _stop.Token));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_accept is not null)
        {
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
        }

        Task[] open;
        lock (_connections)
        {
            open = [.. _connections];
        }

        try
        {
            await Task.WhenAll(open).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Stopping.
        }

        _stop.Dispose();
    }

    /// <summary>Creates one pipe instance with the explicit flags and DACL.</summary>
    /// <param name="first">The very first instance (FILE_FLAG_FIRST_PIPE_INSTANCE).</param>
    public NamedPipeServerStream CreateInstance(bool first)
    {
        var handle = GCHandle.Alloc(_securityDescriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = handle.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            var openMode = PipeAccessDuplex | FileFlagOverlapped | (first ? FileFlagFirstPipeInstance : 0);
            var pipeMode = PipeTypeByte | PipeReadmodeByte | PipeWait | PipeRejectRemoteClients;
            var pipe = CreateNamedPipeW(@"\\.\pipe\" + PipeName, openMode, pipeMode, MaxInstances, BufferSize, BufferSize, 0, ref attributes);
            if (pipe.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                pipe.Dispose();
                throw new IOException("pipe.create_failed", new Win32Exception(error));
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, pipe);
        }
        finally
        {
            handle.Free();
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream listening, CancellationToken ct)
    {
        var current = listening;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await current.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await current.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                // A client that connected and vanished: reuse a fresh instance.
                await current.DisposeAsync().ConfigureAwait(false);
                current = CreateInstanceWithRetry(ct);
                continue;
            }

            // Keep an instance listening BEFORE handing this one off: the name is never free for a squatter.
            var connected = current;
            current = CreateInstanceWithRetry(ct);
            var task = Task.Run(() => ServeAsync(connected, ct), CancellationToken.None);
            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }

        await current.DisposeAsync().ConfigureAwait(false);
    }

    private NamedPipeServerStream CreateInstanceWithRetry(CancellationToken ct)
    {
        // All instances busy (ERROR_PIPE_BUSY 231): wait for one to close.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return CreateInstance(first: false);
            }
            catch (IOException) when (attempt < 600 && !ct.IsCancellationRequested)
            {
                Thread.Sleep(100);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe.ConfigureAwait(false);
        ClientIdentity identity;
        try
        {
            identity = _identify(pipe);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return;
        }

        ClientDecided?.Invoke(this, identity);
        if (identity.Origin == EventOrigin.Unspecified)
        {
            pipe.Disconnect();
            return;
        }

        Interlocked.Increment(ref _active);
        try
        {
            var target = _connect(identity);
            var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = VtdJson.CreateRpcOptions() };
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, formatter));
            rpc.AddLocalRpcTarget<IEngineRpc>(target, new JsonRpcTargetOptions { DisposeOnDisconnect = true });
            rpc.StartListening();
            using var registration = ct.Register(() => rpc.Dispose());
            try
            {
                await rpc.Completion.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or RemoteRpcException)
            {
                // The client went away.
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, ref SecurityAttributes attributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }
}
