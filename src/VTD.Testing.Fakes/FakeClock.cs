using VTD.Contracts;

namespace VTD.Testing.Fakes;

/// <summary>
/// A manual <see cref="TimeProvider"/>: time moves only when a test calls <see cref="Advance"/> or <see cref="SetUtcNow"/>.
/// Timers created through it (deadman countdowns, <c>Task.Delay(TimeSpan, TimeProvider)</c>, token expiry) fire
/// deterministically during <see cref="Advance"/>. Local time zone is UTC so nothing depends on the test machine.
/// </summary>
public sealed class FakeClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now;
    private long _ticks;

    /// <summary>Default start: 2026-10-21T18:00:00Z.</summary>
    public static DateTimeOffset DefaultStart { get; } = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);

    /// <summary>Creates the clock.</summary>
    /// <param name="startUtc">Start time (default <see cref="DefaultStart"/>).</param>
    public FakeClock(DateTimeOffset? startUtc = null)
    {
        _now = (startUtc ?? DefaultStart).ToUniversalTime();
    }

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    /// <summary>Moves time forward and fires due timers in order.</summary>
    /// <param name="delta">Non-negative amount.</param>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Time only moves forward; use SetUtcNow to model a clock jump.");
        }

        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + delta;
        }

        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueUtc is { } due && due <= target).OrderBy(t => t.DueUtc).FirstOrDefault();
                if (next is null)
                {
                    _ticks += (target - _now).Ticks;
                    _now = target;
                    return;
                }

                _ticks += (next.DueUtc!.Value - _now).Ticks;
                _now = next.DueUtc!.Value;
                next.Reschedule(_now);
            }

            next.Fire();
        }
    }

    /// <summary>Sets the wall clock (models a wrong PC clock or an NTP resync; timers are not fired).</summary>
    /// <param name="utcNow">New time.</param>
    public void SetUtcNow(DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            _now = utcNow.ToUniversalTime();
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Change(_now, dueTime, period);
        }

        return timer;
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private DateTimeOffset NowUnlocked() => _now;

    private sealed class FakeTimer(FakeClock owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueUtc { get; private set; }

        public void Change(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueUtc = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Change(owner.NowUnlocked(), dueTime, period);
            }

            return true;
        }

        public void Reschedule(DateTimeOffset now)
            => DueUtc = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : now + _period;

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Deterministic ULIDs: timestamp from the clock, entropy from a counter. Same sequence on every run.</summary>
/// <param name="clock">Time source.</param>
public sealed class SequentialIdGenerator(TimeProvider clock) : IIdGenerator
{
    private long _counter;

    /// <inheritdoc />
    public string NewId()
    {
        var n = Interlocked.Increment(ref _counter);
        Span<byte> entropy = stackalloc byte[10];
        BitConverter.TryWriteBytes(entropy[2..], n);
        entropy[2..].Reverse();
        return Ulid.Format(clock.GetUtcNow(), entropy);
    }
}
