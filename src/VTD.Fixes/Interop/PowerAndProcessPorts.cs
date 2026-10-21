using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VTD.Fixes.Ports;
using static VTD.Fixes.Interop.NativeMethods;

namespace VTD.Fixes.Interop;

/// <summary>Power schemes through powrprof (no powercfg.exe).</summary>
public sealed class PowrProfPowerPort : IPowerPort
{
    private const uint AccessScheme = 16;

    /// <inheritdoc />
    public Guid GetActiveScheme()
    {
        var rc = PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc);
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> EnumerateSchemes()
    {
        var list = new List<Guid>();
        for (uint i = 0; i < 64; i++)
        {
            var size = 16u;
            var buffer = new byte[16];
            var rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref size);
            if (rc == 259)
            {
                break; // ERROR_NO_MORE_ITEMS
            }

            if (rc != 0)
            {
                throw new Win32Exception((int)rc);
            }

            list.Add(new Guid(buffer));
        }

        return list;
    }

    /// <inheritdoc />
    public uint? ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac)
    {
        var rc = ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);
        if (rc == ErrorFileNotFound)
        {
            return null;
        }

        return rc == 0 ? value : throw new Win32Exception((int)rc);
    }

    /// <inheritdoc />
    public void WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value)
    {
        var rc = ac
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc);
        }
    }

    /// <inheritdoc />
    public void SetActiveScheme(Guid scheme)
    {
        var rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != 0)
        {
            throw new Win32Exception((int)rc);
        }
    }
}

/// <summary>
/// Process listing from a ToolHelp snapshot (names, PIDs, session ids — no process handle is ever opened) and graceful
/// close by posting WM_CLOSE to the process's top-level windows (what <c>taskkill</c> without <c>/f</c> does).
/// </summary>
public sealed class ToolHelpProcessPort : IProcessPort
{
    /// <inheritdoc />
    public IReadOnlyList<ProcessEntry> Snapshot()
    {
        using var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var list = new List<ProcessEntry>();
        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32First(snap, ref entry))
        {
            return list;
        }

        do
        {
            int? session = ProcessIdToSessionId(entry.ProcessId, out var s) ? (int)s : null;
            list.Add(new ProcessEntry((int)entry.ProcessId, entry.ExeFile, session));
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32Next(snap, ref entry));

        return list;
    }

    /// <inheritdoc />
    public int ActiveConsoleSessionId() => (int)WTSGetActiveConsoleSessionId();

    /// <inheritdoc />
    public int PostClose(int processId)
    {
        if (processId <= 4)
        {
            return 0;
        }

        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == (uint)processId)
            {
                windows.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        var posted = 0;
        foreach (var hwnd in windows)
        {
            if (PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
            {
                posted++;
            }
        }

        return posted;
    }
}

/// <summary>
/// Runs the fixed Windows tools of <see cref="SystemTool"/>: an absolute System32 path and a fixed argument list, no shell,
/// no window, output discarded, bounded time (only a process we started is ever stopped, on timeout).
/// </summary>
public sealed class SystemToolRunner : IToolRunner
{
    private readonly TimeProvider _clock;

    /// <summary>Creates the runner.</summary>
    /// <param name="clock">Clock.</param>
    public SystemToolRunner(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The executable and arguments of a tool.</summary>
    /// <param name="tool">Tool.</param>
    public static (string Exe, IReadOnlyList<string> Args) CommandOf(SystemTool tool) => tool switch
    {
        SystemTool.FlushDns => ("ipconfig.exe", ["/flushdns"]),
        SystemTool.WinsockReset => ("netsh.exe", ["winsock", "reset"]),
        SystemTool.IpReset => ("netsh.exe", ["int", "ip", "reset"]),
        SystemTool.TimeResync => ("w32tm.exe", ["/resync", "/force"]),
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    /// <inheritdoc />
    public async Task<int> RunAsync(SystemTool tool, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (exe, args) = CommandOf(tool);
        var system = Environment.SystemDirectory;
        var path = Path.Combine(system, exe);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("System tool not found.", path);
        }

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = system,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new IOException("The system tool did not start.");
        var drainOut = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var drainErr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCts = new CancellationTokenSource(timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: false); // our own child only
            throw new TimeoutException("The system tool did not finish in time.");
        }

        await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>Boot time of the running kernel: now minus the tick count (which includes time asleep).</summary>
public sealed class TickBootClock(TimeProvider clock) : IBootClock
{
    /// <inheritdoc />
    public DateTimeOffset? LastBootUtc() => clock.GetUtcNow() - TimeSpan.FromMilliseconds(GetTickCount64());
}
