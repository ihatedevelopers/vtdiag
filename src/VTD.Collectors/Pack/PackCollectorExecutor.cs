using System.Globalization;
using System.Text.RegularExpressions;
using VTD.Collectors.Collectors;
using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Pack;

/// <summary>
/// Executes the KB's pack-declared READ-ONLY collectors (exactly six kinds) inside <see cref="PackCollectorLimits"/>.
/// A definition that breaks a limit is not run: its result is Unknown(Failed) with an <c>invalid:*</c> detail.
/// </summary>
public static partial class PackCollectorExecutor
{
    /// <summary>Runs one definition.</summary>
    /// <param name="env">Environment.</param>
    /// <param name="def">Definition.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static PackFactResult Run(ScanEnvironment env, CollectorDef def, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(def);
        var source = "pack/" + Kind(def);
        try
        {
            var data = def switch
            {
                EventLogCollectorDef e => EventLog(env, e, source, cancellationToken),
                UninstallRegexCollectorDef u => Uninstall(env, u, source),
                LoadedDriverCollectorDef d => Drivers(env, d, source),
                FaultingModuleCollectorDef f => FaultingModule(env, f, source, cancellationToken),
                RegistryValueCollectorDef r => RegistryValue(env, r, source),
                FileExistsCollectorDef f => FileExists(env, f, source),
                _ => Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:unknown_kind", source),
            };
            return new PackFactResult { CollectorId = def.Id, Data = data };
        }
        catch (RegexMatchTimeoutException)
        {
            return new PackFactResult { CollectorId = def.Id, Data = Fact<PackFactData>.Unknown(UnknownReason.TimedOut, "regex_timeout", source) };
        }
        catch (ArgumentException)
        {
            return new PackFactResult { CollectorId = def.Id, Data = Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:regex", source) };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new PackFactResult { CollectorId = def.Id, Data = Fact<PackFactData>.Unknown(UnknownReason.Failed, "exception:" + e.GetType().Name, source) };
        }
    }

    internal static string Kind(CollectorDef def) => def switch
    {
        EventLogCollectorDef => "eventLog",
        UninstallRegexCollectorDef => "uninstallRegex",
        LoadedDriverCollectorDef => "loadedDriver",
        FaultingModuleCollectorDef => "faultingModule",
        RegistryValueCollectorDef => "registryValue",
        FileExistsCollectorDef => "fileExists",
        _ => "unknown",
    };

