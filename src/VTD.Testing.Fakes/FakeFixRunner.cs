using System.Runtime.CompilerServices;
using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Sessions;

namespace VTD.Testing.Fakes;

/// <summary>
/// In-memory <see cref="IFixRunner"/> that enforces the consent contract like the real one: a token must echo the
/// preview's hashes and single-use nonce and arrive before the preview's ENGINE-side expiry; one attempt at a time;
/// one-way fixes need a second click that echoes <see cref="FixRunResult.SecondConsentNonce"/>. Nothing touches the
/// system: each step, and the deadman, is recorded in <see cref="Journal"/>.
/// </summary>
public sealed class FakeFixRunner : IFixRunner
{
    private readonly IKnowledgeBase _kb;
    private readonly TimeProvider _clock;
    private readonly IIdGenerator _ids;
    private readonly Dictionary<string, (FixPreview Preview, bool Used, string? SecondNonce)> _previews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AttemptSummary Summary, FixRunResult Result)> _attempts = new(StringComparer.Ordinal);
    private int _running;

    /// <summary>Creates the runner.</summary>
    /// <param name="kb">KB to read fix definitions from.</param>
    /// <param name="clock">Clock (token expiry).</param>
    /// <param name="ids">Ids.</param>
    /// <param name="journal">Journal to record into (default: a new <see cref="InMemoryJournal"/>).</param>
    public FakeFixRunner(IKnowledgeBase kb, TimeProvider clock, IIdGenerator ids, InMemoryJournal? journal = null)
    {
        _kb = kb ?? throw new ArgumentNullException(nameof(kb));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        Journal = journal ?? new InMemoryJournal();
    }

    /// <summary>The journal the fake writes to.</summary>
    public InMemoryJournal Journal { get; }

    /// <summary>Session id written into journal records.</summary>
    public string SessionId { get; set; } = "01JB6R7X3K9M2N4P5Q6R7S8TB0";

    /// <summary>When set, the next apply fails verification (to exercise auto-undo paths).</summary>
    public bool FailNextVerify { get; set; }

    /// <summary>When set, previews report <see cref="FixPreview.CanApply"/> false with this reason.</summary>
    public string? BlockReasonKey { get; set; }

    /// <inheritdoc />
    public Task<FixPreview> PreviewAsync(string fixId, FixBinding binding, Proposer proposedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(proposedBy);
        cancellationToken.ThrowIfCancellationRequested();
        var fix = _kb.FindFix(fixId) ?? throw new KeyNotFoundException($"Unknown fix '{fixId}'.");
        var bindingHash = binding.ComputeHash();
        var steps = fix.Steps.Select((step, i) => new PreviewStep
        {
            Index = i,
            Op = step.Op,
            Args = Resolve(step.Args, binding),
            SummaryKey = $"{fix.Id}.step{i}",
            Reversibility = fix.Reversibility,
            Policy = PolicyDecision.Allow,
        }).ToList();
        var previewHash = CanonicalHash.Of(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fix"] = fix.Id,
            ["rev"] = fix.Rev.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["binding"] = bindingHash,
            ["steps"] = string.Join('|', steps.Select(s => s.Op + "(" + string.Join(',', s.Args.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value)) + ")")),
            ["risk"] = fix.Risk.ToString(),
            ["reversibility"] = fix.Reversibility.ToString(),
        });
        var now = _clock.GetUtcNow();
        var preview = new FixPreview
        {
            PreviewId = _ids.NewId(),
            FixId = fix.Id,
            FixRev = fix.Rev,
            Binding = binding,
            BindingHash = bindingHash,
            PreviewHash = previewHash,
            ConsentNonce = _ids.NewId(),
            CreatedUtc = now,
            ExpiresUtc = now + SessionLimits.TokenLifetime,
            ProposedBy = proposedBy,
            TitleKey = fix.TitleKey,
            ExplainKey = fix.ExplainKey,
            UndoExplainKey = fix.UndoExplainKey,
            NoticeKey = fix.NoticeKey,
            SideEffectKeys = fix.SideEffectKeys,
            Risk = fix.Risk,
            Reversibility = fix.Reversibility,
            Reboot = fix.Reboot,
            Scope = fix.Scope,
            NeedsAdmin = fix.Admin,
            RequiresSecondConsent = fix.Reversibility == Reversibility.OneWay,
            RestorePointFirst = fix.Risk >= RiskLevel.Medium,
            Deadman = fix.Deadman,
            Steps = steps,
            CanApply = BlockReasonKey is null,
            BlockReasonKeys = BlockReasonKey is null ? [] : [BlockReasonKey],
        };
        _previews[preview.PreviewId] = (preview, false, null);
        return Task.FromResult(preview);
    }

    /// <summary>
    /// The reference consent check, without side effects: null when <see cref="ApplyAsync"/> would accept the token,
    /// otherwise the refusal key it would throw (<c>consent.unknown_preview</c>, <c>consent.replayed</c>,
    /// <c>consent.expired</c>, <c>consent.preview_changed</c>, <c>consent.blocked</c>,
    /// <c>consent.second_consent_mismatch</c>). An engine checks this BEFORE leaving AwaitingConsent.
    /// </summary>
    /// <param name="consent">Token.</param>
    public string? CheckConsent(ConsentToken consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (!_previews.TryGetValue(consent.PreviewId, out var entry))
        {
            return "consent.unknown_preview";
        }

        var preview = entry.Preview;
        if (entry.Used)
        {
            return "consent.replayed";
        }

        if (_clock.GetUtcNow() > preview.ExpiresUtc)
        {
            return "consent.expired";
        }

        if (!string.Equals(consent.Nonce, preview.ConsentNonce, StringComparison.Ordinal)
            || !string.Equals(consent.BindingHash, preview.BindingHash, StringComparison.Ordinal)
            || !string.Equals(consent.PreviewHash, preview.PreviewHash, StringComparison.Ordinal)
            || !string.Equals(consent.FixId, preview.FixId, StringComparison.Ordinal))
        {
            return "consent.preview_changed";
        }

        if (!preview.CanApply)
        {
            return "consent.blocked";
        }

        if (preview.RequiresSecondConsent && consent.SecondConsentNonce is not null
            && !string.Equals(consent.SecondConsentNonce, entry.SecondNonce, StringComparison.Ordinal))
        {
            return "consent.second_consent_mismatch";
        }

        return null;
    }

    /// <summary>The unused, unexpired preview with this id (what the engine's GetPreviewAsync returns), or null.</summary>
    /// <param name="previewId">Preview id.</param>
    public FixPreview? FindPreview(string previewId)
        => previewId is not null && _previews.TryGetValue(previewId, out var e) && !e.Used && _clock.GetUtcNow() <= e.Preview.ExpiresUtc ? e.Preview : null;

    /// <inheritdoc />
    public async Task<FixRunResult> ApplyAsync(ConsentToken consent, IProgress<FixProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consent);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("Another fix is running (one fix at a time, S1).");
        }

        try
        {
            if (CheckConsent(consent) is { } refused)
            {
                throw new InvalidOperationException(refused);
            }

            var entry = _previews[consent.PreviewId];
            var preview = entry.Preview;
            var now = _clock.GetUtcNow();
            if (preview.RequiresSecondConsent && consent.SecondConsentNonce is null)
            {
                var second = _ids.NewId();
                _previews[consent.PreviewId] = (preview, false, second);
                return new FixRunResult { AttemptId = string.Empty, FixId = preview.FixId, Status = AttemptStatus.AwaitingSecondConsent, SecondConsentNonce = second, MessageKey = "consent.one_way_confirm" };
            }

            _previews[consent.PreviewId] = (preview, true, entry.SecondNonce);
            var attemptId = _ids.NewId();
            var stepResults = new List<StepResult>();
            long? first = null;
            long? last = null;
            foreach (var step in preview.Steps)
            {
                progress?.Report(new FixProgress { AttemptId = attemptId, StepIndex = step.Index, StepCount = preview.Steps.Count, Phase = FixPhase.Apply });
                var before = Capture(step.Op, new Dictionary<string, string>(StringComparer.Ordinal) { ["state"] = "before" });
                var intentRecord = Record(attemptId, preview.FixId, step, JournalPhase.Intent, before, null) with
                {
                    ConsentTokenId = consent.TokenId,
                    PreviewHash = preview.PreviewHash,
                    ProposedBy = preview.ProposedBy,
                };
                var intent = await Journal.AppendAsync(intentRecord, cancellationToken).ConfigureAwait(false);
                first ??= intent.Seq;
                var after = Capture(step.Op, new Dictionary<string, string>(StringComparer.Ordinal) { ["state"] = "after" });
                var applied = await Journal.AppendAsync(Record(attemptId, preview.FixId, step, JournalPhase.Applied, before, after), cancellationToken).ConfigureAwait(false);
                last = applied.Seq;
                stepResults.Add(new StepResult { Index = step.Index, Op = step.Op, Status = StepStatus.Applied });
            }

            var verifyFailed = FailNextVerify;
            FailNextVerify = false;
            var verify = new VerifyResult { AttemptId = attemptId, Status = verifyFailed ? VerifyStatus.Failed : (preview.Reboot == RebootNeed.Required ? VerifyStatus.DeferredUntilReboot : VerifyStatus.Passed), VerifiedUtc = now };
            var status = verifyFailed ? AttemptStatus.VerifyFailed : preview.Reboot == RebootNeed.Required ? AttemptStatus.AwaitingReboot : AttemptStatus.Verified;
            var result = new FixRunResult
            {
                AttemptId = attemptId,
                FixId = preview.FixId,
                Status = status,
                Steps = stepResults,
                Verify = verify,
                RebootPending = preview.Reboot == RebootNeed.Required,
                Deadman = preview.Deadman is { } d && !verifyFailed ? new DeadmanState { Status = DeadmanStatus.Armed, DeadlineUtc = now.AddSeconds(d.Seconds) } : null,
                FirstJournalSeq = first,
                LastJournalSeq = last,
            };
            if (result.Deadman is { } armed)
            {
                var last0 = preview.Steps[^1];
                var record = await Journal.AppendAsync(Record(attemptId, preview.FixId, last0, JournalPhase.DeadmanArmed, null, null) with { DeadlineUtc = armed.DeadlineUtc }, cancellationToken).ConfigureAwait(false);
                result = result with { LastJournalSeq = record.Seq };
            }

            var summary = new AttemptSummary
            {
                AttemptId = attemptId,
                SessionId = SessionId,
                FixId = preview.FixId,
                TitleKey = preview.TitleKey,
                Status = status,
                Outcome = FixOutcome.Pending,
                Reversibility = preview.Reversibility,
                AppliedUtc = now,
                CanUndo = preview.Reversibility != Reversibility.OneWay,
            };
            _attempts[attemptId] = (summary, result);
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <inheritdoc />
    public Task<VerifyResult> VerifyAsync(string attemptId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (summary, _) = Get(attemptId);
        _attempts[attemptId] = (summary with { Status = AttemptStatus.Verified }, _attempts[attemptId].Result with { Status = AttemptStatus.Verified });
        return Task.FromResult(new VerifyResult { AttemptId = attemptId, Status = VerifyStatus.Passed, VerifiedUtc = _clock.GetUtcNow() });
    }

    /// <inheritdoc />
    public async Task<FixRunResult> ConfirmDeadmanAsync(string attemptId, bool keep, CancellationToken cancellationToken)
    {
        var (summary, result) = Get(attemptId);
        var marker = new PreviewStep { Index = result.Steps.Count - 1, Op = result.Steps[^1].Op, SummaryKey = string.Empty, Reversibility = summary.Reversibility, Policy = PolicyDecision.Allow };
        if (!keep)
        {
            var undo = await UndoAsync(attemptId, UndoMode.Force, cancellationToken).ConfigureAwait(false);
            await Journal.AppendAsync(Record(attemptId, summary.FixId, marker, JournalPhase.DeadmanReverted, null, null), cancellationToken).ConfigureAwait(false);
            return result with { Status = undo.Status == UndoStatus.Undone ? AttemptStatus.RolledBack : AttemptStatus.InDoubt, Deadman = result.Deadman! with { Status = DeadmanStatus.Reverted } };
        }

        await Journal.AppendAsync(Record(attemptId, summary.FixId, marker, JournalPhase.DeadmanConfirmed, null, null), cancellationToken).ConfigureAwait(false);
        var kept = result with { Deadman = result.Deadman is null ? null : result.Deadman with { Status = DeadmanStatus.Confirmed } };
        _attempts[attemptId] = (summary, kept);
        return kept;
    }

    /// <inheritdoc />
    public async Task<UndoResult> UndoAsync(string attemptId, UndoMode mode, CancellationToken cancellationToken)
    {
        var (summary, result) = Get(attemptId);
        if (summary.Reversibility == Reversibility.OneWay)
        {
            return new UndoResult { AttemptId = attemptId, Status = UndoStatus.NotUndoable };
        }

        if (summary.Status is AttemptStatus.Undone or AttemptStatus.RolledBack)
        {
            return new UndoResult { AttemptId = attemptId, Status = UndoStatus.NothingToUndo };
        }

        var steps = new List<StepResult>();
        foreach (var step in result.Steps.OrderByDescending(s => s.Index))
        {
            var preview = new PreviewStep { Index = step.Index, Op = step.Op, SummaryKey = string.Empty, Reversibility = summary.Reversibility, Policy = PolicyDecision.Allow };
            await Journal.AppendAsync(Record(attemptId, summary.FixId, preview, JournalPhase.UndoIntent, null, null), cancellationToken).ConfigureAwait(false);
            await Journal.AppendAsync(Record(attemptId, summary.FixId, preview, JournalPhase.Undone, null, null), cancellationToken).ConfigureAwait(false);
            steps.Add(new StepResult { Index = step.Index, Op = step.Op, Status = StepStatus.Undone });
        }

        _attempts[attemptId] = (summary with { Status = AttemptStatus.Undone, CanUndo = false }, result with { Status = AttemptStatus.Undone });
        return new UndoResult { AttemptId = attemptId, Status = UndoStatus.Undone, Steps = steps };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DriftFinding>> CheckDriftAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DriftFinding>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<AttemptSummary>> ListAttemptsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AttemptSummary>>(_attempts.Values.Select(a => a.Summary).OrderBy(a => a.AppliedUtc).ToList());

    private (AttemptSummary Summary, FixRunResult Result) Get(string attemptId)
        => _attempts.TryGetValue(attemptId, out var a) ? a : throw new KeyNotFoundException($"Unknown attempt '{attemptId}'.");

    private static Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string> args, FixBinding binding)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            var v = value;
            if (v.StartsWith('$'))
            {
                var dot = v.IndexOf('.', StringComparison.Ordinal);
                var name = v[1..dot];
                var field = v[(dot + 1)..];
                if (binding.Entities.TryGetValue(name, out var entities) && entities.Count > 0)
                {
                    v = string.Equals(field, "name", StringComparison.Ordinal) ? entities[0].DisplayName ?? entities[0].Id : entities[0].Id;
                }
            }

            resolved[key] = v;
        }

        return resolved;
    }

    private static StateCapture Capture(string op, IReadOnlyDictionary<string, string> data)
        => new() { Op = op, Data = data, Hash = CanonicalHash.Of(data) };

    private JournalRecord Record(string attemptId, string fixId, PreviewStep step, JournalPhase phase, StateCapture? before, StateCapture? after) => new()
    {
        Utc = _clock.GetUtcNow(),
        SessionId = SessionId,
        AttemptId = attemptId,
        FixId = fixId,
        StepIndex = step.Index,
        Op = step.Op,
        Phase = phase,
        Args = step.Args,
        Before = before,
        After = after,
        AppVersion = FixtureSnapshots.AppVersion,
        KbVersion = _kb.Version.Version,
    };
}

