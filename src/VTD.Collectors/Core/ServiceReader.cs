using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Core;

/// <summary>Builds <see cref="ServiceInfo"/> from the SCM list plus the service registry key (start type, image path).</summary>
public static class ServiceReader
{
    /// <summary>Builds one service record; null when the service is neither in the SCM list nor in the registry.</summary>
    /// <param name="env">Environment.</param>
    /// <param name="name">Service name.</param>
    public static ServiceInfo? Read(ScanEnvironment env, string name)
    {
        ArgumentNullException.ThrowIfNull(env);
        var scm = env.Services.Status == ReadStatus.Ok
            ? env.Services.Items.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;
        var key = env.Hklm(@"SYSTEM\CurrentControlSet\Services\" + name);
        if (scm is null && !key.Ok())
        {
            return null;
        }

        return Build(env, scm ?? new ServiceRec { Name = name, Type = (int)(key.Num("Type") ?? 0x10), State = 1 }, key);
    }

    /// <summary>Builds a record from an SCM entry and its registry key.</summary>
    /// <param name="env">Environment.</param>
    /// <param name="scm">SCM entry.</param>
    /// <param name="key">Registry key (Status NotFound allowed).</param>
    public static ServiceInfo Build(ScanEnvironment env, ServiceRec scm, RegKeyData key)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(scm);
        ArgumentNullException.ThrowIfNull(key);
        var type = scm.Type;
        var kind = (type & 0x1) != 0 ? ServiceKind.KernelDriver
            : (type & 0x2) != 0 ? ServiceKind.FileSystemDriver
            : (type & 0xC0) != 0 ? ServiceKind.UserService
            : ServiceKind.Win32;
        var start = key.Num("Start") switch
        {
            0 => ServiceStartType.Boot,
            1 => ServiceStartType.System,
            2 => ServiceStartType.Automatic,
            4 => ServiceStartType.Disabled,
            _ => ServiceStartType.Manual,
        };
        var state = scm.State is >= 1 and <= 7 ? (ServiceState)(scm.State - 1) : ServiceState.Stopped;
        return new ServiceInfo
        {
            Name = scm.Name,
            DisplayName = string.IsNullOrEmpty(scm.DisplayName) ? null : scm.DisplayName,
            Kind = kind,
            StartType = start,
            DelayedAutoStart = start == ServiceStartType.Automatic && key.Num("DelayedAutostart") == 1,
            State = state,
            ImagePath = PathText.NormalizeImagePath(key.Str("ImagePath"), env.WindowsDir, env.Env.ProgramFiles),
        };
    }
}
