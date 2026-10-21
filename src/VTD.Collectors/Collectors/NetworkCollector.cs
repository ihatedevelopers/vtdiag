using System.Text;
using System.Text.RegularExpressions;
using VTD.Collectors.Core;
using VTD.Contracts;
using VTD.Contracts.Scanning;
using VTD.Contracts.Snapshots;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>
/// Adapters (PnP + Net class key + System.Net + iphlpapi; keyed by InstanceId), connectivity (NLM), proxy flags (no
/// hosts), IPv6, Winsock catalog DLLs, firewall rules on Riot executables, profiles, Wi-Fi link, indicative probes.
/// Never parses localized <c>netsh</c> output.
/// </summary>
/// <param name="sources">Sources for standalone runs.</param>
public sealed partial class NetworkCollector(SystemSources sources) : CollectorBase(sources)
{
    internal static readonly string[] AdvancedKeywords =
    [
        "AdvancedEEE", "EEELinkAdvertisement", "GreenEthernet", "EnableGreenEthernet", "PowerSavingMode", "EnablePME", "ULPMode",
        "EnableSavePowerNow", "S5WakeOnLan", "WakeOnLink", "GigaLite", "AutoDisableGigabit", "ReduceSpeedOnPowerDown",
        "WaitAutoNegComplete", "EnableExtraPowerSaving", "RoamingPreferredBandType", "RoamAggressiveness", "ThroughputBoosterEnabled",
        "MIMOPowerSaveMode", "uAPSDSupport", "EnableDynamicPowerGating", "EnableConnectedPowerGating", "EnablePowerManagement",
    ];

    private static readonly string[] MicrosoftWinsockDlls =
    [
        "mswsock.dll", "napinsp.dll", "pnrpnsp.dll", "wshbth.dll", "nlaapi.dll", "winrnr.dll", "rsvpsp.dll", "hvsocket.dll",
        "wshqos.dll", "wshtcpip.dll", "wship6.dll", "vsocklib.dll", "wshhyperv.dll", "mdnsnsp.dll.disabled", "nlansp_c.dll",
    ];

    /// <inheritdoc />
    public override string Id => "network";

    /// <inheritdoc />
    public override ScanArea Area => ScanArea.Network;

    /// <inheritdoc />
    public override CollectorNeeds Needs => CollectorNeeds.Network;

    /// <inheritdoc />
    public override TimeSpan Timeout => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override SnapshotPatch Collect(ScanEnvironment env, RunBuilder run, CancellationToken cancellationToken)
    {
        var net = env.Sources.System.GetNetwork();
        var conn = env.Sources.System.GetConnectivity();
        var connectivity = conn.Status == ReadStatus.Ok
            ? run.Known(new ConnectivityInfo { Ipv4Internet = (conn.Flags & 0x40) != 0, Ipv6Internet = (conn.Flags & 0x400) != 0, LocalOnly = (conn.Flags & 0x440) == 0 && (conn.Flags & 0x330) != 0 }, "nlm")
            : run.FromStatus<ConnectivityInfo>(env, conn.Status, "nlm");
        var adapters = run.Try("iphlpapi+pnp", () => Adapters(env, run, net, connectivity));
        cancellationToken.ThrowIfCancellationRequested();
        var state = new NetworkState
        {
            Adapters = adapters,
            Connectivity = connectivity,
            Proxy = run.Try("winhttp+wininet", () => Proxy(env, run, net)),
            Ipv6 = run.Try("registry:Tcpip6", () => Ipv6(env, run)),
            WinsockProviders = run.Try("registry:WinSock2", () => Winsock(env, run)),
            WinsockNamespaceProviders = run.Try("registry:WinSock2-NameSpace", () => WinsockNamespaces(env, run)),
            RiotFirewallRules = run.Try("registry:FirewallRules", () => FirewallRules(env, run)),
            Firewall = run.Try("registry:FirewallPolicy", () => Profiles(env, run)),
            Wlan = run.Try("wlanapi", () => Wlan(env, run, adapters)),
            Probes = run.Try("icmp", () => Probes(env, run, adapters, net)),
        };
        return new SnapshotPatch { Network = state };
    }

