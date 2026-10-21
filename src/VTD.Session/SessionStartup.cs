using VTD.Contracts;
using VTD.Contracts.Journaling;
using VTD.Contracts.Sessions;

namespace VTD.Session;

/// <summary>What the engine should do after start-up.</summary>
public enum StartupOutcome
{
    /// <summary>Serve clients.</summary>
    Serve = 0,

    /// <summary>A resume task for another (or a finished) session fired: it was cleaned up; exit.</summary>
    ExitStaleResume = 1,
}

/// <summary>
/// Engine start-up order (design_B.md sections 6.4 and 6.5), before any RPC is accepted:
/// <list type="number">
/// <item>Resume mode (<c>--resume &lt;id&gt;</c> from the logon task): a stale task is removed and the engine exits;
/// otherwise <see cref="ResumeStarted"/> is handled FIRST, so the attempt counter is persisted before anything else.</item>
/// <item>A manual start that finds the PC really restarted since "Restart now" continues the same resume (counted too).</item>
/// <item>Crash recovery (A3) runs and its report goes through the machine (in-flight phases resolve; a terminal session
/// cleans up any leftover registration; an unfinished "undo all" is finished).</item>
/// <item>An expired, open session is closed.</item>
/// </list>
/// </summary>
public static class SessionStartup
{
    /// <summary>Runs the start-up sequence.</summary>
    /// <param name="controller">Controller (state loaded).</param>
    /// <param name="runtime">Runtime.</param>
    /// <param name="resumeSessionId">The <c>--resume</c> argument, if any.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<StartupOutcome> RunAsync(SessionController controller, SessionRuntime runtime, string? resumeSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(runtime);
        var state = controller.Current;
        var clock = runtime.Services.Clock;
        var ids = runtime.Services.Ids;
        var boot = runtime.CurrentBoot();
        var outcome = StartupOutcome.Serve;
        if (resumeSessionId is not null)
        {
            if (!string.Equals(state.SessionId, resumeSessionId, StringComparison.Ordinal))
            {
                // A task for a session that is not the current one. Never remove the CURRENT session's registration.
                if (!(SessionMachine.IsResumable(state) && state.Resume.Registered) && runtime.Services.Reboot is { } reboot)
                {
                    await reboot.UnregisterResumeAsync(cancellationToken).ConfigureAwait(false);
                }

                outcome = StartupOutcome.ExitStaleResume;
            }
            else
            {
                // Unknown boot (registry unreadable): treat as a real restart; the attempt cap still bounds it.
                var seen = boot ?? new BootIdentity { BootId = uint.MaxValue, LastBootUpTimeUtc = clock.GetUtcNow(), Kind = BootKind.Unknown };
                await controller.HandleAsync(new ResumeStarted { EventId = ids.NewId(), Utc = clock.GetUtcNow(), Boot = seen, Origin = EventOrigin.Resume }, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (boot is not null && NeedsResume(state, boot))
        {
            await controller.HandleAsync(new ResumeStarted { EventId = ids.NewId(), Utc = clock.GetUtcNow(), Boot = boot, Origin = EventOrigin.Engine }, cancellationToken).ConfigureAwait(false);
        }

        var recovered = await runtime.RecoverAsync(cancellationToken).ConfigureAwait(false);
        await controller.HandleAsync(recovered, cancellationToken).ConfigureAwait(false);

        if (!SessionMachine.IsClosed(controller.Current.Phase) && clock.GetUtcNow() > controller.Current.ExpiresUtc)
        {
            await controller.HandleAsync(new SessionExpired { EventId = ids.NewId(), Utc = clock.GetUtcNow(), Origin = EventOrigin.Engine }, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>A manual start after the PC really restarted continues the pending resume.</summary>
    /// <param name="state">State.</param>
    /// <param name="boot">Current boot.</param>
    public static bool NeedsResume(SessionState state, BootIdentity boot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(boot);
        if (!SessionMachine.IsResumable(state))
        {
            return false;
        }

        return state.Phase == Phase.Resuming || (state.Restart is { } restart && BootIdentities.IsRealRestart(restart.BootAtRequest, boot));
    }
}

/// <summary>
/// <see cref="IStateRebuilder{T}"/> for the session (A3's state store calls it when both <c>current.json</c> and
/// <c>current.bak</c> are unreadable). The journal alone lists every change, so the rebuilt session is conservative:
/// NeedsAttention ("here is what we changed: [Undo all] [Keep]"), resume mechanism assumed registered so start-up removes
/// any leftover task, and attempt statuses taken from the last journal phase of each attempt.
/// </summary>
public sealed class SessionStateRebuilder : IStateRebuilder<SessionState>
{
    /// <summary>Reason key of a rebuilt session.</summary>
    public const string RebuiltReason = "session.rebuilt";

    /// <inheritdoc />
    public SessionState? Rebuild(IReadOnlyList<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return null;
        }

        // The latest session in the journal (ULIDs sort by time); records in sequence order.
        var sessionId = records.Select(r => r.SessionId).Where(Ulid.IsValid).Order(StringComparer.Ordinal).LastOrDefault()
                        ?? records[^1].SessionId;
        var mine = records.Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)).OrderBy(r => r.Seq).ToList();
        if (mine.Count == 0)
        {
            return null;
        }

        var attempts = mine
            .GroupBy(r => r.AttemptId, StringComparer.Ordinal)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g =>
            {
                var first = g.First();
                var last = g.Last();
                return new FixAttempt
                {
                    AttemptId = g.Key,
                    FixId = first.FixId,
                    BindingHash = string.Empty,
                    Status = StatusOf(g.Select(r => r.Phase).ToList()),
                    Reversibility = Contracts.Fixes.Reversibility.BestEffort,
                    AppliedUtc = first.Utc,
                    VerifiedUtc = g.Any(r => r.Phase == JournalPhase.Verified) ? g.First(r => r.Phase == JournalPhase.Verified).Utc : null,
                    PlanId = first.ProposedBy?.PlanId,
                };
            })
            .ToList();
        var created = mine[0].Utc;
        var updated = mine[^1].Utc;
        return new SessionState
        {
            SessionId = sessionId,
            SchemaVersion = SessionState.CurrentSchemaVersion,
            Revision = Math.Max(1, mine[^1].Seq),
            Phase = Phase.NeedsAttention,
            CreatedUtc = created,
            UpdatedUtc = updated,
            ExpiresUtc = created + SessionLimits.Expiry,
            Privilege = PrivilegeMode.Elevated,
            KbVersion = mine[^1].KbVersion,
            Attempts = attempts,
            Resume = new ResumeInfo { Mechanism = ResumeMechanism.ScheduledTasks, Registered = false },
            NeedsAttentionReasonKey = RebuiltReason,
        };
    }

    private static Contracts.Fixes.AttemptStatus StatusOf(IReadOnlyList<JournalPhase> phases)
    {
        var last = phases[^1];
        return last switch
        {
            JournalPhase.Undone => Contracts.Fixes.AttemptStatus.Undone,
            JournalPhase.DeadmanReverted or JournalPhase.RecoveredNotApplied => Contracts.Fixes.AttemptStatus.RolledBack,
            JournalPhase.UndoConflict => Contracts.Fixes.AttemptStatus.UndoConflict,
            JournalPhase.InDoubt or JournalPhase.UndoFailed or JournalPhase.Intent or JournalPhase.UndoIntent => Contracts.Fixes.AttemptStatus.InDoubt,
            JournalPhase.Failed => phases.Contains(JournalPhase.Applied) ? Contracts.Fixes.AttemptStatus.InDoubt : Contracts.Fixes.AttemptStatus.Failed,
            JournalPhase.Verified or JournalPhase.DeadmanConfirmed => Contracts.Fixes.AttemptStatus.Verified,
            _ => Contracts.Fixes.AttemptStatus.Applied,
        };
    }
}

/// <summary>
/// In-memory <see cref="IStateStore{T}"/> for LIMITED sessions, which are never persisted to <c>%ProgramData%</c> (nothing
/// can change, so nothing needs to survive a crash or a restart).
/// </summary>
public sealed class VolatileStateStore : IStateStore<SessionState>
{
    private SessionState? _state;

    /// <inheritdoc />
    public StateLoadResult<SessionState> Load()
        => _state is null ? new StateLoadResult<SessionState> { Source = StateLoadSource.Empty } : new StateLoadResult<SessionState> { State = _state, Source = StateLoadSource.Primary };

    /// <inheritdoc />
    public void Save(SessionState state) => _state = state ?? throw new ArgumentNullException(nameof(state));
}
