using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using VTD.Platform.Sources;

namespace VTD.Platform.Wmi;

/// <summary>
/// Live WQL queries. Each query runs on a worker with a hard timeout, so a hung WMI provider (Winmgmt is a common victim
/// on broken PCs) costs at most the timeout and never blocks a collector forever.
/// </summary>
public sealed class WmiSource : IWmiSource
{
    /// <inheritdoc />
    public WmiResult Query(string wmiNamespace, string wql, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(wmiNamespace);
        ArgumentNullException.ThrowIfNull(wql);
        if (!wql.TrimStart().StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase))
        {
            return new WmiResult { Status = ReadStatus.Failed, Error = "not_a_select" };
        }

        var task = Task.Run(() => Run(wmiNamespace, wql, timeout));
        try
        {
            return task.Wait(timeout + TimeSpan.FromMilliseconds(500))
                ? task.Result
                : new WmiResult { Status = ReadStatus.TimedOut, Error = "timeout" };
        }
        catch (AggregateException e)
        {
            return Map(e.InnerException ?? e);
        }
    }

    internal static WmiResult Run(string wmiNamespace, string wql, TimeSpan timeout)
    {
        try
        {
            var scope = new ManagementScope(wmiNamespace, new ConnectionOptions { Timeout = timeout });
            scope.Connect();
            var options = new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false, DirectRead = false };
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), options);
            using var results = searcher.Get();
            var rows = new List<IReadOnlyDictionary<string, string>>();
            foreach (var item in results)
            {
                using (item)
                {
                    var row = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in item.Properties)
                    {
                        var v = FormatValue(p.Value);
                        if (v is not null)
                        {
                            row[p.Name] = v;
                        }
                    }

                    rows.Add(row);
                }
            }

            return new WmiResult { Status = ReadStatus.Ok, Rows = rows };
        }
        catch (Exception e)
        {
            return Map(e);
        }
    }

    internal static WmiResult Map(Exception e) => e switch
    {
        ManagementException me when me.ErrorCode == ManagementStatus.AccessDenied => new WmiResult { Status = ReadStatus.AccessDenied, Error = "wbem_access_denied" },
        ManagementException me when me.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.NotFound
            => new WmiResult { Status = ReadStatus.NotFound, Error = "wbem_" + me.ErrorCode.ToString() },
        ManagementException me when me.ErrorCode == ManagementStatus.Timedout => new WmiResult { Status = ReadStatus.TimedOut, Error = "wbem_timeout" },
        ManagementException me => new WmiResult { Status = ReadStatus.Failed, Error = "wbem_" + me.ErrorCode.ToString() },
        UnauthorizedAccessException => new WmiResult { Status = ReadStatus.AccessDenied, Error = "access_denied" },
        COMException ce when ce.HResult == unchecked((int)0x80070005) => new WmiResult { Status = ReadStatus.AccessDenied, Error = "access_denied" },
        COMException ce => new WmiResult { Status = ReadStatus.Failed, Error = "hresult_0x" + ce.HResult.ToString("x8", CultureInfo.InvariantCulture) },
        _ => new WmiResult { Status = ReadStatus.Failed, Error = e.GetType().Name },
    };

    internal static string? FormatValue(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        Array a => string.Join(',', a.Cast<object?>().Select(x => FormatValue(x) ?? string.Empty)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
