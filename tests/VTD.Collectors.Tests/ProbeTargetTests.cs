using VTD.Collectors.Collectors;
using VTD.Collectors.Core;
using VTD.Contracts.Scanning;

namespace VTD.Collectors.Tests;

/// <summary>KB region ping targets: canonical public IP literals only, capped, no shadowing of the built-in targets.</summary>
public sealed class ProbeTargetTests
{
    [Fact]
    public void Only_canonical_ip_literals_with_safe_labels_are_kept_and_the_list_is_capped()
    {
        var run = new RunBuilder("network");
        IReadOnlyList<ProbeTarget> requested =
        [
            new() { Label = "eu-central", Host = "203.0.113.10" },
            new() { Label = "us-east", Host = "2001:db8::10" },
            new() { Label = "dns-name", Host = "example.com" },
            new() { Label = "shorthand", Host = "127.1" },
            new() { Label = "hex", Host = "0x7f000001" },
            new() { Label = "loop", Host = "127.0.0.1" },
            new() { Label = "gateway", Host = "203.0.113.11" },
            new() { Label = "bad label!", Host = "203.0.113.12" },
            new() { Label = "eu-central", Host = "203.0.113.13" },
            .. Enumerable.Range(20, 10).Select(i => new ProbeTarget { Label = "r" + i, Host = "198.51.100." + i }),
        ];
        var kept = NetworkCollector.RegionTargets(requested, run);
        Assert.Equal(ProbeTarget.MaxTargets, kept.Count);
        Assert.Equal(["eu-central", "us-east", "r20", "r21", "r22", "r23"], kept.Select(k => k.Label));
    }
}
