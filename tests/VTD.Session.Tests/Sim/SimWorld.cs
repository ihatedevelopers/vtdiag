using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;
using VTD.Session;
using VTD.Testing.Fakes;

namespace VTD.Session.Tests.Sim;

/// <summary>The process died (power cut, crash, restart): thrown by every simulated component of a dead engine.</summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("simulated crash")
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The "PC" the property tests drive: which changes are really in place, what the write-ahead journal would say about
/// in-flight changes, the resume registration, the boot identity, and every invariant violation seen. Each engine process
/// gets a generation; components of an older generation (a dead process) throw on any use.
/// </summary>
public sealed class SimWorld
{
    private readonly Dictionary<string, SimAttempt> _attempts = new(StringComparer.Ordinal);

    public SimWorld(FakeClock clock)
    {
        Clock = clock;
        Boot = new BootIdentity { BootId = 100, LastBootUpTimeUtc = clock.GetUtcNow().AddHours(-2), Kind = BootKind.Cold };
    }

    public FakeClock Clock { get; }

    public int Generation { get; private set; }

    public bool Dead { get; private set; }

    public BootIdentity Boot { get; set; }

    /// <summary>Session id the resume task would start, or null.</summary>
    public string? Tasks { get; set; }

    public bool RestartCalled { get; set; }

    public List<string> Violations { get; } = [];

    /// <summary>Coverage tags (phases entered, reasons, error codes) so the property test can prove it reached the hard paths.</summary>
    public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

    public int Crashes { get; private set; }

    /// <summary>Token ids minted by a "Restart now" click in the verified UI (whether or not the engine accepted it).</summary>
    public HashSet<string> UiClickedRestartTokens { get; } = new(StringComparer.Ordinal);

    public int RestartCalls { get; set; }

    /// <summary>Crash after writing the intent, before the change.</summary>
    public bool CrashBeforeChange { get; set; }

    /// <summary>Crash after the change, before the runner returns.</summary>
    public bool CrashAfterChange { get; set; }

    public IReadOnlyCollection<SimAttempt> Attempts => _attempts.Values;

    public int NewProcess()
    {
        Generation++;
        Dead = false;
        return Generation;
    }

    public void Kill()
    {
        Crashes++;
        Dead = true;
        Generation++;
    }

    public void ThrowIfDead(int generation)
    {
        if (Dead || generation != Generation)
        {
            throw new SimulatedCrashException();
        }
    }

    public SimAttempt Add(SimAttempt attempt)
    {
        _attempts[attempt.AttemptId] = attempt;
        return attempt;
    }

    public SimAttempt? Find(string attemptId) => _attempts.GetValueOrDefault(attemptId);

    private bool _justBooted;

    /// <summary>The resume task fires at the first logon after a boot (only then).</summary>
    public string? TakeLogonTask()
    {
        var task = _justBooted ? Tasks : null;
        _justBooted = false;
        return task;
    }

    public void RealRestart(TimeSpan downtime)
    {
        _justBooted = true;
        Clock.Advance(downtime);
        Boot = new BootIdentity { BootId = Boot.BootId + 1, LastBootUpTimeUtc = Clock.GetUtcNow(), Kind = BootKind.Cold };
    }

    public void FastStartupShutdown(TimeSpan downtime)
    {
        _justBooted = true;
        Clock.Advance(downtime);
        Boot = Boot with { BootId = Boot.BootId + 1, Kind = BootKind.FastStartupResume };
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"gen={Generation} dead={Dead} boot={Boot.BootId} tasks={Tasks ?? "-"} attempts={string.Join(",", _attempts.Values.Select(a => a.FixId + ":" + a.Journal + (a.Applied ? "+" : "-")))}");
}

/// <summary>Journal view of one attempt.</summary>
public enum SimJournal
{
    InFlight,
    Done,
    DeadmanArmed,
}

public sealed class SimAttempt
{
    public required string AttemptId { get; init; }

    public required string SessionId { get; init; }

    public required string FixId { get; init; }

    public required Reversibility Reversibility { get; init; }

    public required bool RebootRequired { get; init; }

    public bool Applied { get; set; }

    public bool RebootVerified { get; set; }

    public SimJournal Journal { get; set; }

    public AttemptStatus Status { get; set; } = AttemptStatus.Applying;

