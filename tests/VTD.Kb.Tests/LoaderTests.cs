using VTD.Contracts.Knowledge;
using VTD.Knowledge;
using VTD.Knowledge.Crypto;

namespace VTD.Kb.Tests;

/// <summary>Ed25519 and the pack verification rules (signature, hashes, revocation, rollback, app version, clock).</summary>
public sealed class LoaderTests
{
    [Fact]
    public void Ed25519_matches_rfc8032_test_vector_1()
    {
        var seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var pub = Ed25519.PublicKeyFromSeed(seed);
        Assert.Equal("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", Convert.ToHexStringLower(pub));
        var sig = Ed25519.Sign(seed, []);
        Assert.Equal("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b", Convert.ToHexStringLower(sig));
        Assert.True(Ed25519.Verify(pub, [], sig));
    }

    [Fact]
    public void Ed25519_rejects_tampering_and_small_order_keys()
    {
        var seed = new byte[32];
        seed[0] = 7;
        var pub = Ed25519.PublicKeyFromSeed(seed);
        var msg = "kb.manifest"u8.ToArray();
        var sig = Ed25519.Sign(seed, msg);
        Assert.True(Ed25519.Verify(pub, msg, sig));
        msg[0] ^= 1;
        Assert.False(Ed25519.Verify(pub, msg, sig));
        msg[0] ^= 1;
        sig[10] ^= 0x40;
        Assert.False(Ed25519.Verify(pub, msg, sig));
        Assert.False(Ed25519.Verify(new byte[32], msg, sig)); // all-zero key decodes to a small-order point
        Assert.False(Ed25519.Verify(pub[..31], msg, sig));
    }

    [Fact]
    public void Embedded_anchors_are_unset_until_the_release_keys_exist()
    {
        Assert.Empty(KbTrustAnchors.Embedded);
    }

