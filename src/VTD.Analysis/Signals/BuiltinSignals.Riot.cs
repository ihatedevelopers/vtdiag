using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Snapshots;
using VTD.Knowledge;

namespace VTD.Analysis.Signals;

public static partial class BuiltinSignals
{
    /// <summary>Codes after which Riot's "set vgc to Automatic" step applies (research/error_codes.json tool_design_notes).</summary>
    private static readonly HashSet<string> VgcCodes = new(StringComparer.Ordinal)
    {
        "VAN 0", "VAN 6", "VAN 25", "VAN 57", "VAN -1", "VAN -72", "VAN -79", "VAN -81", "VAN -83", "VAN -92", "VAN -102", "VAN -104",
        "VAN 128", "VAN 133", "VAN 135", "VAN 148", "VAN 193", "VAN 1067", "VAN 2266", "VAN MISSING -117", "VAL 44", "VAN 79", "VAN 81",
    };

    private static readonly string[] OsCompatTokens = ["WIN95", "WIN98", "WIN2000", "WINXP", "VISTA", "WIN7", "WIN8", "NT4", "256COLOR", "16BITCOLOR", "640X480"];

    private static IEnumerable<SignalResult> VgcService(SignalContext c)
    {
        string[] ids = ["vg.installed", "vg.vgc_missing", "vg.vgk_missing", "vg.vgc_disabled", "vg.vgc_running", "vg.vgk_running", "vg.vgc_logs_recent", "vg.vgc_crash_loop", "vg.vgc_never_started", "vg.vgc_automatic_needed"];
        if (!c.Snapshot.Riot.Vanguard.TryGetValue(out var v))
        {
            var r = Sig.ReasonOf(c.Snapshot.Riot.Vanguard);
            return ids.Select(i => Sig.Unknown(i, r));
        }

        var asOf = AsOf(c);
        var results = new List<SignalResult> { Sig.Of("vg.installed", v.Installed) };
        if (!v.Installed)
        {
            results.Add(Sig.True("vg.vgc_missing"));
            results.Add(Sig.True("vg.vgk_missing"));
            results.AddRange(ids.Skip(3).Select(Sig.NotApplicable));
            return results;
        }

        var vgcEntity = v.Vgc is null ? null : new EntityRef { Kind = EntityKind.Service, Id = v.Vgc.Name, DisplayName = v.Vgc.DisplayName ?? v.Vgc.Name };
        results.Add(Sig.Of("vg.vgc_missing", v.Vgc is null));
        results.Add(Sig.Of("vg.vgk_missing", v.Vgk is null));
        results.Add(v.Vgc is null ? Sig.NotApplicable("vg.vgc_disabled") : Sig.Of("vg.vgc_disabled", v.Vgc.StartType == ServiceStartType.Disabled, 1.0, null, [vgcEntity!]));
        results.Add(v.Vgc is null ? Sig.NotApplicable("vg.vgc_running") : Sig.Of("vg.vgc_running", v.Vgc.State == ServiceState.Running, 1.0, null, [vgcEntity!]));
        results.Add(VgkRunning(c, v));

        var logs = v.Logs;
        SignalResult recent, loop, never;
        if (logs is null)
        {
            recent = Sig.Unknown("vg.vgc_logs_recent", UnknownReason.NotCollected);
            loop = Sig.Unknown("vg.vgc_crash_loop", UnknownReason.NotCollected);
            never = Sig.Unknown("vg.vgc_never_started", UnknownReason.NotCollected);
        }
        else
        {
            var newest = logs.NewestUtc;
            recent = Sig.Of("vg.vgc_logs_recent", logs.CountLast24Hours > 0 || (newest is not null && newest >= asOf.AddHours(-24)));
            loop = Sig.Of("vg.vgc_crash_loop", logs.CountLastHour >= 10, Math.Min(1.0, logs.CountLastHour / 20.0), [Sig.Ev("vg.vgc_crash_loop", ("count", Sig.Num(logs.CountLastHour)))]);
            never = VgcNeverStarted(c, newest);
        }

        results.Add(recent);
        results.Add(loop);
        results.Add(never);
        results.Add(VgcAutomaticNeeded(c, v, vgcEntity, loop, never));
        return results;
    }