    public DateTimeOffset AppliedUtc { get; init; }
}

/// <summary>An <see cref="IFixRunner"/> over <see cref="SimWorld"/> with the real consent contract.</summary>
public sealed class SimFixRunner(SimWorld world, int generation, IKnowledgeBase kb, TimeProvider clock, IIdGenerator ids) : IFixRunner
{
    private readonly Dictionary<string, (FixPreview Preview, bool Used, string? Second)> _previews = new(StringComparer.Ordinal);
    private bool _running;

    public Task<FixPreview> PreviewAsync(string fixId, FixBinding binding, Proposer proposedBy, CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        var fix = kb.FindFix(fixId) ?? throw new KeyNotFoundException(fixId);
        var now = clock.GetUtcNow();
        var preview = new FixPreview
        {
            PreviewId = ids.NewId(),
            FixId = fix.Id,
            Binding = binding,
            BindingHash = binding.ComputeHash(),
            PreviewHash = CanonicalHash.OfText(fix.Id + "|" + binding.ComputeHash()),
            ConsentNonce = ids.NewId(),
            CreatedUtc = now,
            ExpiresUtc = now + SessionLimits.TokenLifetime,
            ProposedBy = proposedBy,
            TitleKey = fix.TitleKey,
            ExplainKey = fix.ExplainKey,
            UndoExplainKey = fix.UndoExplainKey,
            Risk = fix.Risk,
            Reversibility = fix.Reversibility,
            Reboot = fix.Reboot,
            Scope = fix.Scope,
            NeedsAdmin = fix.Admin,
            RequiresSecondConsent = fix.Reversibility == Reversibility.OneWay,
            RestorePointFirst = false,
            Deadman = fix.Deadman,
            Steps = [],
            CanApply = true,
        };
        _previews[preview.PreviewId] = (preview, false, null);
        return Task.FromResult(preview);
    }

