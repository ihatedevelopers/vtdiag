using System.Security.Cryptography;
using System.Text.Json;

namespace VTD.Knowledge.Crypto;

/// <summary>A public key the loader trusts for KB packs and revocation lists.</summary>
/// <param name="KeyId">Key id: <c>k-</c> + first 16 hex chars of SHA-256(public key).</param>
/// <param name="PublicKey">32-byte Ed25519 public key.</param>
public sealed record KbTrustedKey(string KeyId, byte[] PublicKey)
{
    /// <summary>Parses <c>ed25519:&lt;base64&gt;</c>.</summary>
    /// <param name="text">Key text.</param>
    public static KbTrustedKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const string Prefix = "ed25519:";
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new FormatException("Expected ed25519:<base64>.");
        }

        var bytes = Convert.FromBase64String(text[Prefix.Length..]);
        if (bytes.Length != Ed25519.PublicKeySize)
        {
            throw new FormatException("An Ed25519 public key is 32 bytes.");
        }

        return new KbTrustedKey(IdOf(bytes), bytes);
    }

    /// <summary>Computes the key id of a public key.</summary>
    /// <param name="publicKey">Public key bytes.</param>
    public static string IdOf(ReadOnlySpan<byte> publicKey) => "k-" + Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];

    /// <summary>The key as <c>ed25519:&lt;base64&gt;</c>.</summary>
    public override string ToString() => "ed25519:" + Convert.ToBase64String(PublicKey);
}

/// <summary>
/// The two public keys embedded in the app (current and next). <b>Lead: both slots are empty until the release keys
/// exist</b> (create them offline with <c>python -I tools/kb_builder.py keygen --out &lt;private file&gt;</c>, keep the
/// private seeds off the repo, paste the printed <c>ed25519:...</c> public keys here). With no anchors every downloaded
/// pack is rejected and the bundled KB is used, which is the safe default.
/// </summary>
public static class KbTrustAnchors
{
    /// <summary>Current release key (<c>ed25519:&lt;base64&gt;</c>), or null while unset.</summary>
    public const string? Current = null;

    /// <summary>Next release key (rotation), or null while unset.</summary>
    public const string? Next = null;

    /// <summary>The embedded keys that are set.</summary>
    public static IReadOnlyList<KbTrustedKey> Embedded { get; } =
        new[] { Current, Next }.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => KbTrustedKey.Parse(k!)).ToList();
}

/// <summary>A detached signature file (<c>kb.manifest.sig</c>, <c>kb.revocations.sig</c>): JSON <c>{"alg":"ed25519","keyId":"k-…","sig":"&lt;base64&gt;"}</c>.</summary>
/// <param name="KeyId">Signing key id.</param>
/// <param name="Signature">64-byte signature.</param>
public sealed record KbSignature(string KeyId, byte[] Signature)
{
    /// <summary>Parses a signature file; returns null when malformed.</summary>
    /// <param name="utf8">File bytes.</param>
    public static KbSignature? TryParse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("alg", out var alg) || !string.Equals(alg.GetString(), "ed25519", StringComparison.Ordinal)
                || !root.TryGetProperty("keyId", out var keyId) || keyId.GetString() is not { Length: > 0 } id
                || !root.TryGetProperty("sig", out var sig) || sig.GetString() is not { Length: > 0 } sigText)
            {
                return null;
            }

            var bytes = Convert.FromBase64String(sigText);
            return bytes.Length == Ed25519.SignatureSize ? new KbSignature(id, bytes) : null;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Serializes a signature file (tests and tooling).</summary>
    /// <param name="keyId">Key id.</param>
    /// <param name="signature">Signature.</param>
    public static string Format(string keyId, ReadOnlySpan<byte> signature)
        => "{\n  \"alg\": \"ed25519\",\n  \"keyId\": \"" + keyId + "\",\n  \"sig\": \"" + Convert.ToBase64String(signature) + "\"\n}\n";

    /// <summary>Verifies <paramref name="data"/> against the trusted key named by this signature.</summary>
    /// <param name="data">Signed bytes.</param>
    /// <param name="keys">Trusted keys.</param>
    /// <param name="revokedKeyIds">Revoked key ids.</param>
    public bool VerifyWith(ReadOnlySpan<byte> data, IReadOnlyList<KbTrustedKey> keys, IReadOnlyCollection<string> revokedKeyIds)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(revokedKeyIds);
        if (revokedKeyIds.Contains(KeyId, StringComparer.Ordinal))
        {
            return false;
        }

        var key = keys.FirstOrDefault(k => string.Equals(k.KeyId, KeyId, StringComparison.Ordinal));
        return key is not null && Ed25519.Verify(key.PublicKey, data, Signature);
    }
}
