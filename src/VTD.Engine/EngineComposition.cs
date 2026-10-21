using System.Reflection;
using VTD.Analysis;
using VTD.Collectors;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Ipc;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Engine.Platform;
using VTD.Knowledge;
using VTD.Platform;
using VTD.Reporting;
using VTD.Session;

namespace VTD.Engine;

/// <summary>Start-up options of an engine process.</summary>
public sealed record EngineStartOptions
{
    /// <summary>Elevated (the host verified the Program Files location) or limited.</summary>
    public required PrivilegeMode Privilege { get; init; }

    /// <summary>Bundled KB folder.</summary>
    public required string KbPath { get; init; }

    /// <summary>Allow network probes (clock skew, ping targets).</summary>
    public bool AllowNetwork { get; init; } = true;

    /// <summary>Run long effects inline (CLI one-shot runs) instead of in the background.</summary>
    public bool InlineEffects { get; init; }
}

/// <summary>
/// The write side of an ELEVATED engine (A3's journal, state store, fix runner, recovery). The host creates it through
/// <see cref="EngineComposition.WriteSideFactory"/>; until A3's types are merged the default is an in-memory store and no
/// fix runner, so the elevated engine scans, analyses and exports but offers no fixes (capability "fixes" absent).
/// </summary>
public sealed record WriteSide
{
    /// <summary>Session state store (A3: atomic replace + .bak + journal rebuild through <see cref="SessionStateRebuilder"/>).</summary>
    public required IStateStore<SessionState> Store { get; init; }

    /// <summary>Fix runner (A3), or null.</summary>
    public IFixRunner? FixRunner { get; init; }

    /// <summary>Crash recovery (A3), or null.</summary>
    public IRecovery? Recovery { get; init; }

    /// <summary>Monitor (A5, M3), or null.</summary>
    public IMonitorService? Monitor { get; init; }

    /// <summary>Called with the initial and every later session state (keeps the fix runner's session context current).</summary>
    public Action<SessionState>? OnSessionChanged { get; init; }
}

/// <summary>What <see cref="EngineComposition.WriteSideFactory"/> gets to build A3's types.</summary>
public sealed record WriteSideContext
{
    /// <summary>Storage layout (<c>%ProgramData%\VTDiag</c>).</summary>
    public required EngineStorage Storage { get; init; }

    /// <summary>KB.</summary>
    public required IKnowledgeBase Kb { get; init; }

    /// <summary>Live probes for guards.</summary>
    public required IFactProbe Probe { get; init; }

    /// <summary>Restore points (A3's runner calls it before the first medium/high-risk fix).</summary>
    public required IRestorePointService RestorePoints { get; init; }

    /// <summary>Rebuilds the session from the journal when both state files are unreadable.</summary>
    public required IStateRebuilder<SessionState> Rebuilder { get; init; }

    /// <summary>The interactive player.</summary>
    public required Func<PlayerContext> Player { get; init; }

    /// <summary>Clock.</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>Ids.</summary>
    public required IIdGenerator Ids { get; init; }

    /// <summary>App version (journal records).</summary>
    public required string AppVersion { get; init; }
}

/// <summary>Builds an engine (the DI host, by hand: few parts, explicit order).</summary>
public static class EngineComposition
{
    /// <summary>
    /// Builds the elevated write side. Lead: replace with A3's <c>FileStateStore&lt;SessionState&gt;</c> (rebuilder =
    /// <see cref="WriteSideContext.Rebuilder"/>), <c>Journal</c>, <c>FixRunner</c> (with
    /// <see cref="WriteSideContext.RestorePoints"/> and <see cref="WriteSideContext.Probe"/>) and <c>Recovery</c>.
    /// </summary>
    public static Func<WriteSideContext, WriteSide> WriteSideFactory { get; set; } = RealWriteSide;