    public Task<FixRunResult> ApplyAsync(ConsentToken consent, IProgress<FixProgress>? progress, CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        if (_running)
        {
            throw new InvalidOperationException("Another fix is running.");
        }

        if (!_previews.TryGetValue(consent.PreviewId, out var entry))
        {
            throw new InvalidOperationException("consent.unknown_preview");
        }

        if (entry.Used)
        {
            throw new InvalidOperationException("consent.replayed");
        }

        if (!string.Equals(consent.Nonce, entry.Preview.ConsentNonce, StringComparison.Ordinal) || !string.Equals(consent.PreviewHash, entry.Preview.PreviewHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("consent.preview_changed");
        }

        var preview = entry.Preview;
        if (preview.RequiresSecondConsent && consent.SecondConsentNonce is null)
        {
            var second = ids.NewId();
            _previews[consent.PreviewId] = (preview, false, second);
            return Task.FromResult(new FixRunResult { AttemptId = string.Empty, FixId = preview.FixId, Status = AttemptStatus.AwaitingSecondConsent, SecondConsentNonce = second });
        }

        if (preview.RequiresSecondConsent && !string.Equals(consent.SecondConsentNonce, entry.Second, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("consent.second_consent_mismatch");
        }

        _running = true;
        try
        {
            _previews[consent.PreviewId] = (preview, true, entry.Second);
            var attempt = world.Add(new SimAttempt
            {
                AttemptId = ids.NewId(),
                SessionId = consent.SessionId,
                FixId = preview.FixId,
                Reversibility = preview.Reversibility,
                RebootRequired = preview.Reboot == RebootNeed.Required,
                Journal = SimJournal.InFlight,
                AppliedUtc = clock.GetUtcNow(),
            });
            if (world.CrashBeforeChange)
            {
                world.CrashBeforeChange = false;
                world.Kill();
                throw new SimulatedCrashException("crash before change");
            }

            attempt.Applied = true;
            if (world.CrashAfterChange)
            {
                world.CrashAfterChange = false;
                world.Kill();
                throw new SimulatedCrashException("crash after change");
            }

            DeadmanState? deadman = null;
            if (preview.Deadman is { } d)
            {
                attempt.Journal = SimJournal.DeadmanArmed;
                deadman = new DeadmanState { Status = DeadmanStatus.Armed, DeadlineUtc = clock.GetUtcNow().AddSeconds(d.Seconds) };
            }
            else
            {
                attempt.Journal = SimJournal.Done;
            }

            attempt.Status = attempt.RebootRequired ? AttemptStatus.AwaitingReboot : AttemptStatus.Verified;
            return Task.FromResult(new FixRunResult
            {
                AttemptId = attempt.AttemptId,
                FixId = attempt.FixId,
                Status = attempt.Status,
                RebootPending = attempt.RebootRequired,
                Deadman = deadman,
            });
        }
        finally
        {
            _running = false;
        }
    }

    public Task<VerifyResult> VerifyAsync(string attemptId, CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        var a = world.Find(attemptId) ?? throw new KeyNotFoundException(attemptId);
        if (a.Applied)
        {
            a.RebootVerified = true;
            a.Status = AttemptStatus.Verified;
        }

        return Task.FromResult(new VerifyResult { AttemptId = attemptId, Status = a.Applied ? VerifyStatus.Passed : VerifyStatus.Failed, VerifiedUtc = clock.GetUtcNow() });
    }

    public Task<FixRunResult> ConfirmDeadmanAsync(string attemptId, bool keep, CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        var a = world.Find(attemptId) ?? throw new KeyNotFoundException(attemptId);
        if (a.Journal != SimJournal.DeadmanArmed)
        {
            return Task.FromResult(new FixRunResult { AttemptId = attemptId, FixId = a.FixId, Status = a.Status });
        }

        a.Journal = SimJournal.Done;
        if (keep)
        {
            a.Status = AttemptStatus.Verified;
            return Task.FromResult(new FixRunResult { AttemptId = attemptId, FixId = a.FixId, Status = AttemptStatus.Verified, Deadman = new DeadmanState { Status = DeadmanStatus.Confirmed, DeadlineUtc = clock.GetUtcNow() } });
        }

        a.Applied = false;
        a.Status = AttemptStatus.RolledBack;
        return Task.FromResult(new FixRunResult { AttemptId = attemptId, FixId = a.FixId, Status = AttemptStatus.RolledBack, Deadman = new DeadmanState { Status = DeadmanStatus.Reverted, DeadlineUtc = clock.GetUtcNow() } });
    }

    public Task<UndoResult> UndoAsync(string attemptId, UndoMode mode, CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        var a = world.Find(attemptId) ?? throw new KeyNotFoundException(attemptId);
        if (a.Reversibility == Reversibility.OneWay)
        {
            return Task.FromResult(new UndoResult { AttemptId = attemptId, Status = UndoStatus.NotUndoable });
        }

        if (!a.Applied)
        {
            return Task.FromResult(new UndoResult { AttemptId = attemptId, Status = UndoStatus.NothingToUndo });
        }

        a.Applied = false;
        a.Journal = SimJournal.Done;
        a.Status = AttemptStatus.Undone;
        return Task.FromResult(new UndoResult { AttemptId = attemptId, Status = UndoStatus.Undone });
    }

    public Task<IReadOnlyList<DriftFinding>> CheckDriftAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DriftFinding>>([]);

    public Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        return Task.FromResult<IReadOnlyList<AttemptSummary>>([.. world.Attempts
            .Where(a => a.Journal != SimJournal.InFlight)
            .Select(a => new AttemptSummary
            {
                AttemptId = a.AttemptId,
                SessionId = a.SessionId,
                FixId = a.FixId,
                TitleKey = a.FixId,
                Status = a.Status,
                Outcome = FixOutcome.Pending,
                Reversibility = a.Reversibility,
                AppliedUtc = a.AppliedUtc,
                CanUndo = a.Applied && a.Reversibility != Reversibility.OneWay,
            })]);
    }
}

/// <summary>Recovery over the simulated journal: in-flight intents are resolved by probing, armed deadmen are undone.</summary>
public sealed class SimRecovery(SimWorld world, int generation, TimeProvider clock) : IRecovery
{
    public Task<RecoveryReport> RunAsync(CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        var items = new List<RecoveryItem>();
        foreach (var a in world.Attempts.ToList())
        {
            if (a.Journal == SimJournal.InFlight)
            {
                a.Journal = SimJournal.Done;
                a.Status = a.Applied ? (a.RebootRequired ? AttemptStatus.AwaitingReboot : AttemptStatus.Applied) : AttemptStatus.Failed;
                items.Add(new RecoveryItem { AttemptId = a.AttemptId, FixId = a.FixId, StepIndex = 0, Op = "sim", Resolution = a.Applied ? RecoveryResolution.RolledForward : RecoveryResolution.NotApplied });
            }
            else if (a.Journal == SimJournal.DeadmanArmed)
            {
                a.Journal = SimJournal.Done;
                a.Applied = false;
                a.Status = AttemptStatus.RolledBack;
                items.Add(new RecoveryItem { AttemptId = a.AttemptId, FixId = a.FixId, StepIndex = 0, Op = "sim", Resolution = RecoveryResolution.RolledBack });
            }
        }

        return Task.FromResult(new RecoveryReport { RanUtc = clock.GetUtcNow(), Items = items });
    }
}

/// <summary>The reboot coordinator over <see cref="SimWorld"/>; it checks invariant R1 on every restart call.</summary>
public sealed class SimReboot(SimWorld world, int generation, RestartPromptLedger ledger) : IRebootCoordinator
{
    public Fact<BootIdentity> CurrentBoot() => Fact.Known(world.Boot, "sim");

