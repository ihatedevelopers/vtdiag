using System.Diagnostics;

namespace VTD.Engine;

/// <summary>
/// <c>VTDiag.Engine.exe --install</c>: a portable copy starts it with one UAC prompt when the player first presses Fix.
/// It copies the release to <c>%ProgramFiles%\VTDiag</c>, because the elevated engine runs only from Program Files (an
/// unpacked folder in Downloads is writable by any program, so it must never be the one that runs elevated for long).
/// It copies exactly the files listed in <see cref="ManifestName"/> (written at publish time), never "whatever sits next
/// to the exe", so a zip unpacked flat into Downloads cannot drag the player's files along. No pipe, no session, no
/// registry, no fixes. The copy is staged next to the target and swapped in at the end; the previous copy comes back
/// when anything fails. A newer installed copy is kept as is (the UI then simply reopens it).
/// </summary>
public static class SelfInstall
{
    /// <summary>Folder under Program Files.</summary>
    public const string FolderName = "VTDiag";

    /// <summary>The publish-time list of release files (relative paths, one per line).</summary>
    public const string ManifestName = "install.manifest";

    /// <summary>Exit code: an installed VTDiag (engine or app) is running, so its files are in use.</summary>
    public const int ExitBusy = 4;

    /// <summary>Exit code: copying failed; the installed copy (if any) is unchanged.</summary>
    public const int ExitFailed = 5;

    private const string EngineImage = "VTDiag.Engine.exe";

    private static readonly string[] Required = ["VTDiag.exe", EngineImage, @"kb\kb.manifest.json"];

    /// <summary><c>%ProgramFiles%\VTDiag</c>.</summary>
    public static string TargetDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), FolderName);

    /// <summary>Copies this release to <see cref="TargetDirectory"/>.</summary>
    /// <param name="elevated">This process is elevated.</param>
    /// <param name="log">Log (stderr).</param>
    public static async Task<int> RunAsync(bool elevated, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!elevated)
        {
            await log.WriteLineAsync("install.needs_admin").ConfigureAwait(false);
            return EngineHost.ExitRefused;
        }

        if (EngineHost.IsUnderProgramFiles(Environment.ProcessPath))
        {
            await log.WriteLineAsync("install.already_installed").ConfigureAwait(false);
            return 0;
        }

        int code;
        string message;
        using (var instance = SingleInstance.TryAcquire(@"Global\VTDiag.Engine"))
        {
            (code, message) = instance is null ? (ExitBusy, "install.busy: the installed engine is running") : Install();
        }

        await log.WriteLineAsync(message).ConfigureAwait(false);
        return code;
    }

    private static (int Code, string Message) Install()
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var target = TargetDirectory;
        var staging = target + ".new";
        var old = target + ".old";
        IReadOnlyList<string> files;
        try
        {
            files = ReadManifest(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (ExitFailed, "install.bad_release: " + ex.Message);
        }

        if (Directory.Exists(target))
        {
            if (!File.Exists(Path.Combine(target, EngineImage)))
            {
                return (ExitFailed, "install.foreign_folder: " + target + " exists and is not VTDiag");
            }

            if (VersionOf(Path.Combine(target, EngineImage)) > VersionOf(Path.Combine(source, EngineImage)))
            {
                return (0, "install.newer_installed: kept");
            }
        }

        var swapped = false;
        try
        {
            DeleteIfExists(staging);
            foreach (var rel in files)
            {
                var from = new FileInfo(Path.Combine(source, rel));
                if (!from.Exists || from.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("install.missing_or_link: " + rel);
                }

                var to = Path.Combine(staging, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                from.CopyTo(to, overwrite: false);
            }

            if (!EngineHost.IsUnderProgramFiles(Path.Combine(staging, EngineImage)))
            {
                throw new IOException("install.target_not_in_program_files");
            }

            if (Directory.Exists(target))
            {
                DeleteIfExists(old);
                try
                {
                    Directory.Move(target, old);
                }
                catch (IOException ex)
                {
                    DeleteQuietly(staging);
                    return (ExitBusy, "install.busy: " + ex.Message);
                }

                swapped = true;
            }

            Directory.Move(staging, target);
            DeleteQuietly(old);
            return (0, "install.done: " + target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (swapped && !Directory.Exists(target))
            {
                try
                {
                    Directory.Move(old, target); // put the previous copy back
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    return (ExitFailed, "install.failed: " + ex.Message + "; restore failed: " + back.Message + " (previous copy is in " + old + ")");
                }
            }

            DeleteQuietly(staging);
            return (ExitFailed, "install.failed: " + ex.Message);
        }
    }

    private static List<string> ReadManifest(string source)
    {
        var path = Path.Combine(source, ManifestName);
        var files = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var rel = line.Trim().Replace('/', '\\');
            if (rel.Length == 0)
            {
                continue;
            }

            if (Path.IsPathRooted(rel) || rel.Contains(':', StringComparison.Ordinal) || rel.Split('\\').Any(p => p is "" or "." or ".."))
            {
                throw new InvalidDataException("install.manifest_path: " + rel);
            }

            files.Add(rel);
        }

        foreach (var required in Required)
        {
            if (!files.Contains(required, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("install.manifest_missing: " + required);
            }
        }

        return files;
    }

    private static Version VersionOf(string exe)
        => Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var v) ? v : new Version(0, 0);

    // Only ever called on VTDiag.new / VTDiag.old under Program Files, which only administrators can create.
    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            DeleteIfExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover VTDiag.new / VTDiag.old is harmless; the next install removes it.
        }
    }
}
