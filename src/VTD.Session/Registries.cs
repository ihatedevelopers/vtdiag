using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Sessions;

namespace VTD.Session;

/// <summary>
/// The previews the engine computed for <see cref="FixSelected"/> (engine memory only, never persisted: a restarted
/// engine honours no old consent). The consent pre-check runs here BEFORE the session leaves AwaitingConsent.
/// </summary>
public sealed class PreviewRegistry
{
    private const int MaxEntries = 64;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>Creates the registry.</summary>
    /// <param name="clock">Engine clock (expiry is never taken from a client).</param>
    public PreviewRegistry(TimeProvider clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Records a preview computed for a proposal.</summary>
    /// <param name="preview">Preview.</param>
    public void Add(FixPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries)
            {
                foreach (var old in _entries.Values.OrderBy(e => e.Preview.CreatedUtc).Take(_entries.Count - MaxEntries + 1).ToList())
                {
                    _entries.Remove(old.Preview.PreviewId);
                }
            }

            _entries[preview.PreviewId] = new Entry(preview);
        }
    }

    /// <summary>The unused, unexpired preview (what <see cref="IEngineRpc.GetPreviewAsync"/> returns), or null.</summary>
    /// <param name="previewId">Preview id.</param>
    public FixPreview? Find(string? previewId)
    {
        lock (_gate)
        {
            return previewId is not null && _entries.TryGetValue(previewId, out var e) && !e.Used && _clock.GetUtcNow() <= e.Preview.ExpiresUtc ? e.Preview : null;
        }
    }

    /// <summary>The preview, used or not (attempt bookkeeping).</summary>
    /// <param name="previewId">Preview id.</param>
    public FixPreview? Get(string? previewId)
    {
        lock (_gate)
        {
            return previewId is not null && _entries.TryGetValue(previewId, out var e) ? e.Preview : null;
        }
    }

    /// <summary>
    /// The engine's own consent check (reference: <c>FakeFixRunner.CheckConsent</c>): null when acceptable, else
    /// <c>consent.unknown_preview</c>, <c>consent.replayed</c>, <c>consent.expired</c>, <c>consent.preview_changed</c>,
    /// <c>consent.blocked</c>, <c>consent.second_consent_mismatch</c>.
    /// </summary>
    /// <param name="token">Token.</param>
    public string? Check(ConsentToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            if (!_entries.TryGetValue(token.PreviewId, out var e))
            {
                return "consent.unknown_preview";
            }

            var p = e.Preview;
            if (e.Used)
            {
                return "consent.replayed";
            }

            if (_clock.GetUtcNow() > p.ExpiresUtc)
            {
                return "consent.expired";
            }

            if (p.ConsentNonce is null
                || !string.Equals(token.Nonce, p.ConsentNonce, StringComparison.Ordinal)
                || !string.Equals(token.BindingHash, p.BindingHash, StringComparison.Ordinal)
                || !string.Equals(token.PreviewHash, p.PreviewHash, StringComparison.Ordinal)
                || !string.Equals(token.FixId, p.FixId, StringComparison.Ordinal))
            {
                return "consent.preview_changed";
            }

            if (!p.CanApply)
            {
                return "consent.blocked";
            }

            if (p.RequiresSecondConsent && token.SecondConsentNonce is not null
                && !string.Equals(token.SecondConsentNonce, e.SecondNonce, StringComparison.Ordinal))
            {
                return "consent.second_consent_mismatch";
            }

            return null;
        }
    }

    /// <summary>The runner asked for the second click: remember the nonce it must echo.</summary>
    /// <param name="previewId">Preview.</param>
    /// <param name="nonce">Nonce.</param>
    public void SetSecondNonce(string previewId, string? nonce)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(previewId, out var e))
            {
                e.SecondNonce = nonce;
            }
        }
    }

    /// <summary>The token was used (single use).</summary>
    /// <param name="previewId">Preview.</param>
    public void MarkUsed(string previewId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(previewId, out var e))
            {
                e.Used = true;
            }
        }
    }

    private sealed class Entry(FixPreview preview)
    {
        public FixPreview Preview { get; } = preview;

        public bool Used { get; set; }

        public string? SecondNonce { get; set; }
    }
}

/// <summary>Completes <see cref="IEngineRpc.WaitForOutcomeAsync"/> for CLI/MCP proposals.</summary>
public sealed class OutcomeWaiters
{
    private const int MaxResults = 64;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<FixRunResult?>> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FixRunResult?> _done = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>Records the decision (null = declined, skipped, replaced) and wakes every waiter.</summary>
    /// <param name="previewId">Preview.</param>
    /// <param name="result">Result.</param>
    public void Resolve(string previewId, FixRunResult? result)
    {
        ArgumentNullException.ThrowIfNull(previewId);
        TaskCompletionSource<FixRunResult?>? waiter;
        lock (_gate)
        {
            if (!_done.ContainsKey(previewId))
            {
                _order.Enqueue(previewId);
                while (_order.Count > MaxResults)
                {
                    _done.Remove(_order.Dequeue());
                }
            }

            _done[previewId] = result;
            _waiting.Remove(previewId, out waiter);
        }

        waiter?.TrySetResult(result);
    }

