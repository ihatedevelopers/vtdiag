using System.Collections.Concurrent;
using System.Diagnostics;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Capture;
using VTD.Platform.Sources;

namespace VTD.Collectors.Core;

/// <summary>Tracks one collector run: issue codes and whether anything was Unknown.</summary>
public sealed class RunBuilder
{
    private readonly ConcurrentQueue<string> _issues = new();
    private int _unknown;
    private int _known;

    /// <summary>Creates a run builder.</summary>
    /// <param name="collectorId">Collector id.</param>
    public RunBuilder(string collectorId)
    {
        CollectorId = collectorId;
    }

    /// <summary>Collector id.</summary>
    public string CollectorId { get; }

    /// <summary>Forced status (for example NotApplicable).</summary>
    public CollectorStatus? ForcedStatus { get; set; }

    /// <summary>Adds an issue code (machine text, never PII).</summary>
    /// <param name="code">Code, for example <c>needs_admin:BcdStore</c>.</param>
    public void Issue(string code)
    {
        if (!_issues.Contains(code, StringComparer.Ordinal) && _issues.Count < 64)
        {
            _issues.Enqueue(code);
        }
    }

    /// <summary>A Known fact.</summary>
    /// <typeparam name="T">Type.</typeparam>
    /// <param name="value">Value.</param>
    /// <param name="method">Method part of the source.</param>
    public Fact<T> Known<T>(T value, string method)
    {
        Interlocked.Increment(ref _known);
        return Fact<T>.Known(value, CollectorId + "/" + method);
    }

    /// <summary>A Known but partial fact (some entries unreadable or capped; see <see cref="Fact{T}.IsComplete"/>); also
    /// records an issue code <c>partial:&lt;method&gt;</c>.</summary>
    /// <typeparam name="T">Type.</typeparam>
    /// <param name="value">What could be read.</param>
    /// <param name="method">Method.</param>
    /// <param name="detail">Short machine detail of what is missing.</param>
    public Fact<T> Partial<T>(T value, string method, string? detail = null)
    {
        Interlocked.Increment(ref _known);
        Issue("partial:" + method);
        return Fact<T>.Partial(value, detail, CollectorId + "/" + method);
    }

    /// <summary>An Unknown fact; also records an issue code <c>&lt;reason&gt;:&lt;method&gt;</c>.</summary>
    /// <typeparam name="T">Type.</typeparam>
    /// <param name="reason">Reason.</param>
    /// <param name="method">Method part of the source.</param>
    /// <param name="detail">Machine detail.</param>
    public Fact<T> Unknown<T>(UnknownReason reason, string method, string? detail = null)
    {
        Interlocked.Increment(ref _unknown);
        Issue(IssuePrefix(reason) + ":" + method);
        return Fact<T>.Unknown(reason, detail, CollectorId + "/" + method);
    }

    /// <summary>A NotApplicable fact.</summary>
    /// <typeparam name="T">Type.</typeparam>
    /// <param name="method">Method part of the source.</param>
    /// <param name="detail">Machine detail.</param>
    public Fact<T> NotApplicable<T>(string method, string? detail = null)
    {
        Interlocked.Increment(ref _known);
        return Fact<T>.NotApplicable(detail, CollectorId + "/" + method);
    }

    /// <summary>Unknown from a read status.</summary>
    /// <typeparam name="T">Type.</typeparam>
    /// <param name="env">Environment (for the limited-mode mapping).</param>
    /// <param name="status">Status.</param>
    /// <param name="method">Method.</param>
    public Fact<T> FromStatus<T>(ScanEnvironment env, ReadStatus status, string method)
        => Unknown<T>(env.ReasonOf(status), method, status.ToString().ToLowerInvariant());

    /// <summary>Evaluates <paramref name="read"/>; an exception becomes Unknown(Failed) instead of failing the collector.</summary>
    /// <typeparam name="T">Type.</typeparam>
    /// <param name="method">Method.</param>
    /// <param name="read">Read.</param>
    public Fact<T> Try<T>(string method, Func<Fact<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Issue("exception:" + e.GetType().Name + "@" + method);
            return Unknown<T>(UnknownReason.Failed, method, "exception:" + e.GetType().Name);
        }
    }

    /// <summary>Builds the run record.</summary>
    /// <param name="elapsedMs">Elapsed.</param>
    public CollectorRun Build(long elapsedMs)
    {
        var status = ForcedStatus ?? (_unknown == 0 ? CollectorStatus.Ok : _known == 0 && _issues.All(i => i.StartsWith("needs_admin:", StringComparison.Ordinal)) ? CollectorStatus.SkippedNoAdmin : CollectorStatus.Partial);
        return new CollectorRun { CollectorId = CollectorId, Status = status, ElapsedMs = elapsedMs, IssueCodes = _issues.ToList() };
    }

    private static string IssuePrefix(UnknownReason reason) => reason switch
    {
        UnknownReason.NeedsAdmin => "needs_admin",
        UnknownReason.AccessDenied => "access_denied",
        UnknownReason.TimedOut => "timeout",
        UnknownReason.Unsupported => "unsupported",
        UnknownReason.Unparseable => "unparseable",
        UnknownReason.NotCollected => "not_collected",
        UnknownReason.InsufficientData => "insufficient_data",
        UnknownReason.Redacted => "redacted",
        UnknownReason.Incomplete => "partial",
        _ => "failed",
    };
}

/// <summary>
/// Base of every built-in collector: runs <see cref="Collect"/> on a worker, never throws for data problems
/// (exceptions become a Failed run with Unknown(Failed) sections), and honours cancellation.
/// </summary>
/// <param name="sources">Sources used when the collector runs standalone (outside the orchestrator).</param>
public abstract class CollectorBase(SystemSources sources) : ICollector
{
    /// <summary>Sources for standalone runs.</summary>
    protected SystemSources Sources { get; } = sources ?? throw new ArgumentNullException(nameof(sources));

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract ScanArea Area { get; }

    /// <inheritdoc />
    public virtual CollectorNeeds Needs => CollectorNeeds.None;

    /// <inheritdoc />
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<CollectorOutput> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.CaptureRaw)
        {
            return await CollectAsync(new ScanEnvironment(Sources, context), cancellationToken).ConfigureAwait(false);
        }

        var recorder = new CaptureRecorder();
        var env = new ScanEnvironment(CapturingSources.Wrap(Sources, recorder), context);
        var output = await CollectAsync(env, cancellationToken).ConfigureAwait(false);
        return output with { Capture = recorder.Entries };
    }

    /// <summary>Collects against a shared scan environment (used by the orchestrator).</summary>
    /// <param name="env">Environment.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<CollectorOutput> CollectAsync(ScanEnvironment env, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(env);
        return Task.Run(
            () =>
            {
                CaptureRecorder.CurrentCollector = Id;
                var sw = Stopwatch.StartNew();
                var run = new RunBuilder(Id);
                try
                {
                    var patch = Collect(env, run, cancellationToken);
                    return new CollectorOutput { Run = run.Build(sw.ElapsedMilliseconds), Patch = patch };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    run.Issue("exception:" + e.GetType().Name);
                    run.ForcedStatus = CollectorStatus.Failed;
                    return new CollectorOutput { Run = run.Build(sw.ElapsedMilliseconds), Patch = new SnapshotPatch() };
                }
            },
            cancellationToken);
    }

    /// <summary>Collects.</summary>
    /// <param name="env">Environment.</param>
    /// <param name="run">Run builder.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    protected abstract SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken);
}
