using System.Reflection;
using System.Runtime.InteropServices;
using VTD.Platform.Sources;

namespace VTD.Platform.Native;

/// <summary>Read-only COM queries: WUA installer busy flag and the default audio endpoints.</summary>
public sealed partial class SystemInfoSource
{
    /// <inheritdoc />
    public WuaRaw GetWindowsUpdate()
    {
        object? installer = null;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Installer", throwOnError: false);
            if (type is null)
            {
                return new WuaRaw { Status = ReadStatus.Unsupported };
            }

            installer = Activator.CreateInstance(type);
            if (installer is null)
            {
                return new WuaRaw { Status = ReadStatus.Failed };
            }

            var busy = type.InvokeMember("IsBusy", BindingFlags.GetProperty, null, installer, null, System.Globalization.CultureInfo.InvariantCulture);
            return new WuaRaw { Status = ReadStatus.Ok, IsBusy = busy as bool? };
        }
        catch (Exception e) when (e is COMException or TargetInvocationException or MissingMethodException or InvalidCastException)
        {
            return new WuaRaw { Status = ReadStatus.Failed };
        }
        finally
        {
            if (installer is not null && Marshal.IsComObject(installer))
            {
                Marshal.FinalReleaseComObject(installer);
            }
        }
    }

    /// <inheritdoc />
    public AudioDefaultsRaw GetAudioDefaults()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: false);
            enumerator = type is null ? null : Activator.CreateInstance(type) as IMMDeviceEnumerator;
            if (enumerator is null)
            {
                return new AudioDefaultsRaw { Status = ReadStatus.Unsupported };
            }

            return new AudioDefaultsRaw
            {
                Status = ReadStatus.Ok,
                RenderConsole = DefaultId(enumerator, 0, 0),
                RenderCommunications = DefaultId(enumerator, 0, 2),
                CaptureConsole = DefaultId(enumerator, 1, 0),
                CaptureCommunications = DefaultId(enumerator, 1, 2),
            };
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return new AudioDefaultsRaw { Status = ReadStatus.Failed };
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.FinalReleaseComObject(enumerator);
            }
        }
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, int flow, int role)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device is null)
        {
            return null;
        }

        try
        {
            return device.GetId(out var id) == 0 ? id : null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(device);
        }
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
}
