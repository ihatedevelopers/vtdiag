using VTD.Contracts;
using VTD.Contracts.Fixes;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Serialization;

namespace VTD.Kb.Tests;

/// <summary>
/// Regression for a contract-level trap (fixed at the root in VtdJson): ABSENT init-only members must keep their
/// initializer defaults, because hand-written KB files omit optional members.
/// </summary>
public sealed class DefaultsTests
{
    private const string Rule = "{\"id\":\"x\",\"category\":\"other\",\"titleKey\":\"t\",\"prior\":{\"threads\":1}}";
    private const string Guide = "{\"id\":\"g\",\"titleKey\":\"t\",\"branches\":[]}";

    [Fact]
    public void Kb_reader_keeps_contract_defaults_for_absent_members()
    {
        var r = VtdJson.Deserialize<RuleDef>(Rule);
        Assert.Equal(1, r.Rev);
        Assert.NotNull(r.Symptoms);
        var g = VtdJson.Deserialize<GuideDef>(Guide);
        Assert.Equal(RiskLevel.High, g.Risk);
        Assert.Equal(HonestyBucket.GuidedManual, g.Bucket);
        var guard = VtdJson.Deserialize<GuardDef>("{\"probe\":\"riot.game_running\",\"failKey\":\"k\"}");
        Assert.Equal(Tri.True, guard.Must);
        var c = VtdJson.Deserialize<IReadOnlyList<CollectorDef>>("[{\"kind\":\"eventLog\",\"id\":\"a\",\"log\":\"System\"}]");
        Assert.Equal(14, ((EventLogCollectorDef)c[0]).SinceDays);
    }
}
