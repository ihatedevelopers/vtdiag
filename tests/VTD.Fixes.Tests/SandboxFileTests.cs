using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using VTD.Contracts.Fixes;
using VTD.Fixes.Interop;
using VTD.Fixes.Ports;
using VTD.Fixes.Tests.Fakes;
using VTD.Journal;
using VTD.Testing.Fakes;

namespace VTD.Fixes.Tests;

/// <summary>
/// The REAL reparse-safe file port in a temp folder (created and deleted by the test): moves are renames of the validated
/// handle, junctions, hard links, hash changes and occupied targets are refused, nothing is ever deleted.
/// </summary>
public sealed class SandboxFileTests : IDisposable
{
    private readonly string _dir;

    public SandboxFileTests()
    {
        _dir = LongPath(Path.Combine(Path.GetTempPath(), "VTDiag.Tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_dir);
        _dir = LongPath(_dir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Me => WindowsIdentity.GetCurrent().User!.Value;

    [Fact]
    public void A_file_moves_into_the_quarantine_and_back_unchanged()
    {
        var port = Port();
        var src = Write(@"src\a.dat", "payload");
        var sha = port.Sha256(src);
        var dst = Path.Combine(port.QuarantineRoot, "abc", "a.dat");
        var lastWrite = File.GetLastWriteTimeUtc(src);
        port.SafeMove(src, dst, sha);
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(dst));
        Assert.Equal(sha, port.Sha256(dst));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(dst));
        port.WriteManifest(Path.GetDirectoryName(dst)!, new Dictionary<string, string>(StringComparer.Ordinal) { ["originalPath"] = src });
        Assert.Contains("originalPath", File.ReadAllText(Path.Combine(Path.GetDirectoryName(dst)!, "manifest.json")), StringComparison.Ordinal);

        port.SafeMove(dst, src, sha);
        Assert.True(File.Exists(src));
        Assert.Equal("payload", File.ReadAllText(src));
        Assert.True(port.Probe(src).Exists);
        Assert.False(port.Probe(dst).Exists);
        Assert.Contains(Path.Combine(port.QuarantineRoot, "abc"), port.ListDirectories(port.QuarantineRoot));
    }

    [Fact]
    public void A_junction_anywhere_on_the_path_is_refused()
    {
        var port = Port();
        var real = Path.Combine(_dir, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.dat"), "x");
        var link = Path.Combine(_dir, "link");
        if (!Junction(link, real))
        {
            Assert.Skip("mklink /J not available");
        }

        var viaLink = Path.Combine(link, "a.dat");
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("x")));
        var ex = Assert.Throws<PortRefusedException>(() => port.SafeMove(viaLink, Path.Combine(port.QuarantineRoot, "q", "a.dat"), sha));
        Assert.Equal("file.reparse_point", ex.Code);
        Assert.True(File.Exists(Path.Combine(real, "a.dat")));
        Assert.False(port.DirectoryExists(link));

        // A destination through a junction is refused too.
        var src = Write(@"src\b.dat", "y");
        var ex2 = Assert.Throws<PortRefusedException>(() => port.SafeMove(src, Path.Combine(link, "b.dat"), port.Sha256(src)));
        Assert.Equal("file.reparse_point", ex2.Code);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Hard_links_changed_content_and_occupied_targets_are_refused()
    {
        var port = Port();
        var src = Write(@"src\a.dat", "payload");
        var sha = port.Sha256(src);
        var hard = Path.Combine(_dir, "src", "hard.dat");
        if (HardLink(hard, src))
        {
            Assert.Equal("file.hard_links", Assert.Throws<PortRefusedException>(() => port.SafeMove(src, Path.Combine(port.QuarantineRoot, "q", "a.dat"), sha)).Code);
            File.Delete(hard);
        }

        Assert.Equal("file.hash_mismatch", Assert.Throws<PortRefusedException>(() => port.SafeMove(src, Path.Combine(port.QuarantineRoot, "q", "a.dat"), new string('0', 64))).Code);
        var occupied = Write(@"data\quarantine\q\a.dat", "other");
        Assert.Equal("file.destination_exists", Assert.Throws<PortRefusedException>(() => port.SafeMove(src, occupied, sha)).Code);
        Assert.Equal("other", File.ReadAllText(occupied));
        Assert.Equal("payload", File.ReadAllText(src));
    }

    [Fact]
    public void An_untrusted_owner_on_the_path_is_refused()
    {
        var strict = new ReparseSafeFilePort(Folders(), Path.Combine(_dir, "data"), adminOnly: false, [SecureDirectory.SystemSid, SecureDirectory.AdministratorsSid, SecureDirectory.TrustedInstallerSid]);
        var src = Write(@"src\a.dat", "payload");
        // An elevated process (the CI runner) creates files owned by Administrators, which is trusted; make the owner the
        // current user explicitly so the untrusted owner exists whether or not the tests run elevated.
        var file = new FileInfo(src);
        var acl = file.GetAccessControl();
        acl.SetOwner(new SecurityIdentifier(Me));
        file.SetAccessControl(acl);
        var ex = Assert.Throws<PortRefusedException>(() => strict.SafeMove(src, Path.Combine(_dir, "data", "quarantine", "q", "a.dat"), strict.Sha256(src)));
        Assert.Equal("file.untrusted_owner", ex.Code);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task The_quarantine_primitive_round_trips_on_real_files()
    {
        var clock = new FakeClock();
        var system = FakeSystem.Typical(clock);
        var folders = Folders();
        Directory.CreateDirectory(folders.Windows);
        var dat = Path.Combine(folders.Windows, "392667600.dat");
        await File.WriteAllBytesAsync(dat, RandomNumberGenerator.GetBytes(4096), Ct);
        File.SetLastWriteTimeUtc(dat, system.Boot.BootUtc!.Value.AddDays(-3).UtcDateTime);
        var port = new ReparseSafeFilePort(folders, Path.Combine(_dir, "data"), adminOnly: false, [.. SecureDirectory.TrustedOwners, Me]);
        var context = new FixSessionContext("01JB6R7X3K9M2N4P5Q6R7S8TB0", "t") { Player = FakePlayer.Default, IsElevated = true };
        var registry = new OperationRegistry(system.Ports with { Files = port }, context, clock, FakeFactProbe.ReadyToFix());
        var op = registry.FindBase(OpIds.FileQuarantine)!;
        var args = PrimitiveTests.Args(@"path=%SystemRoot%\392667600.dat");
        Assert.True((await op.CheckPolicyAsync(args, Ct)).Allowed);
        var before = await op.CaptureAsync(args, Ct);
        var applied = await op.ApplyAsync(args, before, Ct);
        Assert.True(applied.Success, applied.ErrorCode);
        Assert.False(File.Exists(dat));
        var after = await op.ProbeAsync(args, Ct);
        Assert.True(File.Exists(after.Data["quarantinePath"]));
        var undone = await op.UndoAsync(args, before, after, UndoMode.IfUnchanged, Ct);
        Assert.True(undone.Success, undone.ErrorCode);
        Assert.Equal(before.Hash, (await op.ProbeAsync(args, Ct)).Hash);
        Assert.True(File.Exists(dat));

        // Rewritten this boot: refused by the staleness rule.
        File.SetLastWriteTimeUtc(dat, system.Boot.BootUtc!.Value.AddMinutes(5).UtcDateTime);
        Assert.Equal("protected.file.vg_dat_not_stale", (await op.CheckPolicyAsync(args, Ct)).RuleId);
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
            {
                Directory.Delete(d);
            }

            Directory.Delete(_dir, recursive: true);
            var parent = Path.GetDirectoryName(_dir)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private ReparseSafeFilePort Port() => new(Folders(), Path.Combine(_dir, "data"), adminOnly: false, [.. SecureDirectory.TrustedOwners, Me]);

    private KnownFolders Folders() => new()
    {
        Windows = Path.Combine(_dir, "Windows"),
        ProgramFiles = Path.Combine(_dir, "Program Files"),
        ProgramFilesX86 = Path.Combine(_dir, "Program Files (x86)"),
        ProgramData = Path.Combine(_dir, "ProgramData"),
    };

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string LongPath(string path)
    {
        var buffer = new StringBuilder(32768);
        var n = GetLongPathName(path, buffer, buffer.Capacity);
        return n > 0 ? buffer.ToString() : path;
    }

    private static bool Junction(string link, string target) => Cmd("mklink", "/J", link, target) && Directory.Exists(link);

    private static bool HardLink(string link, string target) => Cmd("mklink", "/H", link, target) && File.Exists(link);

    private static bool Cmd(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        return p.ExitCode == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
    private static extern int GetLongPathName(string shortPath, StringBuilder longPath, int size);
}
