using System.Globalization;
using System.Management;
using Microsoft.Win32;
using VTD.Contracts.Sessions;

namespace VTD.Engine.Platform;

/// <summary>
/// The real <see cref="IRestorePointService"/> (tech_stack.md section 4.1): one restore point per session, before the
/// first medium/high-risk fix (A3's runner calls it). WMI <c>root\default:SystemRestore.CreateRestorePoint</c>, with the
/// 24-hour throttle lifted by setting <c>SystemRestorePointCreationFrequency</c> to 0 for the call and restoring the
/// previous value (or its absence) afterwards; the point is then looked up to verify it exists. System Protection off is
/// reported (enabling it is a consented fix), never changed here. Constructed only by an elevated Program Files engine;
/// tested only with <c>VTD_ALLOW_SYSTEM_WRITES=1</c>.
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";
    private const int ErrorServiceDisabled = 1058;

    private readonly TimeProvider _clock;
    private readonly Func<string, RestorePointInfo?> _known;
    private readonly Action<RestorePointInfo> _recorded;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the service.</summary>
    /// <param name="clock">Clock.</param>
    /// <param name="known">The restore point the session already has, if any.</param>
    /// <param name="recorded">Called with the result so the session persists it.</param>
    public RestorePointService(TimeProvider clock, Func<string, RestorePointInfo?> known, Action<RestorePointInfo> recorded)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _known = known ?? throw new ArgumentNullException(nameof(known));
        _recorded = recorded ?? throw new ArgumentNullException(nameof(recorded));
    }

    /// <inheritdoc />
    public async Task<RestorePointInfo> EnsureAsync(string sessionId, string reasonKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_known(sessionId) is { Status: RestorePointStatus.Created or RestorePointStatus.ContinuedWithout } existing)
            {
                return existing;
            }

            var info = await Task.Run(() => Create(sessionId), cancellationToken).ConfigureAwait(false);
            _recorded(info);
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    private RestorePointInfo Create(string sessionId)
    {
        var description = "VTDiag " + sessionId[..Math.Min(10, sessionId.Length)];
        var started = _clock.GetUtcNow();
        object? previous = null;
        var hadPrevious = false;
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKey, writable: true))
            {
                if (key is null)
                {
                    return new RestorePointInfo { Status = RestorePointStatus.Failed, MessageKey = "restorepoint.unavailable" };
                }

                if (key.GetValue("DisableSR") is int disabled && disabled != 0)
                {
                    return new RestorePointInfo { Status = RestorePointStatus.SystemProtectionOff, MessageKey = "restorepoint.system_protection_off" };
                }

                previous = key.GetValue(FrequencyValue);
                hadPrevious = previous is not null;
                key.SetValue(FrequencyValue, 0, RegistryValueKind.DWord);
            }

            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = 12; // MODIFY_SETTINGS
            input["EventType"] = 100; // BEGIN_SYSTEM_CHANGE
            using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToInt32(output?["ReturnValue"] ?? -1, CultureInfo.InvariantCulture);
            if (code == ErrorServiceDisabled)
            {
                return new RestorePointInfo { Status = RestorePointStatus.SystemProtectionOff, MessageKey = "restorepoint.system_protection_off" };
            }

            if (code != 0)
            {
                return new RestorePointInfo { Status = RestorePointStatus.Failed, MessageKey = "restorepoint.failed" };
            }

            return Verify(description, started);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidCastException or FormatException)
        {
            return new RestorePointInfo { Status = RestorePointStatus.Failed, MessageKey = "restorepoint.failed" };
        }
        finally
        {
            RestoreFrequency(hadPrevious, previous);
        }
    }

    private static RestorePointInfo Verify(string description, DateTimeOffset started)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"), new ObjectQuery("SELECT Description, SequenceNumber, CreationTime FROM SystemRestore"));
        long? sequence = null;
        DateTimeOffset? created = null;
        foreach (var point in searcher.Get().Cast<ManagementBaseObject>())
        {
            using (point)
            {
                if (!string.Equals(point["Description"] as string, description, StringComparison.Ordinal))
                {
                    continue;
                }

                var time = point["CreationTime"] is string wmi ? new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(wmi).ToUniversalTime(), TimeSpan.Zero) : (DateTimeOffset?)null;
                if (time is { } t && t >= started.AddMinutes(-5))
                {
                    sequence = Convert.ToInt64(point["SequenceNumber"], CultureInfo.InvariantCulture);
                    created = t;
                }
            }
        }

        return sequence is null
            ? new RestorePointInfo { Status = RestorePointStatus.Failed, MessageKey = "restorepoint.not_found" }
            : new RestorePointInfo { Status = RestorePointStatus.Created, SequenceNumber = sequence, CreatedUtc = created };
    }

    private static void RestoreFrequency(bool hadPrevious, object? previous)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (hadPrevious && previous is int value)
            {
                key.SetValue(FrequencyValue, value, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(FrequencyValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Best effort; the value only lifts a throttle.
        }
    }
}