/// <summary>In-memory <see cref="IJournal"/>: assigns monotonic sequence numbers; can simulate a write failure.</summary>
public sealed class InMemoryJournal : IJournal
{
    private readonly List<JournalRecord> _records = [];
    private long _seq;

    /// <summary>All records in order.</summary>
    public IReadOnlyList<JournalRecord> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>When true, the next append throws <see cref="JournalWriteException"/> (disk full / AV lock).</summary>
    public bool FailNextAppend { get; set; }

    /// <inheritdoc />
    public ValueTask<JournalRecord> AppendAsync(JournalRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextAppend)
        {
            FailNextAppend = false;
            throw new JournalWriteException("Simulated journal write failure.");
        }

        lock (_records)
        {
            var stored = record with { Seq = ++_seq };
            _records.Add(stored);
            return ValueTask.FromResult(stored);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<JournalRecord> ReadAsync(string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var r in Records.Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return r;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<JournalRecord> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var r in Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return r;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// In-memory <see cref="IStateStore{T}"/> that keeps a primary and a backup copy as JSON, so tests can corrupt the
/// primary (<see cref="CorruptPrimary"/>) and see the backup fallback, as with <c>current.json</c> / <c>current.bak</c>.
/// </summary>
/// <typeparam name="T">State type registered in the JSON contract.</typeparam>
public sealed class InMemoryStateStore<T> : IStateStore<T>
    where T : class
{
    private string? _primary;
    private string? _backup;

    /// <summary>Number of saves.</summary>
    public int SaveCount { get; private set; }

    /// <inheritdoc />
    public StateLoadResult<T> Load()
    {
        if (_primary is null && _backup is null)
        {
            return new StateLoadResult<T> { Source = StateLoadSource.Empty };
        }

        if (TryParse(_primary, out var primary))
        {
            return new StateLoadResult<T> { State = primary, Source = StateLoadSource.Primary };
        }

        if (TryParse(_backup, out var backup))
        {
            return new StateLoadResult<T> { State = backup, Source = StateLoadSource.Backup, Issues = ["primary.unreadable"] };
        }

        return new StateLoadResult<T> { Source = StateLoadSource.Empty, Issues = ["primary.unreadable", "backup.unreadable"] };
    }

    /// <inheritdoc />
    public void Save(T state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _backup = _primary;
        _primary = VTD.Contracts.Serialization.VtdJson.Serialize(state);
        SaveCount++;
    }

    /// <summary>Simulates a torn write of the primary file.</summary>
    public void CorruptPrimary() => _primary = _primary is null ? "{" : _primary[..(_primary.Length / 2)];

    private static bool TryParse(string? json, out T? value)
    {
        value = null;
        if (json is null)
        {
            return false;
        }

        try
        {
            value = VTD.Contracts.Serialization.VtdJson.Deserialize<T>(json);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

/// <summary><see cref="IRecovery"/> returning a scripted report (default: nothing to recover).</summary>
/// <param name="clock">Clock.</param>
public sealed class FakeRecovery(TimeProvider clock) : IRecovery
{
    /// <summary>Items to report on the next run.</summary>
    public IReadOnlyList<RecoveryItem> NextItems { get; set; } = [];

    /// <inheritdoc />
    public Task<RecoveryReport> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = NextItems;
        NextItems = [];
        return Task.FromResult(new RecoveryReport
        {
            RanUtc = clock.GetUtcNow(),
            Items = items,
            NeedsAttention = items.Any(i => i.Resolution == RecoveryResolution.InDoubt),
        });
    }
}
