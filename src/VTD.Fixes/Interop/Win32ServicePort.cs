using System.ComponentModel;
using System.Runtime.InteropServices;
using VTD.Fixes.Ports;
using static VTD.Fixes.Interop.NativeMethods;

namespace VTD.Fixes.Interop;

/// <summary>SCM access with the narrowest rights per call (query for reads; change-config / start / stop only when needed).</summary>
public sealed class Win32ServicePort : IServicePort
{
    private readonly TimeProvider _clock;

    /// <summary>Creates the port.</summary>
    /// <param name="clock">Clock for wait loops.</param>
    public Win32ServicePort(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public ServiceInfo? Query(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var h = Open(name, ServiceQueryConfig | ServiceQueryStatus, allowMissing: true);
        if (h is null)
        {
            return null;
        }

        var (serviceType, startType, imagePath) = ReadConfig(h.Value);
        var delayed = ReadDword(h.Value, ServiceConfigDelayedAutoStartInfo) != 0;
        var launchProtected = ReadDword(h.Value, ServiceConfigLaunchProtected) != 0;
        var status = ReadStatus(h.Value);
        return new ServiceInfo
        {
            Name = name,
            StartType = (ServiceStart)Math.Clamp((int)startType, 0, 4),
            Delayed = delayed,
            State = (ServiceRun)Math.Clamp((int)status.CurrentState, 1, 7),
            IsDriver = (serviceType & 0x3) != 0, // SERVICE_KERNEL_DRIVER | SERVICE_FILE_SYSTEM_DRIVER
            IsSharedProcess = (serviceType & 0x20) != 0, // SERVICE_WIN32_SHARE_PROCESS
            LaunchProtected = launchProtected,
            ImagePath = imagePath,
        };
    }

    /// <inheritdoc />
    public void SetStartType(string name, ServiceStart startType, bool delayed)
    {
        using var h = Open(name, ServiceChangeConfig | ServiceQueryConfig, allowMissing: false)!;
        if (!ChangeServiceConfig(h.Value, ServiceNoChange, (uint)startType, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // The flag only matters for Automatic, but it is stored either way; restore it exactly when Windows lets us.
        var info = new ServiceDelayedAutoStartInfo { DelayedAutostart = delayed ? 1 : 0 };
        if (!ChangeServiceConfig2(h.Value, ServiceConfigDelayedAutoStartInfo, ref info) && startType == Ports.ServiceStart.Automatic)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using (var h = Open(name, ServiceStop | ServiceQueryStatus, allowMissing: false)!)
        {
            var s = ReadStatus(h.Value);
            if (s.CurrentState != 1 && s.CurrentState != 3 && !ControlService(h.Value, ServiceControlStop, out _))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ErrorServiceNotActive)
                {
                    throw new Win32Exception(err);
                }
            }
        }

        await WaitFor(name, 1, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using (var h = Open(name, NativeMethods.ServiceStart | ServiceQueryStatus, allowMissing: false)!)
        {
            var s = ReadStatus(h.Value);
            if (s.CurrentState != 4 && s.CurrentState != 2 && !StartService(h.Value, 0, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ErrorServiceAlreadyRunning)
                {
                    throw new Win32Exception(err);
                }
            }
        }

        await WaitFor(name, 4, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitFor(string name, uint state, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + timeout;
        while (true)
        {
            using (var h = Open(name, ServiceQueryStatus, allowMissing: false)!)
            {
                if (ReadStatus(h.Value).CurrentState == state)
                {
                    return;
                }
            }

            if (_clock.GetUtcNow() >= deadline)
            {
                throw new TimeoutException("The service did not reach the requested state in time.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ScHandle? Open(string name, uint access, bool allowMissing)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var svc = OpenService(scm, name, access);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ErrorServiceDoesNotExist && allowMissing)
                {
                    return null;
                }

                throw new Win32Exception(err);
            }

            return new ScHandle(svc);
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static (uint ServiceType, uint StartType, string? ImagePath) ReadConfig(IntPtr h)
    {
        QueryServiceConfig(h, IntPtr.Zero, 0, out var needed);
        var buffer = Marshal.AllocHGlobal((int)Math.Max(needed, 256));
        try
        {
            if (!QueryServiceConfig(h, buffer, Math.Max(needed, 256), out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var header = Marshal.PtrToStructure<QueryServiceConfigHeader>(buffer);
            return (header.ServiceType, header.StartType, header.BinaryPathName == IntPtr.Zero ? null : Marshal.PtrToStringUni(header.BinaryPathName));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadDword(IntPtr h, int level)
    {
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            return QueryServiceConfig2(h, level, buffer, 64, out _) ? Marshal.ReadInt32(buffer) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ServiceStatusProcess ReadStatus(IntPtr h)
    {
        var status = default(ServiceStatusProcess);
        if (!QueryServiceStatusEx(h, ScStatusProcessInfo, ref status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return status;
    }

    private sealed class ScHandle(IntPtr value) : IDisposable
    {
        public IntPtr Value { get; } = value;

        public void Dispose() => CloseServiceHandle(Value);
    }
}
