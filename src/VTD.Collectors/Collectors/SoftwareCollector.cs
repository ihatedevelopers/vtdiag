using System.Globalization;
using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>
/// Services (SCM + registry), processes (ToolHelp names/PIDs only), installed apps (HKLM, WOW6432Node, the player's
/// HKU hive; uninstall command lines are not captured), startup entries (target image only, never arguments) and
/// enabled optional features.
/// </summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class SoftwareCollector(SystemSources sources) : CollectorBase(sources)
{
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string Uninstall32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string UserUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <inheritdoc />
    public override string Id => "software";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Apps;

    /// <inheritdoc />
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var services = run.Try("scm", () => Services(env, run, cancellationToken));
        var processes = run.Try("toolhelp", () => Processes(env, run));
        var apps = run.Try("uninstall", () => Apps(env, run));
        cancellationToken.ThrowIfCancellationRequested();
        var startup = run.Try("startup", () => Startup(env, run));
        var features = run.Try("wmi:Win32_OptionalFeature", () => Features(env, run));
        return new SnapshotPatch { Services = services, Processes = processes, Apps = apps, Startup = startup, OptionalFeatures = features };
    }

    private static Fact<IReadOnlyList<ServiceInfo>> Services(ScanEnvironment env, RunBuilder run, CancellationToken ct)
    {
        const string m = "scm";
        var list = env.Services;
        if (list.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<ServiceInfo>>(env, list.Status, m);
        }

        var result = new List<ServiceInfo>(list.Items.Count);
        foreach (var s in list.Items)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(ServiceReader.Build(env, s, env.Hklm(@"SYSTEM\CurrentControlSet\Services\" + s.Name)));
        }

        return run.Known<IReadOnlyList<ServiceInfo>>(result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), m);
    }

    private static Fact<IReadOnlyList<ProcessInfo>> Processes(ScanEnvironment env, RunBuilder run)
    {
        const string m = "toolhelp";
        var list = env.Processes;
        if (list.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<ProcessInfo>>(env, list.Status, m);
        }

        return run.Known<IReadOnlyList<ProcessInfo>>(
            list.Items
                .Where(p => p.Pid != 0)
                .Select(p => new ProcessInfo { Name = p.Name, ProcessId = p.Pid, ParentProcessId = p.ParentPid, ThreadCount = p.Threads, IsProtectedRiotProcess = RiotProcessNames.IsProtected(p.Name) })
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.ProcessId)
                .ToList(),
            m);
    }

    /// <summary>Installed apps (shared with the pack executor's uninstall-regex kind).</summary>
    /// <param name="env">Environment.</param>
    internal static (ReadStatus Status, IReadOnlyList<InstalledApp> Apps, bool UserHiveRead) ReadApps(ScanEnvironment env)
        => env.Memo("software.apps", () => new AppsResult(ReadAppsCore(env))).Value;

    private sealed record AppsResult((ReadStatus, IReadOnlyList<InstalledApp>, bool) Value);

    private static (ReadStatus, IReadOnlyList<InstalledApp>, bool) ReadAppsCore(ScanEnvironment env)
    {
        var apps = new List<InstalledApp>();
        var machine = env.Hklm(UninstallKey);
        if (!machine.Ok())
        {
            return (machine.Status, apps, false);
        }

        void Read(RegKeyData root, AppHive hive, Func<string, RegKeyData> open)
        {
            foreach (var sub in root.SubKeys.Take(2000))
            {
                var k = open(sub);
                var name = k.Str("DisplayName")?.Trim();
                if (!k.Ok() || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                apps.Add(new InstalledApp
                {
                    DisplayName = name,
                    Publisher = k.Str("Publisher")?.Trim(),
                    Version = k.Str("DisplayVersion")?.Trim(),
                    InstallDate = DateOnly.TryParseExact(k.Str("InstallDate") ?? string.Empty, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                    Hive = hive,
                    KeyName = sub,
                    HasQuietUninstall = !string.IsNullOrEmpty(k.Str("QuietUninstallString")),
                    SystemComponent = k.Num("SystemComponent") == 1,
                    InstallLocation = string.IsNullOrWhiteSpace(k.Str("InstallLocation")) ? null : k.Str("InstallLocation")!.Trim().Trim('"'),
                });
            }
        }

        Read(machine, AppHive.Hklm, s => env.Hklm(UninstallKey.Sub(s)));
        var wow = env.Hklm(Uninstall32Key);
        if (wow.Ok())
        {
            Read(wow, AppHive.Hklm32, s => env.Hklm(Uninstall32Key.Sub(s)));
        }

        var user = env.Hkcu(UserUninstallKey);
        if (user.Ok())
        {
            Read(user, AppHive.Hkcu, s => env.Hkcu(UserUninstallKey.Sub(s)));
        }

        var userRead = user.Ok() || user.Status == ReadStatus.NotFound;
        return (ReadStatus.Ok, apps.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.KeyName, StringComparer.Ordinal).ToList(), userRead);
    }

    private static Fact<IReadOnlyList<InstalledApp>> Apps(ScanEnvironment env, RunBuilder run)
    {
        const string m = "uninstall";
        var (status, apps, userRead) = ReadApps(env);
        if (status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<InstalledApp>>(env, status, m);
        }

        if (!userRead)
        {
            // Per-user installs (Discord, many overlays) live in the player's hive: without it the list is partial.
            run.Issue("player_hive_unreadable:Uninstall");
            return run.Partial(apps, m, "player_hive_unreadable");
        }

        return run.Known(apps, m);
    }

    private static Fact<IReadOnlyList<StartupItem>> Startup(ScanEnvironment env, RunBuilder run)
    {
        const string m = "startup";
        var items = new List<StartupItem>();
        const string run64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string runOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        const string run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
        var hklmApproved = Approved(env.Hklm(approved.Sub("Run")), env.Hklm(approved.Sub("Run32")));
        var hkcuApproved = Approved(env.Hkcu(approved.Sub("Run")), env.Hkcu(approved.Sub("Run32")));
        var folderApprovedUser = Approved(env.Hkcu(approved.Sub("StartupFolder")));
        var folderApprovedCommon = Approved(env.Hklm(approved.Sub("StartupFolder")));
        AddRunKey(env, items, env.Hklm(run64), StartupLocation.HklmRun, hklmApproved);
        AddRunKey(env, items, env.Hklm(run32), StartupLocation.HklmRun, hklmApproved);
        AddRunKey(env, items, env.Hklm(runOnce), StartupLocation.HklmRunOnce, null);
        var userRun = env.Hkcu(run64);
        var partial = false;
        if (!userRun.Ok() && userRun.Status != ReadStatus.NotFound)
        {
            partial = true;
            run.Issue("player_hive_unreadable:Run");
        }

        AddRunKey(env, items, userRun, StartupLocation.HkcuRun, hkcuApproved);
        AddRunKey(env, items, env.Hkcu(runOnce), StartupLocation.HkcuRunOnce, null);
        AddFolder(env, items, Path.Combine(env.Player.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"), StartupLocation.StartupFolderUser, folderApprovedUser);
        AddFolder(env, items, Path.Combine(env.Env.ProgramData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"), StartupLocation.StartupFolderCommon, folderApprovedCommon);
        partial |= !AddLogonTasks(env, run, items);
        return partial ? run.Partial<IReadOnlyList<StartupItem>>(items, m, "some_locations_unreadable") : run.Known<IReadOnlyList<StartupItem>>(items, m);
    }

    private static Dictionary<string, bool> Approved(params RegKeyData[] keys)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys.Where(k => k.Ok()))
        {
            foreach (var v in k.Values)
            {
                var bytes = Parse.Hex(v.Data);
                if (bytes is { Length: > 0 })
                {
                    map[v.Name] = (bytes[0] & 1) == 0;
                }
            }
        }

        return map;
    }

    private static void AddRunKey(ScanEnvironment env, List<StartupItem> items, RegKeyData key, StartupLocation location, Dictionary<string, bool>? approved)
    {
        if (!key.Ok())
        {
            return;
        }

        foreach (var v in key.Values.Where(v => v.Name.Length > 0))
        {
            var target = PathText.Executable(PlayerPaths.Expand(env, v.Data ?? string.Empty));
            items.Add(new StartupItem
            {
                Name = v.Name,
                Location = location,
                ImageName = target is null ? null : PathText.FileName(target),
                TargetPath = target,
                Enabled = approved is null ? true : approved.TryGetValue(v.Name, out var e) ? e : true,
            });
        }
    }

    private static void AddFolder(ScanEnvironment env, List<StartupItem> items, string dir, StartupLocation location, Dictionary<string, bool> approved)
    {
        var listing = env.Sources.Files.List(dir, "*", recursive: false, max: 200);
        if (listing.Status != ReadStatus.Ok)
        {
            return;
        }

        foreach (var e in listing.Entries.Where(e => !e.IsDirectory && !string.Equals(e.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            string? target = Path.Combine(dir, e.Name);
            if (e.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                target = env.Sources.Files.ReadShortcut(Path.Combine(dir, e.Name)).TargetPath;
            }

            items.Add(new StartupItem
            {
                Name = e.Name,
                Location = location,
                ImageName = target is null ? null : PathText.FileName(target),
                TargetPath = target,
                Enabled = approved.TryGetValue(e.Name, out var en) ? en : true,
            });
        }
    }

    internal static LineFilter TaskFilter { get; } = new("task.v1", line =>
    {
        var t = line.Trim();
        if (t.StartsWith("<LogonTrigger", StringComparison.Ordinal))
        {
            return "logon|";
        }

        if (t.StartsWith("<Command>", StringComparison.Ordinal) && t.EndsWith("</Command>", StringComparison.Ordinal))
        {
            return "command|" + t["<Command>".Length..^"</Command>".Length];
        }

        if (t is "<Enabled>false</Enabled>")
        {
            return "disabled|";
        }

        return null;
    });

    /// <returns>False when the task folder could not be listed (the logon-task part of the list is missing).</returns>
    private static bool AddLogonTasks(ScanEnvironment env, RunBuilder run, List<StartupItem> items)
    {
        var dir = Path.Combine(env.WindowsDir, "System32", "Tasks");
        var listing = env.Sources.Files.List(dir, "*", recursive: true, max: 1000);
        if (listing.Status != ReadStatus.Ok)
        {
            run.Issue((env.ReasonOf(listing.Status) == UnknownReason.NeedsAdmin ? "needs_admin" : "failed") + ":logon_tasks");
            return false;
        }

        foreach (var e in listing.Entries.Where(e => !e.IsDirectory && !e.Name.StartsWith(@"Microsoft\", StringComparison.OrdinalIgnoreCase)).Take(400))
        {
            var lines = env.Sources.Files.ReadLines(Path.Combine(dir, e.Name), TaskFilter, 256 * 1024, 16).Lines;
            if (!lines.Contains("logon|", StringComparer.Ordinal))
            {
                continue;
            }

            var command = lines.Where(l => l.StartsWith("command|", StringComparison.Ordinal)).Select(l => l["command|".Length..]).FirstOrDefault();
            var target = PathText.Executable(command is null ? null : PlayerPaths.Expand(env, command));
            items.Add(new StartupItem
            {
                Name = e.Name,
                Location = StartupLocation.LogonTask,
                ImageName = target is null ? null : PathText.FileName(target),
                TargetPath = target,
                Enabled = !lines.Contains("disabled|", StringComparer.Ordinal),
            });
        }

        return true;
    }

    private static Fact<IReadOnlyList<string>> Features(ScanEnvironment env, RunBuilder run)
    {
        const string m = "wmi:Win32_OptionalFeature";
        var r = env.Sources.Wmi.Query(@"root\cimv2", "SELECT Name FROM Win32_OptionalFeature WHERE InstallState = 1", TimeSpan.FromSeconds(12));
        if (r.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<string>>(env, r.Status, m);
        }

        return run.Known<IReadOnlyList<string>>(
            r.Rows.Select(x => x.GetValueOrDefault("Name")).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Order(StringComparer.Ordinal).ToList(),
            m);
    }
}