    private static Fact<PackFactData> EventLog(ScanEnvironment env, EventLogCollectorDef def, string source, CancellationToken ct)
    {
        if (def.SinceDays is < 1 or > PackCollectorLimits.MaxSinceDays || def.Max is < 1 or > PackCollectorLimits.MaxEvents)
        {
            return Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:limits", source);
        }

        if (string.IsNullOrWhiteSpace(def.Log) || (def.Providers.Count == 0 && def.EventIds.Count == 0))
        {
            return Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:unbounded_query", source);
        }

        var q = new EventQuery
        {
            Log = def.Log,
            Providers = def.Providers,
            EventIds = def.EventIds,
            Levels = def.Levels.Select(l => (int)l).ToList(),
            SinceUtc = env.StartedUtc.AddDays(-def.SinceDays),
            Max = def.Max,
            Fields = def.Project.Take(32).ToList(),
        };
        var r = env.Sources.Events.Query(q, ct);
        if (r.Status == ReadStatus.NotFound)
        {
            return Fact<PackFactData>.Known(new PackFactData { Count = 0 }, source);
        }

        if (r.Status != ReadStatus.Ok)
        {
            return Fact<PackFactData>.Unknown(env.ReasonOf(r.Status), r.Error, source);
        }

        var items = r.Records.Select(e =>
        {
            var item = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["utc"] = Utc(e.Utc),
                ["provider"] = e.Provider,
                ["eventId"] = e.Id.ToString(CultureInfo.InvariantCulture),
                ["level"] = e.Level.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var (k, v) in e.Data)
            {
                item[k] = Cap(v);
            }

            return (IReadOnlyDictionary<string, string>)item;
        }).ToList();
        return Fact<PackFactData>.Known(Data(items, r.Records.Select(e => e.Utc), [], r.Truncated), source);
    }

    private static Fact<PackFactData> Uninstall(ScanEnvironment env, UninstallRegexCollectorDef def, string source)
    {
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        var name = new Regex(def.DisplayNameRegex, options, PackCollectorLimits.RegexTimeout);
        var publisher = def.PublisherRegex is null ? null : new Regex(def.PublisherRegex, options, PackCollectorLimits.RegexTimeout);
        var (status, apps, _) = SoftwareCollector.ReadApps(env);
        if (status != ReadStatus.Ok)
        {
            return Fact<PackFactData>.Unknown(env.ReasonOf(status), "uninstall_unreadable", source);
        }

        var matches = apps.Where(a => name.IsMatch(a.DisplayName) && (publisher is null || (a.Publisher is not null && publisher.IsMatch(a.Publisher)))).ToList();
        var items = matches.Select(a => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["displayName"] = Cap(a.DisplayName),
            ["publisher"] = Cap(a.Publisher ?? string.Empty),
            ["version"] = Cap(a.Version ?? string.Empty),
            ["hive"] = a.Hive.ToString().ToLowerInvariant(),
        }).ToList();
        var entities = matches.Select(a => new EntityRef { Kind = EntityKind.App, Id = EntityIds.App(a.Hive, a.KeyName), DisplayName = a.DisplayName }).ToList();
        return Fact<PackFactData>.Known(Data(items, [], entities, false), source);
    }

    private static Fact<PackFactData> Drivers(ScanEnvironment env, LoadedDriverCollectorDef def, string source)
    {
        if (def.Names.Count == 0 && def.NameRegex is null)
        {
            return Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:no_names", source);
        }

        var regex = def.NameRegex is null ? null : new Regex(def.NameRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, PackCollectorLimits.RegexTimeout);
        var drivers = DevicesCollector.LoadedDrivers(env);
        if (!drivers.TryGetValue(out var list))
        {
            return Fact<PackFactData>.Unknown(drivers.Reason ?? UnknownReason.Failed, drivers.Detail, source);
        }

        var matches = list.Where(d => def.Names.Contains(d.Name, StringComparer.OrdinalIgnoreCase) || (regex?.IsMatch(d.Name) ?? false)).ToList();
        var items = matches.Select(d => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = d.Name,
            ["version"] = d.Version ?? string.Empty,
            ["signer"] = d.Signer?.Publisher ?? string.Empty,
        }).ToList();
        var entities = matches.Select(d => new EntityRef { Kind = EntityKind.Driver, Id = d.Name }).ToList();
        return Fact<PackFactData>.Known(Data(items, [], entities, false), source);
    }

    private static Fact<PackFactData> FaultingModule(ScanEnvironment env, FaultingModuleCollectorDef def, string source, CancellationToken ct)
    {
        if (def.Modules.Count == 0 || def.SinceDays is < 1 or > PackCollectorLimits.MaxSinceDays)
        {
            return Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:limits", source);
        }

        var exes = def.Exes.Count > 0 ? def.Exes : RiotProcessNames.All;
        var r = env.Sources.Events.Query(new EventQuery { Log = "Application", Providers = ["Application Error"], EventIds = [1000], SinceUtc = env.StartedUtc.AddDays(-def.SinceDays), Max = PackCollectorLimits.MaxEvents, Fields = ["AppName", "ModuleName", "ExceptionCode", "#0", "#3", "#6"] }, ct);
        if (r.Status != ReadStatus.Ok)
        {
            return Fact<PackFactData>.Unknown(env.ReasonOf(r.Status), r.Error, source);
        }

        var matches = r.Records
            .Select(e => (e.Utc, Exe: Get(e, "AppName", "#0"), Module: Get(e, "ModuleName", "#3"), Code: Get(e, "ExceptionCode", "#6")))
            .Where(x => x.Exe is not null && x.Module is not null && exes.Contains(x.Exe, StringComparer.OrdinalIgnoreCase) && def.Modules.Contains(x.Module, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var items = matches.Select(x => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["utc"] = Utc(x.Utc),
            ["exe"] = Cap(x.Exe!),
            ["module"] = Cap(x.Module!),
            ["exceptionCode"] = Cap(x.Code ?? string.Empty),
        }).ToList();
        return Fact<PackFactData>.Known(Data(items, matches.Select(x => x.Utc), [], r.Truncated), source);
    }

    private static Fact<PackFactData> RegistryValue(ScanEnvironment env, RegistryValueCollectorDef def, string source)
    {
        if (def.Values.Count > PackCollectorLimits.MaxRegistryValues)
        {
            return Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:too_many_values", source);
        }

        var key = NormalizeKey(def.Key);
        if (key is null || !IsAllowedKey(def.Hive, key))
        {
            return Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:registry_root", source);
        }

        var data = def.Hive == RegistryHiveScope.HkcuPlayer ? env.Hkcu(key) : env.Hklm(key);
        if (data.Status == ReadStatus.NotFound)
        {
            return Fact<PackFactData>.Known(new PackFactData { Count = 0 }, source);
        }

        if (!data.Ok())
        {
            return Fact<PackFactData>.Unknown(env.ReasonOf(data.Status), data.Error, source);
        }

        var values = def.Values.Count == 0
            ? data.Values.Take(PackCollectorLimits.MaxRegistryValues).ToList()
            : data.Values.Where(v => def.Values.Contains(v.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var hive = def.Hive == RegistryHiveScope.HkcuPlayer ? "HKCU" : "HKLM";
        var items = values.Select(v => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = v.Name,
            ["kind"] = v.Kind,
            ["data"] = Cap(v.Data ?? string.Empty),
        }).ToList();
        var entities = values.Select(v => new EntityRef { Kind = EntityKind.RegistryValue, Id = EntityIds.RegistryValue(hive, key, v.Name) }).ToList();
        return Fact<PackFactData>.Known(Data(items, [], entities, false), source);
    }

    private static Fact<PackFactData> FileExists(ScanEnvironment env, FileExistsCollectorDef def, string source)
    {
        var path = ExpandPath(env, def.Path);
        if (path is null)
        {
            return Fact<PackFactData>.Unknown(UnknownReason.Failed, "invalid:path", source);
        }

        if (path.Length == 0)
        {
            return Fact<PackFactData>.NotApplicable("path_token_unresolved", source);
        }

        var stat = env.Sources.Files.Stat(path);
        if (stat.Status is not ReadStatus.Ok and not ReadStatus.NotFound)
        {
            return Fact<PackFactData>.Unknown(env.ReasonOf(stat.Status), null, source);
        }

        var exists = stat.Status == ReadStatus.Ok;
        var version = exists && !stat.IsDirectory ? env.Sources.Files.GetVersion(path).FileVersion : null;
        var item = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exists"] = exists ? "true" : "false",
            ["size"] = exists ? stat.Size.ToString(CultureInfo.InvariantCulture) : string.Empty,
            ["mtimeUtc"] = exists && stat.ModifiedUtc is { } t ? Utc(t) : string.Empty,
            ["version"] = version ?? string.Empty,
            ["signer"] = string.Empty,
        };
        return Fact<PackFactData>.Known(
            new PackFactData
            {
                Count = exists ? 1 : 0,
                LastUtc = exists ? stat.ModifiedUtc : null,
                Items = [item],
                Entities = exists ? [new EntityRef { Kind = EntityKind.File, Id = path }] : [],
            },
            source);
    }

    /// <summary>Normalizes <c>ControlSetNNN</c> to <c>CurrentControlSet</c>; null for an unsafe key.</summary>
    /// <param name="key">Key.</param>
    internal static string? NormalizeKey(string key)
    {
        var k = key.Trim().Trim('\\');
        if (k.Length == 0 || k.Contains("..", StringComparison.Ordinal) || k.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        return ControlSet().Replace(k, @"SYSTEM\CurrentControlSet\");
    }

    /// <summary>Allowlist and denylist check (ordinal, case-insensitive).</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="normalizedKey">Normalized key.</param>
    internal static bool IsAllowedKey(RegistryHiveScope hive, string normalizedKey)
    {
        var probe = hive == RegistryHiveScope.HkcuPlayer ? "HKCU:" + normalizedKey : normalizedKey;
        static bool Under(string key, string root) => key.Equals(root, StringComparison.OrdinalIgnoreCase) || key.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        var allowed = PackCollectorLimits.AllowedRegistryRoots
            .Where(r => r.StartsWith("HKCU:", StringComparison.Ordinal) == (hive == RegistryHiveScope.HkcuPlayer))
            .Any(r => Under(probe, r));
        var denied = PackCollectorLimits.DeniedRegistryPrefixes.Any(d => Under(probe, d) || probe.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        return allowed && !denied;
    }

    /// <summary>Expands an allowlisted path token for the player; null = invalid, empty = token unresolved on this PC.</summary>
    /// <param name="env">Environment.</param>
    /// <param name="template">Template.</param>
    internal static string? ExpandPath(ScanEnvironment env, string template)
    {
        var t = template.Trim();
        if (t.Contains("..", StringComparison.Ordinal) || t.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0 || t.StartsWith(@"\\", StringComparison.Ordinal) || t.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var token = PackCollectorLimits.AllowedPathTokens.FirstOrDefault(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (token is null)
        {
            return null;
        }

        var rest = t[token.Length..];
        if (rest.Length > 0 && rest[0] != '\\')
        {
            return null;
        }

        if (rest.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var loc = RiotLocator.Locate(env);
        var basePath = token.ToUpperInvariant() switch
        {
            "%PROGRAMFILES%" => env.Env.ProgramFiles,
            "%PROGRAMFILES(X86)%" => env.Env.ProgramFilesX86,
            "%PROGRAMDATA%" => env.Env.ProgramData,
            "%SYSTEMROOT%" => env.WindowsDir,
            "%PLAYERLOCALAPPDATA%" => env.Player.LocalAppData,
            "%PLAYERAPPDATA%" => env.Player.RoamingAppData,
            "%RIOTCLIENTDIR%" => loc.ClientDir,
            "%VALORANTDIR%" => loc.ValorantDir,
            "%VANGUARDDIR%" => loc.VanguardDir,
            _ => null,
        };
        return basePath is null ? string.Empty : basePath.TrimEnd('\\') + rest;
    }

    private static PackFactData Data(IReadOnlyList<IReadOnlyDictionary<string, string>> items, IEnumerable<DateTimeOffset> times, IReadOnlyList<EntityRef> entities, bool truncated)
    {
        var t = times.ToList();
        return new PackFactData
        {
            Count = items.Count,
            FirstUtc = t.Count > 0 ? t.Min() : null,
            LastUtc = t.Count > 0 ? t.Max() : null,
            Items = items.Take(PackCollectorLimits.MaxItems).ToList(),
            Entities = entities.DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Take(PackCollectorLimits.MaxItems).ToList(),
            Truncated = truncated || items.Count > PackCollectorLimits.MaxItems,
        };
    }

    private static string Cap(string value) => value.Length > PackCollectorLimits.MaxValueLength ? value[..PackCollectorLimits.MaxValueLength] : value;

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string? Get(EventRec e, params string[] names)
    {
        foreach (var n in names)
        {
            if (e.Data.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }

    [GeneratedRegex(@"^SYSTEM\\ControlSet\d{3}\\", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ControlSet();
}

/// <summary>The <c>pack</c> collector: runs every pack-declared definition from the request.</summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed class PackCollector(SystemSources sources) : CollectorBase(sources)
{
    /// <inheritdoc />
    public override string Id => "pack";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Logs;

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var results = new List<PackFactResult>();
        foreach (var def in env.Request.PackCollectors.DistinctBy(d => d.Id, StringComparer.Ordinal).Take(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = PackCollectorExecutor.Run(env, def, cancellationToken);
            if (!r.Data.IsKnown && r.Data.Status == FactStatus.Unknown)
            {
                run.Issue("pack_unknown:" + def.Id);
            }

            results.Add(r);
        }

        run.ForcedStatus = results.Any(r => r.Data.Status == FactStatus.Unknown) ? CollectorStatus.Partial : CollectorStatus.Ok;

        return new SnapshotPatch { Pack = results };
    }
}
