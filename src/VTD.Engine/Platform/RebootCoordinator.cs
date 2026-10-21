using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Session;

namespace VTD.Engine.Platform;

/// <summary>
/// The real <see cref="IRebootCoordinator"/> (design_B.md section 6.4, critique sections 1.3 S-b and 3.1-3.4). It is
/// constructed only by an ELEVATED engine whose image is under <c>%ProgramFiles%</c>; its tests run only in a VM or CI
/// with <c>VTD_ALLOW_SYSTEM_WRITES=1</c>.
/// <list type="bullet">
/// <item>Resume: two tasks in the <c>\VTDiag\</c> folder (engine at Highest, UI at Limited with a 15 s delay), at logon of
/// the PLAYER, with a 7-day end boundary, actions pointing at the fixed Program Files path, read back after
/// registration. A standard user with a separate admin, or a disabled Task Scheduler, gets an HKCU RunOnce entry in the
/// player's own hive that starts the unelevated UI (which asks for UAC once); failing that, "open VTDiag from Start".</item>
/// <item>Restart (invariant R1): <see cref="RestartAsync"/> is the only entry to <c>InitiateShutdownW</c> (and to
/// <c>shutdown.exe /r /fw</c> for "Restart into BIOS"); it restarts only for the token the session runtime armed after a
/// verified UI click, exactly once. A real restart, never a hybrid shutdown.</item>
/// </list>
/// </summary>
public sealed class RebootCoordinator : IRebootCoordinator
{
    /// <summary>Task folder.</summary>
    public const string TaskFolder = @"\VTDiag";

    /// <summary>Engine task.</summary>
    public const string EngineTaskName = "Resume-Engine";

    /// <summary>UI task.</summary>
    public const string UiTaskName = "Resume-UI";

    /// <summary>RunOnce value name in the player's hive.</summary>
    public const string RunOnceValue = "VTDiag.Resume";

    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;

    private readonly BootReader _boot;
    private readonly IFactProbe _probe;
    private readonly RestartPromptLedger _ledger;
    private readonly Func<PlayerContext> _player;
    private readonly string _installDirectory;
    private readonly string _engineUserSid;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="boot">Boot reader.</param>
    /// <param name="probe">Live probes (AC power, Windows Update).</param>
    /// <param name="ledger">Restart prompts (shared with the session runtime).</param>
    /// <param name="player">Interactive player.</param>
    /// <param name="installDirectory">The engine's directory (verified to be under Program Files by the host).</param>
    /// <param name="engineUserSid">The account the engine runs as.</param>
    public RebootCoordinator(BootReader boot, IFactProbe probe, RestartPromptLedger ledger, Func<PlayerContext> player, string installDirectory, string engineUserSid)
    {
        _boot = boot ?? throw new ArgumentNullException(nameof(boot));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _installDirectory = installDirectory ?? throw new ArgumentNullException(nameof(installDirectory));
        _engineUserSid = engineUserSid ?? throw new ArgumentNullException(nameof(engineUserSid));
    }

    /// <summary>Engine image the tasks start.</summary>
    public string EngineExe => Path.Combine(_installDirectory, "VTDiag.Engine.exe");

    /// <summary>UI image the tasks start.</summary>
    public string UiExe => Path.Combine(_installDirectory, "VTDiag.exe");

    /// <inheritdoc />
    public Fact<BootIdentity> CurrentBoot() => _boot.Read();

