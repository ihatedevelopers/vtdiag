using System.Text.RegularExpressions;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Snapshots;

namespace VTD.Analysis.Signals;

public static partial class BuiltinSignals
{
    private static readonly string[] RealtekDrivers = ["rt25cx21x64.sys", "rt640x64.sys", "rtcx21x64.sys", "rt68cx21x64.sys", "rtux64w10.sys"];
    private static readonly string[] PowerSavingKeywords = ["*EEE", "EEE", "AdvancedEEE", "*GreenEthernet", "EnableGreenEthernet", "GreenEthernet", "GigaLite", "PowerSavingMode"];
    private static readonly Regex RealtekWired = new(@"Realtek.*(2\.5\s?GbE|GbE|Gaming|PCIe)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static bool IsRealtekWired(NetAdapter a)
        => a.Physical && a.Kind == AdapterKind.Ethernet
           && (RealtekWired.IsMatch(a.Description) || (a.DriverFile is not null && RealtekDrivers.Contains(a.DriverFile, StringComparer.OrdinalIgnoreCase)));

    private static bool IsOnline(NetAdapter a) => a.Enabled && a.Status == AdapterOperStatus.Up && (a.HasInternet == true || a.IsDefaultRoute);

    private static EntityRef Entity(NetAdapter a) => new() { Kind = EntityKind.Adapter, Id = a.InstanceId, DisplayName = a.Description };

    private static IEnumerable<SignalResult> RealtekNic(SignalContext c)
    {
        string[] ids = ["net.realtek25.present", "net.realtek25.enabled", "net.realtek25.unused", "net.realtek25.active"];
        if (!c.Snapshot.Network.Adapters.TryGetValue(out var adapters))
        {
            return ids.Select(i => Sig.Unknown(i, Sig.ReasonOf(c.Snapshot.Network.Adapters)));
        }

        var realtek = adapters.Where(IsRealtekWired).OrderBy(a => a.InstanceId, StringComparer.Ordinal).ToList();
        if (realtek.Count == 0)
        {
            return [Sig.False(ids[0]), Sig.NotApplicable(ids[1]), Sig.NotApplicable(ids[2]), Sig.NotApplicable(ids[3])];
        }

        var enabled = realtek.Where(a => a.Enabled).ToList();
        var active = realtek.Where(a => a.IsDefaultRoute).ToList();
        var unused = enabled.Where(a => !a.IsDefaultRoute && adapters.Any(o => o.Physical && !ReferenceEquals(o, a) && IsOnline(o))).ToList();
        return
        [
            Sig.True(ids[0], 1.0, [Sig.Ev(ids[0], ("adapter", realtek[0].Description))], [.. realtek.Select(Entity)]),
            enabled.Count > 0 ? Sig.True(ids[1], 1.0, null, [.. enabled.Select(Entity)]) : Sig.False(ids[1]),
            unused.Count > 0 ? Sig.True(ids[2], 1.0, [Sig.Ev(ids[2], ("adapter", unused[0].Description))], [.. unused.Select(Entity)]) : Sig.False(ids[2]),
            active.Count > 0 ? Sig.True(ids[3], 1.0, null, [.. active.Select(Entity)]) : Sig.False(ids[3]),
        ];
    }

    private static IEnumerable<SignalResult> RealtekEvents(SignalContext c)
    {
        yield return EventSignal(c, "events.realtek_nic_resets", "realtek_nic_errors", ["rt25cx21", "rtcx21", "rt640x64", "rt68cx21", "rtux64w10"], [], 14, 3, 10);
        yield return Sig.FromFact("events.vgm_crash_0xc0000409", c.Snapshot.Events.AppCrashes, crashes =>
        {
            var n = crashes.Count(x => string.Equals(x.Exe, "vgm.exe", StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(x.ExceptionCode, "0xc0000409", StringComparison.OrdinalIgnoreCase)
                                       && x.Utc >= AsOf(c).AddDays(-30));
            return Sig.Of("events.vgm_crash_0xc0000409", n > 0, Math.Min(1.0, n / 3.0));
        });
    }

    private static IEnumerable<SignalResult> MultiNic(SignalContext c)
    {
        if (!c.Snapshot.Network.Adapters.TryGetValue(out var adapters))
        {
            var r = Sig.ReasonOf(c.Snapshot.Network.Adapters);
            return [Sig.Unknown("net.multi_nic_up", r), Sig.Unknown("net.secondary_adapter_up", r)];
        }

        var up = adapters.Where(a => a.Physical && a.Enabled && a.Status == AdapterOperStatus.Up).OrderBy(a => a.InstanceId, StringComparer.Ordinal).ToList();
        var secondary = up.Where(a => !a.IsDefaultRoute && a.HasInternet != false && a.Kind is AdapterKind.Ethernet or AdapterKind.WiFi).ToList();
        var anyDefault = up.Any(a => a.IsDefaultRoute);
        return
        [
            up.Count >= 2 ? Sig.True("net.multi_nic_up", 1.0, [Sig.Ev("net.multi_nic_up", ("count", Sig.Num(up.Count)))], [.. up.Where(a => !a.IsDefaultRoute).Select(Entity)]) : Sig.False("net.multi_nic_up"),
            secondary.Count > 0 && anyDefault ? Sig.True("net.secondary_adapter_up", 1.0, null, [.. secondary.Select(Entity)]) : Sig.False("net.secondary_adapter_up"),
        ];
    }

    private static IEnumerable<SignalResult> NicPower(SignalContext c)
    {
        const string Id = "net.nic_power_saving_on";
        if (!c.Snapshot.Network.Adapters.TryGetValue(out var adapters))
        {
            return [Sig.Unknown(Id, Sig.ReasonOf(c.Snapshot.Network.Adapters))];
        }

        var wired = adapters.Where(a => a.Physical && a.Kind == AdapterKind.Ethernet && a.Enabled).ToList();
        if (wired.Count == 0)
        {
            return [Sig.NotApplicable(Id)];
        }

        if (wired.All(a => a.AdvancedProperties.Count == 0))
        {
            return [Sig.Unknown(Id, UnknownReason.NotCollected)];
        }

        var on = wired.Where(a => PowerSavingKeywords.Any(k => a.AdvancedProperties.TryGetValue(k, out var v) && string.Equals(v, "1", StringComparison.Ordinal)))
            .OrderByDescending(a => a.IsDefaultRoute).ThenBy(a => a.InstanceId, StringComparer.Ordinal).ToList();
        return [on.Count > 0 ? Sig.True(Id, 1.0, [Sig.Ev(Id, ("adapter", on[0].Description))], [.. on.Select(Entity)]) : Sig.False(Id)];
    }

    private static IEnumerable<SignalResult> Wifi(SignalContext c)
    {
        var net = c.Snapshot.Network;
        if (net.Adapters.TryGetValue(out var adapters))
        {
            var def = adapters.FirstOrDefault(a => a.IsDefaultRoute);
            yield return def is null ? Sig.Unknown("net.on_wifi", UnknownReason.InsufficientData) : Sig.Of("net.on_wifi", def.Kind == AdapterKind.WiFi);
        }
        else
        {
            yield return Sig.Unknown("net.on_wifi", Sig.ReasonOf(net.Adapters));
        }

        if (!net.Wlan.TryGetValue(out var w))
        {
            var na = net.Wlan.Status == FactStatus.NotApplicable;
            yield return na ? Sig.NotApplicable("net.wifi_weak") : Sig.Unknown("net.wifi_weak", Sig.ReasonOf(net.Wlan));
            yield return na ? Sig.NotApplicable("net.wifi_2_4ghz") : Sig.Unknown("net.wifi_2_4ghz", Sig.ReasonOf(net.Wlan));
            yield break;
        }

        if (!w.Connected)
        {
            yield return Sig.NotApplicable("net.wifi_weak");
            yield return Sig.NotApplicable("net.wifi_2_4ghz");
            yield break;
        }

        if (w.SignalQualityPercent is null && w.RssiDbm is null)
        {
            yield return Sig.Unknown("net.wifi_weak", UnknownReason.NotCollected);
        }
        else
        {
            var weak = w.SignalQualityPercent is < 60 || w.RssiDbm is < -67;
            yield return Sig.Of("net.wifi_weak", weak, 1.0, [Sig.Ev("net.wifi_weak", ("quality", Sig.Num(w.SignalQualityPercent ?? 0)))]);
        }

        yield return w.Band == WifiBand.Unknown ? Sig.Unknown("net.wifi_2_4ghz", UnknownReason.NotCollected) : Sig.Of("net.wifi_2_4ghz", w.Band == WifiBand.Ghz24);
    }

    private static IEnumerable<SignalResult> PingRoute(SignalContext c)
    {
        var net = c.Snapshot.Network;
        yield return Sig.FromFact("net.no_internet", net.Connectivity, ci => Sig.Of("net.no_internet", !ci.Ipv4Internet));
        if (!net.Probes.TryGetValue(out var probes))
        {
            var r = Sig.ReasonOf(net.Probes);
            foreach (var id in new[] { "net.gateway_loss", "net.region_ping_high", "net.region_loss", "net.region_jitter_high" })
            {
                yield return net.Probes.Status == FactStatus.NotApplicable ? Sig.NotApplicable(id) : Sig.Unknown(id, r);
            }

            yield break;
        }

        static double Loss(PingProbe p) => p.Sent <= 0 ? 0 : (p.Sent - p.Received) / (double)p.Sent;
        var gateway = probes.Where(p => p.Kind == ProbeTargetKind.Gateway && p.Sent >= 5).ToList();
        var regions = probes.Where(p => p.Kind == ProbeTargetKind.Region && p.Sent >= 5).ToList();
        var gwBad = gateway.Any(p => Loss(p) >= 0.05 || p.JitterMs is >= 10);
        if (gateway.Count == 0)
        {
            yield return Sig.Unknown("net.gateway_loss", UnknownReason.NotCollected);
        }
        else
        {
            var g = gateway[0];
            yield return Sig.Of("net.gateway_loss", gwBad, 1.0, [Sig.Ev("net.gateway_loss", ("lost", Sig.Num(g.Sent - g.Received)), ("sent", Sig.Num(g.Sent)))]);
        }

        if (regions.Count == 0)
        {
            yield return Sig.Unknown("net.region_ping_high", UnknownReason.NotCollected);
            yield return Sig.Unknown("net.region_loss", UnknownReason.NotCollected);
            yield return Sig.Unknown("net.region_jitter_high", UnknownReason.NotCollected);
            yield break;
        }

        var best = regions.Where(p => p.RttAvgMs is not null).OrderBy(p => p.RttAvgMs).FirstOrDefault();
        yield return best is null ? Sig.Unknown("net.region_ping_high", UnknownReason.InsufficientData)
            : Sig.Of("net.region_ping_high", best.RttAvgMs >= 90, Math.Min(1.0, (best.RttAvgMs!.Value - 60) / 90.0),
                [Sig.Ev("net.region_ping_high", ("target", best.Target), ("ms", Sig.Num(best.RttAvgMs.Value)))]);
        var regionLoss = regions.Any(p => Loss(p) >= 0.05);
        yield return Sig.Of("net.region_loss", regionLoss && !gwBad, 1.0,
            [Sig.Ev("net.region_loss", ("lost", Sig.Num(regions.Sum(p => p.Sent - p.Received))), ("sent", Sig.Num(regions.Sum(p => p.Sent))))]);
        var jitter = regions.Where(p => p.JitterMs is not null).ToList();
        yield return jitter.Count == 0 ? Sig.Unknown("net.region_jitter_high", UnknownReason.InsufficientData) : Sig.Of("net.region_jitter_high", jitter.Any(p => p.JitterMs >= 15));
    }

    private static IEnumerable<SignalResult> DnsIpv6(SignalContext c)
    {
        var net = c.Snapshot.Network;
        if (net.Adapters.TryGetValue(out var adapters))
        {
            var def = adapters.FirstOrDefault(a => a.IsDefaultRoute);
            yield return def is null ? Sig.False("net.active_adapter") : Sig.True("net.active_adapter", 1.0, null, [Entity(def)]);
            yield return def is null ? Sig.NotApplicable("net.active_ipv6_bound")
                : def.Ipv6Bound is null ? Sig.Unknown("net.active_ipv6_bound", UnknownReason.NotCollected)
                : Sig.Of("net.active_ipv6_bound", def.Ipv6Bound.Value, 1.0, null, [Entity(def)]);
        }
        else
        {
            yield return Sig.Unknown("net.active_adapter", Sig.ReasonOf(net.Adapters));
            yield return Sig.Unknown("net.active_ipv6_bound", Sig.ReasonOf(net.Adapters));
        }

        yield return Sig.FromFact("net.ipv6_disabled", net.Ipv6, v => v.DisabledComponents is null
            ? Sig.Unknown("net.ipv6_disabled", UnknownReason.NotCollected)
            : Sig.Of("net.ipv6_disabled", (v.DisabledComponents.Value & 0x10) != 0 || v.DisabledComponents.Value == 0xFF));
        yield return Sig.FromFact("net.teredo_on", net.Ipv6, v => Sig.FromNullable("net.teredo_on", v.TeredoEnabled));
    }

    private static IEnumerable<SignalResult> TcpStack(SignalContext c)
    {
        yield return Sig.FromFact("net.lsp_third_party", c.Snapshot.Network.WinsockProviders, lsps =>
            lsps.Count == 0 ? Sig.False("net.lsp_third_party") : Sig.True("net.lsp_third_party", 1.0, [Sig.Ev("net.lsp_third_party", ("items", Sig.List(lsps)))]));
    }

    private static IEnumerable<SignalResult> Mdns(SignalContext c)
    {
        // Bonjour's mdnsNSP.dll is a Winsock NAMESPACE provider; older captures folded it into the protocol list, so a hit
        // in either catalog counts. Otherwise the namespace catalog decides (False when it is clean and complete).
        static SignalResult Find(Fact<IReadOnlyList<string>> catalog) => Sig.FromFact("net.mdns_lsp", catalog, dlls =>
        {
            var hits = dlls.Where(l => l.Contains("mdnsNSP", StringComparison.OrdinalIgnoreCase) || l.Contains("nimdns", StringComparison.OrdinalIgnoreCase)).ToList();
            return hits.Count > 0 ? Sig.True("net.mdns_lsp", 1.0, [Sig.Ev("net.mdns_lsp", ("items", Sig.List(hits)))]) : Sig.False("net.mdns_lsp");
        });
        var ns = Find(c.Snapshot.Network.WinsockNamespaceProviders);
        var proto = Find(c.Snapshot.Network.WinsockProviders);
        var lsp = ns.State == Tri.True ? ns : proto.State == Tri.True ? proto : ns;
        yield return lsp;
        var parts = new List<SignalResult> { lsp };
        foreach (var id in new[] { "bonjour_mdns", "ni_mdns" })
        {
            if (c.Kb.FindConflict(id) is { } def)
            {
                parts.AddRange(ConflictMatcher.Evaluate(def, c.Snapshot, AsOf(c)).Where(r => r.Id.EndsWith(".installed", StringComparison.Ordinal) || r.Id.EndsWith(".running", StringComparison.Ordinal)));
            }
        }

        yield return Sig.Any("apps.mdns_component", parts);
    }

    private static IEnumerable<SignalResult> VirtualNic(SignalContext c)
    {
        var net = c.Snapshot.Network;
        if (net.Adapters.TryGetValue(out var adapters))
        {
            var vpn = adapters.Where(a => a.Kind == AdapterKind.Tunnel && a.Enabled && a.Status == AdapterOperStatus.Up).ToList();
            var virt = adapters.Where(a => a.Kind == AdapterKind.Virtual && a.Enabled).ToList();
            yield return vpn.Count > 0 ? Sig.True("net.vpn_adapter_up", 1.0, [Sig.Ev("net.vpn_adapter_up", ("items", Sig.List(vpn.Select(v => v.Description))))], [.. vpn.Select(Entity)]) : Sig.False("net.vpn_adapter_up");
            yield return virt.Count > 0 ? Sig.True("net.virtual_adapter_present", 1.0, [Sig.Ev("net.virtual_adapter_present", ("items", Sig.List(virt.Select(v => v.Description))))], [.. virt.Select(Entity)]) : Sig.False("net.virtual_adapter_present");
        }
        else
        {
            yield return Sig.Unknown("net.vpn_adapter_up", Sig.ReasonOf(net.Adapters));
            yield return Sig.Unknown("net.virtual_adapter_present", Sig.ReasonOf(net.Adapters));
        }

        yield return Sig.FromFact("net.proxy_on", net.Proxy, p => Sig.Of("net.proxy_on", p.WinHttpProxy || p.UserProxyEnabled || p.AutoConfigUrl));
    }

    private static IEnumerable<SignalResult> Firewall(SignalContext c)
    {
        var net = c.Snapshot.Network;
        yield return Sig.FromFact("net.firewall_blocks_riot", net.RiotFirewallRules, rules =>
        {
            var blocks = rules.Where(r => r.Enabled && r.Action == FirewallAction.Block).Select(r => r.ProgramImageName).ToList();
            return blocks.Count > 0 ? Sig.True("net.firewall_blocks_riot", 1.0, [Sig.Ev("net.firewall_blocks_riot", ("items", Sig.List(blocks)))]) : Sig.False("net.firewall_blocks_riot");
        });
        yield return Sig.FromFact("net.firewall_off", net.Firewall, f => Sig.Of("net.firewall_off", !(f.DomainOn && f.PrivateOn && f.PublicOn)));
    }
}
