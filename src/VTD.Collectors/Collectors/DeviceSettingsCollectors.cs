using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>Power plan (by GUID, never the localized name), Windows 11 power mode, USB selective suspend, CPU limits, AC/battery.</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class PowerCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <inheritdoc />
    public override string Id => "power";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Hardware;

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var raw = env.Sources.System.GetPower();
        const string scheme = "PowerGetActiveScheme";
        const string ac = "PowerReadACValueIndex";
        const string dc = "PowerReadDCValueIndex";
        Fact<int> Index(int? v, string m) => raw.Status != ReadStatus.Ok ? run.FromStatus<int>(env, raw.Status, m) : v is { } x ? run.Known(x, m) : run.Unknown<int>(UnknownReason.Unsupported, m);
        Fact<bool> Flag(int? v, string m) => raw.Status != ReadStatus.Ok ? run.FromStatus<bool>(env, raw.Status, m) : v is { } x ? run.Known(x != 0, m) : run.Unknown<bool>(UnknownReason.Unsupported, m);
        var state = new PowerState
        {
            ActiveScheme = raw.Status == ReadStatus.Ok && raw.ActiveScheme is { } g
                ? run.Known(new PowerScheme { Guid = g.ToLowerInvariant(), Kind = SchemeKind(g), Name = raw.SchemeName }, scheme)
                : run.FromStatus<PowerScheme>(env, raw.Status == ReadStatus.Ok ? ReadStatus.Unsupported : raw.Status, scheme),
            Mode = raw.Status == ReadStatus.Ok && raw.Overlay is { } o
                ? run.Known(ModeOf(o), "PowerGetEffectiveOverlayScheme")
                : run.Unknown<PowerMode>(UnknownReason.Unsupported, "PowerGetEffectiveOverlayScheme"),
            UsbSelectiveSuspendAc = Flag(raw.UsbSelectiveSuspendAc, ac),
            UsbSelectiveSuspendDc = Flag(raw.UsbSelectiveSuspendDc, dc),
            ProcessorMinPercentAc = Index(raw.ProcessorMinAc, ac),
            ProcessorMaxPercentAc = Index(raw.ProcessorMaxAc, ac),
            Battery = raw.PowerStatusOk
                ? run.Known(new BatteryInfo { Present = raw.BatteryFlag is not 128 and not 255, OnAc = raw.AcLine != 0, ChargePercent = raw.BatteryPercent is >= 0 and <= 100 && raw.BatteryFlag is not 128 ? raw.BatteryPercent : null }, "GetSystemPowerStatus")
                : run.Unknown<BatteryInfo>(UnknownReason.Failed, "GetSystemPowerStatus"),
        };
        return new SnapshotPatch { Power = state };
    }

    internal static PowerSchemeKind SchemeKind(string guid) => guid.ToLowerInvariant() switch
    {
        "381b4222-f694-41f0-9685-ff5bb260df2e" => PowerSchemeKind.Balanced,
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => PowerSchemeKind.HighPerformance,
        "e9a42b02-d5df-448d-aa00-03f14749eb61" => PowerSchemeKind.UltimatePerformance,
        "a1841308-3541-4fab-bc81-f71556f20b4a" => PowerSchemeKind.PowerSaver,
        _ => PowerSchemeKind.Custom,
    };

    internal static PowerMode ModeOf(string overlay) => overlay.ToLowerInvariant() switch
    {
        "961cc777-2547-4f9d-8174-7d86181b8a7a" => PowerMode.BestPowerEfficiency,
        "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => PowerMode.BetterPerformance,
        "ded574b5-45a0-4f42-8737-46345c09c238" => PowerMode.BestPerformance,
        _ => PowerMode.Balanced, // 00000000-0000-0000-0000-000000000000 (balanced / recommended)
    };
}

