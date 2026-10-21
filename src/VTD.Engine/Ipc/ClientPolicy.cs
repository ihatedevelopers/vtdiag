using System.Security.Principal;
using VTD.Contracts.Sessions;

namespace VTD.Engine.Ipc;

/// <summary>What the engine established about a pipe client, from the pipe itself (never from a client claim).</summary>
public sealed record ClientIdentity
{
    /// <summary>Client PID (GetNamedPipeClientProcessId).</summary>
    public required int ProcessId { get; init; }

    /// <summary>Client image path (by PID, without a process handle).</summary>
    public required string? ImagePath { get; init; }

    /// <summary>SID of the client's token (identification-level impersonation of the pipe client).</summary>
    public required string? UserSid { get; init; }

    /// <summary>The client's token carries Administrators (enabled or deny-only).</summary>
    public required bool UserIsAdmin { get; init; }

    /// <summary>The client token is elevated (Administrators enabled).</summary>
    public required bool Elevated { get; init; }

    /// <summary>Signature of the client image.</summary>
    public required SignatureCheck Signature { get; init; }

    /// <summary>The verified origin (Unspecified = refused).</summary>
    public required EventOrigin Origin { get; init; }

    /// <summary>Why the client was refused.</summary>
    public string? RefusalCode { get; init; }
}

/// <summary>
/// Decides which origin a verified pipe client gets (DESIGN.md section 2: PID → token SID, image path, signer). Only
/// images that sit next to the engine (in an elevated engine: under <c>%ProgramFiles%</c>, admin-protected) are
/// accepted: <c>VTDiag.exe</c> is the UI, <c>VTDiag.Engine.exe</c> (the CLI) is <see cref="EventOrigin.Cli"/>,
/// <c>VTDiag.Mcp.exe</c> is <see cref="EventOrigin.Mcp"/>. The UI must run as the interactive player. When the engine
/// itself is Authenticode-signed, clients must be validly signed by the same certificate; a signed-but-invalid image is
/// always refused.
/// </summary>
public sealed class ClientPolicy
{
    /// <summary>The UI image.</summary>
    public const string UiImage = "VTDiag.exe";

    /// <summary>The engine image (CLI clients).</summary>
    public const string EngineImage = "VTDiag.Engine.exe";

    /// <summary>The MCP host image (M3).</summary>
    public const string McpImage = "VTDiag.Mcp.exe";

    /// <summary>Creates the policy.</summary>
    /// <param name="installDirectory">Directory the client images must be in (the engine's own).</param>
    /// <param name="playerSid">The interactive player.</param>
    /// <param name="engineSignature">The engine's own signature (clients must match a valid one).</param>
    public ClientPolicy(string installDirectory, string playerSid, SignatureCheck engineSignature)
    {
        InstallDirectory = Normalize(installDirectory ?? throw new ArgumentNullException(nameof(installDirectory)));
        PlayerSid = playerSid ?? throw new ArgumentNullException(nameof(playerSid));
        EngineSignature = engineSignature ?? throw new ArgumentNullException(nameof(engineSignature));
    }

    /// <summary>Install directory (normalized, with a trailing separator).</summary>
    public string InstallDirectory { get; }

    /// <summary>The interactive player SID.</summary>
    public string PlayerSid { get; }

    /// <summary>The engine's own signature.</summary>
    public SignatureCheck EngineSignature { get; }

    /// <summary>
    /// Image file name → origin. Defaults to the three VTDiag images; tests add their own host image to stand in for one of
    /// them (still subject to every other check).
    /// </summary>
    public Dictionary<string, EventOrigin> Images { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [UiImage] = EventOrigin.Ui,
        [EngineImage] = EventOrigin.Cli,
        [McpImage] = EventOrigin.Mcp,
    };

    /// <summary>Decides the origin.</summary>
    /// <param name="processId">PID.</param>
    /// <param name="imagePath">Image path.</param>
    /// <param name="userSid">Token SID.</param>
    /// <param name="userIsAdmin">Token has Administrators.</param>
    /// <param name="elevated">Token is elevated.</param>
    /// <param name="signature">Image signature (computed by the caller only when the image passed the path checks).</param>
    public ClientIdentity Decide(int processId, string? imagePath, string? userSid, bool userIsAdmin, bool elevated, Func<string, SignatureCheck> signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ClientIdentity Refuse(string code, SignatureCheck? sig = null) => new()
        {
            ProcessId = processId,
            ImagePath = imagePath,
            UserSid = userSid,
            UserIsAdmin = userIsAdmin,
            Elevated = elevated,
            Signature = sig ?? new SignatureCheck(SignatureState.Unknown, null),
            Origin = EventOrigin.Unspecified,
            RefusalCode = code,
        };

        if (imagePath is null || userSid is null)
        {
            return Refuse("client.unidentified");
        }

        if (PipeClientVerifier.IsProtectedImage(imagePath))
        {
            return Refuse("client.protected_process");
        }

        var directory = Normalize(Path.GetDirectoryName(imagePath) ?? string.Empty);
        if (!string.Equals(directory, InstallDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse("client.wrong_location");
        }

        if (!Images.TryGetValue(Path.GetFileName(imagePath), out var origin))
        {
            return Refuse("client.unknown_image");
        }

        var sameUser = string.Equals(userSid, PlayerSid, StringComparison.OrdinalIgnoreCase);
        if (origin == EventOrigin.Ui && !sameUser)
        {
            // The consent dialog belongs to the person at the PC: the UI must run as the interactive player.
            return Refuse("client.not_player");
        }

        if (origin != EventOrigin.Ui && !sameUser && !elevated)
        {
            return Refuse("client.not_player");
        }

        var sig = signature(imagePath);
        if (sig.State == SignatureState.Invalid)
        {
            return Refuse("client.signature_invalid", sig);
        }

        if (EngineSignature.State == SignatureState.Valid
            && (sig.State != SignatureState.Valid || !string.Equals(sig.SignerThumbprint, EngineSignature.SignerThumbprint, StringComparison.OrdinalIgnoreCase)))
        {
            return Refuse("client.signer_mismatch", sig);
        }

        return new ClientIdentity
        {
            ProcessId = processId,
            ImagePath = imagePath,
            UserSid = userSid,
            UserIsAdmin = userIsAdmin,
            Elevated = elevated,
            Signature = sig,
            Origin = origin,
        };
    }

    /// <summary>True when the token has Administrators enabled (elevated) — and whether it carries it at all (deny-only too).</summary>
    /// <param name="identity">Token.</param>
    public static (bool Admin, bool Elevated) AdminState(WindowsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        const string administrators = "S-1-5-32-544";
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var carries = elevated || identity.Claims.Any(c => string.Equals(c.Value, administrators, StringComparison.Ordinal));
        return (carries, elevated);
    }

    private static string Normalize(string directory)
    {
        var full = Path.GetFullPath(directory);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }
}
