using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>
/// Vanguard readiness: Secure Boot (registry, no admin) reconciled with firmware variables (admin), TPM (TBS + PnP, admin
/// Win32_Tpm), VBS/HVCI (Win32_DeviceGuard), virtualization, IOMMU, BCD flags (admin), BitLocker, Defender, AV, IFEO.
/// </summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class SecurityCollector(SystemSources sources) : CollectorBase(sources)
{
    internal const string IfeoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>Drivers Riot/Microsoft name as blocking Memory Integrity or Vanguard (catalog check hvci_incompat_drivers).</summary>
    internal static readonly string[] KnownHvciBlockers = ["gdrv3.sys", "xhunter1.sys", "ene.sys", "se64a.sys", "mtkwl6ex.sys", "gdrv2.sys", "rtcore64.sys", "winring0x64.sys", "winring0.sys"];

    /// <summary>Firmware TPM manufacturer ids (Intel PTT, AMD fTPM, Qualcomm, Microsoft Pluton).</summary>
    internal static readonly string[] FirmwareTpmVendors = ["INTC", "AMD", "QCOM", "MSFT"];

    /// <inheritdoc />
    public override string Id => "security";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Security;

    /// <inheritdoc />
    public override CollectorNeeds Needs => CollectorNeeds.Admin;

    /// <inheritdoc />
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var fw = env.Sources.System.GetFirmware(env.Elevated);
        var fwType = FirmwareCollector.FirmwareTypeOf(env, fw);
        var dg = env.Sources.Wmi.Query(@"root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard", TimeSpan.FromSeconds(6));
        var cpu = env.Sources.System.GetCpu();
        var hypervisor = run.Try("cpuid", () => Hypervisor(env, run, cpu));
        cancellationToken.ThrowIfCancellationRequested();
        var state = new SecurityState
        {
            SecureBootEnabled = run.Try("registry:SecureBoot\\State", () => SecureBoot(env, run, fwType)),
            SecureBootSetupMode = FirmwareVar(env, run, fw, fwType, "firmware:SetupMode", fw.SetupMode is { } sm ? sm == 1 : null),
            SecureBootDbHasCa2023 = FirmwareVar(env, run, fw, fwType, "firmware:db", fw.DbHasCa2023),
            Tpm = run.Try("tbs", () => Tpm(env, run)),
            Vbs = run.Try("wmi:Win32_DeviceGuard", () => Vbs(env, run, dg)),
            HvciIncompatibleDrivers = run.Try("codeintegrity", () => HvciBlockers(env, run, cancellationToken)),
            VirtualizationFirmwareEnabled = run.Try("wmi:Win32_Processor", () => VirtFirmware(env, run, hypervisor)),
            HypervisorPresent = hypervisor,
            KernelDmaProtection = run.Try("wmi:Win32_DeviceGuard", () => DmaProtection(env, run, dg)),
            Bcd = run.Try("wmi:BcdStore", () => Bcd(env, run)),
            SystemVolumeBitLocker = run.Try("shell:BitLockerProtection", () => BitLocker(env, run)),
            Defender = run.Try("wmi:MSFT_MpComputerStatus", () => Defender(env, run)),
            AntivirusProducts = run.Try("wmi:SecurityCenter2", () => Antivirus(env, run)),
            ExploitOverrides = run.Try("registry:IFEO", () => ExploitOverrides(env, run)),
            Ifeo = run.Try("registry:IFEO", () => Ifeo(env, run)),
        };
        return new SnapshotPatch { Security = state };
    }

    private static Fact<bool> SecureBoot(ScanEnvironment env, RunBuilder run, FirmwareType? fwType)
    {
        const string m = "registry:SecureBoot\\State";
        var key = env.Hklm(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        if (key.Ok() && key.Num("UEFISecureBootEnabled") is { } v)
        {
            return run.Known(v == 1, m);
        }

        if (fwType == FirmwareType.Bios)
        {
            return run.Known(false, m);
        }

        return key.Ok() ? run.Unknown<bool>(UnknownReason.Unsupported, m, "value_absent") : run.FromStatus<bool>(env, key.Status == ReadStatus.NotFound ? ReadStatus.Unsupported : key.Status, m);
    }

    private static Fact<bool> FirmwareVar(ScanEnvironment env, RunBuilder run, FirmwareRaw fw, FirmwareType? fwType, string method, bool? value)
    {
        if (fwType == FirmwareType.Bios)
        {
            return run.NotApplicable<bool>(method, "legacy_bios");
        }

        if (!env.Elevated)
        {
            return run.Unknown<bool>(UnknownReason.NeedsAdmin, method, "not elevated");
        }

        if (fw.VariablesStatus != ReadStatus.Ok)
        {
            return run.FromStatus<bool>(env, fw.VariablesStatus, method);
        }

        return value is { } b ? run.Known(b, method) : run.Unknown<bool>(UnknownReason.Unsupported, method, "variable_absent");
    }

    private static Fact<TpmInfo> Tpm(ScanEnvironment env, RunBuilder run)
    {
        const string m = "tbs";
        var tbs = env.Sources.System.GetTpm();
        var device = env.Devices.Status == ReadStatus.Ok
            ? env.Devices.Items.FirstOrDefault(d => d.Present && (d.InstanceId.StartsWith(@"ACPI\MSFT0101", StringComparison.OrdinalIgnoreCase) || d.InstanceId.StartsWith(@"ACPI\MSFT0200", StringComparison.OrdinalIgnoreCase) || d.InstanceId.StartsWith(@"ACPI\NTC0702", StringComparison.OrdinalIgnoreCase)))
            : null;
        if (tbs.Status is not ReadStatus.Ok and not ReadStatus.NotFound && device is null)
        {
            return run.FromStatus<TpmInfo>(env, tbs.Status, m);
        }

        var present = tbs.Status == ReadStatus.Ok || device is not null;
        var spec = tbs.Status == ReadStatus.Ok ? tbs.Version switch { 2 => "2.0", 1 => "1.2", _ => null } : device?.InstanceId.Contains("MSFT0101", StringComparison.OrdinalIgnoreCase) == true ? "2.0" : null;
        var info = new TpmInfo { Present = present, SpecVersion = spec, InstanceId = device?.InstanceId };
        if (!present)
        {
            return run.Known(info, m);
        }

        if (!env.Elevated)
        {
            run.Issue("needs_admin:Win32_Tpm");
            return run.Known(info, m);
        }

        var wmi = env.Sources.WmiMethods.ReadTpm(TimeSpan.FromSeconds(6));
        if (wmi.Status != ReadStatus.Ok)
        {
            run.Issue("failed:Win32_Tpm");
            return run.Known(info, m);
        }

        var manufacturer = wmi.ManufacturerIdTxt?.Trim();
        var kind = manufacturer is null ? TpmKind.Unknown : FirmwareTpmVendors.Contains(manufacturer, StringComparer.OrdinalIgnoreCase) ? TpmKind.Firmware : TpmKind.Discrete;
        return run.Known(
            info with
            {
                SpecVersion = wmi.SpecVersion ?? info.SpecVersion,
                Kind = kind,
                Ready = wmi.Ready,
                Enabled = wmi.Enabled,
                Activated = wmi.Activated,
                ManufacturerId = manufacturer,
            },
            "tbs+wmi:Win32_Tpm");
    }

    private static Fact<VbsInfo> Vbs(ScanEnvironment env, RunBuilder run, WmiResult dg)
    {
        const string m = "wmi:Win32_DeviceGuard";
        if (dg.Status != ReadStatus.Ok || dg.Rows.Count == 0)
        {
            return run.FromStatus<VbsInfo>(env, dg.Status == ReadStatus.Ok ? ReadStatus.Unsupported : dg.Status, m);
        }

        var row = dg.Rows[0];
        var status = Parse.Int(row.GetValueOrDefault("VirtualizationBasedSecurityStatus")) switch
        {
            2 => VbsStatus.Running,
            1 => VbsStatus.Configured,
            _ => VbsStatus.Off,
        };
        var configured = Parse.IntList(row.GetValueOrDefault("SecurityServicesConfigured"));
        var running = Parse.IntList(row.GetValueOrDefault("SecurityServicesRunning"));
        return run.Known(
            new VbsInfo
            {
                Status = status,
                HvciConfigured = configured.Contains(2),
                HvciRunning = running.Contains(2),
                AvailableSecurityProperties = Parse.IntList(row.GetValueOrDefault("AvailableSecurityProperties")),
                RequiredSecurityProperties = Parse.IntList(row.GetValueOrDefault("RequiredSecurityProperties")),
                SecurityServicesRunning = running,
            },
            m);
    }

    private static Fact<bool> DmaProtection(ScanEnvironment env, RunBuilder run, WmiResult dg)
    {
        const string m = "wmi:Win32_DeviceGuard.AvailableSecurityProperties";
        if (dg.Status != ReadStatus.Ok || dg.Rows.Count == 0)
        {
            return run.FromStatus<bool>(env, dg.Status == ReadStatus.Ok ? ReadStatus.Unsupported : dg.Status, m);
        }

        return run.Known(Parse.IntList(dg.Rows[0].GetValueOrDefault("AvailableSecurityProperties")).Contains(3), m);
    }

    private static Fact<IReadOnlyList<string>> HvciBlockers(ScanEnvironment env, RunBuilder run, CancellationToken ct)
    {
        const string m = "codeintegrity";
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var since = env.StartedUtc.AddDays(-Math.Clamp(env.Request.EventWindowDays, 1, 30));
        var events = env.Sources.Events.Query(
            new EventQuery { Log = "Microsoft-Windows-CodeIntegrity/Operational", EventIds = [3033, 3034, 3063, 3077], SinceUtc = since, Max = 300, Fields = ["FileNameBuffer"] },
            ct);
        if (events.Status == ReadStatus.Ok)
        {
            foreach (var e in events.Records)
            {
                if (e.Data.TryGetValue("FileNameBuffer", out var file) && file.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(PathText.FileName(file).ToLowerInvariant());
                }
            }
        }
        else
        {
            run.Issue((env.ReasonOf(events.Status) == UnknownReason.NeedsAdmin ? "needs_admin" : "failed") + ":CodeIntegrity/Operational");
        }

        var driversDir = Path.Combine(env.WindowsDir, "System32", "drivers");
        foreach (var name in KnownHvciBlockers)
        {
            if (env.Sources.Files.Stat(Path.Combine(driversDir, name)).Status == ReadStatus.Ok)
            {
                found.Add(name);
            }
        }

        if (events.Status != ReadStatus.Ok && found.Count == 0)
        {
            return run.FromStatus<IReadOnlyList<string>>(env, events.Status, m);
        }

        return run.Known<IReadOnlyList<string>>(found.ToList(), m);
    }

    private static Fact<bool> Hypervisor(ScanEnvironment env, RunBuilder run, CpuRaw cpu)
    {
        if (cpu.Status == ReadStatus.Ok && cpu.CpuidAvailable)
        {
            return run.Known(cpu.HypervisorBit, "cpuid");
        }

        var cs = env.Sources.Wmi.Query(@"root\cimv2", "SELECT HypervisorPresent FROM Win32_ComputerSystem", TimeSpan.FromSeconds(5));
        return cs.Status == ReadStatus.Ok && cs.Rows.Count > 0 && Parse.Bool(cs.Rows[0].GetValueOrDefault("HypervisorPresent")) is { } b
            ? run.Known(b, "wmi:Win32_ComputerSystem")
            : run.FromStatus<bool>(env, cs.Status == ReadStatus.Ok ? ReadStatus.Unsupported : cs.Status, "wmi:Win32_ComputerSystem");
    }

    private static Fact<bool> VirtFirmware(ScanEnvironment env, RunBuilder run, Fact<bool> hypervisor)
    {
        const string m = "wmi:Win32_Processor";
        if (hypervisor.TryGetValue(out var hv) && hv)
        {
            // With a hypervisor running Win32_Processor reports FALSE; a running hypervisor proves VT-x/SVM is on.
            return run.Known(true, "cpuid:hypervisor");
        }

        var p = env.Sources.Wmi.Query(@"root\cimv2", "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor", TimeSpan.FromSeconds(5));
        if (p.Status != ReadStatus.Ok || p.Rows.Count == 0)
        {
            return run.FromStatus<bool>(env, p.Status == ReadStatus.Ok ? ReadStatus.Unsupported : p.Status, m);
        }

        return Parse.Bool(p.Rows[0].GetValueOrDefault("VirtualizationFirmwareEnabled")) is { } b ? run.Known(b, m) : run.Unknown<bool>(UnknownReason.Unsupported, m);
    }

    private static Fact<BcdFlags> Bcd(ScanEnvironment env, RunBuilder run)
    {
        const string m = "wmi:BcdStore";
        if (!env.Elevated)
        {
            return run.Unknown<BcdFlags>(UnknownReason.NeedsAdmin, m, "not elevated");
        }

        var bcd = env.Sources.WmiMethods.ReadBcd(TimeSpan.FromSeconds(6));
        if (bcd.Status != ReadStatus.Ok)
        {
            return run.FromStatus<BcdFlags>(env, bcd.Status, m);
        }

        return run.Known(
            new BcdFlags
            {
                TestSigning = bcd.TestSigning,
                NoIntegrityChecks = bcd.NoIntegrityChecks,
                Nx = bcd.Nx switch { 0 => "OptIn", 1 => "OptOut", 2 => "AlwaysOff", 3 => "AlwaysOn", null => "OptIn", _ => null },
                HypervisorLaunchType = bcd.HypervisorLaunchType switch { 0 => "Off", 1 => "Auto", null => null, _ => null },
                SafeBoot = bcd.SafeBoot is not null,
                Debug = bcd.Debug,
            },
            m);
    }

    private static Fact<BitLockerStatus> BitLocker(ScanEnvironment env, RunBuilder run)
    {
        const string m = "shell:BitLockerProtection";
        var raw = env.Sources.System.GetBitLocker(env.SystemDrive + "\\");
        if (raw.Status == ReadStatus.Ok)
        {
            return raw.Protection switch
            {
                1 or 6 => run.Known(BitLockerStatus.On, m),
                2 or null or 0 => run.Known(BitLockerStatus.Off, m),
                3 or 4 => run.Known(BitLockerStatus.InProgress, m),
                5 => run.Known(BitLockerStatus.Suspended, m),
                _ => run.Unknown<BitLockerStatus>(UnknownReason.Unparseable, m),
            };
        }

        if (!env.Elevated)
        {
            return run.FromStatus<BitLockerStatus>(env, raw.Status, m);
        }

        var ev = env.Sources.Wmi.Query(@"root\cimv2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume", TimeSpan.FromSeconds(6));
        var row = ev.Rows.FirstOrDefault(r => string.Equals(r.GetValueOrDefault("DriveLetter"), env.SystemDrive, StringComparison.OrdinalIgnoreCase));
        if (ev.Status != ReadStatus.Ok || row is null)
        {
            return run.FromStatus<BitLockerStatus>(env, ev.Status == ReadStatus.Ok ? ReadStatus.Unsupported : ev.Status, "wmi:Win32_EncryptableVolume");
        }

        var conversion = Parse.Int(row.GetValueOrDefault("ConversionStatus"));
        var protection = Parse.Int(row.GetValueOrDefault("ProtectionStatus"));
        var status = conversion is 2 or 3 ? BitLockerStatus.InProgress : protection == 1 ? BitLockerStatus.On : conversion == 1 ? BitLockerStatus.Suspended : BitLockerStatus.Off;
        return run.Known(status, "wmi:Win32_EncryptableVolume");
    }

    private static Fact<DefenderInfo> Defender(ScanEnvironment env, RunBuilder run)
    {
        const string m = "wmi:MSFT_MpComputerStatus";
        var status = env.Sources.Wmi.Query(@"root\Microsoft\Windows\Defender", "SELECT RealTimeProtectionEnabled, IsTamperProtected FROM MSFT_MpComputerStatus", TimeSpan.FromSeconds(6));
        bool? rtp = null, tamper = null;
        if (status.Status == ReadStatus.Ok && status.Rows.Count > 0)
        {
            rtp = Parse.Bool(status.Rows[0].GetValueOrDefault("RealTimeProtectionEnabled"));
            tamper = Parse.Bool(status.Rows[0].GetValueOrDefault("IsTamperProtected"));
        }
        else
        {
            run.Issue((env.ReasonOf(status.Status) == UnknownReason.NeedsAdmin ? "needs_admin" : "unavailable") + ":MSFT_MpComputerStatus");
        }

        var cfaKey = env.Hklm(@"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access");
        var cfaPolicy = env.Hklm(@"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access");
        var cfa = cfaPolicy.Num("EnableControlledFolderAccess") ?? cfaKey.Num("EnableControlledFolderAccess");
        var ci = env.Hklm(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
        var sac = ci.Num("VerifiedAndReputablePolicyState") switch { 0 => "off", 1 => "on", 2 => "evaluation", _ => null };
        var policy = env.Hklm(@"SOFTWARE\Policies\Microsoft\Windows Defender");
        var rtpPolicy = env.Hklm(@"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection");
        bool? disabledByPolicy = policy.Ok() || policy.Status == ReadStatus.NotFound
            ? policy.Num("DisableAntiSpyware") == 1 || rtpPolicy.Num("DisableRealtimeMonitoring") == 1
            : null;
        if (rtp is null && tamper is null && cfa is null && sac is null && disabledByPolicy is null)
        {
            return run.FromStatus<DefenderInfo>(env, status.Status, m);
        }

        return run.Known(
            new DefenderInfo
            {
                RealTimeProtection = rtp,
                TamperProtection = tamper,
                ControlledFolderAccess = cfa is null ? (cfaKey.Ok() || cfaKey.Status == ReadStatus.NotFound ? false : null) : cfa == 1,
                SmartAppControl = sac,
                DisabledByPolicy = disabledByPolicy,
            },
            m);
    }

    private static Fact<IReadOnlyList<AvProduct>> Antivirus(ScanEnvironment env, RunBuilder run)
    {
        const string m = "wmi:SecurityCenter2";
        var r = env.Sources.Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState, pathToSignedProductExe FROM AntiVirusProduct", TimeSpan.FromSeconds(6));
        if (r.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<AvProduct>>(env, r.Status, m);
        }

        var list = new List<AvProduct>();
        foreach (var row in r.Rows)
        {
            var name = row.GetValueOrDefault("displayName");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var state = Parse.Long(row.GetValueOrDefault("productState"));
            var path = row.GetValueOrDefault("pathToSignedProductExe") ?? string.Empty;
            list.Add(new AvProduct
            {
                Name = name.Trim(),
                Enabled = state is { } s ? ((s >> 12) & 0xF) == 1 : null,
                UpToDate = state is { } u ? ((u >> 4) & 0xF) == 0 : null,
                IsDefender = path.StartsWith("windowsdefender://", StringComparison.OrdinalIgnoreCase) || path.Contains("MsMpeng", StringComparison.OrdinalIgnoreCase),
            });
        }

        return run.Known<IReadOnlyList<AvProduct>>(list, m);
    }

    private static Fact<IReadOnlyList<ExploitOverride>> ExploitOverrides(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:IFEO";
        var root = env.Hklm(IfeoKey);
        if (!root.Ok())
        {
            return run.FromStatus<IReadOnlyList<ExploitOverride>>(env, root.Status, m);
        }

        var list = new List<ExploitOverride>();
        foreach (var name in root.SubKeys.Where(IsRiotImage))
        {
            var key = env.Hklm(IfeoKey.Sub(name));
            var mit = key.Value("MitigationOptions");
            var audit = key.Value("MitigationAuditOptions");
            if (mit is null && audit is null)
            {
                continue;
            }

            list.Add(new ExploitOverride { ImageName = name, MitigationOptionsHex = mit?.Data, MitigationAuditOptionsHex = audit?.Data });
        }

        return run.Known<IReadOnlyList<ExploitOverride>>(list, m);
    }

    private static Fact<IReadOnlyList<IfeoEntry>> Ifeo(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:IFEO";
        var root = env.Hklm(IfeoKey);
        if (!root.Ok())
        {
            return run.FromStatus<IReadOnlyList<IfeoEntry>>(env, root.Status, m);
        }

        var list = new List<IfeoEntry>();
        foreach (var name in root.SubKeys.Take(400))
        {
            var key = env.Hklm(IfeoKey.Sub(name));
            if (!key.Ok())
            {
                continue;
            }

            var hasDebugger = key.Value("Debugger") is not null;
            var perf = key.SubKeys.Contains("PerfOptions", StringComparer.OrdinalIgnoreCase) ? env.Hklm(IfeoKey.Sub(name).Sub("PerfOptions")) : null;
            var cpuPriority = perf?.Num("CpuPriorityClass");
            if (!IsRiotImage(name) && !hasDebugger && cpuPriority is null)
            {
                continue;
            }

            list.Add(new IfeoEntry
            {
                ImageName = name,
                HasDebugger = hasDebugger,
                CpuPriorityClass = cpuPriority is { } c ? (int)c : null,
                ValueNames = key.Values.Select(v => v.Name).Where(n => n.Length > 0).Order(StringComparer.Ordinal).ToList(),
            });
        }

        return run.Known<IReadOnlyList<IfeoEntry>>(list, m);
    }

    internal static bool IsRiotImage(string imageName)
        => RiotProcessNames.IsProtected(imageName) || string.Equals(imageName, "vgk.sys", StringComparison.OrdinalIgnoreCase) || string.Equals(imageName, "RiotClientServices.exe", StringComparison.OrdinalIgnoreCase);
}