    [Fact]
    public async Task Signed_downloaded_pack_is_used()
    {
        using var pack = new DevPack();
        pack.SignManifest();
        var result = await new KnowledgeBaseLoader([pack.Key]).LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None);
        Assert.Equal(KbSource.Downloaded, result.Kb.Version.Source);
        Assert.Equal(pack.Key.KeyId, result.Kb.Version.SignedByKeyId);
        Assert.False(result.FellBackToBundled);
        Assert.Equal(90, result.Kb.Rules.Count(r => r.Kind == RuleKind.Hypothesis));
    }

    [Fact]
    public async Task Unsigned_or_foreign_signed_download_falls_back_to_bundled()
    {
        using var pack = new DevPack();
        var unsigned = await new KnowledgeBaseLoader([pack.Key]).LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None);
        Assert.True(unsigned.FellBackToBundled);
        Assert.Equal(KbSource.Bundled, unsigned.Kb.Version.Source);
        Assert.Contains(unsigned.Issues, i => i.Code == "signature.invalid");

        pack.SignManifest();
        using var other = new DevPack();
        var foreign = await new KnowledgeBaseLoader([other.Key]).LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None);
        Assert.True(foreign.FellBackToBundled);
    }

    [Fact]
    public async Task Tampered_file_is_rejected()
    {
        using var pack = new DevPack();
        pack.SignManifest();
        var target = Path.Combine(pack.Folder, "codes", "codes.json");
        File.WriteAllText(target, File.ReadAllText(target).Replace("\"logged\": 4.0", "\"logged\": 9.0", StringComparison.Ordinal));
        var result = await new KnowledgeBaseLoader([pack.Key]).LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None);
        Assert.True(result.FellBackToBundled);
        Assert.Contains(result.Issues, i => i.Code is "hash.mismatch" or "hash.size_mismatch");
    }

    [Fact]
    public async Task Revoked_key_version_and_rollbacks_are_rejected()
    {
        using var pack = new DevPack();
        pack.SignManifest();
        var loader = new KnowledgeBaseLoader([pack.Key]);

        pack.WriteRevocations(5, keys: [pack.Key.KeyId]);
        var revokedKey = await loader.LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None);
        Assert.True(revokedKey.FellBackToBundled);
        Assert.Contains(revokedKey.Issues, i => i.Code == "signature.invalid"); // the manifest key is revoked

        pack.WriteRevocations(6, versions: ["2026.10.21.1"]);
        var revokedVersion = await loader.LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None);
        Assert.True(revokedVersion.FellBackToBundled);
        Assert.Contains(revokedVersion.Issues, i => i.Code == "version.revoked");

        pack.WriteRevocations(2);
        var rolledBack = await loader.LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder) with { MinRevocationSerial = 6 }, CancellationToken.None);
        Assert.True(rolledBack.FellBackToBundled);
        Assert.Contains(rolledBack.Issues, i => i.Code == "revocations.rollback");
        Assert.Equal(6, rolledBack.RevocationSerial);

        pack.WriteRevocations(7);
        var older = await loader.LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder) with { MinKbVersion = "2026.11.1.1" }, CancellationToken.None);
        Assert.True(older.FellBackToBundled);
        Assert.Contains(older.Issues, i => i.Code == "version.rollback");
    }

    [Fact]
    public async Task A_revoked_key_cannot_unrevoke_itself_with_a_newer_list()
    {
        using var bundled = new DevPack();   // signed by key B, revokes key A
        using var download = new DevPack();  // signed by key A, newer serial, revokes nothing
        download.SignManifest();
        download.WriteRevocations(9);
        bundled.WriteRevocations(5, keys: [download.Key.KeyId]);
        var loader = new KnowledgeBaseLoader([download.Key, bundled.Key]);
        var result = await loader.LoadAsync(Repo.Options(bundled.Folder, downloaded: download.Folder), CancellationToken.None);
        Assert.True(result.FellBackToBundled);
        Assert.Equal(KbSource.Bundled, result.Kb.Version.Source);
        Assert.Equal(5, result.RevocationSerial);
        Assert.Contains(result.Issues, i => i.Code == "revocations.revoked_signer");
    }

    [Fact]
    public async Task Persisted_revocations_survive_a_deleted_revocation_list()
    {
        using var pack = new DevPack();
        pack.SignManifest();
        var loader = new KnowledgeBaseLoader([pack.Key]);
        pack.WriteRevocations(5, keys: [pack.Key.KeyId]);
        var first = await loader.LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None);
        Assert.Contains(pack.Key.KeyId, first.RevokedKeyIds);

        // An attacker (or a cleanup tool) deletes the list: only the serial would not be enough to keep the key revoked.
        File.Delete(Path.Combine(pack.Folder, KnowledgeBaseLoader.RevocationsFile));
        File.Delete(Path.Combine(pack.Folder, KnowledgeBaseLoader.RevocationsSigFile));
        var persisted = Repo.Options(Repo.Kb, downloaded: pack.Folder) with
        {
            MinRevocationSerial = first.RevocationSerial,
            KnownRevokedKeyIds = first.RevokedKeyIds,
        };
        var second = await loader.LoadAsync(persisted, CancellationToken.None);
        Assert.True(second.FellBackToBundled);
        Assert.Contains(second.Issues, i => i.Code == "signature.invalid");
        Assert.Contains(pack.Key.KeyId, second.RevokedKeyIds);

        // Persisted revoked versions apply the same way.
        using var clean = new DevPack();
        clean.SignManifest();
        var byVersion = await new KnowledgeBaseLoader([clean.Key]).LoadAsync(Repo.Options(Repo.Kb, downloaded: clean.Folder) with { KnownRevokedVersions = ["2026.10.21.1"] }, CancellationToken.None);
        Assert.True(byVersion.FellBackToBundled);
        Assert.Contains(byVersion.Issues, i => i.Code == "version.revoked");
        Assert.Contains("2026.10.21.1", byVersion.RevokedVersions);
    }

    [Fact]
    public async Task Revocations_are_a_union_a_newer_list_cannot_drop_an_older_entry()
    {
        using var bundled = new DevPack();
        using var download = new DevPack();
        download.SignManifest();
        bundled.WriteRevocations(3, versions: ["2026.10.21.1"]);
        download.WriteRevocations(8);
        var loader = new KnowledgeBaseLoader([download.Key, bundled.Key]);
        var result = await loader.LoadAsync(Repo.Options(bundled.Folder, downloaded: download.Folder), CancellationToken.None);
        Assert.Contains(result.Issues, i => i.Code == "version.revoked");
        Assert.Equal(8, result.RevocationSerial);
    }

    [Fact]
    public async Task Newer_app_requirement_future_date_and_newer_format_are_rejected()
    {
        using var pack = new DevPack();
        var loader = new KnowledgeBaseLoader([pack.Key]);

        pack.RewriteManifest(m => m with { MinApp = "9.0.0" });
        pack.SignManifest();
        Assert.Contains((await loader.LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None)).Issues, i => i.Code == "app.too_old");

        pack.RewriteManifest(m => m with { MinApp = "0.1.0", CreatedUtc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        pack.SignManifest();
        Assert.Contains((await loader.LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None)).Issues, i => i.Code == "manifest.from_future");

        pack.RewriteManifest(m => m with { CreatedUtc = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), Format = KbManifest.CurrentFormat + 1 });
        pack.SignManifest();
        Assert.Contains((await loader.LoadAsync(Repo.Options(Repo.Kb, downloaded: pack.Folder), CancellationToken.None)).Issues, i => i.Code == "format.unsupported");
    }

    [Fact]
    public async Task Dev_pack_loads_unsigned_and_schema_errors_reject_it()
    {
        using var pack = new DevPack();
        var ok = await new KnowledgeBaseLoader([]).LoadAsync(Repo.Options(Repo.Kb, dev: pack.Folder), CancellationToken.None);
        Assert.Equal(KbSource.Dev, ok.Kb.Version.Source);

        // A schema violation in a dev pack: the manifest hash is updated so only the schema check can catch it.
        var target = Path.Combine(pack.Folder, "questions", "questions.json");
        File.WriteAllText(target, File.ReadAllText(target).Replace("\"kind\": \"yesNo\"", "\"kind\": \"essay\"", StringComparison.Ordinal));
        var bytes = File.ReadAllBytes(target);
        pack.RewriteManifest(m => m with
        {
            Files = [.. m.Files.Select(f => f.Path == "questions/questions.json"
                ? f with { Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), Size = bytes.Length }
                : f)],
        });
        var bad = await new KnowledgeBaseLoader([]).LoadAsync(Repo.Options(Repo.Kb, dev: pack.Folder), CancellationToken.None);
        Assert.Equal(KbSource.Bundled, bad.Kb.Version.Source);
        Assert.True(bad.FellBackToBundled);
        Assert.Contains(bad.Issues, i => i.Code == "schema");
    }

    [Fact]
    public async Task Broken_bundled_pack_still_returns_a_kb()
    {
        using var pack = new DevPack();
        File.Delete(Path.Combine(pack.Folder, "symptoms.json"));
        var result = await new KnowledgeBaseLoader([]).LoadAsync(Repo.Options(pack.Folder), CancellationToken.None);
        Assert.NotNull(result.Kb);
        Assert.Empty(result.Kb.Rules);
        Assert.Contains(result.Issues, i => i.Code == "bundled.unusable");
    }

    [Theory]
    [InlineData("2026.10.21.1", "2026.10.21.1", 0)]
    [InlineData("2026.10.21.2", "2026.10.21.1", 1)]
    [InlineData("2026.9.30.1", "2026.10.1.1", -1)]
    [InlineData("0.1.0", "0.2.0-beta", -1)]
    [InlineData("1.0.0", "1.0", 0)]
    public void Versions_compare_numerically(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(VersionComparer.Compare(a, b)));
}