    public Task<RebootReadiness> CheckReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(new RebootReadiness
    {
        OnAcPower = Tri.True,
        WindowsUpdateRebootPending = Tri.False,
        WindowsUpdateInstalling = Tri.False,
        FastStartupEnabled = Tri.True,
        TaskSchedulerAvailable = Tri.True,
        InteractiveUserIsAdmin = Tri.True,
        FirmwareRestartSupported = Tri.True,
        PlannedMechanism = ResumeMechanism.ScheduledTasks,
    });

    public Task<ResumeRegistration> RegisterResumeAsync(string sessionId, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        world.Tasks = sessionId;
        return Task.FromResult(new ResumeRegistration { Mechanism = ResumeMechanism.ScheduledTasks, Verified = true });
    }

    public Task UnregisterResumeAsync(CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        world.Tasks = null;
        return Task.CompletedTask;
    }

    public Task RestartAsync(RestartToken token, CancellationToken cancellationToken)
    {
        world.ThrowIfDead(generation);
        world.RestartCalls++;
        if (!world.UiClickedRestartTokens.Contains(token.TokenId))
        {
            world.Violations.Add("R1: restart with a token no UI click minted: " + token.TokenId);
        }

        if (!ledger.TryConsume(token))
        {
            world.Violations.Add("R1: restart with a token the engine did not arm: " + token.TokenId);
            throw new InvalidOperationException("restart.token_invalid");
        }

        world.RestartCalled = true;
        return Task.CompletedTask;
    }
}

/// <summary>Counts saves across engine processes; a crash fires once, at <see cref="CrashAt"/>.</summary>
public sealed class SaveCounter
{
    public int Total { get; set; }

    public int? CrashAt { get; set; }

    public bool CrashAfterWrite { get; set; }

    public bool Fired { get; set; }

    public void Arm(int atTotal, bool afterWrite)
    {
        CrashAt = atTotal;
        CrashAfterWrite = afterWrite;
        Fired = false;
    }
}

/// <summary>The disk as one engine process sees it: saves fail once the process is dead; a crash can be injected at any save.</summary>
public sealed class CrashingStore(InMemoryStateStore<SessionState> disk, SimWorld world, int generation, SaveCounter counter) : IStateStore<SessionState>
{
    public SaveCounter Counter => counter;

    public long Revision { get; set; }

    public StateLoadResult<SessionState> Load()
    {
        world.ThrowIfDead(generation);
        var load = disk.Load();
        Revision = load.State?.Revision ?? 0;
        return load;
    }

    public void Save(SessionState state)
    {
        world.ThrowIfDead(generation);
        counter.Total++;
        if (state.Revision != Revision + 1)
        {
            world.Violations.Add(string.Create(CultureInfo.InvariantCulture, $"revision {state.Revision} after {Revision}"));
        }

        var crash = !counter.Fired && counter.Total == counter.CrashAt;
        if (crash && !counter.CrashAfterWrite)
        {
            counter.Fired = true;
            world.Kill();
            throw new SimulatedCrashException("crash before write");
        }

        disk.Save(state);
        Revision = state.Revision;
        if (crash)
        {
            counter.Fired = true;
            world.Kill();
            throw new SimulatedCrashException("crash after write");
        }
    }
}