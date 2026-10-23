using System.IO;
using System.ComponentModel;
using System.Diagnostics;

namespace VTD.App.Services;

/// <summary>A started engine process.</summary>
/// <param name="ProcessId">PID (the pipe server must have exactly this PID).</param>
/// <param name="Elevated">Started through the UAC prompt.</param>
/// <param name="HasExited">Reports whether the process has exited (it refused to run, or crashed).</param>
public sealed record LaunchedEngine(int ProcessId, bool Elevated, Func<bool> HasExited);

/// <summary>
/// Starts <c>VTDiag.Engine.exe</c> (audited RS0030 exception, <c>banned/rs0030-allowlist.txt</c>): ShellExecute "runas"
/// for the elevated engine (the player accepts the UAC prompt; the engine itself refuses to run elevated outside Program
/// Files), or an ordinary child with <c>--limited</c> for the read-only scan. The path is always the engine next to this
/// UI (no PATH lookup, no user-supplied value) and the arguments are fixed: <c>--pipe &lt;name&gt;</c> and optionally
/// <c>--limited</c>. No shell, no command line from outside.
/// </summary>
public static class EngineLauncher
{
    /// <summary>The engine image next to this UI.</summary>
    public const string EngineFileName = "VTDiag.Engine.exe";

    /// <summary>Path of the engine next to this UI.</summary>
    public static string EnginePath => Path.Combine(AppContext.BaseDirectory, EngineFileName);

    /// <summary>
    /// True when this UI runs from under Program Files (installed). The elevated engine refuses to run anywhere else,
    /// so a portable copy only offers the read-only scan.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles))
            {
                return false;
            }

            var root = Path.TrimEndingDirectorySeparator(programFiles) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(AppContext.BaseDirectory).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>True when the engine image exists next to the UI.</summary>
    public static bool EngineExists => File.Exists(EnginePath);

    /// <summary>Starts the engine; null when the player declined the UAC prompt or it cannot be started.</summary>
    /// <param name="elevated">Ask for administrator rights (UAC).</param>
    /// <param name="pipeName">Pipe name the UI chose (validated by the engine).</param>
    public static LaunchedEngine? Start(bool elevated, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var path = EnginePath;
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipeName);
        if (elevated)
        {
            info.UseShellExecute = true;
            info.Verb = "runas";
        }
        else
        {
            info.ArgumentList.Add("--limited");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
        }

        try
        {
#pragma warning disable RS0030 // Audited: fixed engine image, fixed arguments (allowlisted file).
            var process = Process.Start(info);
#pragma warning restore RS0030
            return process is null ? null : new LaunchedEngine(process.Id, elevated, () => process.HasExited);
        }
        catch (Win32Exception)
        {
            // UAC declined (1223) or the image cannot be started.
            return null;
        }
    }
}

/// <summary>Copies a portable VTDiag to Program Files and reopens it from there (the first Fix press of an unpacked zip).</summary>
public interface IPortableInstaller
{
    /// <summary>Runs <c>VTDiag.Engine.exe --install</c> with one UAC prompt; its exit code, or null when the prompt was declined.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<int?> InstallAsync(CancellationToken cancellationToken);

    /// <summary>Starts the installed UI (not elevated) and closes this one; false when it could not be started.</summary>
    /// <param name="language">UI language for the reopened app.</param>
    bool ReopenInstalled(string language);
}

/// <summary>
/// The real <see cref="IPortableInstaller"/> (same audited RS0030 exception as <see cref="EngineLauncher"/>): fixed
/// images (the engine next to this UI, then <c>%ProgramFiles%\VTDiag\VTDiag.exe</c>) and fixed arguments.
/// </summary>
public sealed class PortableInstaller : IPortableInstaller
{
    /// <summary>The installed UI.</summary>
    public static string InstalledAppPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VTDiag", "VTDiag.exe");

    /// <inheritdoc />
    public async Task<int?> InstallAsync(CancellationToken cancellationToken)
    {
        if (!EngineLauncher.EngineExists)
        {
            return null;
        }

        var info = new ProcessStartInfo { FileName = EngineLauncher.EnginePath, WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add("--install");
        Process? process;
        try
        {
#pragma warning disable RS0030 // Audited: fixed engine image, fixed argument (allowlisted file).
            process = Process.Start(info);
#pragma warning restore RS0030
        }
        catch (Win32Exception)
        {
            return null; // UAC declined (1223)
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
            return process.ExitCode;
        }
    }

    /// <inheritdoc />
    public bool ReopenInstalled(string language)
    {
        var path = InstalledAppPath;
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new ProcessStartInfo { FileName = path, WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false };
        info.ArgumentList.Add("--lang");
        info.ArgumentList.Add(language);
        try
        {
#pragma warning disable RS0030 // Audited: fixed installed image, fixed arguments (allowlisted file).
            using var process = Process.Start(info);
#pragma warning restore RS0030
        }
        catch (Win32Exception)
        {
            return false;
        }

        System.Windows.Application.Current?.Shutdown();
        return true;
    }
}
