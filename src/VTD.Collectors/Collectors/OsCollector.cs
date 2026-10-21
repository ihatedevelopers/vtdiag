using System.Globalization;
using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>Windows build, pending reboot, Fast Startup, clock, hotfixes, modified-Windows markers, environment.</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class OsCollector(SystemSources sources) : CollectorBase(sources)
{
    internal const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>Virtual machine SMBIOS markers (ordinal, case-insensitive; vendor strings are never localized).</summary>
    internal static readonly string[] VmMarkers = ["VMware", "VirtualBox", "innotek", "QEMU", "KVM", "Xen", "Parallels", "Bochs", "BHYVE", "Virtual Machine", "HVM domU"];

    /// <inheritdoc />
    public override string Id => "os";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Windows;

    /// <inheritdoc />
    public override CollectorNeeds Needs => CollectorNeeds.Network;

    /// <inheritdoc />
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var cv = env.Hklm(CurrentVersionKey);
        var cvMethod = "registry:CurrentVersion";
        Fact<string> Str(string name) => cv.Ok() && cv.Str(name) is { Length: > 0 } s ? run.Known(s, cvMethod) : cv.Ok() ? run.Unknown<string>(UnknownReason.Unsupported, cvMethod + "." + name) : run.FromStatus<string>(env, cv.Status, cvMethod);
        Fact<int> Int(string name, string? alt = null)
        {
            var v = cv.Num(name) ?? Parse.Long(cv.Str(name)) ?? (alt is null ? null : cv.Num(alt) ?? Parse.Long(cv.Str(alt)));
            return v is { } n ? run.Known((int)n, cvMethod) : cv.Ok() ? run.Unknown<int>(UnknownReason.Unsupported, cvMethod + "." + name) : run.FromStatus<int>(env, cv.Status, cvMethod);
        }

        var install = cv.Num("InstallDate");
        cancellationToken.ThrowIfCancellationRequested();
        var os = new OsInfo
        {
            ProductName = Str("ProductName"),
            Build = Int("CurrentBuildNumber", "CurrentBuild"),
            Ubr = Int("UBR"),
            DisplayVersion = cv.Ok() && (cv.Str("DisplayVersion") ?? cv.Str("ReleaseId")) is { Length: > 0 } dv ? run.Known(dv, cvMethod) : run.Unknown<string>(UnknownReason.Unsupported, cvMethod + ".DisplayVersion"),
            EditionId = Str("EditionID"),
            InsiderBranch = Insider(env, run),
            InstallDateUtc = install is > 0 ? run.Known(DateTimeOffset.FromUnixTimeSeconds(install.Value), cvMethod) : run.Unknown<DateTimeOffset>(UnknownReason.Unsupported, cvMethod + ".InstallDate"),
            IsVirtualMachine = run.Try("registry:HARDWARE\\BIOS", () => VirtualMachine(env, run)),
            PendingReboot = run.Try("registry:pending_reboot", () => PendingReboot(env, run)),
            WindowsUpdateInstalling = run.Try("wua:IsBusy", () => WuInstalling(env, run)),
            FastStartupEnabled = run.Try("registry:HiberbootEnabled", () => FastStartup(env, run)),
            HibernationEnabled = run.Try("registry:HibernateEnabled", () => Hibernation(env, run)),
            Clock = run.Try("clock", () => Clock(env, run)),
            Hotfixes = run.Try("wmi:Win32_QuickFixEngineering", () => Hotfixes(env, run)),
            ModifiedWindowsMarkers = run.Try("markers", () => run.Known<IReadOnlyList<string>>(ModifiedMarkers(env), "markers")),
            EnvironmentIssues = run.Try("env", () => EnvironmentIssues(env, run)),
            TaskSchedulerAvailable = run.Try("scm:Schedule", () => ServiceRunning(env, run, "Schedule", "scm:Schedule")),
            InteractiveUserIsAdmin = run.Known(env.Player.IsAdmin, "token"),
        };
        return new SnapshotPatch { Os = os };
    }

    private static Fact<string> Insider(ScanEnvironment env, RunBuilder run)
    {
        var key = env.Hklm(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
        const string m = "registry:WindowsSelfHost";
        if (key.Status == ReadStatus.NotFound)
        {
            return run.NotApplicable<string>(m, "not an insider build");
        }

        if (!key.Ok())
        {
            return run.FromStatus<string>(env, key.Status, m);
        }

        var branch = key.Str("BranchName");
        var enabled = key.Num("EnablePreviewBuilds");
        return string.IsNullOrWhiteSpace(branch) || enabled == 0 ? run.NotApplicable<string>(m, "not an insider build") : run.Known(branch, m);
    }

    private static Fact<bool> VirtualMachine(ScanEnvironment env, RunBuilder run)
    {
        var bios = env.Hklm(@"HARDWARE\DESCRIPTION\System\BIOS");
        const string m = "registry:HARDWARE\\BIOS";
        if (!bios.Ok())
        {
            return run.FromStatus<bool>(env, bios.Status, m);
        }

        string[] fields = ["SystemManufacturer", "SystemProductName", "BIOSVendor", "BaseBoardManufacturer", "BaseBoardProduct"];
        var text = string.Join("|", fields.Select(f => bios.Str(f) ?? string.Empty));
        var vm = VmMarkers.Any(mk => text.Contains(mk, StringComparison.OrdinalIgnoreCase));
        return run.Known(vm, m);
    }

    internal static Fact<PendingRebootInfo> PendingReboot(ScanEnvironment env, RunBuilder run)
    {
        var cbs = env.Hklm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        var wu = env.Hklm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        var sm = env.Hklm(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        const string m = "registry:pending_reboot";
        if (cbs.Status is ReadStatus.AccessDenied or ReadStatus.Failed || wu.Status is ReadStatus.AccessDenied or ReadStatus.Failed || !sm.Ok())
        {
            return run.FromStatus<PendingRebootInfo>(env, !sm.Ok() ? sm.Status : ReadStatus.AccessDenied, m);
        }

        var rename = sm.Value("PendingFileRenameOperations")?.Data;
        return run.Known(
            new PendingRebootInfo
            {
                ComponentServicing = cbs.Ok(),
                WindowsUpdate = wu.Ok(),
                PendingFileRename = !string.IsNullOrWhiteSpace(rename),
            },
            m);
    }

    internal static Fact<bool> WuInstalling(ScanEnvironment env, RunBuilder run)
    {
        var wua = env.Sources.System.GetWindowsUpdate();
        if (wua.Status == ReadStatus.Ok && wua.IsBusy is { } busy)
        {
            return run.Known(busy, "wua:IsBusy");
        }

        const string m = "heuristic:TrustedInstaller";
        var services = env.Services;
        if (services.Status != ReadStatus.Ok)
        {
            return run.FromStatus<bool>(env, services.Status, m);
        }

        var ti = services.Items.FirstOrDefault(s => string.Equals(s.Name, "TrustedInstaller", StringComparison.OrdinalIgnoreCase));
        var tiWorker = env.Processes.Status == ReadStatus.Ok && env.Processes.Items.Any(p => string.Equals(p.Name, "TiWorker.exe", StringComparison.OrdinalIgnoreCase));
        return run.Known((ti?.State == 4) || tiWorker, m);
    }

    private static Fact<bool> FastStartup(ScanEnvironment env, RunBuilder run)
    {
        var key = env.Hklm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
        const string m = "registry:HiberbootEnabled";
        if (!key.Ok())
        {
            return run.FromStatus<bool>(env, key.Status, m);
        }

        var hiberboot = key.Num("HiberbootEnabled");
        if (hiberboot is null)
        {
            return run.Unknown<bool>(UnknownReason.Unsupported, m, "value_absent");
        }

        var hib = Hibernation(env, new RunBuilder("os"));
        return run.Known(hiberboot == 1 && (!hib.TryGetValue(out var h) || h), m);
    }

    private static Fact<bool> Hibernation(ScanEnvironment env, RunBuilder run)
    {
        var key = env.Hklm(@"SYSTEM\CurrentControlSet\Control\Power");
        const string m = "registry:HibernateEnabled";
        if (!key.Ok())
        {
            return run.FromStatus<bool>(env, key.Status, m);
        }

        var v = key.Num("HibernateEnabled") ?? key.Num("HibernateEnabledDefault");
        return v is null ? run.Unknown<bool>(UnknownReason.Unsupported, m, "value_absent") : run.Known(v != 0, m);
    }

    private static Fact<ClockInfo> Clock(ScanEnvironment env, RunBuilder run)
    {
        var w32 = env.Hklm(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters");
        var w32Svc = env.Hklm(@"SYSTEM\CurrentControlSet\Services\W32Time");
        bool? auto = null;
        if (w32.Ok())
        {
            var type = w32.Str("Type");
            auto = type is null ? null : !string.Equals(type, "NoSync", StringComparison.OrdinalIgnoreCase);
            if (w32Svc.Num("Start") == 4)
            {
                auto = false;
            }
        }

        long? skew = null;
        string? reference = null;
        if (env.Request.AllowNetworkProbes)
        {
            var c = env.Sources.Probes.MeasureClock(TimeSpan.FromSeconds(3));
            if (c.Status == ReadStatus.Ok)
            {
                skew = c.SkewSeconds;
                reference = c.Source;
            }
            else
            {
                run.Issue("clock_reference_unreachable");
            }
        }

        return run.Known(new ClockInfo { SkewSeconds = skew, ReferenceSource = reference, TimeZoneId = env.Env.TimeZoneId, AutoTimeSync = auto }, "clock");
    }

    private static Fact<IReadOnlyList<HotfixInfo>> Hotfixes(ScanEnvironment env, RunBuilder run)
    {
        const string m = "wmi:Win32_QuickFixEngineering";
        var r = env.Sources.Wmi.Query(@"root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering", TimeSpan.FromSeconds(8));
        if (r.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<HotfixInfo>>(env, r.Status, m);
        }

        var list = new List<HotfixInfo>();
        foreach (var row in r.Rows)
        {
            if (!row.TryGetValue("HotFixID", out var id) || !id.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateOnly? date = null;
            if (row.TryGetValue("InstalledOn", out var on)
                && DateTime.TryParseExact(on, ["M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                date = DateOnly.FromDateTime(d);
            }

            list.Add(new HotfixInfo { Id = id.ToUpperInvariant(), InstalledOn = date });
        }

        return run.Known<IReadOnlyList<HotfixInfo>>(list.DistinctBy(h => h.Id, StringComparer.Ordinal).OrderBy(h => h.Id, StringComparer.Ordinal).ToList(), m);
    }

    internal static IReadOnlyList<string> ModifiedMarkers(ScanEnvironment env)
    {
        var markers = new List<string>();
        var win = env.WindowsDir;
        if (env.Sources.Files.Stat(Path.Combine(win, "AtlasModules")).Status == ReadStatus.Ok || env.Sources.Files.Stat(Path.Combine(win, "AtlasDesktop")).Status == ReadStatus.Ok)
        {
            markers.Add("image:atlas");
        }

        var oem = env.Hklm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation");
        var cv = env.Hklm(CurrentVersionKey);
        var text = string.Join("|", oem.Str("Manufacturer"), oem.Str("Model"), oem.Str("SupportURL"), cv.Str("RegisteredOrganization"));
        (string Needle, string Marker)[] known =
        [
            ("ReviOS", "image:revios"), ("Ghost Spectre", "image:ghost_spectre"), ("GhostSpectre", "image:ghost_spectre"),
            ("Tiny11", "image:tiny11"), ("Ameliorated", "image:ameliorated"), ("AtlasOS", "image:atlas"), ("Superlite", "image:superlite"),
        ];
        foreach (var (needle, marker) in known)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase) && !markers.Contains(marker, StringComparer.Ordinal))
            {
                markers.Add(marker);
            }
        }

        if (env.Hklm(@"SYSTEM\CurrentControlSet\Services\wuauserv").Num("Start") == 4)
        {
            markers.Add("debloat:wuauserv_disabled");
        }

        if (env.Hklm(@"SYSTEM\CurrentControlSet\Services\Schedule").Num("Start") == 4)
        {
            markers.Add("debloat:schedule_disabled");
        }

        return markers;
    }

    private static Fact<IReadOnlyList<EnvVarFinding>> EnvironmentIssues(ScanEnvironment env, RunBuilder run)
    {
        const string m = "env";
        var machine = env.Hklm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        if (!machine.Ok())
        {
            return run.FromStatus<IReadOnlyList<EnvVarFinding>>(env, machine.Status, m);
        }

        var findings = new List<EnvVarFinding>();
        foreach (var name in new[] { "TEMP", "TMP", "ComSpec", "Path", "windir", "PATHEXT" })
        {
            if (string.IsNullOrWhiteSpace(machine.Str(name)))
            {
                findings.Add(new EnvVarFinding { Name = name, Issue = "missing", Scope = "machine" });
            }
        }

        if ((machine.Str("Path")?.Length ?? 0) > 2047)
        {
            findings.Add(new EnvVarFinding { Name = "Path", Issue = "too_long", Scope = "machine" });
        }

        if (!string.IsNullOrWhiteSpace(machine.Str("__COMPAT_LAYER")))
        {
            findings.Add(new EnvVarFinding { Name = "__COMPAT_LAYER", Issue = "unexpected_set", Scope = "machine" });
        }

        var user = env.Hkcu("Environment");
        if (user.Ok())
        {
            foreach (var name in new[] { "TEMP", "TMP" })
            {
                var value = user.Str(name);
                if (value is null)
                {
                    findings.Add(new EnvVarFinding { Name = name, Issue = "missing", Scope = "user" });
                    continue;
                }

                var expanded = PlayerPaths.Expand(env, value);
                expanded = expanded.Contains('%', StringComparison.Ordinal) ? null : expanded;
                if (expanded is not null && env.Sources.Files.Stat(expanded) is { Status: ReadStatus.NotFound })
                {
                    findings.Add(new EnvVarFinding { Name = name, Issue = "points_to_missing_dir", Scope = "user" });
                }
            }

            if (!string.IsNullOrWhiteSpace(user.Str("__COMPAT_LAYER")))
            {
                findings.Add(new EnvVarFinding { Name = "__COMPAT_LAYER", Issue = "unexpected_set", Scope = "user" });
            }

            if ((user.Str("Path")?.Length ?? 0) > 2047)
            {
                findings.Add(new EnvVarFinding { Name = "Path", Issue = "too_long", Scope = "user" });
            }
        }
        else if (user.Status != ReadStatus.NotFound)
        {
            run.Issue("player_hive_unreadable:Environment");
            if (findings.Count == 0)
            {
                return run.FromStatus<IReadOnlyList<EnvVarFinding>>(env, user.Status, m);
            }
        }

        return run.Known<IReadOnlyList<EnvVarFinding>>(findings, m);
    }

    internal static Fact<bool> ServiceRunning(ScanEnvironment env, RunBuilder run, string name, string method)
    {
        var services = env.Services;
        if (services.Status != ReadStatus.Ok)
        {
            return run.FromStatus<bool>(env, services.Status, method);
        }

        var s = services.Items.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return run.Known(s is not null && s.State == 4, method);
    }
}
