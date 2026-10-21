using System.Globalization;
using System.Text;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Sessions;
using VTD.Session.Tests.Sim;
using VTD.Testing.Fakes;

namespace VTD.Session.Tests;

/// <summary>
/// Property tests of the session (design_B.md section 15): random event sequences from the UI, the CLI and an MCP agent,
/// with a crash injected at random persist points (before and after the write), power cuts inside a fix, Windows Update
/// reboots, Fast Startup shut-downs, expiry, and real restarts. After every step and every restart:
/// <list type="bullet">
/// <item>R1: the PC restarts only with a token minted by a UI "Restart now" click the engine accepted, at most once per click.</item>
/// <item>Resume work runs at most three times per session; the counter never exceeds the cap.</item>
/// <item>No resume registration in a closed phase (Completed, RolledBack, Abandoned, NeedsAttention).</item>
/// <item>Recovery is consistent: no transient phase survives a restart, and the session's attempts match the changes
/// really in place.</item>
/// <item>UI-only commands from the CLI/MCP are refused and change nothing; the revision grows by exactly one per save.</item>
/// </list>
/// </summary>
public sealed class SessionPropertyTests(ITestOutputHelper output)
{
    private static readonly string[] FixIds = ["fix.net.disable_unused_adapter", "fix.svc.disable_conflict_service", "fix.net.dns_flush", "fix.display.hags_off"];

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < 40; i++)
        {
            data.Add(1000 + i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Random_sequences_with_crashes_keep_every_invariant(int seed)
    {
        // Each theory case runs 10 independent histories of 80 steps.
        for (var run = 0; run < 10; run++)
        {
            await RunAsync((seed * 31) + run, steps: 80);
        }
    }

    [Fact]
    public async Task A_crash_at_every_persist_point_of_a_full_fix_and_restart_flow_recovers_consistently()
    {
        // Count the saves of one complete, crash-free flow, then crash once at each of them (before and after the write).
        var total = await CountSavesOfScriptedFlowAsync();
        Assert.True(total > 10, $"flow made only {total} saves");
        for (var k = 1; k <= total; k++)
        {
            foreach (var after in new[] { false, true })
            {
                await RunScriptedFlowWithCrashAsync(k, after);
            }
        }
    }

    [Fact]
    public async Task The_random_histories_reach_the_hard_paths()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var restarts = 0;
        var crashes = 0;
        for (var i = 0; i < 120; i++)
        {
            var world = await RunAsync(77_000 + i, steps: 80);
            seen.UnionWith(world.Seen);
            restarts += world.RestartCalls;
            crashes += world.Crashes;
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"restarts {restarts}, crashes {crashes}"));
        output.WriteLine(string.Join("\n", seen.Order(StringComparer.Ordinal)));
        Assert.True(restarts > 20, "too few restarts");
        Assert.True(crashes > 100, "too few crashes");
        foreach (var tag in new[]
        {
            "phase:Resuming", "phase:Verifying", "phase:RolledBack", "phase:NeedsAttention", "phase:Completed", "phase:Abandoned",
            "phase:AwaitingRestart", "phase:RestartRequested", "phase:GuidedStep", "phase:Monitoring",
            "error:" + SessionCodes.ResumeNotRealRestart, "error:" + SessionCodes.DeadmanReverted, "error:" + SessionCodes.RecoveryInterrupted,
            "error:" + SessionCodes.ScanInterrupted, "error:" + SessionCodes.RestartDidNotHappen, "reason:" + SessionCodes.Expired,
            "reason:" + SessionCodes.ResumeTooManyAttempts,
        })
        {
            Assert.Contains(tag, seen);
        }
    }

    private async Task<SimWorld> RunAsync(int seed, int steps)
    {
        var random = new Random(seed);
        var clock = new FakeClock();
        var ids = new SequentialIdGenerator(clock);
        var world = new SimWorld(clock);
        var disk = new InMemoryStateStore<VTD.Contracts.Sessions.SessionState>();
        var resumeWork = new Dictionary<string, int>(StringComparer.Ordinal);
        var counter = new SaveCounter();
        var log = new StringBuilder();
        SimEngine? engine = null;
        try
        {
            for (var step = 0; step < steps; step++)
            {
                if (engine is null)
                {
                    engine = await TryStartAsync(world, disk, clock, ids, world.TakeLogonTask(), random, counter, resumeWork, log);
                    if (engine is null)
                    {
                        continue;
                    }
                }

                try
                {
                    await RandomStepAsync(engine, random, clock, ids, log);
                    if (world.RestartCalled)
                    {
                        // The OS restarts: the engine dies, the PC boots, the player signs in.
                        world.RestartCalled = false;
                        world.Kill();
                        world.RealRestart(TimeSpan.FromMinutes(2));
                        engine = null;
                        log.AppendLine("  -> real restart");
                        continue;
                    }

                    CheckAlive(engine, resumeWork);
                }
                catch (SimulatedCrashException crash)
                {
                    log.AppendLine("  -> CRASH: " + crash.Message);
                    engine = null;
                }
            }
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            output.WriteLine(log.ToString());
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"seed {seed}: unexpected {ex.GetType().Name}: {ex.Message}"), ex);
        }

        if (world.Violations.Count > 0 || resumeWork.Values.Any(v => v > SessionLimits.MaxResumeAttempts))
        {
            output.WriteLine(log.ToString());
        }

        Assert.True(world.Violations.Count == 0, $"seed {seed}: " + string.Join("; ", world.Violations));
        Assert.All(resumeWork.Where(kv => !kv.Key.Contains('#', StringComparison.Ordinal)), kv => Assert.True(kv.Value <= SessionLimits.MaxResumeAttempts, $"seed {seed}: session {kv.Key} resumed {kv.Value} times"));
        return world;
    }

    private static async Task<SimEngine?> TryStartAsync(SimWorld world, InMemoryStateStore<VTD.Contracts.Sessions.SessionState> disk, FakeClock clock, IIdGenerator ids, string? resumeArg, Random random, SaveCounter counter, Dictionary<string, int> resumeWork, StringBuilder log)
    {
        // A crash can also hit the saves the start-up itself makes (resume counter, recovery).
        if (random.NextDouble() < 0.25)
        {
            counter.Arm(counter.Total + random.Next(1, 4), random.Next(2) == 0);
        }

        try
        {
            var (engine, outcome) = await SimEngine.StartAsync(world, disk, clock, ids, resumeArg, counter, resumeWork);
            log.AppendLine(string.Create(CultureInfo.InvariantCulture, $"START resume={resumeArg ?? "-"} -> {outcome} {SessionRuntime.Describe(engine.State)} | {world.Describe()}"));
            CheckAfterStart(engine);
            if (outcome == Session.StartupOutcome.ExitStaleResume)
            {
                // The stale-task engine exits; the player opens VTDiag.
                world.Kill();
                return null;
            }

            counter.Fired = true; // a crash armed for the start-up that did not fire is disarmed
            if (random.NextDouble() < 0.9)
            {
                await engine.Controller.HandleAsync(new UiConnected { EventId = ids.NewId(), Utc = clock.GetUtcNow(), Origin = EventOrigin.Engine }, CancellationToken.None);
                await engine.Controller.WhenIdleAsync();
            }

            return engine;
        }
        catch (SimulatedCrashException crash)
        {
            log.AppendLine("START CRASH: " + crash.Message);
            return null;
        }
    }

    private static void CheckAfterStart(SimEngine engine)
    {
        var s = engine.State;
        if (s.Phase is Phase.Applying or Phase.Scanning or Phase.Resuming or Phase.Verifying or Phase.Monitoring or Phase.RestartRequested)
        {
            engine.World.Violations.Add($"transient phase {s.Phase} survived a start");
        }

        CheckConsistency(engine);
    }

    private static void CheckAlive(SimEngine engine, Dictionary<string, int> resumeWork)
    {
        var s = engine.State;
        if (SessionMachine.IsClosed(s.Phase) && (s.Resume.Registered || engine.World.Tasks is not null))
        {
            engine.World.Violations.Add($"registration left in closed phase {s.Phase} (state={s.Resume.Registered}, world={engine.World.Tasks})");
        }

        if (s.Resume.Attempts > SessionLimits.MaxResumeAttempts)
        {
            engine.World.Violations.Add($"resume attempts {s.Resume.Attempts}");
        }

        if (s.Phase is Phase.Applying or Phase.Scanning or Phase.Resuming)
        {
            engine.World.Violations.Add($"idle in transient phase {s.Phase}");
        }

        _ = resumeWork;
        CheckConsistency(engine);
    }

    private static void CheckConsistency(SimEngine engine)
    {
        var s = engine.State;
        foreach (var a in s.Attempts)
        {
            var w = engine.World.Find(a.AttemptId);
            var claimsApplied = a.Status is AttemptStatus.Verified or AttemptStatus.Applied or AttemptStatus.AwaitingReboot;
            if (w is null)
            {
                engine.World.Violations.Add($"attempt {a.AttemptId} unknown to the PC");
                continue;
            }

            if (claimsApplied && !w.Applied)
            {
                engine.World.Violations.Add($"attempt {a.FixId} says {a.Status} but the change is not in place");
            }

            if (!claimsApplied && w.Applied && a.Status is AttemptStatus.Undone or AttemptStatus.RolledBack)
            {
                engine.World.Violations.Add($"attempt {a.FixId} says {a.Status} but the change is still in place");
            }
        }

        foreach (var w in engine.World.Attempts.Where(w => w.Applied && string.Equals(w.SessionId, s.SessionId, StringComparison.Ordinal)))
        {
            if (!s.Attempts.Any(a => string.Equals(a.AttemptId, w.AttemptId, StringComparison.Ordinal)))
            {
                engine.World.Violations.Add($"change {w.FixId} ({w.Journal}) is in place but the session lost it (phase {s.Phase})");
            }
        }
    }

    private static async Task RandomStepAsync(SimEngine engine, Random random, FakeClock clock, IIdGenerator ids, StringBuilder log)
    {
        clock.Advance(TimeSpan.FromSeconds(random.Next(1, 20)));
        await engine.Controller.WhenIdleAsync();
        var s = engine.State;
        var world = engine.World;

        // Disruptions.
        var roll = random.NextDouble();
        if (roll < 0.04)
        {
            engine.Store.Counter.Arm(engine.Store.Counter.Total + random.Next(1, 4), random.Next(2) == 0);
            log.AppendLine("  (crash armed at save " + engine.Store.Counter.CrashAt!.Value.ToString(CultureInfo.InvariantCulture) + ")");
        }
        else if (roll < 0.06)
        {
            world.CrashBeforeChange = random.Next(2) == 0;
            world.CrashAfterChange = !world.CrashBeforeChange;
        }
        else if (roll < 0.08)
        {
            log.AppendLine("POWER CUT");
            world.Kill();
            throw new SimulatedCrashException("power cut");
        }
        else if (roll < 0.10)
        {
            log.AppendLine("WINDOWS UPDATE REBOOT");
            world.Kill();
            world.RealRestart(TimeSpan.FromMinutes(5));
            world.RestartCalled = false;
            throw new SimulatedCrashException("windows update reboot");
        }
        else if (roll < 0.11)
        {
            log.AppendLine("FAST STARTUP SHUTDOWN");
            world.Kill();
            world.FastStartupShutdown(TimeSpan.FromHours(8));
            throw new SimulatedCrashException("fast startup");
        }
        else if (roll < 0.115)
        {
            log.AppendLine("8 DAYS PASS");
            clock.Advance(TimeSpan.FromDays(8));
            await engine.Controller.WhenIdleAsync();
        }

        if (s.Phase == Phase.Verifying && random.Next(3) == 0)
        {
            log.AppendLine("61 SECONDS PASS (deadman)");
            clock.Advance(TimeSpan.FromSeconds(61));
            await engine.Controller.WhenIdleAsync();
            log.AppendLine("  -> " + SessionRuntime.Describe(engine.State) + " | " + world.Describe());
            return;
        }

        var origin = random.NextDouble() switch { < 0.8 => EventOrigin.Ui, < 0.9 => EventOrigin.Cli, _ => EventOrigin.Mcp };
        var command = Pick(engine, random, clock, ids, origin, out var expectRejected);
        if (command is null)
        {
            return;
        }

        log.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{command.GetType().Name} from {command.Origin} in {s.Phase} r{s.Revision}"));
        var revision = engine.State.Revision;
        if (command is RestartNowClicked click && command.Origin == EventOrigin.Ui)
        {
            world.UiClickedRestartTokens.Add(click.Token.TokenId);
        }

        try
        {
            await engine.SendAsync(command);
            if (expectRejected)
            {
                world.Violations.Add($"{command.GetType().Name} from {command.Origin} was accepted");
            }
        }
        catch (SessionRejectedException rejected)
        {
            log.AppendLine("  rejected " + rejected.Code);
            if (rejected.Code == SessionCodes.NotAllowed && engine.State.Revision != revision)
            {
                world.Violations.Add($"a refused {command.GetType().Name} changed the session");
            }
        }

        log.AppendLine("  -> " + SessionRuntime.Describe(engine.State) + " | " + world.Describe());
    }

    private static SessionCommand? Pick(SimEngine engine, Random random, FakeClock clock, IIdGenerator ids, EventOrigin origin, out bool expectRejected)
    {
        var s = engine.State;
        expectRejected = false;
        T C<T>(T c)
            where T : SessionCommand => c with { Origin = origin };
        string Id() => ids.NewId();
        var now = clock.GetUtcNow();
        var fixId = FixIds[random.Next(FixIds.Length)];

        // Sometimes: a UI-only command from an agent (must be refused).
        if (origin != EventOrigin.Ui && random.NextDouble() < 0.5)
        {
            expectRejected = true;
            return random.Next(5) switch
            {
                0 => C(new ConsentGiven { EventId = Id(), Utc = now, Token = new ConsentToken { TokenId = Id(), SessionId = s.SessionId, PreviewId = s.CurrentPreviewId ?? "x", FixId = s.CurrentFixId ?? fixId, BindingHash = "b", PreviewHash = "h", Nonce = "guess", IssuedUtc = now } }),
                1 => C(new RestartNowClicked { EventId = Id(), Utc = now, Token = new RestartToken { TokenId = Id(), SessionId = s.SessionId, Kind = RestartKind.Normal, Nonce = "guess", IssuedUtc = now } }),
                2 => C(new UndoAllRequested { EventId = Id(), Utc = now }),
                3 => C(new DeadmanAnswered { EventId = Id(), Utc = now, AttemptId = s.CurrentAttemptId ?? "x", Keep = true }),
                _ => C(new AbandonRequested { EventId = Id(), Utc = now }),
            };
        }

        switch (s.Phase)
        {
            case Phase.Intake:
                return random.Next(3) == 0 ? C(new IntakeSubmitted { EventId = Id(), Utc = now, Intake = new IntakeAnswers { Symptoms = ["stutter"] } }) : C(new ScanRequested { EventId = Id(), Utc = now });
            case Phase.Analyzed or Phase.Proposing:
                return random.Next(10) switch
                {
                    < 5 => C(new FixSelected { EventId = Id(), Utc = now, FixId = fixId, Binding = FixBinding.Empty }),
                    5 => C(new QuestionAnswered { EventId = Id(), Utc = now, QuestionId = "ask.only_valorant_affected", OptionId = "yes" }),
                    6 => C(new MonitorRequested { EventId = Id(), Utc = now }),
                    7 => C(new RescanRequested { EventId = Id(), Utc = now }),
                    8 => C(new UndoAllRequested { EventId = Id(), Utc = now }),
                    _ => C(new GuideStarted { EventId = Id(), Utc = now, GuideId = "guide.secureboot.enable", BranchId = "uefi_sb_off" }),
                };
            case Phase.AwaitingConsent:
                var preview = engine.Runtime.Previews.Find(s.CurrentPreviewId);
                if (preview is null || random.Next(6) == 0)
                {
                    return C(new FixSkipped { EventId = Id(), Utc = now, FixId = s.CurrentFixId ?? fixId });
                }

                if (random.Next(8) == 0)
                {
                    // A forged UI token (wrong nonce): refused, the phase stays.
                    return C(new ConsentGiven { EventId = Id(), Utc = now, Token = Token(s, preview, ids, now) with { Nonce = "forged" } });
                }

                var second = engine.LastConsentRequest is { Kind: ConsentKind.SecondConsent } req && string.Equals(req.PreviewId, preview.PreviewId, StringComparison.Ordinal) ? req.SecondConsentNonce : null;
                return C(new ConsentGiven { EventId = Id(), Utc = now, Token = Token(s, preview, ids, now) with { SecondConsentNonce = second } });
            case Phase.Verifying:
                return C(new DeadmanAnswered { EventId = Id(), Utc = now, AttemptId = s.CurrentAttemptId!, Keep = random.Next(2) == 0 });
            case Phase.AwaitingRestart:
                switch (random.Next(6))
                {
                    case 0:
                        return C(new RestartLaterChosen { EventId = Id(), Utc = now });
                    case 1:
                        return C(new RestartNowClicked { EventId = Id(), Utc = now, Token = new RestartToken { TokenId = Id(), SessionId = s.SessionId, Kind = RestartKind.Normal, Nonce = "forged", IssuedUtc = now } });
                    case 2 when s.CurrentAttemptId is { } attempt:
                        return C(new UndoRequested { EventId = Id(), Utc = now, AttemptId = attempt });
                    default:
                        var prompt = engine.Runtime.PrepareRestartAsync(s, RestartKind.Normal, CancellationToken.None).GetAwaiter().GetResult();
                        return C(new RestartNowClicked { EventId = Id(), Utc = now, Token = new RestartToken { TokenId = Id(), SessionId = s.SessionId, Kind = RestartKind.Normal, Nonce = prompt.Nonce, IssuedUtc = now } });
                }

            case Phase.AwaitingOutcome:
                var current = s.CurrentAttemptId ?? s.Attempts.LastOrDefault()?.AttemptId;
                return random.Next(5) switch
                {
                    0 => C(new MonitorRequested { EventId = Id(), Utc = now, Purpose = Contracts.Monitoring.MonitorPurpose.OutcomeCheck }),
                    1 when current is not null => C(new UndoRequested { EventId = Id(), Utc = now, AttemptId = current }),
                    _ when current is not null => C(new OutcomeReported { EventId = Id(), Utc = now, AttemptId = current, Outcome = (FixOutcome)random.Next(1, 4) }),
                    _ => C(new ScanRequested { EventId = Id(), Utc = now }),
                };
            case Phase.GuidedStep:
                if (random.Next(3) == 0 && s.Guide is { } g)
                {
                    var prompt = engine.Runtime.PrepareRestartAsync(s, RestartKind.ToFirmware, CancellationToken.None).GetAwaiter().GetResult();
                    return C(new RestartNowClicked { EventId = Id(), Utc = now, Token = new RestartToken { TokenId = Id(), SessionId = s.SessionId, Kind = RestartKind.ToFirmware, Nonce = prompt.Nonce, IssuedUtc = now } });
                }

                return random.Next(2) == 0
                    ? C(new GuideStepCompleted { EventId = Id(), Utc = now, GuideId = s.Guide!.GuideId, StepIndex = s.Guide.StepIndex })
                    : C(new RescanRequested { EventId = Id(), Utc = now });
            case Phase.NeedsAttention:
                return random.Next(2) == 0 ? C(new UndoAllRequested { EventId = Id(), Utc = now }) : C(new AbandonRequested { EventId = Id(), Utc = now });
            case Phase.Completed or Phase.RolledBack or Phase.Abandoned:
                return C(new ScanRequested { EventId = Id(), Utc = now });
            default:
                return null;
        }
    }

    private static ConsentToken Token(SessionState s, FixPreview preview, IIdGenerator ids, DateTimeOffset now) => new()
    {
        TokenId = ids.NewId(),
        SessionId = s.SessionId,
        PreviewId = preview.PreviewId,
        FixId = preview.FixId,
        BindingHash = preview.BindingHash,
        PreviewHash = preview.PreviewHash,
        Nonce = preview.ConsentNonce!,
        IssuedUtc = now,
    };

    // ---- exhaustive crash points over one scripted flow -------------------------------------------------------------

    private static async Task<int> CountSavesOfScriptedFlowAsync()
    {
        var harness = new ScriptedFlow(new SaveCounter());
        await harness.RunAsync();
        Assert.Empty(harness.World.Violations);
        Assert.Equal(Phase.Completed, harness.FinalPhase);
        return harness.Counter.Total;
    }

    private async Task RunScriptedFlowWithCrashAsync(int crashAt, bool crashAfterWrite)
    {
        var counter = new SaveCounter();
        counter.Arm(crashAt, crashAfterWrite);
        var harness = new ScriptedFlow(counter);
        await harness.RunAsync();
        if (harness.World.Violations.Count > 0)
        {
            output.WriteLine(harness.Log.ToString());
        }

        Assert.True(counter.Fired, string.Create(CultureInfo.InvariantCulture, $"crash at save {crashAt} never fired"));
        Assert.True(harness.World.Violations.Count == 0, string.Create(CultureInfo.InvariantCulture, $"crash at save {crashAt} ({(crashAfterWrite ? "after" : "before")} write): ") + string.Join("; ", harness.World.Violations));
        Assert.True(harness.World.RestartCalls <= 2, "more restarts than clicks");
        Assert.Equal(Phase.Completed, harness.FinalPhase);
    }

    /// <summary>
    /// Scan → select the restart-requiring fix → consent → apply → "Restart now" → real reboot → resume → verify →
    /// "Fixed". The player keeps pushing the flow forward after any crash (the UI is reopened), so a crash at save k is
    /// followed by an engine restart and the rest of the flow.
    /// </summary>
    private sealed class ScriptedFlow(SaveCounter counter)
    {
        private readonly FakeClock _clock = new();
        private readonly InMemoryStateStore<SessionState> _disk = new();
        private readonly Dictionary<string, int> _resumeWork = new(StringComparer.Ordinal);
        private IIdGenerator? _ids;
        private SimWorld? _world;

        public SimWorld World => _world ??= new SimWorld(_clock);

        public SaveCounter Counter => counter;

        public StringBuilder Log { get; } = new();

        public Phase FinalPhase { get; private set; }

        private IIdGenerator Ids => _ids ??= new SequentialIdGenerator(_clock);

        public async Task RunAsync()
        {
            string? resume = null;
            for (var round = 0; round < 12; round++)
            {
                SimEngine engine;
                try
                {
                    var started = await SimEngine.StartAsync(World, _disk, _clock, Ids, resume, counter, _resumeWork);
                    engine = started.Engine;
                    resume = null;
                    Log.AppendLine("START -> " + started.Outcome + " " + SessionRuntime.Describe(engine.State) + " | " + World.Describe());
                    CheckAfterStart(engine);
                    if (started.Outcome == StartupOutcome.ExitStaleResume)
                    {
                        World.Kill();
                        continue;
                    }

                    await engine.Controller.HandleAsync(new UiConnected { EventId = Ids.NewId(), Utc = _clock.GetUtcNow(), Origin = EventOrigin.Engine }, CancellationToken.None);
                    await engine.Controller.WhenIdleAsync();
                }
                catch (SimulatedCrashException e)
                {
                    Log.AppendLine("START CRASH " + e.Message);
                    continue;
                }

                try
                {
                    for (var step = 0; step < 30; step++)
                    {
                        CheckAlive(engine, _resumeWork);
                        var s = engine.State;
                        Log.AppendLine("  " + SessionRuntime.Describe(s) + " | " + World.Describe());
                        if (s.Phase == Phase.Completed)
                        {
                            FinalPhase = s.Phase;
                            return;
                        }

                        await NextAsync(engine, s);
                        if (World.RestartCalled)
                        {
                            World.RestartCalled = false;
                            World.Kill();
                            World.RealRestart(TimeSpan.FromMinutes(2));
                            resume = World.Tasks;
                            Log.AppendLine("  REBOOT task=" + (resume ?? "-"));
                            break;
                        }
                    }
                }
                catch (SimulatedCrashException e)
                {
                    Log.AppendLine("  CRASH " + e.Message);
                }
            }

            World.Violations.Add("the flow never completed");
        }

        private async Task NextAsync(SimEngine engine, SessionState s)
        {
            var now = _clock.GetUtcNow();
            var verified = s.Attempts.LastOrDefault(a => a.Status == AttemptStatus.Verified);
            SessionCommand command = s.Phase switch
            {
                Phase.Intake or Phase.Abandoned => new ScanRequested { EventId = Ids.NewId(), Utc = now },
                Phase.Analyzed or Phase.Proposing when verified is null && !s.Attempts.Any(a => a.Status == AttemptStatus.AwaitingReboot)
                    => new FixSelected { EventId = Ids.NewId(), Utc = now, FixId = "fix.display.hags_off", Binding = FixBinding.Empty },
                Phase.Analyzed or Phase.Proposing when verified is not null => new UndoAllRequested { EventId = Ids.NewId(), Utc = now },
                Phase.AwaitingConsent => new ConsentGiven { EventId = Ids.NewId(), Utc = now, Token = Token(s, engine.Runtime.Previews.Find(s.CurrentPreviewId)!, Ids, now) },
                Phase.AwaitingRestart => new RestartNowClicked
                {
                    EventId = Ids.NewId(),
                    Utc = now,
                    Token = new RestartToken { TokenId = Ids.NewId(), SessionId = s.SessionId, Kind = RestartKind.Normal, Nonce = (await engine.Runtime.PrepareRestartAsync(s, RestartKind.Normal, CancellationToken.None)).Nonce, IssuedUtc = now },
                },
                Phase.AwaitingOutcome => new OutcomeReported { EventId = Ids.NewId(), Utc = now, AttemptId = s.CurrentAttemptId ?? s.Attempts[^1].AttemptId, Outcome = FixOutcome.Fixed },
                Phase.NeedsAttention => new AbandonRequested { EventId = Ids.NewId(), Utc = now },
                Phase.RolledBack => new ScanRequested { EventId = Ids.NewId(), Utc = now },
                _ => throw new InvalidOperationException("unexpected phase " + s.Phase),
            };
            command = command with { Origin = EventOrigin.Ui };
            if (command is RestartNowClicked click)
            {
                World.UiClickedRestartTokens.Add(click.Token.TokenId);
            }

            try
            {
                await engine.SendAsync(command);
            }
            catch (SessionRejectedException ex)
            {
                World.Violations.Add($"scripted {command.GetType().Name} refused in {s.Phase}: {ex.Code}");
            }
        }
    }
}