using System.Globalization;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Scanning;
using VTD.Contracts.Sessions;
using VTD.Fixes.Ops;
using VTD.Journal;

namespace VTD.Fixes.Runner;

/// <summary>A consent token or apply request was refused (the message is the refusal key, as in the reference fake).</summary>
public sealed class ConsentRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public ConsentRefusedException()
        : this("consent.refused")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Refusal key (<c>consent.unknown_preview</c>, <c>consent.replayed</c>, <c>consent.expired</c>,
    /// <c>consent.preview_changed</c>, <c>consent.blocked</c>, <c>consent.second_consent_mismatch</c>,
    /// <c>consent.wrong_session</c>, <c>fix.busy</c>, <c>fix.deadman_pending</c>).</param>
    public ConsentRefusedException(string code)
        : base(code)
    {
        Code = code;
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Refusal key.</param>
    /// <param name="innerException">Cause.</param>
    public ConsentRefusedException(string code, Exception innerException)
        : base(code, innerException)
    {
        Code = code;
    }

    /// <summary>Refusal key.</summary>
    public string Code { get; } = "consent.refused";
}

/// <summary>
/// The fix runner (design_B.md sections 6.3 and 6.6, S1–S3, S12, S15). Preview from fresh probes; apply only with a UI
/// consent token whose hashes match a preview recomputed at apply time (TOCTOU); one fix at a time; one-way fixes need a
/// second click; a restore point before the first medium/high-risk or one-way fix of the session; per step: capture →
/// fsynced journal intent → apply → probe → journal; all-or-nothing rollback on an aborting step failure; mechanical
/// verification with auto-undo on failure; a journaled 60 s deadman on network-cutting fixes; undo in reverse order with a
/// conflict check; drift watch. The journal alone is enough to list and undo every change, across sessions and restarts.
/// </summary>
public sealed partial class FixRunner : IFixRunner, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> NoArgs = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    private readonly IKnowledgeBase _kb;
    private readonly OperationRegistry _registry;
    private readonly IJournal _journal;
    private readonly IFactProbe _probe;
    private readonly IRestorePointService _restorePoints;
    private readonly IFixSessionContext _session;
    private readonly TimeProvider _clock;
    private readonly IIdGenerator _ids;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, PreviewEntry> _previews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeadmanEntry> _deadmans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _restorePointSessions = new(StringComparer.Ordinal);
    private readonly List<Task> _background = [];

    /// <summary>Creates the runner.</summary>
    /// <param name="kb">Knowledge base (pinned for the session).</param>
    /// <param name="registry">Primitives.</param>
    /// <param name="journal">Write-ahead journal.</param>
    /// <param name="probe">Live probes (guards, deadman check).</param>
    /// <param name="restorePoints">A4's restore point service.</param>
    /// <param name="session">Session context.</param>
    /// <param name="clock">Engine clock.</param>
    /// <param name="ids">Ids.</param>
    public FixRunner(IKnowledgeBase kb, OperationRegistry registry, IJournal journal, IFactProbe probe, IRestorePointService restorePoints, IFixSessionContext session, TimeProvider clock, IIdGenerator ids)
    {
        _kb = kb ?? throw new ArgumentNullException(nameof(kb));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _restorePoints = restorePoints ?? throw new ArgumentNullException(nameof(restorePoints));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
    }

    /// <summary>Deadman countdown bounds (seconds) whatever the KB declares.</summary>
    public static (int Min, int Max) DeadmanBounds { get; } = (15, 300);

    /// <summary>True while a deadman is counting down (no other fix may start).</summary>
    public bool DeadmanPending
    {
        get
        {
            lock (_sync)
            {
                return _deadmans.Count > 0;
            }
        }
    }

    /// <summary>Waits for background work started by deadman timers (tests; engine shutdown).</summary>
    public async Task DrainBackgroundAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_sync)
            {
                pending = [.. _background.Where(t => !t.IsCompleted)];
                _background.RemoveAll(t => t.IsCompleted);
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var d in _deadmans.Values)
            {
                d.Timer.Dispose();
            }

            _deadmans.Clear();
        }

        _gate.Dispose();
    }

    /// <inheritdoc />
    public async Task<FixPreview> PreviewAsync(string fixId, FixBinding binding, Proposer proposedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixId);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(proposedBy);
        var fix = _kb.FindFix(fixId) ?? throw new KeyNotFoundException("Unknown fix '" + fixId + "'.");
        var built = await BuildAsync(fix, binding, proposedBy, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var preview = built.Preview with
        {
            PreviewId = _ids.NewId(),
            ConsentNonce = _ids.NewId(),
            CreatedUtc = now,
            ExpiresUtc = now + SessionLimits.TokenLifetime,
        };
        lock (_sync)
        {
            foreach (var stale in _previews.Where(p => p.Value.Used || now > p.Value.Preview.ExpiresUtc + SessionLimits.TokenLifetime).Select(p => p.Key).ToList())
            {
                _previews.Remove(stale);
            }

            _previews[preview.PreviewId] = new PreviewEntry(preview, built.Plan, _session.SessionId);
        }

        return preview;
    }

    /// <summary>The unused, unexpired preview with this id (what the engine's <c>GetPreviewAsync</c> returns), or null.</summary>
    /// <param name="previewId">Preview id.</param>
    public FixPreview? FindPreview(string previewId)
    {
        lock (_sync)
        {
            return previewId is not null && _previews.TryGetValue(previewId, out var e) && !e.Used && _clock.GetUtcNow() <= e.Preview.ExpiresUtc ? e.Preview : null;
        }
    }

    /// <summary>
    /// The consent check without side effects (the engine runs it BEFORE leaving AwaitingConsent): null when
    /// <see cref="ApplyAsync"/> would accept the token's form, otherwise the refusal key. The TOCTOU recomputation still
    /// happens inside <see cref="ApplyAsync"/>.
    /// </summary>
    /// <param name="consent">Token.</param>
    public string? CheckConsent(ConsentToken consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        lock (_sync)
        {
            return CheckConsentLocked(consent, out _);
        }
    }

    private string? CheckConsentLocked(ConsentToken consent, out PreviewEntry? entry)
    {
        if (!_previews.TryGetValue(consent.PreviewId, out entry))
        {
            return "consent.unknown_preview";
        }

        var p = entry.Preview;
        if (entry.Used)
        {
            return "consent.replayed";
        }

        if (_clock.GetUtcNow() > p.ExpiresUtc)
        {
            return "consent.expired";
        }

        if (!string.Equals(consent.SessionId, entry.SessionId, StringComparison.Ordinal) || !string.Equals(entry.SessionId, _session.SessionId, StringComparison.Ordinal))
        {
            return "consent.wrong_session";
        }

        if (!string.Equals(consent.Nonce, p.ConsentNonce, StringComparison.Ordinal)
            || !string.Equals(consent.BindingHash, p.BindingHash, StringComparison.Ordinal)
            || !string.Equals(consent.PreviewHash, p.PreviewHash, StringComparison.Ordinal)
            || !string.Equals(consent.FixId, p.FixId, StringComparison.Ordinal))
        {
            return "consent.preview_changed";
        }

        if (!p.CanApply)
        {
            return "consent.blocked";
        }

        if (p.RequiresSecondConsent && consent.SecondConsentNonce is not null
            && !string.Equals(consent.SecondConsentNonce, entry.SecondNonce, StringComparison.Ordinal))
        {
            return "consent.second_consent_mismatch";
        }

        return null;
    }

    // ------------------------------------------------------------------ preview

    private async Task<(FixPreview Preview, FixPlan Plan)> BuildAsync(FixDef fix, FixBinding binding, Proposer proposer, CancellationToken cancellationToken)
    {
        var plan = FixPlanner.Plan(fix, binding, _registry);
        var blocks = new List<string>(plan.Errors);
        var steps = new List<PreviewStep>();
        var hash = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var allNoOp = plan.Steps.Count > 0;
        foreach (var step in plan.Steps)
        {
            var p = string.Create(CultureInfo.InvariantCulture, $"step[{step.Index}].");
            hash[p + "op"] = step.Def.Op;
            foreach (var (k, v) in step.Args)
            {
                hash[p + "args." + k] = v;
            }

            if (step.Op is not { } op)
            {
                allNoOp = false;
                continue;
            }

            PolicyDecision policy;
            try
            {
                policy = await op.CheckPolicyAsync(step.OpArgs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                policy = Policy.ProtectedTargetPolicy.Deny("error");
            }

            hash[p + "policy"] = policy.Allowed ? "allow" : "deny:" + policy.RuleId;
            StateCapture? before = null;
            StateCapture? intended = null;
            if (policy.Allowed)
            {
                try
                {
                    before = await op.CaptureAsync(step.OpArgs, cancellationToken).ConfigureAwait(false);
                    intended = op.Intended(step.OpArgs, before);
                    if (op.Precondition(step.OpArgs, before) is { } pre)
                    {
                        blocks.Add(pre);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    blocks.Add("capture.failed:" + op.Op);
                    before = null;
                    intended = null;
                }
            }
            else
            {
                blocks.Add(policy.ReasonKey ?? "policy.denied");
            }

            if (before is not null && intended is not null)
            {
                foreach (var (k, v) in before.Data.Where(kv => !op.VolatileKeys.Contains(kv.Key, StringComparer.Ordinal)))
                {
                    hash[p + "before." + k] = v;
                }

                foreach (var (k, v) in intended.Data.Where(kv => !op.VolatileKeys.Contains(kv.Key, StringComparer.Ordinal)))
                {
                    hash[p + "intended." + k] = v;
                }

                if (op.AlwaysApply || !op.Same(step.OpArgs, before, intended))
                {
                    allNoOp = false;
                }
            }
            else
            {
                allNoOp = false;
            }

            steps.Add(new PreviewStep
            {
                Index = step.Index,
                Op = op.Op,
                Args = step.Args,
                SummaryKey = "op." + op.Op + ".summary",
                SummaryArgs = SummaryArgs(step),
                Before = Public(before),
                Intended = Public(intended),
                Reversibility = op.Reversibility,
                Policy = policy,
            });
        }

        if (fix.Steps.Count == 0)
        {
            blocks.Add("fix.no_steps");
        }

        if (allNoOp)
        {
            blocks.Add("fix.nothing_to_change");
        }

        if (fix.Deprecated is not null)
        {
            blocks.Add("fix.deprecated");
        }

        if (plan.Risk == RiskLevel.High)
        {
            blocks.Add("fix.high_risk_guide_only");
        }

        if (plan.AffectsNetwork && fix.Deadman is null)
        {
            blocks.Add("fix.deadman_missing");
        }

        if (plan.NeedsAdmin && !_session.IsElevated)
        {
            blocks.Add("fix.needs_admin");
        }

        var guards = new List<GuardResult>();
        for (var i = 0; i < plan.Guards.Count; i++)
        {
            var g = plan.Guards[i];
            var observed = await ProbeTri(g.Probe, g.Args, cancellationToken).ConfigureAwait(false);
            var passed = observed == g.Must;
            guards.Add(new GuardResult { Probe = g.Probe, Observed = observed, Must = g.Must, Passed = passed, Severity = g.Severity, FailKey = g.FailKey });
            var p = string.Create(CultureInfo.InvariantCulture, $"guard[{i}].");
            hash[p + "probe"] = g.Probe;
            foreach (var (k, v) in g.Args)
            {
                hash[p + "args." + k] = v;
            }

            hash[p + "must"] = g.Must.ToString();
            hash[p + "observed"] = observed.ToString();
            hash[p + "severity"] = g.Severity.ToString();
            if (!passed && g.Severity == GuardSeverity.Block)
            {
                blocks.Add(g.FailKey);
            }
        }

        var restoreFirst = (plan.Risk >= RiskLevel.Medium || plan.Reversibility == Reversibility.OneWay) && !RestorePointMade(_session.SessionId);
        var bindingHash = binding.ComputeHash();
        var distinctBlocks = blocks.Distinct(StringComparer.Ordinal).ToList();
        hash["fix"] = fix.Id;
        hash["rev"] = fix.Rev.ToString(CultureInfo.InvariantCulture);
        hash["binding"] = bindingHash;
        hash["risk"] = plan.Risk.ToString();
        hash["reversibility"] = plan.Reversibility.ToString();
        hash["reboot"] = plan.Reboot.ToString();
        hash["scope"] = fix.Scope.ToString();
        hash["admin"] = Captures.Bool(plan.NeedsAdmin);
        hash["secondConsent"] = Captures.Bool(plan.Reversibility == Reversibility.OneWay);
        hash["restorePointFirst"] = Captures.Bool(restoreFirst);
        hash["deadman"] = fix.Deadman is { } dm ? DeadmanSeconds(dm).ToString(CultureInfo.InvariantCulture) + "|" + dm.Check : string.Empty;
        hash["canApply"] = Captures.Bool(distinctBlocks.Count == 0);
        hash["blocks"] = string.Join('|', distinctBlocks);
        hash["proposer.origin"] = proposer.Origin.ToString();
        hash["proposer.plan"] = proposer.PlanId ?? string.Empty;

        var preview = new FixPreview
        {
            PreviewId = string.Empty,
            FixId = fix.Id,
            FixRev = fix.Rev,
            Binding = binding,
            BindingHash = bindingHash,
            PreviewHash = CanonicalHash.Of(hash),
            CreatedUtc = default,
            ExpiresUtc = default,
            ProposedBy = proposer,
            TitleKey = fix.TitleKey,
            ExplainKey = fix.ExplainKey,
            UndoExplainKey = fix.UndoExplainKey,
            NoticeKey = fix.NoticeKey,
            SideEffectKeys = fix.SideEffectKeys,
            Risk = plan.Risk,
            Reversibility = plan.Reversibility,
            Reboot = plan.Reboot,
            Scope = fix.Scope,
            NeedsAdmin = plan.NeedsAdmin,
            RequiresSecondConsent = plan.Reversibility == Reversibility.OneWay,
            RestorePointFirst = restoreFirst,
            Deadman = fix.Deadman,
            Steps = steps,
            Guards = guards,
            CanApply = distinctBlocks.Count == 0,
            BlockReasonKeys = distinctBlocks,
        };
        return (preview, plan);
    }

    private static Dictionary<string, string> SummaryArgs(PlannedStep step)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (step.Entities.Count > 0)
        {
            map["target"] = step.Entities[0].DisplayName ?? step.Entities[0].Id;
        }

        foreach (var key in new[] { "name", "imageName", "keyword", "value", "startType", "data" })
        {
            if (step.Args.TryGetValue(key, out var v))
            {
                map[key] = v;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> Public(StateCapture? capture)
        => capture is null
            ? NoArgs
            : capture.Data.Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private async Task<Tri> ProbeTri(string probeId, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken)
    {
        try
        {
            var r = await _probe.ProbeAsync(probeId, args, cancellationToken).ConfigureAwait(false);
            return r.State;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Tri.Unknown;
        }
    }

    private bool RestorePointMade(string sessionId)
    {
        lock (_sync)
        {
            return _restorePointSessions.Contains(sessionId);
        }
    }

    private static int DeadmanSeconds(DeadmanDef d) => Math.Clamp(d.Seconds, DeadmanBounds.Min, DeadmanBounds.Max);

    private JournalRecord Rec(string sessionId, string attemptId, string fixId, int step, string op, JournalPhase phase, IReadOnlyDictionary<string, string>? args = null, StateCapture? before = null, StateCapture? after = null, string? error = null) => new()
    {
        Utc = _clock.GetUtcNow(),
        SessionId = sessionId,
        AttemptId = attemptId,
        FixId = fixId,
        StepIndex = step,
        Op = op,
        Phase = phase,
        Args = args ?? NoArgs,
        Before = before,
        After = after,
        BootId = _session.BootId,
        AppVersion = _session.AppVersion,
        KbVersion = _kb.Version.Version,
        ErrorCode = error,
    };

    private async Task<bool> TryAppend(JournalRecord record)
    {
        try
        {
            await _journal.AppendAsync(record, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (JournalWriteException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<AttemptLog>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var records = new List<JournalRecord>();
        await foreach (var r in _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(r);
        }

        return AttemptHistory.Build(records);
    }

    private async Task<AttemptLog> LoadAttemptAsync(string attemptId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptId);
        var all = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(a => string.Equals(a.AttemptId, attemptId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("Unknown attempt '" + attemptId + "'.");
    }

    private sealed class PreviewEntry(FixPreview preview, FixPlan plan, string sessionId)
    {
        public FixPreview Preview { get; } = preview;

        public FixPlan Plan { get; } = plan;

        public string SessionId { get; } = sessionId;

        public bool Used { get; set; }

        public string? SecondNonce { get; set; }
    }

    private sealed class DeadmanEntry(DateTimeOffset deadline, string check, ITimer timer)
    {
        public DateTimeOffset Deadline { get; } = deadline;

        public string Check { get; } = check;

        public ITimer Timer { get; } = timer;
    }
}
