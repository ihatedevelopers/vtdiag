using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Serialization;
using VTD.Knowledge.Crypto;
using VTD.Knowledge.Packs;

namespace VTD.Knowledge;

/// <summary>
/// Loads and verifies the KB (DESIGN.md section 2): a downloaded pack must be Ed25519-signed by an embedded key that is
/// not revoked, list every file with a matching SHA-256, pass schema validation (then it is read leniently), not be a
/// revoked or rolled-back version, and not need a newer app. A dev pack is unsigned (red banner; the analyzer offers no
/// fixes). Anything that fails is never loaded, and the bundled KB is used instead. Validity windows use the
/// clock-corrected <see cref="KbLoadOptions.NowUtc"/> with <see cref="KbLoadOptions.ClockSkewTolerance"/>.
/// </summary>
public sealed class KnowledgeBaseLoader : IKnowledgeBaseLoader
{
    /// <summary>Manifest file name.</summary>
    public const string ManifestFile = "kb.manifest.json";

    /// <summary>Manifest signature file name.</summary>
    public const string ManifestSigFile = "kb.manifest.sig";

    /// <summary>Revocation list file name.</summary>
    public const string RevocationsFile = "kb.revocations.json";

    /// <summary>Revocation signature file name.</summary>
    public const string RevocationsSigFile = "kb.revocations.sig";

    private const long MaxFileBytes = 16L * 1024 * 1024;
    private readonly IReadOnlyList<KbTrustedKey> _keys;

    /// <summary>Creates a loader with the embedded trust anchors.</summary>
    public KnowledgeBaseLoader()
        : this(KbTrustAnchors.Embedded)
    {
    }

    /// <summary>Creates a loader with explicit trusted keys (tests use a per-run dev key).</summary>
    /// <param name="trustedKeys">Trusted keys.</param>
    public KnowledgeBaseLoader(IReadOnlyList<KbTrustedKey> trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        _keys = trustedKeys;
    }

    /// <inheritdoc />
    public async Task<KbLoadResult> LoadAsync(KbLoadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<KbIssue>();
        var schemas = await LoadSchemasAsync(Path.Combine(options.BundledPath, "schema"), issues, cancellationToken).ConfigureAwait(false);
        try
        {
            var revocation = await LoadRevocationsAsync(options, issues, cancellationToken).ConfigureAwait(false);
            var tried = false;

            if (!string.IsNullOrEmpty(options.DevPath))
            {
                tried = true;
                var dev = await TryLoadPackAsync(options.DevPath, KbSource.Dev, options, revocation, schemas, issues, cancellationToken).ConfigureAwait(false);
                if (dev is not null)
                {
                    return new KbLoadResult { Kb = dev, FellBackToBundled = false, RevocationSerial = revocation.Serial, RevokedKeyIds = revocation.RevokedKeyIds, RevokedVersions = revocation.RevokedVersions, Issues = issues };
                }
            }

            if (!string.IsNullOrEmpty(options.DownloadedPath))
            {
                tried = true;
                if (revocation.RolledBack)
                {
                    issues.Add(Issue("downloaded.rejected", KbIssueSeverity.Error, options.DownloadedPath, "Revocation state is older than the last one seen; downloaded packs are not trusted."));
                }
                else
                {
                    var downloaded = await TryLoadPackAsync(options.DownloadedPath, KbSource.Downloaded, options, revocation, schemas, issues, cancellationToken).ConfigureAwait(false);
                    if (downloaded is not null)
                    {
                        var bundledVersion = await PeekVersionAsync(options.BundledPath, cancellationToken).ConfigureAwait(false);
                        if (bundledVersion is null || VersionComparer.Compare(downloaded.Version.Version, bundledVersion) >= 0)
                        {
                            return new KbLoadResult { Kb = downloaded, FellBackToBundled = false, RevocationSerial = revocation.Serial, RevokedKeyIds = revocation.RevokedKeyIds, RevokedVersions = revocation.RevokedVersions, Issues = issues };
                        }

                        issues.Add(Issue("downloaded.older_than_bundled", KbIssueSeverity.Info, options.DownloadedPath,
                            $"Downloaded KB {downloaded.Version.Version} is older than the bundled {bundledVersion}; using the bundled KB."));
                        tried = false;
                    }
                }
            }

            var bundled = await TryLoadPackAsync(options.BundledPath, KbSource.Bundled, options, revocation, schemas, issues, cancellationToken).ConfigureAwait(false);
            if (bundled is null)
            {
                issues.Add(Issue("bundled.unusable", KbIssueSeverity.Error, options.BundledPath, "The bundled KB failed verification; running with an empty KB."));
            }

            return new KbLoadResult { Kb = bundled ?? KnowledgeBase.Empty(), FellBackToBundled = tried, RevocationSerial = revocation.Serial, RevokedKeyIds = revocation.RevokedKeyIds, RevokedVersions = revocation.RevokedVersions, Issues = issues };
        }
        finally
        {
            foreach (var s in schemas.Values)
            {
                s.Dispose();
            }
        }
    }