    /// <summary>
    /// The production write side: A3's write-ahead journal and atomic state store under <c>%ProgramData%\VTDiag</c> (admin-only
    /// ACL), the audited primitives on the real Win32 ports, and the fix runner (which is also the crash recovery). Consent
    /// still comes only from the UI: <see cref="CommandPolicy"/> and the consent token are enforced by the session and the runner.
    /// </summary>
    /// <param name="ctx">Context.</param>
    public static WriteSide RealWriteSide(WriteSideContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var session = new VTD.Fixes.FixSessionContext(ctx.Ids.NewId(), ctx.AppVersion) { Player = ctx.Player(), IsElevated = true };
        var ports = VTD.Fixes.Interop.RealSystemPorts.Create(session, ctx.Storage.Root, ctx.Clock);
        var registry = new VTD.Fixes.OperationRegistry(ports, session, ctx.Clock, ctx.Probe);
        var journal = new VTD.Journal.FileJournal(new VTD.Journal.JournalOptions { DataRoot = ctx.Storage.Root, Directory = ctx.Storage.JournalFolder });
        var store = new VTD.Journal.AtomicStateStore<SessionState>(ctx.Storage.Root, ctx.Storage.SessionFolder, rebuilder: ctx.Rebuilder, journalRecords: () => journal.ReadAll().Records);
        var runner = new VTD.Fixes.Runner.FixRunner(ctx.Kb, registry, journal, ctx.Probe, ctx.RestorePoints, session, ctx.Clock, ctx.Ids);
        return new WriteSide
        {
            Store = store,
            FixRunner = runner,
            Recovery = runner,
            OnSessionChanged = s =>
            {
                session.SessionId = s.SessionId;
                session.Player = ctx.Player();
            },
        };
    }

    /// <summary>The engine/app version (informational version without the build metadata).</summary>
    public static string Version()
    {
        var v = typeof(EngineComposition).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }

    /// <summary>Builds the engine: player, storage, clock skew, KB (with persisted revocation), scan, analysis, reports, session.</summary>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<EngineCore> BuildAsync(EngineStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clock = TimeProvider.System;
        var ids = new UlidGenerator(clock);
        var elevated = options.Privilege == PrivilegeMode.Elevated;
        var version = Version();

