using System.Text.Json.Serialization;

namespace VTD.Contracts;

/// <summary>Whether the engine that produced data ran elevated.</summary>
public enum PrivilegeMode
{
    /// <summary>Elevated engine: full scan, fixes available.</summary>
    Elevated = 0,

    /// <summary>Limited mode (UAC declined): admin-only facts are Unknown(NeedsAdmin), no fixes, nothing written to %ProgramData%.</summary>
    Limited = 1,
}

/// <summary>How the current boot started, as far as Windows tells us.</summary>
public enum BootKind
{
    /// <summary>Not determined.</summary>
    Unknown = 0,

    /// <summary>A full (cold) boot or a real restart.</summary>
    Cold = 1,

    /// <summary>Resume from hybrid shutdown (Fast Startup): the kernel session was NOT restarted.</summary>
    FastStartupResume = 2,
}

/// <summary>
/// Identifies one boot of Windows. Used to tell "my reboot happened" from "the user chose Shut down with Fast Startup"
/// and from "Windows Update rebooted the PC" (DESIGN.md section 2, reboot-resume).
/// </summary>
public sealed record BootIdentity
{
    /// <summary><c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters\BootId</c>.</summary>
    public required uint BootId { get; init; }

    /// <summary>Last boot-up time (UTC).</summary>
    public required DateTimeOffset LastBootUpTimeUtc { get; init; }

    /// <summary>Boot kind (Kernel-Boot event 27 boot type when available).</summary>
    public BootKind Kind { get; init; }
}

/// <summary>
/// The honesty buckets (DESIGN.md section 1). The report always says which bucket the player is in, and we never claim
/// to fix what a PC tool cannot.
/// </summary>
public enum HonestyBucket
{
    /// <summary>Fixable here: an audited, consented, undoable fix exists and applies.</summary>
    FixableHere = 0,

    /// <summary>Guided manual step (BIOS, Secure Boot, uninstall, driver update): the player acts, we guide and verify.</summary>
    GuidedManual = 1,

    /// <summary>ISP or network outside this PC.</summary>
    ExternalNetwork = 2,

    /// <summary>Riot or account side (server incident, account-specific errors, HWID ban appeal).</summary>
    RiotOrAccount = 3,

    /// <summary>Likely hardware (failing part, thermal, hardware that cannot meet a requirement).</summary>
    LikelyHardware = 4,

    /// <summary>Unknown: we could not pin it down; offer the escalation ladder and a helper export.</summary>
    Unknown = 5,
}

/// <summary>Short-circuit verdicts that end ranking for a reason outside the PC (design_A.md section 6.7, critique section 2 row 7).</summary>
public enum RouteKind
{
    /// <summary>HWID ban (VAN 152): appeal only. Never offer fixes, spoofers or workarounds.</summary>
    HwidBanAppeal = 0,

    /// <summary>Account-side failure (VAL 43/57/59 and similar): "try another account on this PC".</summary>
    TryAnotherAccount = 1,

    /// <summary>Console, Mac, ARM, VM, cloud gaming: out of scope for a Windows PC tool.</summary>
    UnsupportedPlatform = 2,

    /// <summary>Riot server-side incident or maintenance.</summary>
    RiotServerIncident = 3,

    /// <summary>The hardware cannot meet a Vanguard requirement (for example no TPM 2.0, HVCI-incompatible platform).</summary>
    HardwareCannotComply = 4,

    /// <summary>Riot support ticket with logs is the only route.</summary>
    RiotSupportTicket = 5,
}

/// <summary>Problem category from <c>data/catalog.json</c>. JSON values are the catalog's snake_case names.</summary>
public enum ProblemCategory
{
    /// <summary>FPS, stutter, frame time.</summary>
    [JsonStringEnumMemberName("fps_performance")] FpsPerformance,

    /// <summary>Vanguard errors and service problems.</summary>
    [JsonStringEnumMemberName("vanguard")] Vanguard,

    /// <summary>Crashes, freezes, BSODs.</summary>
    [JsonStringEnumMemberName("crash_freeze_bsod")] CrashFreezeBsod,

    /// <summary>TPM, Secure Boot, BIOS requirements.</summary>
    [JsonStringEnumMemberName("tpm_secureboot_bios")] TpmSecureBootBios,

    /// <summary>Install, update, corrupt files.</summary>
    [JsonStringEnumMemberName("install_update_corrupt")] InstallUpdateCorrupt,

    /// <summary>Login, account, Riot Client.</summary>
    [JsonStringEnumMemberName("login_account_client")] LoginAccountClient,

    /// <summary>Network, ping, packet loss.</summary>
    [JsonStringEnumMemberName("network_ping")] NetworkPing,

    /// <summary>GPU driver.</summary>
    [JsonStringEnumMemberName("gpu_driver")] GpuDriver,