/// <summary>Monitors (QueryDisplayConfig), HAGS, MPO, Game Mode, Game DVR, windowed optimizations, GPU preferences.</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class DisplayCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <inheritdoc />
    public override string Id => "display";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Hardware;

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var gd = env.Hklm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        var dwm = env.Hklm(@"SOFTWARE\Microsoft\Windows\Dwm");
        var gameBar = env.Hkcu(@"Software\Microsoft\GameBar");
        var gameDvr = env.Hkcu(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
        var dvrPolicy = env.Hklm(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR");
        var prefs = env.Hkcu(@"Software\Microsoft\DirectX\UserGpuPreferences");
        var state = new DisplayState
        {
            Monitors = run.Try("QueryDisplayConfig", () => Monitors(env, run)),
            HagsEnabled = !gd.Ok() ? run.FromStatus<bool>(env, gd.Status, "registry:HwSchMode")
                : gd.Num("HwSchMode") is { } hags ? run.Known(hags == 2, "registry:HwSchMode") : run.Unknown<bool>(UnknownReason.Unsupported, "registry:HwSchMode", "value_absent"),
            MpoDisabled = dwm.Ok() || dwm.Status == ReadStatus.NotFound ? run.Known(dwm.Num("OverlayTestMode") == 5, "registry:OverlayTestMode") : run.FromStatus<bool>(env, dwm.Status, "registry:OverlayTestMode"),
            GameModeEnabled = gameBar.Ok() || gameBar.Status == ReadStatus.NotFound
                ? run.Known((gameBar.Num("AutoGameModeEnabled") ?? gameBar.Num("AllowAutoGameMode") ?? 1) != 0, "registry:AutoGameModeEnabled")
                : run.FromStatus<bool>(env, gameBar.Status, "registry:AutoGameModeEnabled"),
            GameDvrEnabled = gameDvr.Ok() || gameDvr.Status == ReadStatus.NotFound
                ? run.Known(dvrPolicy.Num("AllowGameDVR") != 0 && gameDvr.Num("AppCaptureEnabled") == 1, "registry:GameDVR")
                : run.FromStatus<bool>(env, gameDvr.Status, "registry:GameDVR"),
            WindowedOptimizations = WindowedOpt(env, run, prefs),
            GpuPreferences = prefs.Ok() || prefs.Status == ReadStatus.NotFound
                ? run.Known<IReadOnlyList<GpuPreference>>(GpuPrefs(prefs), "registry:UserGpuPreferences")
                : run.FromStatus<IReadOnlyList<GpuPreference>>(env, prefs.Status, "registry:UserGpuPreferences"),
        };
        return new SnapshotPatch { Display = state };
    }

    private static Fact<IReadOnlyList<MonitorInfo>> Monitors(ScanEnvironment env, RunBuilder run)
    {
        const string m = "QueryDisplayConfig";
        var raw = env.Sources.System.GetDisplays();
        if (raw.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<MonitorInfo>>(env, raw.Status, m);
        }

        return run.Known<IReadOnlyList<MonitorInfo>>(
            raw.Paths.Select(p => new MonitorInfo
            {
                Name = p.Name,
                Primary = p.Primary,
                Width = p.Width,
                Height = p.Height,
                RefreshHz = p.RefreshHz,
                HdrEnabled = p.Hdr,
                Internal = p.Internal,
                GpuInstanceId = p.AdapterDevicePath is null ? null : StorageCollector.DevicePathToInstanceId(p.AdapterDevicePath)?.ToUpperInvariant(),
            }).ToList(),
            m);
    }

    private static Fact<bool> WindowedOpt(ScanEnvironment env, RunBuilder run, RegKeyData prefs)
    {
        const string m = "registry:DirectXUserGlobalSettings";
        if (!prefs.Ok() && prefs.Status != ReadStatus.NotFound)
        {
            return run.FromStatus<bool>(env, prefs.Status, m);
        }

        var global = prefs.Str("DirectXUserGlobalSettings");
        var value = Setting(global, "SwapEffectUpgradeEnable");
        return value is { } v ? run.Known(v == 1, m) : run.Unknown<bool>(UnknownReason.Unsupported, m, "not_set_default");
    }

    private static List<GpuPreference> GpuPrefs(RegKeyData prefs)
    {
        var list = new List<GpuPreference>();
        foreach (var v in prefs.Values)
        {
            if (!RiotCollector.IsRiotPath(v.Name) || Setting(v.Data, "GpuPreference") is not { } pref)
            {
                continue;
            }

            list.Add(new GpuPreference { ImageName = PathText.FileName(v.Name), Preference = pref });
        }

        return list;
    }

    internal static int? Setting(string? data, string name)
    {
        if (data is null)
        {
            return null;
        }

        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0], name, StringComparison.OrdinalIgnoreCase))
            {
                return Parse.Int(kv[1]);
            }
        }

        return null;
    }
}

