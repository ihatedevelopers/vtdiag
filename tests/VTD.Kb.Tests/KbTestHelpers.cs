using System.Security.Cryptography;
using VTD.Contracts.Knowledge;
using VTD.Knowledge;
using VTD.Knowledge.Crypto;

namespace VTD.Kb.Tests;

/// <summary>Repository paths and a shared load of the real kb/ folder.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string Kb => Path.Combine(Root, "kb");

    public static string SchemaDir => Path.Combine(Kb, "schema");

    private static readonly Lazy<KbLoadResult> Loaded = new(() => new KnowledgeBaseLoader([]).LoadAsync(Options(Kb), CancellationToken.None).GetAwaiter().GetResult());

    /// <summary>The real KB, loaded through the production loader (bundled path).</summary>
    public static KbLoadResult RealKb => Loaded.Value;

    public static KnowledgeBase Real => (KnowledgeBase)RealKb.Kb;

    public static KbLoadOptions Options(string bundled, string? downloaded = null, string? dev = null) => new()
    {
        BundledPath = bundled,
        DownloadedPath = downloaded,
        DevPath = dev,
        NowUtc = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
        AppVersion = "0.1.0",
    };

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VTDiag.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("VTDiag.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A per-run dev Ed25519 key (never written to the repository) and a scratch copy of kb/ to tamper with.</summary>
internal sealed class DevPack : IDisposable
{
    public DevPack()
    {
        Seed = RandomNumberGenerator.GetBytes(32);
        var pub = Ed25519.PublicKeyFromSeed(Seed);
        Key = new KbTrustedKey(KbTrustedKey.IdOf(pub), pub);
        Folder = Path.Combine(Path.GetTempPath(), "vtd-kb-tests", Guid.NewGuid().ToString("N"));
        Copy(Repo.Kb, Folder);
    }

    public byte[] Seed { get; }

    public KbTrustedKey Key { get; }

    public string Folder { get; }

    public void SignManifest() => Sign(KnowledgeBaseLoader.ManifestFile, KnowledgeBaseLoader.ManifestSigFile);

    public void SignRevocations() => Sign(KnowledgeBaseLoader.RevocationsFile, KnowledgeBaseLoader.RevocationsSigFile);

    public void WriteRevocations(long serial, IReadOnlyList<string>? versions = null, IReadOnlyList<string>? keys = null)
    {
        var list = new KbRevocationList { Serial = serial, IssuedUtc = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), RevokedVersions = versions ?? [], RevokedKeyIds = keys ?? [] };
        File.WriteAllText(Path.Combine(Folder, KnowledgeBaseLoader.RevocationsFile), VTD.Contracts.Serialization.VtdJson.Serialize(list, indented: true));
        SignRevocations();
    }

    public void RewriteManifest(Func<KbManifest, KbManifest> change)
    {
        var path = Path.Combine(Folder, KnowledgeBaseLoader.ManifestFile);
        var m = VTD.Contracts.Serialization.VtdJson.Deserialize<KbManifest>(File.ReadAllText(path));
        File.WriteAllText(path, VTD.Contracts.Serialization.VtdJson.Serialize(change(m), indented: true));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Sign(string file, string sigFile)
    {
        var data = File.ReadAllBytes(Path.Combine(Folder, file));
        File.WriteAllText(Path.Combine(Folder, sigFile), KbSignature.Format(Key.KeyId, Ed25519.Sign(Seed, data)));
    }

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }
}