    /// <summary>
    /// "Vanguard didn't start when the game was launched": the Riot Client wrote logs in the last 7 days, but vgc wrote
    /// none since (metadata only; vgc logs are encrypted). Not playing for a week is NOT a failure, so with no recent
    /// client activity this is False, and Unknown when the client logs couldn't be read.
    /// </summary>
    private static SignalResult VgcNeverStarted(SignalContext c, DateTimeOffset? vgcNewest)
    {
        const string Id = "vg.vgc_never_started";
        if (!c.Snapshot.Riot.ClientLogs.TryGetValue(out var client))
        {
            return Sig.Unknown(Id, Sig.ReasonOf(c.Snapshot.Riot.ClientLogs));
        }

        if (client.NewestUtc is not { } clientNewest || clientNewest < AsOf(c).AddDays(-7))
        {
            return Sig.False(Id);
        }

        var silent = vgcNewest is null || vgcNewest < clientNewest.AddHours(-1);
        return Sig.Of(Id, silent, 1.0, [Sig.Ev(Id, ("date", vgcNewest?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-"))]);
    }

    private static SignalResult VgkRunning(SignalContext c, VanguardInfo v)
    {
        if (v.Vgk is null)
        {
            return Sig.NotApplicable("vg.vgk_running");
        }

        if (v.Vgk.State == ServiceState.Running)
        {
            return Sig.True("vg.vgk_running");
        }

        return c.Snapshot.Drivers.TryGetValue(out var drivers)
            ? Sig.Of("vg.vgk_running", drivers.Any(d => string.Equals(d.Name, "vgk.sys", StringComparison.OrdinalIgnoreCase)))
            : Sig.Unknown("vg.vgk_running", Sig.ReasonOf(c.Snapshot.Drivers));
    }

    /// <summary>
    /// The Pre-Check gate (DESIGN.md: vgc Manual + Stopped is normal under Pre-Check; never force Automatic unless the
    /// player confirms Pre-Check is off AND the logs show a failure). True only when vgc exists, isn't Automatic, isn't
    /// running, the player answered "No" to Pre-Check, and log metadata shows a failure: a vgc restart loop, the Riot
    /// Client running without vgc ever logging, a Vanguard start-up failure in VALORANT's log, a Vanguard code found in
    /// the logs in the last 14 days, or vgc set to Disabled (never a normal state). Typed codes alone are not enough.
    /// Unknown when the question wasn't answered, so the fix can't bind until the player answers.
    /// </summary>
    private static SignalResult VgcAutomaticNeeded(SignalContext c, VanguardInfo v, EntityRef? vgc, SignalResult loop, SignalResult never)
    {
        const string Id = "vg.vgc_automatic_needed";
        if (v.Vgc is null || vgc is null)
        {
            return Sig.NotApplicable(Id);
        }

        if (v.Vgc.StartType is ServiceStartType.Automatic || v.Vgc.State == ServiceState.Running)
        {
            return Sig.False(Id);
        }

        if (!c.Intake.Answers.TryGetValue("ask.vanguard_precheck_on", out var answer) || string.Equals(answer, "unsure", StringComparison.Ordinal))
        {
            return Sig.Unknown(Id, UnknownReason.InsufficientData);
        }

        if (!string.Equals(answer, "no", StringComparison.Ordinal))
        {
            return Sig.False(Id);
        }

        var loggedCodes = CodeUses.Collect(c.Snapshot, IntakeAnswers.Empty, c.Kb).Where(h => h.Use.Origin == CodeOrigin.Logged && !h.Stale).Select(h => h.Use.Code);
        var failing = v.Vgc.StartType == ServiceStartType.Disabled || loop.State == Tri.True || never.State == Tri.True || loggedCodes.Any(VgcCodes.Contains)
                      || (c.Snapshot.Riot.ShooterLog.TryGetValue(out var log) && log.PlatformInitFailures.Any(f => f.Manager.Contains("Vanguard", StringComparison.OrdinalIgnoreCase)));
        return failing ? Sig.True(Id, 1.0, null, [vgc]) : Sig.False(Id);
    }

    private static IEnumerable<SignalResult> VgkFiles(SignalContext c)
    {
        if (!c.Snapshot.Riot.Vanguard.TryGetValue(out var v))
        {
            var r = Sig.ReasonOf(c.Snapshot.Riot.Vanguard);
            return [Sig.Unknown("vg.vgk_not_loaded", r), Sig.Unknown("vg.files_missing", r), Sig.Unknown("vg.bootstatus_dat", r)];
        }

        if (!v.Installed)
        {
            return [Sig.NotApplicable("vg.vgk_not_loaded"), Sig.NotApplicable("vg.files_missing"), Sig.NotApplicable("vg.bootstatus_dat")];
        }

        var vgkRunning = VgkRunning(c, v);
        var notLoaded = v.Vgk is null ? Sig.NotApplicable("vg.vgk_not_loaded")
            : vgkRunning.State == Tri.True ? Sig.False("vg.vgk_not_loaded")
            : c.Snapshot.Drivers.IsKnown ? Sig.True("vg.vgk_not_loaded", 1.0, null, [new EntityRef { Kind = EntityKind.Service, Id = v.Vgk.Name, DisplayName = v.Vgk.Name }])
            : Sig.Unknown("vg.vgk_not_loaded", Sig.ReasonOf(c.Snapshot.Drivers));
        SignalResult missing;
        if (v.Files.Count == 0)
        {
            missing = Sig.Unknown("vg.files_missing", UnknownReason.NotCollected);
        }
        else
        {
            var gone = v.Files.Where(f => (string.Equals(f.Name, "vgc.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Name, "vgk.sys", StringComparison.OrdinalIgnoreCase)) && !f.Exists)
                .Select(f => f.Name).ToList();
            missing = gone.Count > 0 ? Sig.True("vg.files_missing", 1.0, [Sig.Ev("vg.files_missing", ("items", Sig.List(gone)))]) : Sig.False("vg.files_missing");
        }

        return [notLoaded, missing, FileSignal("vg.bootstatus_dat", v, "vgkbootstatus.dat")];
    }

    private static IEnumerable<SignalResult> StaleDat(SignalContext c)
    {
        yield return c.Snapshot.Riot.Vanguard.TryGetValue(out var v)
            ? (v.Installed || v.Files.Count > 0 ? FileSignal("vg.stale_dat", v, "392667600.dat") : Sig.NotApplicable("vg.stale_dat"))
            : Sig.Unknown("vg.stale_dat", Sig.ReasonOf(c.Snapshot.Riot.Vanguard));
    }

    private static SignalResult FileSignal(string id, VanguardInfo v, string name)
    {
        var entries = v.Files.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0)
        {
            return Sig.Unknown(id, UnknownReason.NotCollected);
        }

        var present = entries.Where(f => f.Exists).ToList();
        if (present.Count == 0)
        {
            return Sig.False(id);
        }

        var ents = present.Select(f => new EntityRef { Kind = EntityKind.File, Id = VanguardFilePath(v, f), DisplayName = f.Name }).DistinctBy(e => e.Id).ToList();
        return Sig.True(id, 1.0, [Sig.Ev(id, ("items", Sig.List(ents.Select(e => e.Id))))], ents);
    }

    /// <summary>Full path of a known Vanguard file. System folders use the <c>%SystemRoot%</c> token (A3 expands it).</summary>
    private static string VanguardFilePath(VanguardInfo v, RiotFileInfo f) => f.Location switch
    {
        "system32" => "%SystemRoot%\\System32\\" + f.Name,
        "windows" => "%SystemRoot%\\" + f.Name,
        _ => (v.InstallPath ?? "%ProgramFiles%\\Riot Vanguard").TrimEnd('\\') + "\\" + f.Name,
    };

    private static IEnumerable<SignalResult> Minidump(SignalContext c)
    {
        var ev = c.Snapshot.Events;
        var since = AsOf(c).AddDays(-14);
        var recentBug = ev.BugChecks.TryGetValue(out var bugs) ? bugs.Where(b => b.Utc >= since).ToList() : null;
        var recentDumps = ev.Minidumps.TryGetValue(out var dumps) ? dumps.Where(d => d.ModifiedUtc >= since).ToList() : null;
        if (recentBug is null && recentDumps is null)
        {
            yield return Sig.Unknown("events.bugcheck_recent", Sig.ReasonOf(ev.BugChecks));
        }
        else
        {
            var n = Math.Max(recentBug?.Count ?? 0, recentDumps?.Count ?? 0);
            yield return Sig.Of("events.bugcheck_recent", n > 0, Math.Min(1.0, n / 3.0), [Sig.Ev("events.bugcheck_recent", ("count", Sig.Num(n)))]);
        }

        foreach (var (id, driver) in new[] { ("events.bugcheck_vgk", "vgk.sys"), ("events.bugcheck_wdfilter", "WdFilter.sys") })
        {
            if (dumps is null)
            {
                yield return Sig.Unknown(id, Sig.ReasonOf(ev.Minidumps));
                continue;
            }

            var n = dumps.Count(d => d.ModifiedUtc >= AsOf(c).AddDays(-30) && string.Equals(d.FaultingDriver, driver, StringComparison.OrdinalIgnoreCase));
            yield return Sig.Of(id, n > 0, Math.Min(1.0, n / 2.0), [Sig.Ev(id, ("count", Sig.Num(n)))]);
        }
    }

    private static IEnumerable<SignalResult> RiotFolders(SignalContext c)
    {
        var riot = c.Snapshot.Riot;
        yield return Sig.FromFact("riot.client_installed", riot.Client, i => Sig.Of("riot.client_installed", i.Installed));
        yield return Sig.FromFact("riot.valorant_installed", riot.Valorant, i => Sig.Of("riot.valorant_installed", i.Installed));
        var nonAscii = new List<bool?>();
        if (riot.Client.TryGetValue(out var client))
        {
            nonAscii.Add(client.PathHasNonAscii);
        }

        if (riot.Valorant.TryGetValue(out var val))
        {
            nonAscii.Add(val.PathHasNonAscii);
        }

        yield return nonAscii.Any(b => b == true) ? Sig.True("riot.path_non_ascii")
            : nonAscii.Count > 0 && nonAscii.All(b => b == false) ? Sig.False("riot.path_non_ascii")
            : Sig.Unknown("riot.path_non_ascii", UnknownReason.NotCollected);
        yield return riot.Valorant.TryGetValue(out var v) && v.Installed && v.Path is not null
            ? Sig.True("riot.valorant_exe", 1.0, null, [new EntityRef { Kind = EntityKind.File, Id = ValorantExePath(v.Path), DisplayName = "VALORANT-Win64-Shipping.exe" }])
            : riot.Valorant.IsKnown ? Sig.False("riot.valorant_exe") : Sig.Unknown("riot.valorant_exe", Sig.ReasonOf(riot.Valorant));
    }

    private static string ValorantExePath(string installPath) => installPath.TrimEnd('\\') + "\\ShooterGame\\Binaries\\Win64\\VALORANT-Win64-Shipping.exe";

    private static IEnumerable<SignalResult> RiotElevation(SignalContext c)
    {
        const string Id = "riot.client_not_elevated";
        var riot = c.Snapshot.Riot;
        if (!riot.Client.TryGetValue(out var client) || !client.Installed || client.Path is null)
        {
            yield return riot.Client.IsKnown ? Sig.NotApplicable(Id) : Sig.Unknown(Id, Sig.ReasonOf(riot.Client));
            yield break;
        }

        var exe = client.Path.TrimEnd('\\') + "\\RiotClientServices.exe";
        yield return Sig.FromFact(Id, riot.CompatLayers, layers =>
        {
            var elevated = layers.Any(l => string.Equals(l.ImageName, "RiotClientServices.exe", StringComparison.OrdinalIgnoreCase)
                                           && l.Layers.Contains("RUNASADMIN", StringComparer.OrdinalIgnoreCase));
            return elevated ? Sig.False(Id) : Sig.True(Id, 1.0, null, [new EntityRef { Kind = EntityKind.File, Id = exe, DisplayName = "RiotClientServices.exe" }]);
        });
    }

    private static IEnumerable<SignalResult> FsoMpo(SignalContext c)
    {
        var riot = c.Snapshot.Riot;
        yield return Sig.FromFact("riot.compat_layers_present", riot.CompatLayers, layers =>
        {
            var stale = layers.Where(l => l.Hive == AppHive.Hkcu)
                .Select(l => (Entry: l, Tokens: l.Layers.Where(t => OsCompatTokens.Any(o => t.StartsWith(o, StringComparison.OrdinalIgnoreCase))).ToList()))
                .Where(x => x.Tokens.Count > 0).OrderBy(x => x.Entry.ExePath, StringComparer.OrdinalIgnoreCase).ToList();
            return stale.Count == 0 ? Sig.False("riot.compat_layers_present")
                : Sig.True("riot.compat_layers_present", 1.0, [Sig.Ev("riot.compat_layers_present", ("items", Sig.List(stale.Select(s => s.Entry.ImageName))))],
                    [.. stale.Select(s => new EntityRef { Kind = EntityKind.CompatLayer, Id = s.Entry.ExePath, DisplayName = string.Join(' ', s.Tokens.Select(t => t.ToUpperInvariant())) })]);
        });
        yield return Sig.FromFact("riot.fso_disabled", riot.CompatLayers, layers => Sig.Of("riot.fso_disabled",
            layers.Any(l => string.Equals(l.ImageName, "VALORANT-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                            && l.Layers.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparer.OrdinalIgnoreCase))));
        yield return Sig.FromFact("display.mpo_enabled", c.Snapshot.Display.MpoDisabled, invert: true);
    }

    private static IEnumerable<SignalResult> RiotLogs(SignalContext c)
    {
        var riot = c.Snapshot.Riot;
        yield return Sig.FromFact("riot.game_running", riot.GameRunning);
        if (riot.ShooterLog.TryGetValue(out var log))
        {
            yield return Sig.Of("log.platform_init_failed", log.PlatformInitFailures.Count > 0, 1.0,
                [Sig.Ev("log.platform_init_failed", ("items", Sig.List(log.PlatformInitFailures.Select(f => f.Manager))))]);
            yield return Sig.Of("log.vanguard_init_failed", log.PlatformInitFailures.Any(f => f.Manager.Contains("Vanguard", StringComparison.OrdinalIgnoreCase)));
            yield return Sig.Of("log.d3d_device_lost", log.D3dDeviceLostCount > 0, Math.Min(1.0, log.D3dDeviceLostCount / 2.0), [Sig.Ev("log.d3d_device_lost", ("count", Sig.Num(log.D3dDeviceLostCount)))]);
            yield return Sig.Of("log.fatal_error", log.FatalErrorCount > 0, Math.Min(1.0, log.FatalErrorCount / 2.0), [Sig.Ev("log.fatal_error", ("count", Sig.Num(log.FatalErrorCount)))]);
            yield return Sig.Of("log.net_disconnects", log.NetDisconnectCount >= 3, Math.Min(1.0, log.NetDisconnectCount / 6.0), [Sig.Ev("log.net_disconnects", ("count", Sig.Num(log.NetDisconnectCount)))]);
        }
        else
        {
            var na = riot.ShooterLog.Status == FactStatus.NotApplicable;
            foreach (var id in new[] { "log.platform_init_failed", "log.vanguard_init_failed", "log.d3d_device_lost", "log.fatal_error", "log.net_disconnects" })
            {
                yield return na ? Sig.NotApplicable(id) : Sig.Unknown(id, Sig.ReasonOf(riot.ShooterLog));
            }
        }

        if (riot.Crashes.TryGetValue(out var crashes))
        {
            var recent = crashes.Where(x => x.CreatedUtc is { } t && t >= AsOf(c).AddDays(-7)).ToList();
            yield return Sig.Of("crash.recent", recent.Count > 0, Math.Min(1.0, recent.Count / 2.0), [Sig.Ev("crash.recent", ("count", Sig.Num(recent.Count)))]);
            yield return Sig.Of("crash.gpu", crashes.Any(x => x.CreatedUtc is { } t && t >= AsOf(c).AddDays(-30) && string.Equals(x.CrashType, "GPUCrash", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            yield return Sig.Unknown("crash.recent", Sig.ReasonOf(riot.Crashes));
            yield return Sig.Unknown("crash.gpu", Sig.ReasonOf(riot.Crashes));
        }
    }
}
