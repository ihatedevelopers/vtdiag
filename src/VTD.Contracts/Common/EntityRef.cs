namespace VTD.Contracts;

/// <summary>Kind of a concrete thing on the PC that a signal points at and a fix binds to.</summary>
public enum EntityKind
{
    /// <summary>Network adapter; id = PnP instance id.</summary>
    Adapter = 0,

    /// <summary>Windows service; id = service name (invariant, not the display name).</summary>
    Service = 1,

    /// <summary>Running process; id = image name (for example <c>NahimicSvc64.exe</c>). Never a handle.</summary>
    Process = 2,

    /// <summary>Kernel driver; id = driver file name (for example <c>rt25cx21x64.sys</c>).</summary>
    Driver = 3,

    /// <summary>Installed application; id = uninstall key name (hive-qualified, for example <c>HKLM:{GUID}</c>).</summary>
    App = 4,

    /// <summary>File; id = full path.</summary>
    File = 5,

    /// <summary>Registry value; id = <c>HKLM\Path\To\Key|ValueName</c>.</summary>
    RegistryValue = 6,

    /// <summary>PnP device other than a network adapter; id = PnP instance id.</summary>
    Device = 7,

    /// <summary>Startup entry; id = <c>&lt;location&gt;|&lt;name&gt;</c>.</summary>
    StartupItem = 8,

    /// <summary>AppCompat layer entry for an executable; id = executable path.</summary>
    CompatLayer = 9,

    /// <summary>Audio endpoint; id = MMDevice endpoint id.</summary>
    AudioEndpoint = 10,

    /// <summary>Power setting; id = <c>&lt;subgroup guid&gt;/&lt;setting guid&gt;</c>.</summary>
    PowerSetting = 11,

    /// <summary>Windows optional feature; id = feature name (for example <c>VirtualMachinePlatform</c>).</summary>
    WindowsFeature = 12,
}

/// <summary>A reference to a concrete entity. Signals return these; fixes bind to them; plans name them by <see cref="Id"/>.</summary>
public sealed record EntityRef
{
    /// <summary>What kind of entity this is.</summary>
    public required EntityKind Kind { get; init; }

    /// <summary>Stable, locale-independent id (see <see cref="EntityKind"/> for the format per kind).</summary>
    [Sensitivity(Sensitivity.EntityId)]
    public required string Id { get; init; }

    /// <summary>Human label for display only (may be localized by Windows, may embed a user or device name). Never match
    /// on it; scrubbed as free text on export.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? DisplayName { get; init; }
}
