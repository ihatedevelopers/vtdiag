namespace VTD.Contracts.Knowledge;

/// <summary>Where the loaded KB came from.</summary>
public enum KbSource
{
    /// <summary>The KB bundled with the app (fallback when anything else fails verification).</summary>
    Bundled = 0,

    /// <summary>A downloaded, Ed25519-verified KB release.</summary>
    Downloaded = 1,

    /// <summary>A mod's local dev folder (unsigned; red "dev pack" banner; may not propose fixes).</summary>
    Dev = 2,
}

/// <summary>Identity of a loaded KB.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record KbVersion
{
    /// <summary>CalVer, for example <c>2026.10.21.1</c> (ordered).</summary>
    public required string Version { get; init; }

    /// <summary>KB file format major. The loader refuses a higher format.</summary>
    public required int Format { get; init; }

    /// <summary>Source.</summary>
    public required KbSource Source { get; init; }

    /// <summary>SHA-256 of the manifest.</summary>
    public string? ManifestSha256 { get; init; }

    /// <summary>Id of the key that signed it.</summary>
    public string? SignedByKeyId { get; init; }
}

/// <summary>One file listed in the KB manifest.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record KbFileEntry
{
    /// <summary>Path relative to <c>kb/</c>, forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>Lowercase hex SHA-256.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size (bytes).</summary>
    public required long Size { get; init; }
}

/// <summary>
/// <c>kb/kb.manifest.json</c>. Signed with Ed25519 into <c>kb.manifest.sig</c>; the app embeds two public keys
/// (current and next). A pack that fails verification is never loaded and the bundled KB is used instead.
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record KbManifest
{
    /// <summary>Current KB file format.</summary>
    public const int CurrentFormat = 1;

    /// <summary>KB file format major.</summary>
    public required int Format { get; init; }

    /// <summary>CalVer version.</summary>
    public required string Version { get; init; }

    /// <summary>Minimum app version (SemVer).</summary>
    public required string MinApp { get; init; }

    /// <summary>Build time (UTC).</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Release channel: <c>stable</c>, <c>beta</c> or <c>dev</c>.</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Every file of the pack with its hash; anything not listed is ignored.</summary>
    public required IReadOnlyList<KbFileEntry> Files { get; init; }
}

/// <summary>
/// Signed, monotonically versioned revocation list (<c>kb.revocations.json</c> + <c>.sig</c>). A list with a lower serial
/// than the last one seen is rejected (no rollback).
/// </summary>
[Sensitivity(Sensitivity.None)]
public sealed record KbRevocationList
{
    /// <summary>Monotonic serial.</summary>
    public required long Serial { get; init; }

    /// <summary>Issued (UTC).</summary>
    public required DateTimeOffset IssuedUtc { get; init; }

    /// <summary>Revoked KB versions.</summary>
    public IReadOnlyList<string> RevokedVersions { get; init; } = [];

    /// <summary>Revoked signing key ids.</summary>
    public IReadOnlyList<string> RevokedKeyIds { get; init; } = [];
}

/// <summary>A verified helper's public key (<c>kb/trusted_helpers.json</c>): plans signed by it show "Verified helper".</summary>
[Sensitivity(Sensitivity.None)]
public sealed record TrustedHelperKey
{
    /// <summary>Display name, for example <c>u/ModName</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Public key, <c>ed25519:&lt;base64&gt;</c>.</summary>
    public required string PublicKey { get; init; }

    /// <summary>Valid until.</summary>
    public DateOnly? ValidUntil { get; init; }
}

/// <summary>Options for loading the KB.</summary>
public sealed record KbLoadOptions
{
    /// <summary>Folder of the bundled KB (always present).</summary>
    public required string BundledPath { get; init; }

    /// <summary>Folder of a downloaded KB, if any.</summary>
    public string? DownloadedPath { get; init; }

    /// <summary>Folder of a dev pack, if the player enabled one.</summary>
    public string? DevPath { get; init; }

    /// <summary>Trusted time for signature/expiry checks, AFTER clock-skew resync or measurement (DESIGN.md section 2).</summary>
    public required DateTimeOffset NowUtc { get; init; }

    /// <summary>Tolerated clock skew for validity windows.</summary>
    public TimeSpan ClockSkewTolerance { get; init; } = TimeSpan.FromHours(24);

    /// <summary>App version, for <see cref="KbManifest.MinApp"/>.</summary>
    public required string AppVersion { get; init; }

    /// <summary>Highest revocation-list serial seen before (persisted by A4 in <c>%ProgramData%\VTDiag</c>). A list with a
    /// lower serial is rejected (no rollback).</summary>
    public long MinRevocationSerial { get; init; }

    /// <summary>Signing key ids revoked by any list seen before (persisted by A4 next to the serial). They stay revoked
    /// even when the list that revoked them is later missing or rolled back: revocation only ever grows.</summary>
    public IReadOnlyList<string> KnownRevokedKeyIds { get; init; } = [];

    /// <summary>KB versions revoked by any list seen before (persisted by A4 like <see cref="KnownRevokedKeyIds"/>).</summary>
    public IReadOnlyList<string> KnownRevokedVersions { get; init; } = [];

    /// <summary>Lowest acceptable downloaded KB version (persisted by A4: never go back to an older downloaded pack).</summary>
    public string? MinKbVersion { get; init; }
}

/// <summary>Severity of a KB load issue.</summary>
public enum KbIssueSeverity
{
    /// <summary>Informational.</summary>
    Info = 0,

    /// <summary>Warning (for example a missing Turkish string falls back to English).</summary>
    Warning = 1,

    /// <summary>Error: the pack is rejected as a whole (no partial load).</summary>
    Error = 2,
}

/// <summary>A KB load or lint issue.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record KbIssue
{
    /// <summary>Issue code, for example <c>signature.invalid</c>, <c>schema</c>, <c>ref.unknown_signal</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Severity.</summary>
    public required KbIssueSeverity Severity { get; init; }

    /// <summary>File path and JSON pointer, when relevant.</summary>
    public string? Path { get; init; }

    /// <summary>English message for maintainers.</summary>
    public string? Message { get; init; }
}

/// <summary>Result of loading a KB.</summary>
public sealed record KbLoadResult
{
    /// <summary>The KB in use (never null: the bundled KB is the last fallback).</summary>
    public required IKnowledgeBase Kb { get; init; }

    /// <summary>True when a downloaded or dev pack was rejected and the bundled KB is in use.</summary>
    public required bool FellBackToBundled { get; init; }

    /// <summary>Serial of the revocation list that was applied (A4 persists it as the next <see cref="KbLoadOptions.MinRevocationSerial"/>).</summary>
    public long RevocationSerial { get; init; }

    /// <summary>Every revoked signing key id now known (the persisted ones plus the applied lists); A4 persists it as the
    /// next <see cref="KbLoadOptions.KnownRevokedKeyIds"/>.</summary>
    public IReadOnlyList<string> RevokedKeyIds { get; init; } = [];

    /// <summary>Every revoked KB version now known; A4 persists it as the next <see cref="KbLoadOptions.KnownRevokedVersions"/>.</summary>
    public IReadOnlyList<string> RevokedVersions { get; init; } = [];

    /// <summary>Issues found.</summary>
    public IReadOnlyList<KbIssue> Issues { get; init; } = [];
}

/// <summary>Loads and verifies the KB (A2).</summary>
public interface IKnowledgeBaseLoader
{
    /// <summary>Loads the best verified KB.</summary>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<KbLoadResult> LoadAsync(KbLoadOptions options, CancellationToken cancellationToken);
}