/// <summary>Audio endpoints from the MMDevices registry (InstanceIds and numeric properties; names are display only).</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class AudioCollector(SystemSources sources) : CollectorBase(sources)
{
    private const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string PkeyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string PkeyInterfaceName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const string PkeyDeviceInstance = "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";
    private const string PkeyFormFactor = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0";
    private const string FxClsidPrefix = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";

    /// <inheritdoc />
    public override string Id => "audio";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Hardware;

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        const string m = "registry:MMDevices";
        var render = env.Hklm(Root + @"\Render");
        var capture = env.Hklm(Root + @"\Capture");
        if (!render.Ok() && !capture.Ok())
        {
            return new SnapshotPatch { Audio = new AudioState { Endpoints = run.FromStatus<IReadOnlyList<AudioEndpoint>>(env, render.Status, m) } };
        }

        var samePlayer = env.Env.UserSid is null || string.Equals(env.Env.UserSid, env.Player.Sid, StringComparison.OrdinalIgnoreCase);
        var defaults = samePlayer ? env.Sources.System.GetAudioDefaults() : new AudioDefaultsRaw { Status = ReadStatus.Unsupported };
        if (!samePlayer)
        {
            run.Issue("skipped:default_endpoints_engine_is_not_the_player");
        }
        else if (defaults.Status != ReadStatus.Ok)
        {
            run.Issue("unavailable:default_endpoints");
        }

        // Defaults not read (another account's engine, or the API failed) = IsDefault* null ("not read"), never false.
        var defaultsKnown = samePlayer && defaults.Status == ReadStatus.Ok;
        var list = new List<AudioEndpoint>();
        list.AddRange(Endpoints(env, render, "Render", AudioFlow.Render, "{0.0.0.00000000}.", defaultsKnown, defaults.RenderConsole, defaults.RenderCommunications));
        list.AddRange(Endpoints(env, capture, "Capture", AudioFlow.Capture, "{0.0.1.00000000}.", defaultsKnown, defaults.CaptureConsole, defaults.CaptureCommunications));
        return new SnapshotPatch { Audio = new AudioState { Endpoints = run.Known<IReadOnlyList<AudioEndpoint>>(list, m) } };
    }

    private static List<AudioEndpoint> Endpoints(ScanEnvironment env, RegKeyData root, string flowKey, AudioFlow flow, string idPrefix, bool defaultsKnown, string? defaultConsole, string? defaultComms)
    {
        var raw = new List<AudioEndpoint>();
        foreach (var sub in root.SubKeys.Take(200))
        {
            var key = env.Hklm(Root + "\\" + flowKey + "\\" + sub);
            var props = env.Hklm(Root + "\\" + flowKey + "\\" + sub + @"\Properties");
            var fx = env.Hklm(Root + "\\" + flowKey + "\\" + sub + @"\FxProperties");
            var stateBits = (key.Num("DeviceState") ?? 0) & 0xF;
            var state = stateBits switch
            {
                1 => AudioEndpointState.Active,
                2 => AudioEndpointState.Disabled,
                8 => AudioEndpointState.Unplugged,
                _ => AudioEndpointState.NotPresent,
            };
            var device = props.Str(PkeyDeviceInstance);
            if (device is not null && device.StartsWith('{') && device.IndexOf('}', StringComparison.Ordinal) is var close and > 0 && close + 2 <= device.Length)
            {
                device = device[(close + 2)..];
            }

            var name = props.Str(PkeyName);
            var iface = props.Str(PkeyInterfaceName);
            var apos = fx.Values
                .Where(v => v.Name.StartsWith(FxClsidPrefix, StringComparison.OrdinalIgnoreCase) && v.Data is not null)
                .SelectMany(v => v.Data!.Split(['\n', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
                .Where(s => s.Length == 38 && s.StartsWith('{') && !s.StartsWith("{00000000-0000-0000-0000-000000000000", StringComparison.Ordinal))
                .Select(s => s.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            raw.Add(new AudioEndpoint
            {
                Id = idPrefix + sub,
                Name = name is null ? null : iface is null ? name : name + " (" + iface + ")",
                Flow = flow,
                State = state,
                FormFactor = Parse.Long(props.Str(PkeyFormFactor)) switch
                {
                    0 => AudioFormFactor.Remote,
                    1 => AudioFormFactor.Speakers,
                    2 => AudioFormFactor.LineLevel,
                    3 => AudioFormFactor.Headphones,
                    4 => AudioFormFactor.Microphone,
                    5 => AudioFormFactor.Headset,
                    6 => AudioFormFactor.Handset,
                    7 or 8 or 9 => AudioFormFactor.Digital,
                    _ => AudioFormFactor.Unknown,
                },
                IsBluetoothHandsFree = device is not null && device.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase),
                ApoClsids = apos,
                DeviceInstanceId = device,
            });
        }

        return raw
            .Select(e => e with
            {
                IsDefault = defaultsKnown ? string.Equals(e.Id, defaultConsole, StringComparison.OrdinalIgnoreCase) : null,
                IsDefaultCommunications = defaultsKnown ? string.Equals(e.Id, defaultComms, StringComparison.OrdinalIgnoreCase) : null,
            })
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Mouse settings from the player's hive (<c>HKU\&lt;sid&gt;\Control Panel\Mouse</c>), not the engine account.</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class InputCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <inheritdoc />
    public override string Id => "input";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Hardware;

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        const string m = "registry:Control Panel\\Mouse";
        var key = env.Hkcu(@"Control Panel\Mouse");
        if (!key.Ok())
        {
            return new SnapshotPatch { Input = new InputState { Mouse = run.FromStatus<MouseSettings>(env, key.Status == ReadStatus.NotFound ? ReadStatus.Unsupported : key.Status, m) } };
        }

        var accel = Parse.Int(key.Str("MouseSpeed")) ?? 1;
        var speed = Parse.Int(key.Str("MouseSensitivity")) ?? 10;
        return new SnapshotPatch { Input = new InputState { Mouse = run.Known(new MouseSettings { EnhancePointerPrecision = accel != 0, Speed = Math.Clamp(speed, 1, 20) }, m) } };
    }
}
