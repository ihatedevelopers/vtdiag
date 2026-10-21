using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Snapshots;

namespace VTD.Analysis.Signals;

/// <summary>
/// Matches one <see cref="ConflictDef"/> against a snapshot (DESIGN.md: one generic provider for every conflict entry).
/// Process, service, driver and module names are compared ordinal case-insensitive; regexes are NonBacktracking with the
/// pack-collector timeout and never match localized names (adapter descriptions and DisplayNames come from vendors).
/// </summary>
public static class ConflictMatcher
{
    /// <summary>Signal suffixes computed from detect fields.</summary>
    public static readonly IReadOnlyList<string> Suffixes =
        ["installed", "running", "service_present", "service_running", "driver_loaded", "adapter_present", "adapter_enabled", "crash_module", "audio_endpoint", "feature_enabled"];

    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    /// <summary>Crash window for faulting-module matches.</summary>
    public static readonly TimeSpan CrashWindow = TimeSpan.FromDays(30);

    /// <summary>Evaluates the conflict's signals (only those whose detect field is set) plus <c>.any</c>.</summary>
    /// <param name="c">Conflict.</param>
    /// <param name="s">Snapshot.</param>
    /// <param name="asOf">Analysis time.</param>
    /// <param name="kb">KB (for collector-derived registry/file signals).</param>
    public static IReadOnlyList<SignalResult> Evaluate(ConflictDef c, ScanSnapshot s, DateTimeOffset asOf, IKnowledgeBase? kb = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(s);
        var p = "conflict." + c.Id + ".";
        var d = c.Detect;
        var parts = new List<SignalResult>();
        if (d.UninstallDisplayNameRegex is { } appRx)
        {
            parts.Add(Sig.FromFact(p + "installed", s.Apps, apps =>
            {
                var rx = Compile(appRx);
                var hits = apps.Where(a => IsMatch(rx, a.DisplayName)).OrderBy(a => a.KeyName, StringComparer.Ordinal).ToList();
                return hits.Count == 0 ? Sig.False(p + "installed") : Sig.True(p + "installed", 1.0,
                    [Sig.EvKey("evidence.conflict.installed", EvidenceSource.Scan, ("name", c.Name), ("items", Sig.List(hits.Select(h => h.DisplayName))))],
                    [.. hits.Select(h => new EntityRef { Kind = EntityKind.App, Id = HiveTag(h.Hive) + ":" + h.KeyName, DisplayName = h.DisplayName })]);
            }));
        }

        if (d.ProcessNames.Count > 0)
        {
            parts.Add(Sig.FromFact(p + "running", s.Processes, procs =>
            {
                var hits = procs.Where(x => !x.IsProtectedRiotProcess && d.ProcessNames.Contains(x.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
                return hits.Count == 0 ? Sig.False(p + "running") : Sig.True(p + "running", 1.0,
                    [Sig.EvKey("evidence.conflict.running", EvidenceSource.Scan, ("name", c.Name), ("items", Sig.List(hits)))],
                    [.. hits.Select(h => new EntityRef { Kind = EntityKind.Process, Id = h, DisplayName = h })]);
            }));
        }

        if (d.ServiceNames.Count > 0)
        {
            parts.Add(Sig.FromFact(p + "service_present", s.Services, svcs => Services(p + "service_present", c, svcs, running: false)));
            parts.Add(Sig.FromFact(p + "service_running", s.Services, svcs => Services(p + "service_running", c, svcs, running: true)));
        }

        if (d.DriverNames.Count > 0)
        {
            parts.Add(Sig.FromFact(p + "driver_loaded", s.Drivers, drivers =>
            {
                var hits = drivers.Where(x => d.DriverNames.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).Select(x => x.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
                return hits.Count == 0 ? Sig.False(p + "driver_loaded") : Sig.True(p + "driver_loaded", 1.0,
                    [Sig.EvKey("evidence.conflict.driver", EvidenceSource.Scan, ("name", c.Name), ("items", Sig.List(hits)))],
                    [.. hits.Select(h => new EntityRef { Kind = EntityKind.Driver, Id = h, DisplayName = h })]);
            }));
        }

        if (d.NetworkAdapterRegex is { } nicRx)
        {
            var rx = Compile(nicRx);
            parts.Add(Sig.FromFact(p + "adapter_present", s.Network.Adapters, nics => Adapters(p + "adapter_present", c, nics.Where(n => IsMatch(rx, n.Description)).ToList(), enabledOnly: false)));
            parts.Add(Sig.FromFact(p + "adapter_enabled", s.Network.Adapters, nics => Adapters(p + "adapter_enabled", c, nics.Where(n => IsMatch(rx, n.Description)).ToList(), enabledOnly: true)));
        }

        if (d.ModuleNames.Count > 0)
        {
            parts.Add(Sig.FromFact(p + "crash_module", s.Events.AppCrashes, crashes =>
            {
                var hits = crashes.Where(x => x.Module is not null && x.Utc >= asOf - CrashWindow && d.ModuleNames.Contains(x.Module, StringComparer.OrdinalIgnoreCase)).ToList();
                return hits.Count == 0 ? Sig.False(p + "crash_module") : Sig.True(p + "crash_module", Math.Min(1.0, hits.Count / 2.0),
                    [Sig.EvKey("evidence.conflict.crash", EvidenceSource.Log, ("name", c.Name), ("items", Sig.List(hits.Select(h => h.Module!))), ("count", Sig.Num(hits.Count)))]);
            }));
        }

        if (d.AudioEndpointRegex is { } audioRx)
        {
            var rx = Compile(audioRx);
            var handsFree = audioRx.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);
            parts.Add(Sig.FromFact(p + "audio_endpoint", s.Audio.Endpoints, eps =>
            {
                var hits = eps.Where(e => (handsFree && e.IsBluetoothHandsFree) || IsMatch(rx, e.Name)).ToList();
                return hits.Count == 0 ? Sig.False(p + "audio_endpoint") : Sig.True(p + "audio_endpoint", 1.0,
                    [Sig.EvKey("evidence.conflict.audio", EvidenceSource.Scan, ("name", c.Name), ("count", Sig.Num(hits.Count)))],
                    [.. hits.Where(h => h.DeviceInstanceId is not null).Select(h => new EntityRef { Kind = EntityKind.Device, Id = h.DeviceInstanceId!, DisplayName = h.Name }).DistinctBy(e => e.Id)]);
            }));
        }

        if (d.WindowsFeatures.Count > 0)
        {
            parts.Add(Sig.FromFact(p + "feature_enabled", s.OptionalFeatures, features =>
            {
                var hits = features.Where(f => d.WindowsFeatures.Contains(f, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
                return hits.Count == 0 ? Sig.False(p + "feature_enabled") : Sig.True(p + "feature_enabled", 1.0,
                    [Sig.EvKey("evidence.conflict.feature", EvidenceSource.Scan, ("name", c.Name), ("items", Sig.List(hits)))],
                    [.. hits.Select(h => new EntityRef { Kind = EntityKind.WindowsFeature, Id = h, DisplayName = h })]);
            }));
        }

        if (kb is not null)
        {
            foreach (var def in kb.Signals.Where(x => x.FromCollector is not null && x.Id.StartsWith(p, StringComparison.Ordinal)))
            {
                parts.Add(CollectorSignalProvider.Evaluate(def, s, asOf));
            }
        }

        var all = new List<SignalResult>(parts) { Sig.Any(p + "any", parts) };
        return all;
    }

    /// <summary>A NonBacktracking, case-insensitive, culture-invariant regex (null when the pattern is invalid: lint reports it).</summary>
    /// <param name="pattern">Pattern.</param>
    public static Regex? Compile(string pattern) => RegexCache.GetOrAdd(pattern, static p =>
    {
        try
        {
            return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, PackCollectorLimits.RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    });

    /// <summary>Safe match (timeouts count as no match).</summary>
    /// <param name="rx">Regex.</param>
    /// <param name="text">Text.</param>
    public static bool IsMatch(Regex? rx, string? text)
    {
        if (rx is null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            return rx.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static SignalResult Services(string id, ConflictDef c, IReadOnlyList<ServiceInfo> svcs, bool running)
    {
        var hits = svcs.Where(x => c.Detect.ServiceNames.Contains(x.Name, StringComparer.OrdinalIgnoreCase) && (!running || x.State == ServiceState.Running))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return hits.Count == 0 ? Sig.False(id) : Sig.True(id, 1.0,
            [Sig.EvKey("evidence.conflict.service", EvidenceSource.Scan, ("name", c.Name), ("items", Sig.List(hits.Select(h => h.Name))))],
            [.. hits.Select(h => new EntityRef { Kind = EntityKind.Service, Id = h.Name, DisplayName = h.DisplayName ?? h.Name })]);
    }

    private static SignalResult Adapters(string id, ConflictDef c, IReadOnlyList<NetAdapter> nics, bool enabledOnly)
    {
        var hits = nics.Where(n => !enabledOnly || n.Enabled).OrderBy(n => n.InstanceId, StringComparer.Ordinal).ToList();
        if (hits.Count == 0)
        {
            return enabledOnly && nics.Count == 0 ? Sig.NotApplicable(id) : Sig.False(id);
        }

        return Sig.True(id, 1.0, [Sig.EvKey("evidence.conflict.adapter", EvidenceSource.Scan, ("name", c.Name), ("items", Sig.List(hits.Select(h => h.Description))))],
            [.. hits.Select(h => new EntityRef { Kind = EntityKind.Adapter, Id = h.InstanceId, DisplayName = h.Description })]);
    }

    private static string HiveTag(AppHive hive) => hive switch { AppHive.Hklm => "HKLM", AppHive.Hklm32 => "HKLM32", _ => "HKCU" };
}

/// <summary>The generic provider: <c>conflict.&lt;id&gt;.*</c> for every conflict entry in the KB.</summary>
public sealed class ConflictSignalProvider : ISignalProvider
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Provides { get; } = ["conflict.*"];

    /// <inheritdoc />
    public IEnumerable<SignalResult> Evaluate(SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var declared = context.Kb.Signals.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var c in context.Kb.Conflicts.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            foreach (var r in ConflictMatcher.Evaluate(c, context.Snapshot, context.AsOfUtc, context.Kb))
            {
                if (declared.Contains(r.Id))
                {
                    yield return r;
                }
            }
        }
    }
}