    private async Task<KnowledgeBase?> TryLoadPackAsync(string folder, KbSource source, KbLoadOptions options, RevocationState revocation,
        IReadOnlyDictionary<string, MiniJsonSchema> schemas, List<KbIssue> issues, CancellationToken ct)
    {
        var errorsBefore = issues.Count(i => i.Severity == KbIssueSeverity.Error);
        var label = source.ToString();
        var manifestPath = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            issues.Add(Issue("manifest.missing", KbIssueSeverity.Error, label, $"No {ManifestFile} in the {label} pack."));
            return null;
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
        string? signedBy = null;
        var sigPath = Path.Combine(folder, ManifestSigFile);
        if (source == KbSource.Downloaded || File.Exists(sigPath))
        {
            var sig = File.Exists(sigPath) ? KbSignature.TryParse(await File.ReadAllBytesAsync(sigPath, ct).ConfigureAwait(false)) : null;
            var ok = sig is not null && sig.VerifyWith(manifestBytes, _keys, revocation.RevokedKeyIds);
            if (ok)
            {
                signedBy = sig!.KeyId;
            }
            else if (source == KbSource.Downloaded)
            {
                issues.Add(Issue("signature.invalid", KbIssueSeverity.Error, label, "The pack signature is missing, invalid, from an unknown key or from a revoked key."));
                return null;
            }
            else
            {
                issues.Add(Issue("signature.invalid", KbIssueSeverity.Warning, label, "The pack signature does not verify (pack loaded because it is not a download)."));
            }
        }

        KbManifest manifest;
        try
        {
            manifest = source == KbSource.Downloaded ? VtdJson.DeserializeLenient<KbManifest>(System.Text.Encoding.UTF8.GetString(manifestBytes)) : VtdJson.Deserialize<KbManifest>(System.Text.Encoding.UTF8.GetString(manifestBytes));
        }
        catch (JsonException e)
        {
            issues.Add(Issue("manifest.invalid", KbIssueSeverity.Error, label, e.Message));
            return null;
        }

        if (!CheckManifest(manifest, source, options, revocation, issues, label))
        {
            return null;
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            if (!IsSafeRelativePath(entry.Path))
            {
                issues.Add(Issue("manifest.bad_path", KbIssueSeverity.Error, label, $"Unsafe path in manifest: {entry.Path}"));
                return null;
            }

            var full = Path.Combine(folder, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                issues.Add(Issue("hash.missing_file", KbIssueSeverity.Error, entry.Path, $"{label} pack: listed file is missing."));
                return null;
            }

            var length = new FileInfo(full).Length;
            if (length != entry.Size || length > MaxFileBytes)
            {
                issues.Add(Issue("hash.size_mismatch", KbIssueSeverity.Error, entry.Path, $"{label} pack: size does not match the manifest."));
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Sha256, StringComparison.Ordinal))
            {
                issues.Add(Issue("hash.mismatch", KbIssueSeverity.Error, entry.Path, $"{label} pack: SHA-256 does not match the manifest."));
                return null;
            }

            files[entry.Path] = bytes;
        }