    /// <summary>Audio and microphone.</summary>
    [JsonStringEnumMemberName("audio_mic")] AudioMic,

    /// <summary>Display and resolution.</summary>
    [JsonStringEnumMemberName("display_resolution")] DisplayResolution,

    /// <summary>Input and mouse.</summary>
    [JsonStringEnumMemberName("input_mouse")] InputMouse,

    /// <summary>Other hardware.</summary>
    [JsonStringEnumMemberName("hardware_other")] HardwareOther,

    /// <summary>Everything else.</summary>
    [JsonStringEnumMemberName("other")] Other,
}

/// <summary>Confidence label carried over from the research data.</summary>
public enum ConfidenceLevel
{
    /// <summary>Low.</summary>
    Low = 0,

    /// <summary>Medium.</summary>
    Medium = 1,

    /// <summary>High.</summary>
    High = 2,
}

/// <summary>
/// Ranking constants fixed by DESIGN.md / critique section 2 so Analysis (A2), UI and report (A6) and the CI accuracy
/// gate agree. Changing one is a Contracts change.
/// </summary>
public static class RankingConstants
{
    /// <summary>Posterior at or above which a hypothesis is "Very likely" (design_A.md section 8.2).</summary>
    public const double VeryLikely = 0.80;

    /// <summary>Posterior at or above which a hypothesis is "Likely".</summary>
    public const double Likely = 0.50;

    /// <summary>Posterior at or above which a hypothesis is "Possible"; below is "Long shot".</summary>
    public const double Possible = 0.20;

    /// <summary>
    /// The fixed "unknown cause" floor (probability), never derived by subtraction (critique A3). With independent
    /// posteriors P(h), the unknown cause is <c>P(unknown) = max(UnknownCauseFloor, Π over surviving h of (1 − P(h)))</c>,
    /// and it is <c>Leading</c> when <c>P(unknown) ≥ max P(h)</c> or <c>max P(h) &lt; PinDownThreshold</c>.
    /// </summary>
    public const double UnknownCauseFloor = 0.15;

    /// <summary>When the top hypothesis is below this, the UI says "We couldn't pin this down" (design_B.md section 5).</summary>
    public const double PinDownThreshold = 0.35;

    /// <summary>The unknown-cause probability for a set of independent posteriors (see <see cref="UnknownCauseFloor"/>).</summary>
    /// <param name="posteriors">Posteriors of the surviving hypotheses.</param>
    public static double UnknownCauseProbability(IEnumerable<double> posteriors)
    {
        ArgumentNullException.ThrowIfNull(posteriors);
        var none = 1.0;
        foreach (var p in posteriors)
        {
            none *= 1.0 - Math.Clamp(p, 0.0, 1.0);
        }

        return Math.Max(UnknownCauseFloor, none);
    }

    /// <summary>The display band of a posterior.</summary>
    /// <param name="posterior">Posterior 0..1.</param>
    public static ConfidenceBand BandOf(double posterior) => posterior switch
    {
        >= VeryLikely => ConfidenceBand.VeryLikely,
        >= Likely => ConfidenceBand.Likely,
        >= Possible => ConfidenceBand.Possible,
        _ => ConfidenceBand.LongShot,
    };

    /// <summary>Ask a question only when the top two are within this many log-odds and a question separates them.</summary>
    public const double QuestionMarginLogOdds = 1.0;

    /// <summary>At most this many intake questions per session (DESIGN.md section 2).</summary>
    public const int MaxQuestions = 3;

    /// <summary>Cap on the total absolute evidence log-odds per hypothesis: ln(10^4) (no runaway certainty).</summary>
    public const double MaxEvidenceLogOdds = 9.210340371976184;
}

/// <summary>Display band of a posterior. Players see bands, never fake-precise percentages.</summary>
public enum ConfidenceBand
{
    /// <summary>Posterior below <see cref="RankingConstants.Possible"/>.</summary>
    LongShot = 0,

    /// <summary>Posterior at or above <see cref="RankingConstants.Possible"/>.</summary>
    Possible = 1,

    /// <summary>Posterior at or above <see cref="RankingConstants.Likely"/>.</summary>
    Likely = 2,

    /// <summary>Posterior at or above <see cref="RankingConstants.VeryLikely"/>.</summary>
    VeryLikely = 3,
}

/// <summary>UI and KB languages. Machine keys are always English; the helper-facing report section defaults to English.</summary>
public static class Languages
{
    /// <summary>English (default, fallback).</summary>
    public const string English = "en";

    /// <summary>Turkish.</summary>
    public const string Turkish = "tr";

    /// <summary>All supported languages.</summary>
    public static IReadOnlyList<string> All { get; } = [English, Turkish];
}

