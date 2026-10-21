using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Platform.Sources;

namespace VTD.Collectors.Core;

/// <summary>
/// Per-scan shared state: the sources, the player, the privilege mode and memoized reads that several collectors need
/// (process snapshot, service list, PnP devices). Per-user data always goes through <see cref="Player"/>
/// (<c>HKU\&lt;sid&gt;</c> and the player's folders), never through the engine's own profile or HKCU.
/// </summary>
public sealed class ScanEnvironment
{
    private readonly Lazy<EnvRaw> _env;
    private readonly Lazy<ProcessList> _processes;
    private readonly Lazy<ServiceList> _services;
    private readonly Lazy<DeviceList> _devices;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<object>> _memo = new(StringComparer.Ordinal);

    /// <summary>Creates the environment.</summary>
    /// <param name="sources">Sources (live, capturing or replay).</param>
    /// <param name="context">Collect context.</param>
    public ScanEnvironment(SystemSources sources, CollectContext context)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(context);
        var breaker = new WmiBreaker(sources.Wmi, sources.WmiMethods);
        Sources = sources with { Wmi = breaker, WmiMethods = breaker };
        Context = context;
        _env = new Lazy<EnvRaw>(sources.System.GetEnvironment, LazyThreadSafetyMode.ExecutionAndPublication);
        _ = breaker;
        _processes = new Lazy<ProcessList>(sources.System.GetProcesses, LazyThreadSafetyMode.ExecutionAndPublication);
        _services = new Lazy<ServiceList>(sources.System.GetServices, LazyThreadSafetyMode.ExecutionAndPublication);
        _devices = new Lazy<DeviceList>(() => sources.System.GetDevices(PhantomClasses), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Classes whose non-present devices are kept (leftover GPU / NIC / audio drivers).</summary>
    public static IReadOnlyList<string> PhantomClasses { get; } =
    [
        DeviceClasses.Display, DeviceClasses.Net, DeviceClasses.Media,
    ];

    /// <summary>Sources.</summary>
    public SystemSources Sources { get; }

    /// <summary>Context.</summary>
    public CollectContext Context { get; }

    /// <summary>Request.</summary>
    public ScanRequest Request => Context.Request;

    /// <summary>The interactive player.</summary>
    public PlayerContext Player => Context.Request.Player;

    /// <summary>Privilege.</summary>
    public PrivilegeMode Privilege => Context.Privilege;

    /// <summary>Elevated.</summary>
    public bool Elevated => Context.Privilege == PrivilegeMode.Elevated;

    /// <summary>Scan start.</summary>
    public DateTimeOffset StartedUtc => Context.StartedUtc;

    /// <summary>Environment facts.</summary>
    public EnvRaw Env => _env.Value;

    /// <summary>Memoized process snapshot.</summary>
    public ProcessList Processes => _processes.Value;

    /// <summary>Memoized service list.</summary>
    public ServiceList Services => _services.Value;

    /// <summary>Memoized PnP devices.</summary>
    public DeviceList Devices => _devices.Value;

    /// <summary>Windows directory (for example <c>C:\Windows</c>).</summary>
    public string WindowsDir => Env.WindowsDir;

    /// <summary>System drive with colon (for example <c>C:</c>).</summary>
    public string SystemDrive => WindowsDir.Length >= 2 && WindowsDir[1] == ':' ? WindowsDir[..2].ToUpperInvariant() : "C:";

    /// <summary>Computes a value once per scan (shared between collectors).</summary>
    /// <typeparam name="T">Type.</typeparam>
    /// <param name="key">Cache key.</param>
    /// <param name="factory">Factory.</param>
    public T Memo<T>(string key, Func<T> factory)
        where T : class
        => (T)_memo.GetOrAdd(key, _ => new Lazy<object>(() => factory(), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Reads an HKLM key.</summary>
    /// <param name="path">Path.</param>
    public RegKeyData Hklm(string path) => Sources.Registry.ReadKey(RegHive.LocalMachine, path);

    /// <summary>Reads a key of the player's hive (<c>HKU\&lt;sid&gt;\path</c>).</summary>
    /// <param name="path">Path under the player's hive.</param>
    public RegKeyData Hkcu(string path) => Sources.Registry.ReadKey(RegHive.Users, Player.Sid + "\\" + path);

    /// <summary>Maps a read status to an unknown reason, honouring limited mode (AccessDenied there means NeedsAdmin).</summary>
    /// <param name="status">Status.</param>
    public UnknownReason ReasonOf(ReadStatus status) => status switch
    {
        ReadStatus.AccessDenied => Elevated ? UnknownReason.AccessDenied : UnknownReason.NeedsAdmin,
        ReadStatus.TimedOut => UnknownReason.TimedOut,
        ReadStatus.Unsupported => UnknownReason.Unsupported,
        ReadStatus.NotFound => UnknownReason.Unsupported,
        _ => UnknownReason.Failed,
    };
}

/// <summary>Invariant device setup class GUIDs (lowercase, braces).</summary>
public static class DeviceClasses
{
    /// <summary>Display adapters.</summary>
    public const string Display = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Network adapters.</summary>
    public const string Net = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Sound, video and game controllers.</summary>
    public const string Media = "{4d36e96c-e325-11ce-bfc1-08002be10318}";

    /// <summary>Storage controllers (SCSI/RAID/NVMe).</summary>
    public const string ScsiAdapter = "{4d36e97b-e325-11ce-bfc1-08002be10318}";

    /// <summary>IDE ATA/ATAPI controllers (AHCI).</summary>
    public const string Hdc = "{4d36e96a-e325-11ce-bfc1-08002be10318}";

    /// <summary>Security devices (TPM).</summary>
    public const string SecurityDevices = "{d94ee5d8-d189-4994-83d2-f68d7d41b0e6}";

    /// <summary>Disk drives.</summary>
    public const string DiskDrive = "{4d36e967-e325-11ce-bfc1-08002be10318}";
}
