using VTD.Platform.Sources;

namespace VTD.Collectors.Core;

/// <summary>
/// Per-scan WMI circuit breaker: after the first WMI timeout, every later WMI query or method call in the same scan
/// fails fast with <see cref="ReadStatus.TimedOut"/>, so one hung Winmgmt costs one timeout instead of one per query
/// (and collectors stay inside their budget with the registry facts they already read).
/// </summary>
/// <param name="inner">Queries.</param>
/// <param name="methods">Methods.</param>
public sealed class WmiBreaker(IWmiSource inner, IWmiMethodSource methods) : IWmiSource, IWmiMethodSource
{
    private int _timeouts;

    /// <summary>WMI timed out at least once in this scan.</summary>
    public bool Tripped => Volatile.Read(ref _timeouts) > 0;

    /// <inheritdoc />
    public WmiResult Query(string wmiNamespace, string wql, TimeSpan timeout)
    {
        if (Tripped)
        {
            return new WmiResult { Status = ReadStatus.TimedOut, Error = "wmi_unhealthy" };
        }

        var r = inner.Query(wmiNamespace, wql, timeout);
        Note(r.Status);
        return r;
    }

    /// <inheritdoc />
    public BcdRaw ReadBcd(TimeSpan timeout)
    {
        if (Tripped)
        {
            return new BcdRaw { Status = ReadStatus.TimedOut };
        }

        var r = methods.ReadBcd(timeout);
        Note(r.Status);
        return r;
    }

    /// <inheritdoc />
    public TpmWmiRaw ReadTpm(TimeSpan timeout)
    {
        if (Tripped)
        {
            return new TpmWmiRaw { Status = ReadStatus.TimedOut };
        }

        var r = methods.ReadTpm(timeout);
        Note(r.Status);
        return r;
    }

    private void Note(ReadStatus status)
    {
        if (status == ReadStatus.TimedOut)
        {
            Interlocked.Increment(ref _timeouts);
        }
    }
}