    private static Fact<IReadOnlyList<NetAdapter>> Adapters(ScanEnvironment env, RunBuilder run, NetRaw net, Fact<ConnectivityInfo> connectivity)
    {
        const string m = "iphlpapi+pnp";
        var devices = env.Devices;
        if (devices.Status != ReadStatus.Ok)
        {
            return run.FromStatus<IReadOnlyList<NetAdapter>>(env, devices.Status, m);
        }

        if (net.Status != ReadStatus.Ok)
        {
            run.Issue("failed:GetAllNetworkInterfaces");
        }

        var nics = net.Interfaces.ToDictionary(n => n.Guid, StringComparer.OrdinalIgnoreCase);
        var bind = env.Hklm(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Linkage").Str("Bind") ?? string.Empty;
        var list = new List<NetAdapter>();
        foreach (var d in devices.Items.Where(d => string.Equals(d.ClassGuid, DeviceClasses.Net, StringComparison.OrdinalIgnoreCase)))
        {
            if (d.DriverKey is not { Length: > 0 } dk)
            {
                continue;
            }

            var ck = env.Hklm(@"SYSTEM\CurrentControlSet\Control\Class\" + dk);
            var guid = ck.Str("NetCfgInstanceId")?.ToUpperInvariant();
            if (guid is null)
            {
                continue;
            }

            nics.TryGetValue(guid, out var nic);
            var characteristics = ck.Num("Characteristics") ?? 0;
            var hidden = (characteristics & 0x8) != 0;
            var up = nic?.OperStatus == 1;
            var isDefaultRoute = nic is not null && nic.Ipv4Index > 0 && nic.Ipv4Index == net.BestIpv4Index;
            if ((hidden && !isDefaultRoute) || d.InstanceId.StartsWith(@"SWD\MSRRAS\", StringComparison.OrdinalIgnoreCase) || (!d.Present && nic is null && d.ProblemCode != 22 && !string.Equals(d.Enumerator, "PCI", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var description = ck.Str("DriverDesc") ?? nic?.Description ?? d.FriendlyName ?? "Network adapter";
            var kind = KindOf(ck.Num("*IfType"), ck.Num("*PhysicalMediaType"), characteristics, d.Enumerator, description);
            var physical = kind is AdapterKind.Ethernet or AdapterKind.WiFi && (characteristics & 0x1) == 0
                           && d.Enumerator is not null && !d.Enumerator.Equals("ROOT", StringComparison.OrdinalIgnoreCase) && !d.Enumerator.Equals("SWD", StringComparison.OrdinalIgnoreCase);
            var isDefault = nic is not null && nic.Ipv4Index > 0 && nic.Ipv4Index == net.BestIpv4Index;
            var advanced = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in ck.Values)
            {
                if (advanced.Count >= 60 || v.Data is null || string.Equals(v.Name, "NetworkAddress", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var keyword = v.Name.StartsWith('*') && v.Name is not "*IfType" and not "*MediaType" and not "*PhysicalMediaType" and not "*NdisDeviceType";
                if ((keyword || AdvancedKeywords.Contains(v.Name, StringComparer.OrdinalIgnoreCase)) && v.Data.Length <= 32)
                {
                    advanced[v.Name] = v.Data;
                }
            }

            string? driverFile = null;
            if (d.Service is { Length: > 0 } svc)
            {
                var image = PathText.NormalizeImagePath(env.Hklm(@"SYSTEM\CurrentControlSet\Services\" + svc).Str("ImagePath"), env.WindowsDir, env.Env.ProgramFiles);
                driverFile = image is null ? null : PathText.FileName(image);
            }

            var pnpCaps = ck.Num("PnPCapabilities");
            list.Add(new NetAdapter
            {
                InstanceId = d.InstanceId,
                InterfaceGuid = guid,
                Description = description,
                Alias = nic?.Alias,
                Kind = kind,
                Physical = physical,
                Enabled = d.ProblemCode != 22,
                Status = nic is null ? AdapterOperStatus.Down : OperStatus(nic.OperStatus),
                LinkSpeedBps = up && nic!.SpeedBps > 0 ? nic.SpeedBps : null,
                IsDefaultRoute = isDefault,
                HasInternet = isDefault && connectivity.TryGetValue(out var c) ? c.Ipv4Internet : null,
                Ipv4Metric = nic?.Ipv4Metric,
                AutomaticMetric = nic?.AutomaticMetric,
                Ipv6Bound = bind.Length == 0 ? null : bind.Contains(@"\Device\" + guid, StringComparison.OrdinalIgnoreCase),
                DriverFile = driverFile,
                DriverVersion = d.DriverVersion ?? ck.Str("DriverVersion"),
                DriverDate = d.DriverDate is { } dd ? DateOnly.FromDateTime(dd.UtcDateTime) : null,
                DriverProvider = d.DriverProvider ?? ck.Str("ProviderName"),
                AdvancedProperties = advanced,
                AllowPowerOff = pnpCaps is null ? true : (pnpCaps.Value & 0x18) == 0,
                Mac = nic?.Mac,
                Ipv4Addresses = nic?.Ipv4 ?? [],
                DnsServers = nic?.Dns ?? [],
            });
        }

        return run.Known<IReadOnlyList<NetAdapter>>(list.OrderByDescending(a => a.IsDefaultRoute).ThenBy(a => a.InstanceId, StringComparer.OrdinalIgnoreCase).ToList(), m);
    }

    internal static AdapterKind KindOf(long? ifType, long? physicalMedia, long characteristics, string? enumerator, string description)
    {
        if (TunnelNames().IsMatch(description) || ifType is 131 or 53 or 23)
        {
            return AdapterKind.Tunnel;
        }

        if (VirtualNames().IsMatch(description) || ((characteristics & 0x1) != 0 && ifType is not 71))
        {
            return AdapterKind.Virtual;
        }

        if (ifType == 24)
        {
            return AdapterKind.Loopback;
        }

        if (ifType == 71 || physicalMedia is 9 or 1)
        {
            return AdapterKind.WiFi;
        }

        if (physicalMedia == 10 || (enumerator?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return AdapterKind.Bluetooth;
        }

        if (ifType is 243 or 244 || physicalMedia == 8)
        {
            return AdapterKind.Cellular;
        }

        if (enumerator is not null && (enumerator.Equals("ROOT", StringComparison.OrdinalIgnoreCase) || enumerator.Equals("SWD", StringComparison.OrdinalIgnoreCase)))
        {
            return AdapterKind.Virtual;
        }

        return ifType == 6 || physicalMedia == 14 ? AdapterKind.Ethernet : AdapterKind.Other;
    }

    private static AdapterOperStatus OperStatus(int status) => status switch
    {
        1 => AdapterOperStatus.Up,
        2 => AdapterOperStatus.Down,
        3 => AdapterOperStatus.Testing,
        5 => AdapterOperStatus.Dormant,
        6 => AdapterOperStatus.NotPresent,
        7 => AdapterOperStatus.LowerLayerDown,
        _ => AdapterOperStatus.Unknown,
    };

    private static Fact<ProxyInfo> Proxy(ScanEnvironment env, RunBuilder run, NetRaw net)
    {
        const string m = "winhttp+wininet";
        var user = env.Hkcu(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if ((!user.Ok() && user.Status != ReadStatus.NotFound) || net.WinHttpProxy is null)
        {
            return run.FromStatus<ProxyInfo>(env, !user.Ok() && user.Status != ReadStatus.NotFound ? user.Status : ReadStatus.Failed, m);
        }

        return run.Known(
            new ProxyInfo
            {
                WinHttpProxy = net.WinHttpProxy ?? false,
                UserProxyEnabled = user.Num("ProxyEnable") == 1,
                AutoConfigUrl = !string.IsNullOrWhiteSpace(user.Str("AutoConfigURL")),
            },
            m);
    }

    private static Fact<Ipv6Info> Ipv6(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:Tcpip6";
        var p = env.Hklm(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters");
        if (!p.Ok())
        {
            return run.FromStatus<Ipv6Info>(env, p.Status, m);
        }

        var dc = p.Num("DisabledComponents");
        var teredo = env.Hklm(@"SYSTEM\CurrentControlSet\Services\iphlpsvc\Teredo");
        var teredoType = teredo.Str("Type");
        var policy = env.Hklm(@"SOFTWARE\Policies\Microsoft\Windows\TCPIP\v6Transition");
        var teredoOff = (dc is { } d && ((d & 0x8) != 0 || (d & 0xFF) == 0xFF))
                        || string.Equals(teredoType, "4", StringComparison.Ordinal) || string.Equals(teredoType, "disabled", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(policy.Str("Teredo_State"), "Disabled", StringComparison.OrdinalIgnoreCase);
        return run.Known(new Ipv6Info { DisabledComponents = dc is null ? 0 : (int)dc.Value, TeredoEnabled = !teredoOff }, m);
    }

    private const string WinsockRoot = @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters";
    private const int MaxWinsockEntries = 200;

    private static Fact<IReadOnlyList<string>> Winsock(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:WinSock2";
        var protocols = env.Hklm(WinsockRoot + @"\Protocol_Catalog9\Catalog_Entries64");
        if (!protocols.Ok())
        {
            return run.FromStatus<IReadOnlyList<string>>(env, protocols.Status, m);
        }

        var dlls = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        foreach (var sub in protocols.SubKeys.Take(MaxWinsockEntries))
        {
            var entry = env.Hklm(WinsockRoot + @"\Protocol_Catalog9\Catalog_Entries64\" + sub);
            var bytes = entry.Ok() ? Parse.Hex(entry.Str("PackedCatalogItem")) : null;
            if (bytes is { Length: > 0 })
            {
                var end = Array.IndexOf(bytes, (byte)0);
                var path = Encoding.Latin1.GetString(bytes, 0, end < 0 ? Math.Min(bytes.Length, 260) : Math.Min(end, 260));
                AddDll(dlls, path);
            }
            else
            {
                unreadable++;
            }
        }

        return CatalogFact(run, dlls, unreadable, protocols.SubKeys.Count > MaxWinsockEntries, m);
    }

    private static Fact<IReadOnlyList<string>> WinsockNamespaces(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:WinSock2-NameSpace";
        var namespaces = env.Hklm(WinsockRoot + @"\NameSpace_Catalog5\Catalog_Entries64");
        if (!namespaces.Ok())
        {
            return run.FromStatus<IReadOnlyList<string>>(env, namespaces.Status, m);
        }

        var dlls = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        foreach (var sub in namespaces.SubKeys.Take(MaxWinsockEntries))
        {
            var entry = env.Hklm(WinsockRoot + @"\NameSpace_Catalog5\Catalog_Entries64\" + sub);
            if (entry.Ok() && entry.Str("LibraryPath") is { Length: > 0 } path)
            {
                AddDll(dlls, path);
            }
            else
            {
                unreadable++;
            }
        }

        return CatalogFact(run, dlls, unreadable, namespaces.SubKeys.Count > MaxWinsockEntries, m);
    }

    private static Fact<IReadOnlyList<string>> CatalogFact(RunBuilder run, SortedSet<string> dlls, int unreadable, bool capped, string method)
        => unreadable == 0 && !capped
            ? run.Known<IReadOnlyList<string>>(dlls.ToList(), method)
            : run.Partial<IReadOnlyList<string>>(dlls.ToList(), method, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"unreadable:{unreadable}{(capped ? ";capped" : string.Empty)}"));

    private static void AddDll(SortedSet<string> dlls, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var name = PathText.FileName(path.Trim()).ToLowerInvariant();
        if (name.Length > 0 && !MicrosoftWinsockDlls.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            dlls.Add(name);
        }
    }

    private static Fact<IReadOnlyList<FirewallRuleInfo>> FirewallRules(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:FirewallRules";
        var local = env.Hklm(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
        var policy = env.Hklm(@"SOFTWARE\Policies\Microsoft\WindowsFirewall\FirewallRules");
        if (!local.Ok())
        {
            return run.FromStatus<IReadOnlyList<FirewallRuleInfo>>(env, local.Status, m);
        }

        var list = new List<FirewallRuleInfo>();
        foreach (var v in local.Values.Concat(policy.Ok() ? policy.Values : []))
        {
            if (v.Data is null || !v.Data.Contains("App=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = v.Data.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Split('=', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("App", out var app))
            {
                continue;
            }

            var image = PathText.FileName(app);
            if (!SecurityCollector.IsRiotImage(image) && !RiotCollector.IsRiotPath(app))
            {
                continue;
            }

            list.Add(new FirewallRuleInfo
            {
                Name = fields.GetValueOrDefault("Name") ?? v.Name,
                ProgramImageName = image,
                Direction = string.Equals(fields.GetValueOrDefault("Dir"), "Out", StringComparison.OrdinalIgnoreCase) ? FirewallDirection.Out : FirewallDirection.In,
                Action = string.Equals(fields.GetValueOrDefault("Action"), "Block", StringComparison.OrdinalIgnoreCase) ? FirewallAction.Block : FirewallAction.Allow,
                Enabled = string.Equals(fields.GetValueOrDefault("Active"), "TRUE", StringComparison.OrdinalIgnoreCase),
                Group = fields.GetValueOrDefault("EmbedCtxt"),
            });
            if (list.Count >= 100)
            {
                break;
            }
        }

        return run.Known<IReadOnlyList<FirewallRuleInfo>>(list, m);
    }

    private static Fact<FirewallProfiles> Profiles(ScanEnvironment env, RunBuilder run)
    {
        const string m = "registry:FirewallPolicy";
        const string root = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
        var domain = env.Hklm(root + "DomainProfile");
        var priv = env.Hklm(root + "StandardProfile");
        var pub = env.Hklm(root + "PublicProfile");
        if (!priv.Ok() && !pub.Ok())
        {
            return run.FromStatus<FirewallProfiles>(env, pub.Status, m);
        }

        bool On(RegKeyData k, string policyName)
        {
            var policy = env.Hklm(@"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + policyName).Num("EnableFirewall");
            return (policy ?? k.Num("EnableFirewall") ?? 1) != 0;
        }

        return run.Known(new FirewallProfiles { DomainOn = On(domain, "DomainProfile"), PrivateOn = On(priv, "PrivateProfile"), PublicOn = On(pub, "PublicProfile") }, m);
    }

    private static Fact<WlanInfo> Wlan(ScanEnvironment env, RunBuilder run, Fact<IReadOnlyList<NetAdapter>> adapters)
    {
        const string m = "wlanapi";
        if (adapters.TryGetValue(out var list) && !list.Any(a => a.Kind == AdapterKind.WiFi))
        {
            return run.NotApplicable<WlanInfo>(m, "no_wifi_adapter");
        }

        var raw = env.Sources.System.GetWlan();
        if (raw.Status == ReadStatus.Unsupported)
        {
            return run.NotApplicable<WlanInfo>(m, "wlansvc_not_running");
        }

        if (raw.Status != ReadStatus.Ok)
        {
            return run.FromStatus<WlanInfo>(env, raw.Status, m);
        }

        var connected = raw.Interfaces.FirstOrDefault(i => i.Connected);
        if (connected is null)
        {
            return raw.Interfaces.Count == 0 ? run.NotApplicable<WlanInfo>(m, "no_wlan_interface") : run.Known(new WlanInfo { Connected = false }, m);
        }

        return run.Known(
            new WlanInfo
            {
                Connected = true,
                SignalQualityPercent = connected.SignalQuality,
                RssiDbm = connected.Rssi,
                Band = connected.FrequencyKhz switch
                {
                    >= 2_400_000 and < 2_500_000 => WifiBand.Ghz24,
                    >= 4_900_000 and < 5_925_000 => WifiBand.Ghz5,
                    >= 5_925_000 and < 7_200_000 => WifiBand.Ghz6,
                    _ => WifiBand.Unknown,
                },
                PhyType = connected.PhyType switch
                {
                    4 => "802.11a",
                    5 => "802.11b",
                    6 => "802.11g",
                    7 => "802.11n",
                    8 => "802.11ac",
                    10 => "802.11ax",
                    11 => "802.11be",
                    _ => null,
                },
                LinkSpeedMbps = connected.RxRateKbps is { } kbps ? kbps / 1000 : null,
                Ssid = connected.Ssid,
            },
            m);
    }

    /// <summary>KB region targets (<see cref="ScanRequest.ProbeTargets"/>): at most <see cref="ProbeTarget.MaxTargets"/>,
    /// labels short and safe, hosts only an IP literal (no DNS lookups of KB-supplied names). Others are
    /// skipped with an issue code; the KB is signed, this is defence in depth.</summary>
    internal static IReadOnlyList<ProbeTarget> RegionTargets(IReadOnlyList<ProbeTarget> requested, RunBuilder run)
    {
        var kept = new List<ProbeTarget>();
        foreach (var t in requested)
        {
            var label = t.Label?.Trim() ?? string.Empty;
            var host = t.Host?.Trim() ?? string.Empty;
            var labelOk = label.Length is > 0 and <= 32 && label.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
            var hostOk = host.Length is > 0 and <= 45 && System.Net.IPAddress.TryParse(host, out var ip)
                         && string.Equals(ip.ToString(), host, StringComparison.OrdinalIgnoreCase) // canonical: no "127.1", "0x7f000001", no scope id
                         && !System.Net.IPAddress.IsLoopback(ip) && !ip.Equals(System.Net.IPAddress.Any) && !ip.Equals(System.Net.IPAddress.IPv6Any)
                         && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast;
            var reserved = label is "gateway" or "public-dns";
            if (!labelOk || !hostOk || reserved || kept.Exists(k => string.Equals(k.Label, label, StringComparison.OrdinalIgnoreCase)))
            {
                run.Issue("skipped:probe_target");
                continue;
            }

            if (kept.Count == ProbeTarget.MaxTargets)
            {
                run.Issue("capped:probe_targets");
                break;
            }

            kept.Add(new ProbeTarget { Label = label, Host = host });
        }

        return kept;
    }

    private static Fact<IReadOnlyList<PingProbe>> Probes(ScanEnvironment env, RunBuilder run, Fact<IReadOnlyList<NetAdapter>> adapters, NetRaw net)
    {
        const string m = "icmp";
        if (!env.Request.AllowNetworkProbes)
        {
            return run.Unknown<IReadOnlyList<PingProbe>>(UnknownReason.NotCollected, m, "probes_disabled");
        }

        var best = net.Interfaces.FirstOrDefault(i => i.Ipv4Index > 0 && i.Ipv4Index == net.BestIpv4Index);
        var targets = new List<(string Label, ProbeTargetKind Kind, string Address)>();
        if (best?.Gateways.FirstOrDefault() is { } gw)
        {
            targets.Add(("gateway", ProbeTargetKind.Gateway, gw));
        }

        targets.Add(("public-dns", ProbeTargetKind.Dns, "1.1.1.1"));
        foreach (var region in RegionTargets(env.Request.ProbeTargets, run))
        {
            targets.Add((region.Label, ProbeTargetKind.Region, region.Host));
        }

        var tasks = targets.Select(t => Task.Run(() => (t, env.Sources.Probes.Ping(t.Address, 10, TimeSpan.FromMilliseconds(800))))).ToArray();
        Task.WaitAll(tasks, TimeSpan.FromSeconds(12));
        var list = new List<PingProbe>();
        foreach (var task in tasks.Where(t => t.IsCompletedSuccessfully))
        {
            var (target, raw) = task.Result;
            var rtt = raw.RttMs.Select(x => (double)x).ToList();
            list.Add(new PingProbe
            {
                Target = target.Label,
                Kind = target.Kind,
                Sent = raw.Sent,
                Received = rtt.Count,
                RttAvgMs = rtt.Count > 0 ? Math.Round(rtt.Average(), 1) : null,
                RttP95Ms = rtt.Count > 0 ? rtt.Order().ElementAt(Math.Min(rtt.Count - 1, (int)Math.Ceiling(rtt.Count * 0.95) - 1)) : null,
                JitterMs = rtt.Count > 1 ? Math.Round(rtt.Zip(rtt.Skip(1), (a, b) => Math.Abs(b - a)).Average(), 1) : null,
            });
        }

        _ = adapters;
        return run.Known<IReadOnlyList<PingProbe>>(list, m);
    }

    [GeneratedRegex(@"TAP-|TAP Adapter|WireGuard|Wintun|ZeroTier|Radmin|OpenVPN|Tailscale|Hamachi|ExpressVPN|NordLynx|Cloudflare WARP|SoftEther|Fortinet|GlobalProtect|PANGP|AnyConnect|ProtonVPN|Mullvad|Surfshark|ExitLag|NoPing|WTFast|Mudfish|Teredo|6to4|IP-HTTPS|isatap", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TunnelNames();

    [GeneratedRegex(@"Hyper-V|vEthernet|VMware|VirtualBox|WSL|Virtual Adapter|Npcap|Loopback|Docker|Bluestacks|Wi-Fi Direct Virtual|Microsoft Kernel Debug", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VirtualNames();
}