    /// <summary>Waits for the decision on a preview, at most <paramref name="timeout"/> on <paramref name="clock"/>.</summary>
    /// <param name="previewId">Preview.</param>
    /// <param name="timeout">Timeout.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<FixRunResult?> WaitAsync(string previewId, TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previewId);
        TaskCompletionSource<FixRunResult?> waiter;
        lock (_gate)
        {
            if (_done.TryGetValue(previewId, out var done))
            {
                return done;
            }

            if (!_waiting.TryGetValue(previewId, out waiter!))
            {
                waiter = new TaskCompletionSource<FixRunResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting[previewId] = waiter;
            }
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}

/// <summary>
/// The restart nonces the engine handed out (<see cref="IEngineRpc.PrepareRestartAsync"/>), shared by the session runtime
/// (pre-check before the transition) and the reboot coordinator (the only caller of the restart API). A nonce is single
/// use and expires on the engine clock; the coordinator restarts only for a token the runtime armed.
/// </summary>
public sealed class RestartPromptLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Prompt> _prompts = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private string? _armedTokenId;
    private string? _armedNonce;

    /// <summary>Creates the ledger.</summary>
    /// <param name="clock">Engine clock.</param>
    public RestartPromptLedger(TimeProvider clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Records a prompt.</summary>
    /// <param name="sessionId">Session.</param>
    /// <param name="prompt">Prompt.</param>
    public void Issue(string sessionId, RestartPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        lock (_gate)
        {
            foreach (var stale in _prompts.Where(p => _clock.GetUtcNow() > p.Value.Value.ExpiresUtc).Select(p => p.Key).ToList())
            {
                _prompts.Remove(stale);
            }

            _prompts[prompt.Nonce] = new Prompt(sessionId, prompt);
        }
    }

    /// <summary>The readiness of the prompt this token answers (for the Windows Update flag), or null.</summary>
    /// <param name="token">Token.</param>
    public RebootReadiness? ReadinessOf(RestartToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            return _prompts.TryGetValue(token.Nonce, out var p) ? p.Value.Readiness : null;
        }
    }

    /// <summary>Null when the click token answers a live, allowed prompt of this session and kind; else the refusal code.</summary>
    /// <param name="token">Token.</param>
    public string? Check(RestartToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            if (!_prompts.TryGetValue(token.Nonce, out var p) || p.Used)
            {
                return "restart.token_invalid";
            }

            if (_clock.GetUtcNow() > p.Value.ExpiresUtc)
            {
                return "restart.token_expired";
            }

            if (!string.Equals(p.SessionId, token.SessionId, StringComparison.Ordinal) || p.Value.Kind != token.Kind)
            {
                return "restart.token_invalid";
            }

            return p.Value.Allowed ? null : p.Value.BlockReasonKeys.FirstOrDefault() ?? "restart.blocked";
        }
    }

    /// <summary>The machine accepted the click: the nonce is spent and this token is the one restart may use.</summary>
    /// <param name="token">Token.</param>
    public bool Arm(RestartToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            if (!_prompts.TryGetValue(token.Nonce, out var p) || p.Used)
            {
                return false;
            }

            p.Used = true;
            _armedTokenId = token.TokenId;
            _armedNonce = token.Nonce;
            return true;
        }
    }

    /// <summary>The coordinator's gate: true exactly once, for the armed token.</summary>
    /// <param name="token">Token.</param>
    public bool TryConsume(RestartToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            if (_armedTokenId is null || !string.Equals(_armedTokenId, token.TokenId, StringComparison.Ordinal) || !string.Equals(_armedNonce, token.Nonce, StringComparison.Ordinal))
            {
                return false;
            }

            _armedTokenId = null;
            _armedNonce = null;
            return true;
        }
    }

    private sealed class Prompt(string sessionId, RestartPrompt value)
    {
        public string SessionId { get; } = sessionId;

        public RestartPrompt Value { get; } = value;

        public bool Used { get; set; }
    }
}

/// <summary>Engine → client notifications with one sequence (every connection sees the same order).</summary>
public sealed class NotificationHub
{
    private readonly TimeProvider _clock;
    private long _seq;

    /// <summary>Creates the hub.</summary>
    /// <param name="clock">Clock.</param>
    public NotificationHub(TimeProvider clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Raised for every notification.</summary>
    public event EventHandler<EngineNotification>? Raised;

    /// <summary>Raises a notification built with the next sequence number and the engine time.</summary>
    /// <param name="build">Builder (sequence, time).</param>
    public void Raise(Func<long, DateTimeOffset, EngineNotification> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var notification = build(Interlocked.Increment(ref _seq), _clock.GetUtcNow());
        Raised?.Invoke(this, notification);
    }

    /// <summary>Raises <see cref="SessionChangedNotification"/>.</summary>
    /// <param name="state">State.</param>
    public void SessionChanged(SessionState state) => Raise((seq, utc) => new SessionChangedNotification { Seq = seq, Utc = utc, Session = state });

    /// <summary>A label-free proposer for an origin.</summary>
    /// <param name="origin">Origin.</param>
    public static Proposer ProposerOf(EventOrigin origin) => new() { Origin = origin };
}
