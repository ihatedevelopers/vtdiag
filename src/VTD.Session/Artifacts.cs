using System.Collections.Concurrent;
using VTD.Contracts.Analysis;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Snapshots;

namespace VTD.Session;

/// <summary>
/// Where the engine keeps raw (local, unredacted) snapshots, analyses and monitor summaries by id. Elevated engines
/// persist them under <c>%ProgramData%\VTDiag\snapshots</c> (admin-only); limited ones keep them in memory.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Stores a snapshot.</summary>
    /// <param name="snapshot">Snapshot.</param>
    void Put(ScanSnapshot snapshot);

    /// <summary>Stores an analysis.</summary>
    /// <param name="analysis">Analysis.</param>
    void Put(AnalysisResult analysis);

    /// <summary>Stores a monitor summary.</summary>
    /// <param name="summary">Summary.</param>
    void Put(MonitorSummary summary);

    /// <summary>A snapshot, or null.</summary>
    /// <param name="snapshotId">Id.</param>
    ScanSnapshot? Snapshot(string? snapshotId);

    /// <summary>An analysis, or null.</summary>
    /// <param name="analysisId">Id.</param>
    AnalysisResult? Analysis(string? analysisId);

    /// <summary>A monitor summary, or null.</summary>
    /// <param name="summaryId">Id.</param>
    MonitorSummary? Summary(string? summaryId);
}

/// <summary>In-memory <see cref="IArtifactStore"/> (limited mode, tests); keeps the last few of each.</summary>
public sealed class MemoryArtifactStore : IArtifactStore
{
    private const int Keep = 8;
    private readonly ConcurrentDictionary<string, ScanSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AnalysisResult> _analyses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MonitorSummary> _summaries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Put(ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Trim(_snapshots);
        _snapshots[snapshot.Meta.SnapshotId] = snapshot;
    }

    /// <inheritdoc />
    public void Put(AnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Trim(_analyses);
        _analyses[analysis.AnalysisId] = analysis;
    }

    /// <inheritdoc />
    public void Put(MonitorSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Trim(_summaries);
        _summaries[summary.SummaryId] = summary;
    }

    /// <inheritdoc />
    public ScanSnapshot? Snapshot(string? snapshotId) => snapshotId is not null && _snapshots.TryGetValue(snapshotId, out var s) ? s : null;

    /// <inheritdoc />
    public AnalysisResult? Analysis(string? analysisId) => analysisId is not null && _analyses.TryGetValue(analysisId, out var a) ? a : null;

    /// <inheritdoc />
    public MonitorSummary? Summary(string? summaryId) => summaryId is not null && _summaries.TryGetValue(summaryId, out var m) ? m : null;

    // ULIDs sort by creation time: drop the oldest.
    private static void Trim<T>(ConcurrentDictionary<string, T> map)
    {
        while (map.Count >= Keep)
        {
            var oldest = map.Keys.Order(StringComparer.Ordinal).First();
            map.TryRemove(oldest, out _);
        }
    }
}
