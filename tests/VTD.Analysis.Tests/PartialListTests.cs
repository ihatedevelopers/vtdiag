using VTD.Analysis.Signals;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Analysis.Tests;

/// <summary>A partial list (Fact.IsComplete false): presence stays evidence, absence is Unknown(Incomplete), never False.</summary>
public sealed class PartialListTests
{
    private static IReadOnlyDictionary<string, SignalResult> Signals(ScanSnapshot s) => new SignalEngine(SignalEngine.DefaultProviders()).Evaluate(new SignalContext
    {
        Snapshot = s,
        Intake = IntakeAnswers.Empty,
        Kb = TestKb.Real,
        AsOfUtc = s.Meta.CreatedUtc.AddMinutes(5),
    });

    [Fact]
    public void Absence_from_a_partial_app_list_is_unknown_and_presence_still_counts()
    {
        var healthy = Fixtures.Load(Fixtures.RealtekStutter);
        var apps = healthy.Apps.Value;
        var withoutRedist = apps.Where(a => !a.DisplayName.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)).ToList();

        var complete = Signals(healthy with { Apps = Fact.Known<IReadOnlyList<InstalledApp>>(withoutRedist) });
        var partial = Signals(healthy with { Apps = Fact<IReadOnlyList<InstalledApp>>.Partial(withoutRedist, "player_hive_unreadable") });
        Assert.Equal(Tri.True, complete["apps.vcredist_missing"].State);
        Assert.Equal(Tri.Unknown, partial["apps.vcredist_missing"].State); // it may be in the hive we could not read
        Assert.Equal(UnknownReason.Incomplete, partial["apps.vcredist_missing"].UnknownReason);

        IReadOnlyList<InstalledApp> nahimic = [.. withoutRedist, new InstalledApp { DisplayName = "Nahimic", Publisher = "A-Volute", Hive = AppHive.Hklm, KeyName = "{NAHIMIC}" }];
        var present = Signals(healthy with { Apps = Fact<IReadOnlyList<InstalledApp>>.Partial(nahimic) });
        Assert.Equal(Tri.True, present["conflict.nahimic.installed"].State);
        var absent = Signals(healthy with { Apps = Fact<IReadOnlyList<InstalledApp>>.Partial(withoutRedist) });
        Assert.Equal(Tri.Unknown, absent["conflict.nahimic.installed"].State);
    }

    [Fact]
    public void Too_few_events_in_a_partial_digest_is_unknown()
    {
        var s = Fixtures.Load(Fixtures.RealtekStutter);
        var hits = s.Events.Hits.Value;
        var none = Signals(s with { Events = s.Events with { Hits = Fact.Known<IReadOnlyList<EventHit>>([]) } });
        var partial = Signals(s with { Events = s.Events with { Hits = Fact<IReadOnlyList<EventHit>>.Partial([], "failed:1;truncated:0") } });
        var states = none.Where(kv => kv.Key.StartsWith("events.", StringComparison.Ordinal) && kv.Value.State == Tri.False).Select(kv => kv.Key).ToList();
        Assert.NotEmpty(hits);

        // Event-digest signals (count below threshold) flip to Unknown; signals from other facts (bugchecks, app crashes)
        // keep their False; nothing becomes True.
        var flipped = states.Where(id => partial[id].State != Tri.False).ToList();
        Assert.Contains("events.kernel_power_41", flipped);
        Assert.All(flipped, id => Assert.Equal(UnknownReason.Incomplete, partial[id].UnknownReason));
        Assert.Contains("events.bugcheck_recent", states.Except(flipped));
    }
}
