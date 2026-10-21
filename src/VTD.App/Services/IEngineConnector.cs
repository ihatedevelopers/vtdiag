using VTD.Contracts;
using VTD.Contracts.Ipc;

namespace VTD.App.Services;

/// <summary>
/// Gives the UI an <see cref="IEngineRpc"/>. The real connector (launch <c>VTDiag.Engine.exe</c> elevated through
/// <c>Services/EngineLauncher.cs</c>, connect the named pipe with StreamJsonRpc, verify the server PID/image/signer in
/// <c>Services/PipeServerVerifier.cs</c>) arrives with A4's engine; until then the app runs on
/// <see cref="DemoEngineConnector"/>.
/// </summary>
public interface IEngineConnector
{
    /// <summary>True when the engine is the in-process demo (fixture data, nothing on this PC is read or changed).</summary>
    bool IsDemo { get; }

    /// <summary>Starts (or finds) the engine and connects.</summary>
    /// <param name="requested">Elevated (the player accepts the UAC prompt) or limited ("scan without permission").</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IEngineRpc> ConnectAsync(PrivilegeMode requested, CancellationToken cancellationToken);
}
