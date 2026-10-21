namespace VTD.Contracts.Snapshots;

/// <summary>Power state (check ids: power_plan, usb_ss).</summary>
public sealed record PowerState
{
    /// <summary>Active power scheme.</summary>
    public required Fact<PowerScheme> ActiveScheme { get; init; }

    /// <summary>Windows 11 power mode overlay.</summary>
    public required Fact<PowerMode> Mode { get; init; }

    /// <summary>USB selective suspend on AC.</summary>
    public required Fact<bool> UsbSelectiveSuspendAc { get; init; }

    /// <summary>USB selective suspend on battery.</summary>
    public required Fact<bool> UsbSelectiveSuspendDc { get; init; }

    /// <summary>Minimum processor state on AC (%).</summary>
    public required Fact<int> ProcessorMinPercentAc { get; init; }

    /// <summary>Maximum processor state on AC (%).</summary>
    public required Fact<int> ProcessorMaxPercentAc { get; init; }

    /// <summary>Battery and AC line state.</summary>
    public required Fact<BatteryInfo> Battery { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static PowerState Unknown(UnknownReason reason) => new()
    {
        ActiveScheme = Fact<PowerScheme>.Unknown(reason),
        Mode = Fact<PowerMode>.Unknown(reason),
        UsbSelectiveSuspendAc = Fact<bool>.Unknown(reason),
        UsbSelectiveSuspendDc = Fact<bool>.Unknown(reason),
        ProcessorMinPercentAc = Fact<int>.Unknown(reason),
        ProcessorMaxPercentAc = Fact<int>.Unknown(reason),
        Battery = Fact<BatteryInfo>.Unknown(reason),
    };
}

/// <summary>Well-known power scheme.</summary>
public enum PowerSchemeKind
{
    /// <summary>OEM or user-created scheme.</summary>
    Custom = 0,

    /// <summary>Balanced (381b4222-f694-41f0-9685-ff5bb260df2e).</summary>
    Balanced = 1,

    /// <summary>High performance (8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c).</summary>
    HighPerformance = 2,

    /// <summary>Ultimate performance (e9a42b02-d5df-448d-aa00-03f14749eb61).</summary>
    UltimatePerformance = 3,

    /// <summary>Power saver (a1841308-3541-4fab-bc81-f71556f20b4a).</summary>
    PowerSaver = 4,
}

/// <summary>Active power scheme. Match on <see cref="Guid"/>; the name is localized ("Dengeli").</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PowerScheme
{
    /// <summary>Scheme GUID, lowercase.</summary>
    public required string Guid { get; init; }

    /// <summary>Well-known kind.</summary>
    public required PowerSchemeKind Kind { get; init; }

    /// <summary>Localized name (display only).</summary>
    public string? Name { get; init; }
}

/// <summary>Windows 11 power mode overlay.</summary>
public enum PowerMode
{
    /// <summary>Best power efficiency (overlay <c>961cc777-...</c>).</summary>
    BestPowerEfficiency = 0,

    /// <summary>Balanced / recommended (no overlay, <c>00000000-...</c>).</summary>
    Balanced = 1,

    /// <summary>Better performance (overlay <c>3af9b8d9-...</c>: the Windows 10 slider and some OEM images).</summary>
    BetterPerformance = 2,

    /// <summary>Best performance (overlay <c>ded574b5-...</c>).</summary>
    BestPerformance = 3,
}

/// <summary>Battery and AC state.</summary>
public sealed record BatteryInfo
{
    /// <summary>A system battery is present (false on desktops).</summary>
    public required bool Present { get; init; }

    /// <summary>On AC power.</summary>
    public required bool OnAc { get; init; }

    /// <summary>Charge (%).</summary>
    public int? ChargePercent { get; init; }
}

/// <summary>Display state (check ids: hags_windowed, fso_mpo, display_mode, gpu_pref).</summary>
public sealed record DisplayState
{
    /// <summary>Connected monitors.</summary>
    public required Fact<IReadOnlyList<MonitorInfo>> Monitors { get; init; }

    /// <summary>Hardware-accelerated GPU scheduling (<c>HwSchMode = 2</c>).</summary>
    public required Fact<bool> HagsEnabled { get; init; }

    /// <summary>MPO disabled (<c>Dwm\OverlayTestMode = 5</c>).</summary>
    public required Fact<bool> MpoDisabled { get; init; }

    /// <summary>Game Mode on.</summary>
    public required Fact<bool> GameModeEnabled { get; init; }

    /// <summary>Game DVR / background capture on.</summary>
    public required Fact<bool> GameDvrEnabled { get; init; }

    /// <summary>"Optimizations for windowed games" (<c>SwapEffectUpgradeEnable</c>).</summary>
    public required Fact<bool> WindowedOptimizations { get; init; }

