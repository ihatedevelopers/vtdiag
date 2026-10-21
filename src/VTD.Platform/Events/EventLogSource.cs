using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using VTD.Platform.Sources;

namespace VTD.Platform.Events;

/// <summary>
/// Live event-log reads with EventLogReader and an XPath filter on provider, id, level and time. Only the projected
/// EventData fields leave this class; the rendered (localized) message is never formatted.
/// </summary>
public sealed class EventLogSource : IEventLogSource
{
    /// <inheritdoc />
    public EventQueryResult Query(EventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var elq = new EventLogQuery(query.Log, PathType.LogName, BuildXPath(query)) { ReverseDirection = true, TolerateQueryErrors = true };
            using var reader = new EventLogReader(elq);
            var records = new List<EventRec>();
            var fields = new HashSet<string>(query.Fields, StringComparer.Ordinal);
            var truncated = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                if (records.Count >= query.Max)
                {
                    truncated = true;
                    break;
                }

                records.Add(new EventRec
                {
                    Provider = record.ProviderName ?? string.Empty,
                    Id = record.Id,
                    Level = record.Level ?? 0,
                    Utc = new DateTimeOffset((record.TimeCreated ?? DateTime.MinValue).ToUniversalTime(), TimeSpan.Zero),
                    Data = fields.Count == 0 ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty : Project(record, fields),
                });
            }

            return new EventQueryResult { Status = ReadStatus.Ok, Records = records, Truncated = truncated };
        }
        catch (EventLogNotFoundException)
        {
            return new EventQueryResult { Status = ReadStatus.NotFound, Error = "log_not_found" };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException)
        {
            return new EventQueryResult { Status = ReadStatus.AccessDenied, Error = "access_denied" };
        }
        catch (EventLogException e)
        {
            return new EventQueryResult { Status = ReadStatus.Failed, Error = "eventlog_0x" + e.HResult.ToString("x8", CultureInfo.InvariantCulture) };
        }
    }

    /// <summary>Builds the XPath filter.</summary>
    /// <param name="q">Query.</param>
    public static string BuildXPath(EventQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var parts = new List<string>();
        if (q.Providers.Count > 0)
        {
            parts.Add("(" + string.Join(" or ", q.Providers.Select(p => "Provider[@Name='" + Escape(p) + "']")) + ")");
        }

        if (q.EventIds.Count > 0)
        {
            parts.Add("(" + string.Join(" or ", q.EventIds.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture))) + ")");
        }

        if (q.Levels.Count > 0)
        {
            parts.Add("(" + string.Join(" or ", q.Levels.Select(l => "Level=" + l.ToString(CultureInfo.InvariantCulture))) + ")");
        }

        parts.Add("TimeCreated[@SystemTime>='" + q.SinceUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "']");
        return "*[System[" + string.Join(" and ", parts) + "]]";
    }

    private static string Escape(string s) => s.Replace("'", string.Empty, StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, string> Project(EventRecord record, HashSet<string> fields)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        string xml;
        try
        {
            xml = record.ToXml();
        }
        catch (EventLogException)
        {
            return data;
        }

        try
        {
            ProjectXml(xml, fields, data);
        }
        catch (XmlException)
        {
            data["#xml"] = "unparseable";
        }

        return data;
    }

    private static void ProjectXml(string xml, HashSet<string> fields, Dictionary<string, string> data)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var index = 0;
        var inEventData = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && (reader.LocalName is "EventData" or "UserData"))
            {
                inEventData = true;
                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement && (reader.LocalName is "EventData" or "UserData"))
            {
                inEventData = false;
                continue;
            }

            if (!inEventData || reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            string name;
            if (reader.LocalName == "Data")
            {
                name = reader.GetAttribute("Name") ?? "#" + index.ToString(CultureInfo.InvariantCulture);
                index++;
            }
            else
            {
                name = reader.LocalName;
            }

            if (!fields.Contains(name))
            {
                continue;
            }

            var value = reader.IsEmptyElement ? string.Empty : ReadText(reader);
            data[name] = value.Length > 512 ? value[..512] : value;
        }
    }

    private static string ReadText(XmlReader reader)
    {
        var sb = new StringBuilder();
        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
            {
                sb.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }
        }

        return sb.ToString().Trim();
    }
}