        var mode = source == KbSource.Downloaded ? KbReadMode.Lenient : KbReadMode.Strict;
        var packIssues = new List<KbIssue>();
        var content = KbPackReader.Read(files, mode, schemas.Count > 0 ? schemas : null, packIssues);
        issues.AddRange(packIssues.Select(i => i with { Path = label + ":" + i.Path }));
        if (issues.Count(i => i.Severity == KbIssueSeverity.Error) > errorsBefore)
        {
            return null;
        }

        var version = new KbVersion
        {
            Version = manifest.Version,
            Format = manifest.Format,
            Source = source,
            ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            SignedByKeyId = signedBy,
        };
        return new KnowledgeBase(version, content);
    }

    private static bool CheckManifest(KbManifest manifest, KbSource source, KbLoadOptions options, RevocationState revocation, List<KbIssue> issues, string label)
    {
        if (manifest.Format > KbManifest.CurrentFormat)
        {
            issues.Add(Issue("format.unsupported", KbIssueSeverity.Error, label, $"KB format {manifest.Format.ToString(CultureInfo.InvariantCulture)} is newer than this app understands."));
            return false;
        }

        if (VersionComparer.Compare(options.AppVersion, manifest.MinApp) < 0)
        {
            issues.Add(Issue("app.too_old", KbIssueSeverity.Error, label, $"The pack needs app {manifest.MinApp} or newer."));
            return false;
        }

        // The bundled KB ships inside the signed installer and is the last fallback, so only downloaded and dev packs can be revoked.
        if (source != KbSource.Bundled && revocation.RevokedVersions.Contains(manifest.Version, StringComparer.Ordinal))
        {
            issues.Add(Issue("version.revoked", KbIssueSeverity.Error, label, $"KB version {manifest.Version} is revoked."));
            return false;
        }

        if (source == KbSource.Downloaded)
        {
            if (options.MinKbVersion is not null && VersionComparer.Compare(manifest.Version, options.MinKbVersion) < 0)
            {
                issues.Add(Issue("version.rollback", KbIssueSeverity.Error, label, $"KB {manifest.Version} is older than the last accepted {options.MinKbVersion}."));
                return false;
            }

            if (manifest.CreatedUtc > options.NowUtc + options.ClockSkewTolerance)
            {
                issues.Add(Issue("manifest.from_future", KbIssueSeverity.Error, label, "The pack is dated in the future (check the PC clock)."));
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Revocation state = the UNION of every validly signed list (bundled and downloaded): a newer list can add
    /// revocations but never lift one, and a list signed by a key that any list revokes is ignored (a revoked key can
    /// not un-revoke itself). The serial is the highest among the lists that survive; lower than the last seen = rollback.
    /// </summary>
    private async Task<RevocationState> LoadRevocationsAsync(KbLoadOptions options, List<KbIssue> issues, CancellationToken ct)
    {
        var lists = new List<(KbRevocationList List, string KeyId, string Path)>();
        foreach (var folder in new[] { options.BundledPath, options.DownloadedPath })
        {
            if (string.IsNullOrEmpty(folder))
            {
                continue;
            }

            var path = Path.Combine(folder, RevocationsFile);
            var sigPath = Path.Combine(folder, RevocationsSigFile);
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var sig = File.Exists(sigPath) ? KbSignature.TryParse(await File.ReadAllBytesAsync(sigPath, ct).ConfigureAwait(false)) : null;
            if (sig is null || !sig.VerifyWith(bytes, _keys, []))
            {
                issues.Add(Issue("revocations.unsigned", KbIssueSeverity.Info, path, "Revocation list is not signed by a trusted key; ignored."));
                continue;
            }

            try
            {
                var list = VtdJson.DeserializeLenient<KbRevocationList>(System.Text.Encoding.UTF8.GetString(bytes));
                if (list.IssuedUtc > options.NowUtc + options.ClockSkewTolerance)
                {
                    issues.Add(Issue("revocations.from_future", KbIssueSeverity.Warning, path, "Revocation list dated in the future; ignored."));
                    continue;
                }

                lists.Add((list, sig.KeyId, path));
            }
            catch (JsonException e)
            {
                issues.Add(Issue("revocations.invalid", KbIssueSeverity.Warning, path, e.Message));
            }
        }

        // Drop lists signed by a revoked key until nothing changes, then union what is left. Keys and versions revoked by
        // any list seen before (persisted by A4) stay revoked even if that list is now missing: revocation only grows.
        var revokedKeys = new HashSet<string>(options.KnownRevokedKeyIds, StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var key in lists.Where(l => !revokedKeys.Contains(l.KeyId)).SelectMany(l => l.List.RevokedKeyIds).ToList())
            {
                changed |= revokedKeys.Add(key);
            }
        }
        while (changed);

        foreach (var dropped in lists.Where(l => revokedKeys.Contains(l.KeyId)))
        {
            issues.Add(Issue("revocations.revoked_signer", KbIssueSeverity.Warning, dropped.Path, "Revocation list is signed by a revoked key; ignored."));
        }

        var valid = lists.Where(l => !revokedKeys.Contains(l.KeyId)).Select(l => l.List).ToList();
        if (valid.Count == 0)
        {
            return new RevocationState(options.MinRevocationSerial, Sorted(options.KnownRevokedVersions), Sorted(revokedKeys), RolledBack: false);
        }

        var serial = valid.Max(l => l.Serial);
        var versions = Sorted(valid.SelectMany(l => l.RevokedVersions).Concat(options.KnownRevokedVersions));
        if (serial < options.MinRevocationSerial)
        {
            issues.Add(Issue("revocations.rollback", KbIssueSeverity.Error, null,
                string.Create(CultureInfo.InvariantCulture, $"Revocation serial {serial} is lower than the last seen {options.MinRevocationSerial}.")));
            return new RevocationState(options.MinRevocationSerial, versions, Sorted(revokedKeys), RolledBack: true);
        }

        return new RevocationState(serial, versions, Sorted(revokedKeys), RolledBack: false);
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> ids)
        => ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static async Task<IReadOnlyDictionary<string, MiniJsonSchema>> LoadSchemasAsync(string schemaDir, List<KbIssue> issues, CancellationToken ct)
    {
        var map = new Dictionary<string, MiniJsonSchema>(StringComparer.Ordinal);
        if (!Directory.Exists(schemaDir))
        {
            issues.Add(Issue("schema.unavailable", KbIssueSeverity.Info, schemaDir, "No schema folder next to the bundled KB; typed parsing only."));
            return map;
        }

        foreach (var name in new[] { "rule", "signal", "collector", "conflict", "code", "fix", "guide", "question", "symptom", "table", "strings", "trusted-helpers" })
        {
            var path = Path.Combine(schemaDir, name + ".schema.json");
            if (File.Exists(path))
            {
                map[name] = MiniJsonSchema.Parse(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
            }
        }

        return map;
    }

    private static async Task<string?> PeekVersionAsync(string folder, CancellationToken ct)
    {
        var path = Path.Combine(folder, ManifestFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return VtdJson.DeserializeLenient<KbManifest>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Version;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSafeRelativePath(string path)
        => !string.IsNullOrEmpty(path) && !path.Contains('\\', StringComparison.Ordinal) && !path.Contains(':', StringComparison.Ordinal)
           && !path.StartsWith('/') && !path.Split('/').Any(s => s.Length == 0 || string.Equals(s, "..", StringComparison.Ordinal) || string.Equals(s, ".", StringComparison.Ordinal));

    private static KbIssue Issue(string code, KbIssueSeverity severity, string? path, string message)
        => new() { Code = code, Severity = severity, Path = path, Message = message };

    private sealed record RevocationState(long Serial, IReadOnlyList<string> RevokedVersions, IReadOnlyList<string> RevokedKeyIds, bool RolledBack);
}

/// <summary>Orders CalVer (<c>2026.10.21.1</c>) and SemVer (<c>0.1.0</c>, pre-release ignored) strings by numeric segments.</summary>
public static class VersionComparer
{
    /// <summary>Compares two versions.</summary>
    /// <param name="a">First.</param>
    /// <param name="b">Second.</param>
    public static int Compare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var x = Segments(a);
        var y = Segments(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    private static long[] Segments(string v)
    {
        var core = v.Split('-', '+')[0];
        return core.Split('.').Select(s => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
    }
}