    /// <summary>Per-app GPU preference for Riot executables.</summary>
    public required Fact<IReadOnlyList<GpuPreference>> GpuPreferences { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static DisplayState Unknown(UnknownReason reason) => new()
    {
        Monitors = Fact<IReadOnlyList<MonitorInfo>>.Unknown(reason),
        HagsEnabled = Fact<bool>.Unknown(reason),
        MpoDisabled = Fact<bool>.Unknown(reason),
        GameModeEnabled = Fact<bool>.Unknown(reason),
        GameDvrEnabled = Fact<bool>.Unknown(reason),
        WindowedOptimizations = Fact<bool>.Unknown(reason),
        GpuPreferences = Fact<IReadOnlyList<GpuPreference>>.Unknown(reason),
    };
}

/// <summary>One monitor (QueryDisplayConfig).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record MonitorInfo
{
    /// <summary>EDID friendly name.</summary>
    public string? Name { get; init; }

    /// <summary>Primary display.</summary>
    public required bool Primary { get; init; }

    /// <summary>Current width (px).</summary>
    public required int Width { get; init; }

    /// <summary>Current height (px).</summary>
    public required int Height { get; init; }

    /// <summary>Refresh rate (Hz, rounded).</summary>
    public required int RefreshHz { get; init; }

    /// <summary>HDR on.</summary>
    public bool? HdrEnabled { get; init; }

    /// <summary>Internal laptop panel.</summary>
    public bool? Internal { get; init; }

    /// <summary>The GPU driving this monitor.</summary>
    [Sensitivity(Sensitivity.DeviceId)]
    public string? GpuInstanceId { get; init; }
}

/// <summary>Per-app GPU preference (<c>UserGpuPreferences</c>).</summary>
[Sensitivity(Sensitivity.None)]
public sealed record GpuPreference
{
    /// <summary>Executable image name.</summary>
    public required string ImageName { get; init; }

    /// <summary>0 = let Windows decide, 1 = power saving, 2 = high performance.</summary>
    public required int Preference { get; init; }
}

/// <summary>Audio state (check ids: bt_hfp, audio_devices, nahimic).</summary>
public sealed record AudioState
{
    /// <summary>Audio endpoints (MMDevice).</summary>
    public required Fact<IReadOnlyList<AudioEndpoint>> Endpoints { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static AudioState Unknown(UnknownReason reason) => new()
    {
        Endpoints = Fact<IReadOnlyList<AudioEndpoint>>.Unknown(reason),
    };
}

/// <summary>Audio data flow.</summary>
public enum AudioFlow
{
    /// <summary>Playback.</summary>
    Render = 0,

    /// <summary>Recording.</summary>
    Capture = 1,
}

/// <summary>Endpoint form factor (PKEY_AudioEndpoint_FormFactor).</summary>
public enum AudioFormFactor
{
    /// <summary>Unknown.</summary>
    Unknown = 0,

    /// <summary>Speakers.</summary>
    Speakers = 1,

    /// <summary>Line level.</summary>
    LineLevel = 2,

    /// <summary>Headphones.</summary>
    Headphones = 3,

    /// <summary>Microphone.</summary>
    Microphone = 4,

    /// <summary>Headset.</summary>
    Headset = 5,

    /// <summary>Handset.</summary>
    Handset = 6,

    /// <summary>Digital passthrough / SPDIF / HDMI.</summary>
    Digital = 7,

    /// <summary>Remote network device.</summary>
    Remote = 8,
}

/// <summary>Endpoint state.</summary>
public enum AudioEndpointState
{
    /// <summary>Active.</summary>
    Active = 0,

    /// <summary>Disabled.</summary>
    Disabled = 1,

    /// <summary>Not present.</summary>
    NotPresent = 2,

    /// <summary>Unplugged.</summary>
    Unplugged = 3,
}

/// <summary>An audio endpoint. Bluetooth Hands-Free is detected from the HFP service, not from the localized name.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record AudioEndpoint
{
    /// <summary>MMDevice endpoint id.</summary>
    [Sensitivity(Sensitivity.Hashed)]
    public required string Id { get; init; }

    /// <summary>Friendly name (hashed at share level).</summary>
    [Sensitivity(Sensitivity.Hashed)]
    public string? Name { get; init; }

    /// <summary>Render or capture.</summary>
    public required AudioFlow Flow { get; init; }

    /// <summary>State.</summary>
    public required AudioEndpointState State { get; init; }

    /// <summary>Form factor.</summary>
    public AudioFormFactor FormFactor { get; init; }

    /// <summary>Default device (console role). Null = the default endpoint could not be read.</summary>
    public bool? IsDefault { get; init; }

    /// <summary>Default communications device. Null = the default endpoint could not be read.</summary>
    public bool? IsDefaultCommunications { get; init; }

    /// <summary>Bluetooth Hands-Free (HFP) endpoint: drops game audio to mono 8-16 kHz when the mic opens.</summary>
    public bool IsBluetoothHandsFree { get; init; }

    /// <summary>Audio processing object CLSIDs on the endpoint (Nahimic and Sonic Studio APOs show up here).</summary>
    public IReadOnlyList<string> ApoClsids { get; init; } = [];

    /// <summary>PnP instance id of the owning device.</summary>
    [Sensitivity(Sensitivity.DeviceId)]
    public string? DeviceInstanceId { get; init; }
}

/// <summary>Input state (check id: mouse_settings). USB devices are in <see cref="ScanSnapshot.Devices"/>.</summary>
public sealed record InputState
{
    /// <summary>Mouse settings.</summary>
    public required Fact<MouseSettings> Mouse { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static InputState Unknown(UnknownReason reason) => new()
    {
        Mouse = Fact<MouseSettings>.Unknown(reason),
    };
}

/// <summary>Mouse settings (SystemParametersInfo).</summary>
public sealed record MouseSettings
{
    /// <summary>"Enhance pointer precision" (acceleration).</summary>
    public required bool EnhancePointerPrecision { get; init; }

    /// <summary>Pointer speed 1-20 (10 = default).</summary>
    public required int Speed { get; init; }
}
