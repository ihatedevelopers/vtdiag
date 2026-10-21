using System.Globalization;
using Microsoft.Win32;
using VTD.Contracts;
using VTD.Platform.Sources;

namespace VTD.Engine.Platform;

/// <summary>
/// Reads the current boot identity: <c>PrefetchParameters\BootId</c>, the boot time (now minus the tick count, to the
/// second; unchanged by a resume from hybrid shutdown), and the Kernel-Boot event 27 boot type (0 = cold boot,
/// 1 = hybrid boot / Fast Startup, 2 = resume from hibernation).
/// </summary>
public sealed class BootReader(TimeProvider clock, IEventLogSource events)
{
    private const string PrefetchKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    /// <summary>Reads the boot identity.</summary>
    public Fact<BootIdentity> Read()
    {
        const string source = "engine/registry:PrefetchParameters\\BootId";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PrefetchKey);
            if (key?.GetValue("BootId") is not int bootId)
            {
                return Fact<BootIdentity>.Unknown(UnknownReason.Unsupported, null, source);
            }

            var now = clock.GetUtcNow();
            var bootTime = now.AddMilliseconds(-Environment.TickCount64);
            bootTime = new DateTimeOffset(bootTime.Ticks - (bootTime.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
            var kind = BootKind.Unknown;
            var result = events.Query(
                new EventQuery { Log = "System", Providers = ["Microsoft-Windows-Kernel-Boot"], EventIds = [27], SinceUtc = bootTime.AddMinutes(-10), Max = 1, Fields = ["BootType"] },
                CancellationToken.None);
            if (result.Status == ReadStatus.Ok && result.Records.Count > 0
                && int.TryParse(result.Records[0].Data.GetValueOrDefault("BootType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bootType))
            {
                kind = bootType == 0 ? BootKind.Cold : BootKind.FastStartupResume;
            }

            return Fact<BootIdentity>.Known(new BootIdentity { BootId = unchecked((uint)bootId), LastBootUpTimeUtc = bootTime, Kind = kind }, source);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Fact<BootIdentity>.Unknown(UnknownReason.Failed, "exception:" + ex.GetType().Name, source);
        }
    }
}