/// <summary>
/// Riot and Vanguard process image names. No VTDiag code ever opens a handle to, injects into, or reads memory of these
/// (rule S8); they are matched by name only, ordinal and case-insensitive.
/// </summary>
public static class RiotProcessNames
{
    /// <summary>The game process.</summary>
    public const string Game = "VALORANT-Win64-Shipping.exe";

    /// <summary>The game launcher stub.</summary>
    public const string GameLauncher = "VALORANT.exe";

    /// <summary>Vanguard user-mode service.</summary>
    public const string Vgc = "vgc.exe";

    /// <summary>Vanguard tray.</summary>
    public const string VgTray = "vgtray.exe";

    /// <summary>Vanguard helper.</summary>
    public const string Vgm = "vgm.exe";

    /// <summary>Riot Client services.</summary>
    public const string RiotClientServices = "RiotClientServices.exe";

    /// <summary>All protected names (game, Vanguard, Riot Client, League).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Game, GameLauncher, Vgc, VgTray, Vgm, RiotClientServices,
        "RiotClientUx.exe", "RiotClientUxRender.exe", "RiotClientCrashHandler.exe", "Riot Client.exe",
        "LeagueClient.exe", "LeagueClientUx.exe", "LeagueClientUxRender.exe", "League of Legends.exe",
    ];

    /// <summary>True when <paramref name="imageName"/> is a protected Riot/Vanguard process name.</summary>
    /// <param name="imageName">Executable file name (no path).</param>
    public static bool IsProtected(string? imageName)
        => imageName is not null && All.Any(n => string.Equals(n, imageName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Ids of live guard probes (<see cref="Scanning.IFactProbe"/>, implemented by A1, consumed by A3's FixRunner,
/// referenced from fix JSON by A2). A guard is re-evaluated with a FRESH probe immediately before apply (S3).
/// Arguments are invariant strings; <c>$bind.id</c> references are resolved before probing.
/// </summary>
public static class GuardIds
{
    /// <summary>VALORANT is running (by process name). Every persistent fix requires False (S9).</summary>
    public const string GameRunning = "riot.game_running";

    /// <summary>The Riot Client is running.</summary>
    public const string ClientRunning = "riot.client_running";

    /// <summary>A Windows reboot is pending (CBS, WU, PendingFileRenameOperations). Blocks applies (S9).</summary>
    public const string PendingReboot = "os.pending_reboot";

    /// <summary>A Windows Update installation is in progress. Blocks applies (S9).</summary>
    public const string WindowsUpdateInstalling = "os.wu_installing";

    /// <summary>On AC power. Gates the reboot prompt and long operations (S10).</summary>
    public const string OnAcPower = "power.on_ac";

    /// <summary>Battery above <c>args.percent</c> (or on AC).</summary>
    public const string BatteryAbove = "power.battery_above";

    /// <summary>System drive has more than <c>args.mb</c> free (default 200 MB before any apply).</summary>
    public const string DiskFreeAbove = "disk.free_mb_above";

    /// <summary>The live monitor is running (fixes are refused while monitoring).</summary>
    public const string MonitorActive = "monitor.active";

    /// <summary>Adapter <c>args.instanceId</c> is not the default-route adapter.</summary>
    public const string AdapterNotDefaultRoute = "adapter.not_default_route";

    /// <summary>Another physical adapter than <c>args.exceptInstanceId</c> is up with internet.</summary>
    public const string OtherAdapterOnline = "connectivity.other_adapter_online";

    /// <summary>NLM reports internet connectivity. Used by deadman checks.</summary>
    public const string InternetReachable = "connectivity.internet";

    /// <summary>Service <c>args.name</c> exists.</summary>
    public const string ServiceExists = "service.exists";

    /// <summary>BitLocker protects the system volume (firmware/boot changes need the recovery key first, S11).</summary>
    public const string BitLockerOn = "sec.bitlocker_on";

    /// <summary>The Windows image looks modified (Atlas, Tiny11...). Warn-only.</summary>
    public const string ModifiedWindows = "os.modified_windows";

    /// <summary>
    /// The PC clock is within <c>args.maxSkewSeconds</c> (default 300) of a reference time; evidence arg <c>skewSeconds</c>.
    /// A4's startup order: probe this FIRST, offer a resync (or carry the measured skew), then load and verify the KB and
    /// plans with the corrected time, then scan (DESIGN.md: clock skew is handled before signature and expiry checks).
    /// </summary>
    public const string ClockOk = "os.clock_ok";

    /// <summary>All guard ids above.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        GameRunning, ClientRunning, PendingReboot, WindowsUpdateInstalling, OnAcPower, BatteryAbove, DiskFreeAbove,
        MonitorActive, AdapterNotDefaultRoute, OtherAdapterOnline, InternetReachable, ServiceExists, BitLockerOn,
        ModifiedWindows, ClockOk,
    ];
}