        // The player: the user of the engine's Windows session (elevated), or this process's user (limited / CLI).
        PlayerContext player;
        if (elevated)
        {
            var sid = InteractiveUser.SessionUserSid() ?? InteractiveUser.ProcessUserSid();
            player = InteractiveUser.ForSid(sid, isAdmin: string.Equals(sid, InteractiveUser.ProcessUserSid(), StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            player = PlayerContexts.FromCurrentProcess();
        }

        var playerBox = new PlayerBox(player);
        var storage = elevated ? EngineStorage.ForElevated(player.Sid) : EngineStorage.ForLimited(player.LocalAppData);
        var sources = LiveSources.Create(clock);
        var probe = new FactProbe(sources, player, clock, options.Privilege);

        // Clock skew FIRST, then the KB is verified with the corrected time (DESIGN.md, critique 3.11).
        var trustedNow = clock.GetUtcNow();
        if (options.AllowNetwork)
        {
            trustedNow -= TimeSpan.FromSeconds(await MeasureSkewSecondsAsync(probe, cancellationToken).ConfigureAwait(false));
        }

        var revocations = storage.LoadRevocations(clock.GetUtcNow());
        var kbResult = await new KnowledgeBaseLoader().LoadAsync(
            new KbLoadOptions
            {
                BundledPath = options.KbPath,
                NowUtc = trustedNow,
                AppVersion = version,
                MinRevocationSerial = revocations.Serial,
                KnownRevokedKeyIds = revocations.RevokedKeyIds,
                KnownRevokedVersions = revocations.RevokedVersions,
            },
            cancellationToken).ConfigureAwait(false);
        TrySaveRevocations(storage, revocations, kbResult, clock.GetUtcNow());
        var kb = kbResult.Kb;

        var salt = storage.LoadOrCreateSalt();
        var started = clock.GetUtcNow();
        var scanner = new ScanOrchestrator(sources, clock, ids, new ScanOrchestratorOptions { MachineKeySalt = salt, AppVersion = version });
        var analyzer = new Analyzer(kb);
        var known = new[] { InteractiveUser.UserNameOf(player), Environment.MachineName, Environment.UserName }.Where(s => s is { Length: >= 3 }).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var redactor = new Redactor(salt, known);
        var exporter = new ShareExporter(redactor, new ReportBuilder(kb, redactor), new ScanBundleCodec(redactor), ids, clock);
        var shareText = new RedactionPolicy { Level = RedactionLevel.Share };
        var log = new Diagnostics.DiagLog(storage.LogsFolder, clock, text => redactor.RedactText(text, shareText));
        Diagnostics.DiagLog.Current = log;
        log.Info(
            "engine.start",
            options.Privilege.ToString(),
            "engine", version,
            "kb", kb.Version.Version,
            "windows", Environment.OSVersion.Version.ToString(),
            "arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            "dotnet", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            "network", options.AllowNetwork ? "on" : "off");
        foreach (var issue in kbResult.Issues)
        {
            log.Write(issue.Severity == KbIssueSeverity.Error ? ClientLogLevel.Error : ClientLogLevel.Warning, "engine", "kb.issue", issue.Code, [new("path", issue.Path)]);
        }

        var bundles = new Diagnostics.DebugBundleBuilder(redactor, redactor.ContainsKnownIdentifier, ids, clock);
        var hub = new NotificationHub(clock);
        var ledger = new RestartPromptLedger(clock);

        SessionController? controller = null;
        WriteSide write;
        IRebootCoordinator? reboot = null;
        IArtifactStore artifacts = new MemoryArtifactStore();
        if (elevated)
        {
            var restorePoints = new RestorePointService(
                clock,
                sessionId => controller is { } c && string.Equals(c.Current.SessionId, sessionId, StringComparison.Ordinal) ? c.Current.RestorePoint : null,
                info => controller?.Post(new RestorePointRecorded { EventId = ids.NewId(), Utc = clock.GetUtcNow(), Info = info, Origin = EventOrigin.Engine }));
            write = WriteSideFactory(new WriteSideContext
            {
                Storage = storage,
                Kb = kb,
                Probe = probe,
                RestorePoints = restorePoints,
                Rebuilder = new SessionStateRebuilder(),
                Player = playerBox.Get,
                Clock = clock,
                Ids = ids,
                AppVersion = version,
            });
            reboot = new RebootCoordinator(new BootReader(clock, sources.Events), probe, ledger, playerBox.Get, AppContext.BaseDirectory, InteractiveUser.ProcessUserSid());
            artifacts = new FileArtifactStore(storage);
        }
        else
        {
            // Limited: nothing can change, nothing is persisted to ProgramData, no journal, no fixes.
            write = new WriteSide { Store = new VolatileStateStore() };
        }

        var services = new SessionServices
        {
            Kb = kb,
            Scanner = scanner,
            Analyzer = analyzer,
            FixRunner = elevated ? write.FixRunner : null,
            Reboot = reboot,
            Recovery = elevated ? write.Recovery : null,
            Monitor = write.Monitor,
            Probe = probe,
            Artifacts = artifacts,
            Clock = clock,
            Ids = ids,
            Player = playerBox.Get,
            Privilege = options.Privilege,
        };
        var runtime = new SessionRuntime(services, hub, ledger);
        var (state, _) = SessionController.LoadOrCreate(write.Store, () => SessionMachine.NewSession(ids.NewId(), clock.GetUtcNow(), options.Privilege, kb.Version.Version, player.Sid));
        controller = new SessionController(state, write.Store, runtime, clock, new SessionControllerOptions { InlineEffects = options.InlineEffects });
        runtime.Attach(controller);
        if (write.OnSessionChanged is { } observe)
        {
            observe(controller.Current);
            controller.Changed += (_, s) => observe(s);
        }
        var core = new EngineCore(new EngineCoreOptions
        {
            Controller = controller,
            Runtime = runtime,
            Redactor = redactor,
            Exporter = exporter,
            PlanValidator = new PlanValidator(redactor),
            Player = player,
            EngineVersion = version,
            AppVersion = version,
            ProcessId = Environment.ProcessId,
            ReportsFolder = storage.ReportsFolder,
            KbIssues = kbResult.Issues,
            Log = log,
            DebugBundles = bundles,
            JournalFolder = elevated ? storage.JournalFolder : null,
            StartedUtc = started,
        });
        playerBox.Core = core;
        return core;
    }

    private static async Task<long> MeasureSkewSecondsAsync(IFactProbe probe, CancellationToken ct)
    {
        try
        {
            var result = await probe.ProbeAsync(GuardIds.ClockOk, new Dictionary<string, string>(StringComparer.Ordinal), ct).ConfigureAwait(false);
            var skew = result.Evidence.SelectMany(e => e.Args).FirstOrDefault(a => string.Equals(a.Key, "skewSeconds", StringComparison.Ordinal)).Value;
            return long.TryParse(skew, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    private static void TrySaveRevocations(EngineStorage storage, KbRevocationList previous, KbLoadResult result, DateTimeOffset now)
    {
        try
        {
            storage.SaveRevocations(previous, result, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the bundled lists still apply; the next start tries again.
        }
    }

    // The player as the engine knows it right now (refined when the verified UI connects).
    private sealed class PlayerBox(PlayerContext initial)
    {
        public EngineCore? Core { get; set; }

        public PlayerContext Get() => Core?.Player ?? initial;
    }
}