    /// <inheritdoc />
    public async Task<RebootReadiness> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        var ac = await ProbeAsync(GuardIds.OnAcPower, cancellationToken).ConfigureAwait(false);
        var wuInstalling = await ProbeAsync(GuardIds.WindowsUpdateInstalling, cancellationToken).ConfigureAwait(false);
        var wuReboot = KeyExists(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                       || KeyExists(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
            ? Tri.True
            : Tri.False;
        var fastStartup = DwordOf(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled") switch
        {
            1 => Tri.True,
            0 => Tri.False,
            _ => Tri.Unknown,
        };
        var scheduler = TaskSchedulerAvailable() ? Tri.True : Tri.False;
        var player = _player();
        var playerAdmin = player.IsAdmin ? Tri.True : Tri.False;
        var firmware = GetFirmwareType(out var type) ? (type == 2 ? Tri.True : Tri.False) : Tri.Unknown;
        var mechanism = Plan(scheduler == Tri.True, player);
        var warnings = new List<string>();
        if (fastStartup == Tri.True)
        {
            warnings.Add("restart.use_restart_not_shutdown");
        }

        if (wuReboot == Tri.True)
        {
            warnings.Add("restart.windows_update_pending");
        }

        if (mechanism == ResumeMechanism.RunOnceUi)
        {
            warnings.Add("restart.will_ask_permission_again");
        }

        if (mechanism == ResumeMechanism.Manual)
        {
            warnings.Add("restart.open_from_start_menu");
        }

        return new RebootReadiness
        {
            OnAcPower = ac,
            WindowsUpdateRebootPending = wuReboot,
            WindowsUpdateInstalling = wuInstalling,
            FastStartupEnabled = fastStartup,
            TaskSchedulerAvailable = scheduler,
            InteractiveUserIsAdmin = playerAdmin,
            FirmwareRestartSupported = firmware,
            PlannedMechanism = mechanism,
            WarningKeys = warnings,
        };
    }

    /// <inheritdoc />
    public Task<ResumeRegistration> RegisterResumeAsync(string sessionId, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        // The id goes on a command line run at Highest: accept nothing but a ULID.
        if (!Ulid.IsValid(sessionId))
        {
            throw new ArgumentException("A session id must be a ULID.", nameof(sessionId));
        }

        var player = _player();
        if (Plan(TaskSchedulerAvailable(), player) == ResumeMechanism.ScheduledTasks && TryRegisterTasks(sessionId, expiresUtc, player))
        {
            return Task.FromResult(new ResumeRegistration { Mechanism = ResumeMechanism.ScheduledTasks, Verified = true });
        }

        // Tasks failed or do not fit (standard user + separate admin): remove anything half-registered, then RunOnce.
        TryDeleteTasks();
        if (TryRegisterRunOnce(sessionId, player))
        {
            return Task.FromResult(new ResumeRegistration { Mechanism = ResumeMechanism.RunOnceUi, Verified = true, FallbackMessageKey = "resume.runonce_uac" });
        }

        return Task.FromResult(new ResumeRegistration { Mechanism = ResumeMechanism.Manual, Verified = false, FallbackMessageKey = "resume.manual" });
    }

    /// <inheritdoc />
    public Task UnregisterResumeAsync(CancellationToken cancellationToken)
    {
        TryDeleteTasks();
        try
        {
            using var key = Registry.Users.OpenSubKey(_player().Sid + "\\" + RunOnceKey, writable: true);
            key?.DeleteValue(RunOnceValue, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Nothing registered there, or the hive is not loaded.
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RestartAsync(RestartToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        // R1: only the token a verified UI click minted and the session armed, exactly once.
        if (!_ledger.TryConsume(token))
        {
            throw new InvalidOperationException("restart.token_invalid");
        }

        if (token.Kind == RestartKind.ToFirmware)
        {
            var shutdown = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            using var process = Process.Start(new ProcessStartInfo(shutdown) { ArgumentList = { "/r", "/fw", "/t", "0" }, UseShellExecute = false, CreateNoWindow = true })
                                ?? throw new InvalidOperationException("restart.firmware_failed");
            process.WaitForExit(15_000);
            if (!process.HasExited || process.ExitCode != 0)
            {
                throw new InvalidOperationException("restart.firmware_failed");
            }

            return Task.CompletedTask;
        }

        EnableShutdownPrivilege();
        const uint shutdownRestart = 0x00000004; // apps may still ask to save their work (no force)
        const uint reason = 0x00040000 | 0x00000004 | 0x80000000; // SHTDN_REASON_MAJOR_APPLICATION | MINOR_RECONFIG | FLAG_PLANNED
        var error = InitiateShutdownW(null, "VTDiag: restart requested by you to finish a change.", 0, shutdownRestart, reason);
        if (error != 0)
        {
            throw new InvalidOperationException("restart.failed", new Win32Exception((int)error));
        }

        return Task.CompletedTask;
    }

    /// <summary>The mechanism for this player: tasks only when the engine runs as the player and the scheduler works.</summary>
    /// <param name="schedulerAvailable">Task Scheduler usable.</param>
    /// <param name="player">Player.</param>
    public ResumeMechanism Plan(bool schedulerAvailable, PlayerContext player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var samePrincipal = string.Equals(player.Sid, _engineUserSid, StringComparison.OrdinalIgnoreCase);
        if (schedulerAvailable && samePrincipal && player.IsAdmin)
        {
            return ResumeMechanism.ScheduledTasks;
        }

        return KeyExists(Registry.Users, player.Sid) ? ResumeMechanism.RunOnceUi : ResumeMechanism.Manual;
    }

    /// <summary>The task XML (Task Scheduler 1.3 schema).</summary>
    /// <param name="playerSid">Player (logon trigger and principal).</param>
    /// <param name="command">Image path.</param>
    /// <param name="arguments">Arguments.</param>
    /// <param name="highest">Run level Highest (engine) or Limited (UI).</param>
    /// <param name="delay">Logon delay.</param>
    /// <param name="endUtc">End boundary.</param>
    public static string TaskXml(string playerSid, string command, string arguments, bool highest, TimeSpan? delay, DateTimeOffset endUtc)
    {
        static string E(string s) => SecurityElement.Escape(s) ?? string.Empty;
        var end = endUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var delayXml = delay is { } d ? "<Delay>PT" + ((int)d.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "S</Delay>" : string.Empty;
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>"
            + "<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
            + "<RegistrationInfo><Author>VTDiag</Author><Description>Continues a VTDiag fix after the restart you asked for. Removed automatically.</Description></RegistrationInfo>"
            + "<Triggers><LogonTrigger><EndBoundary>" + end + "</EndBoundary><Enabled>true</Enabled><UserId>" + E(playerSid) + "</UserId>" + delayXml + "</LogonTrigger></Triggers>"
            + "<Principals><Principal id=\"Author\"><UserId>" + E(playerSid) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>" + (highest ? "HighestAvailable" : "LeastPrivilege") + "</RunLevel></Principal></Principals>"
            + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
            + "<AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>"
            + "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><AllowStartOnDemand>false</AllowStartOnDemand>"
            + "<Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT30M</ExecutionTimeLimit>"
            + "<DeleteExpiredTaskAfter>PT1H</DeleteExpiredTaskAfter><Priority>7</Priority></Settings>"
            + "<Actions Context=\"Author\"><Exec><Command>\"" + E(command) + "\"</Command><Arguments>" + E(arguments) + "</Arguments></Exec></Actions>"
            + "</Task>";
    }

    private async Task<Tri> ProbeAsync(string id, CancellationToken ct)
    {
        try
        {
            return (await _probe.ProbeAsync(id, new Dictionary<string, string>(StringComparer.Ordinal), ct).ConfigureAwait(false)).State;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Tri.Unknown;
        }
    }

    private bool TryRegisterTasks(string sessionId, DateTimeOffset expiresUtc, PlayerContext player)
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
            dynamic root = service.GetFolder("\\");
            dynamic folder;
            try
            {
                folder = service.GetFolder(TaskFolder);
            }
            catch (COMException)
            {
                folder = root.CreateFolder(TaskFolder.TrimStart('\\'));
            }

            var arguments = "--resume " + sessionId;
            folder.RegisterTask(EngineTaskName, TaskXml(player.Sid, EngineExe, arguments, highest: true, delay: null, expiresUtc), TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
            folder.RegisterTask(UiTaskName, TaskXml(player.Sid, UiExe, arguments, highest: false, delay: TimeSpan.FromSeconds(15), expiresUtc), TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);

            // Read back: the task exists and its action is exactly our Program Files image with our arguments.
            return ReadBack(folder, EngineTaskName, EngineExe, arguments) && ReadBack(folder, UiTaskName, UiExe, arguments);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException)
        {
            return false;
        }
    }

    private static bool ReadBack(dynamic folder, string name, string exe, string arguments)
    {
        dynamic task = folder.GetTask(name);
        dynamic action = task.Definition.Actions.Item(1);
        string path = action.Path;
        string args = action.Arguments;
        return string.Equals(path.Trim('"'), exe, StringComparison.OrdinalIgnoreCase) && string.Equals(args, arguments, StringComparison.Ordinal);
    }

    private static void TryDeleteTasks()
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
            dynamic folder = service.GetFolder(TaskFolder);
            foreach (var name in new[] { EngineTaskName, UiTaskName })
            {
                try
                {
                    folder.DeleteTask(name, 0);
                }
                catch (COMException)
                {
                    // Not there.
                }
            }

            dynamic root = service.GetFolder("\\");
            root.DeleteFolder(TaskFolder.TrimStart('\\'), 0);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException)
        {
            // No folder (nothing registered) or no scheduler.
        }
    }

    private bool TryRegisterRunOnce(string sessionId, PlayerContext player)
    {
        try
        {
            using var key = Registry.Users.CreateSubKey(player.Sid + "\\" + RunOnceKey, writable: true);
            var value = "\"" + UiExe + "\" --resume " + sessionId;
            key.SetValue(RunOnceValue, value, RegistryValueKind.String);
            return string.Equals(key.GetValue(RunOnceValue) as string, value, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    private static bool TaskSchedulerAvailable()
    {
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException or TypeLoadException)
        {
            return false;
        }
    }

    private static bool KeyExists(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static int? DwordOf(RegistryKey hive, string path, string value)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(value) is int i ? i : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
        {
            throw new InvalidOperationException("restart.no_privilege", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
            {
                throw new InvalidOperationException("restart.no_privilege", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x00000002 }; // SE_PRIVILEGE_ENABLED
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException("restart.no_privilege", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint InitiateShutdownW(string? machineName, string? message, uint gracePeriod, uint flags, uint reason);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }
}
