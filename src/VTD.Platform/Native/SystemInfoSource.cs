using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using VTD.Platform.Sources;

namespace VTD.Platform.Native;

/// <summary>
/// Live native reads. Process data comes from a ToolHelp snapshot (names/PIDs only); nothing here opens a handle to any
/// process or thread, reads another process's memory, or touches Riot/Vanguard processes beyond their name.
/// </summary>
public sealed partial class SystemInfoSource : ISystemInfoSource
{
    private const uint Th32csSnapProcess = 0x2;

    /// <inheritdoc />
    public EnvRaw GetEnvironment()
    {
        bool elevated;
        string? sid;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            sid = identity.User?.Value;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            elevated = false;
            sid = null;
        }

        return new EnvRaw
        {
            TimeZoneId = TimeZoneInfo.Local.Id,
            UiCulture = CultureInfo.CurrentUICulture.Name,
            TickCount64 = Environment.TickCount64,
            WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Elevated = elevated,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            UserSid = sid,
        };
    }

    /// <inheritdoc />
    public ProcessList GetProcesses()
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            return new ProcessList { Status = ReadStatus.Failed };
        }

        var items = new List<ProcessRec>();
        var entry = new NativeMethods.ProcessEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
        if (!NativeMethods.Process32First(snapshot, ref entry))
        {
            return new ProcessList { Status = ReadStatus.Failed };
        }

        do
        {
            items.Add(new ProcessRec
            {
                Name = entry.ExeFile ?? string.Empty,
                Pid = unchecked((int)entry.ProcessId),
                ParentPid = unchecked((int)entry.ParentProcessId),
                Threads = unchecked((int)entry.Threads),
            });
            entry.Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>();
        }
        while (NativeMethods.Process32Next(snapshot, ref entry) && items.Count < 10_000);

        return new ProcessList { Status = ReadStatus.Ok, Items = items };
    }

    /// <inheritdoc />
    public ServiceList GetServices()
    {
        const uint ScManagerEnumerateService = 0x0004;
        var scm = NativeMethods.OpenSCManager(null, null, ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            return new ServiceList { Status = Marshal.GetLastWin32Error() == NativeMethods.ErrorAccessDenied ? ReadStatus.AccessDenied : ReadStatus.Failed };
        }

        try
        {
            var items = Enumerate(scm, 0x3FF);
            if (items is null)
            {
                items = Enumerate(scm, 0x3B);
            }

            return items is null ? new ServiceList { Status = ReadStatus.Failed } : new ServiceList { Status = ReadStatus.Ok, Items = items };
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    private static List<ServiceRec>? Enumerate(IntPtr scm, uint serviceType)
    {
        const int ScEnumProcessInfo = 0;
        const uint ServiceStateAll = 3;
        const int EntrySize = 56;
        var items = new List<ServiceRec>();
        uint resume = 0;
        var size = 256u * 1024u;
        for (var round = 0; round < 64; round++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var ok = NativeMethods.EnumServicesStatusEx(scm, ScEnumProcessInfo, serviceType, ServiceStateAll, buffer, size, out var needed, out var returned, ref resume, null);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != NativeMethods.ErrorMoreData)
                {
                    return null;
                }

                for (var i = 0; i < returned; i++)
                {
                    var p = buffer + (i * EntrySize);
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    items.Add(new ServiceRec
                    {
                        Name = name,
                        DisplayName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, 8)),
                        Type = Marshal.ReadInt32(p, 16),
                        State = Marshal.ReadInt32(p, 20),
                    });
                }

                if (ok)
                {
                    return items;
                }

                size = Math.Max(size, needed + 1024);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return items;
    }

    /// <inheritdoc />
    public DriverList GetLoadedDrivers()
    {
        if (!NativeMethods.EnumDeviceDrivers(null, 0, out var needed) || needed == 0)
        {
            return new DriverList { Status = ReadStatus.Failed };
        }

        var bases = new IntPtr[(needed / (uint)IntPtr.Size) + 16];
        if (!NativeMethods.EnumDeviceDrivers(bases, (uint)(bases.Length * IntPtr.Size), out needed))
        {
            return new DriverList { Status = ReadStatus.Failed };
        }

        var count = (int)(needed / (uint)IntPtr.Size);
        var nonZero = bases.Take(count).Count(b => b != IntPtr.Zero);
        if (nonZero < Math.Max(2, count / 2))
        {
            // Windows 11 24H2+ hides kernel addresses from non-elevated callers; names cannot be resolved.
            return new DriverList { Status = ReadStatus.AccessDenied };
        }

        var items = new List<DriverRec>(count);
        var nameBuffer = new char[512];
        var pathBuffer = new char[1024];
        foreach (var b in bases.Take(count))
        {
            if (b == IntPtr.Zero)
            {
                continue;
            }

            var n = NativeMethods.GetDeviceDriverBaseName(b, nameBuffer, (uint)nameBuffer.Length);
            if (n == 0)
            {
                continue;
            }

            var p = NativeMethods.GetDeviceDriverFileName(b, pathBuffer, (uint)pathBuffer.Length);
            items.Add(new DriverRec { Name = new string(nameBuffer, 0, (int)n), Path = p == 0 ? null : new string(pathBuffer, 0, (int)p) });
        }

        return new DriverList { Status = ReadStatus.Ok, Items = items };
    }

    /// <inheritdoc />
    public TpmRaw GetTpm()
    {
        const uint TbsSuccess = 0;
        const uint TbsTpmNotFound = 0x8028400F;
        try
        {
            var result = NativeMethods.Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<NativeMethods.TpmDeviceInfo>(), out var info);
            return result switch
            {
                TbsSuccess => new TpmRaw { Status = ReadStatus.Ok, Version = (int)info.TpmVersion, InterfaceType = (int)info.TpmInterfaceType },
                TbsTpmNotFound => new TpmRaw { Status = ReadStatus.NotFound },
                _ => new TpmRaw { Status = ReadStatus.Failed },
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new TpmRaw { Status = ReadStatus.Unsupported };
        }
    }
}
