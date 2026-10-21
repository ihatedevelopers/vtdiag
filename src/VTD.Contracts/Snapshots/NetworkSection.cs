namespace VTD.Contracts.Snapshots;

/// <summary>
/// Network state (check ids: realtek_nic, multi_nic, nic_power, wifi, virtual_nic_vpn, dns_ipv6, firewall_rules,
/// ping_route, tcp_stack). Never parses localized <c>netsh</c> text.
/// </summary>
public sealed record NetworkState
{
    /// <summary>Network adapters (GetAdaptersAddresses + PnP + adapter Class key).</summary>
    public required Fact<IReadOnlyList<NetAdapter>> Adapters { get; init; }

    /// <summary>Connectivity from the Network List Manager.</summary>
    public required Fact<ConnectivityInfo> Connectivity { get; init; }

    /// <summary>Proxy configuration (host names are not collected).</summary>
    public required Fact<ProxyInfo> Proxy { get; init; }

    /// <summary>IPv6 configuration.</summary>
    public required Fact<Ipv6Info> Ipv6 { get; init; }

    /// <summary>Non-Microsoft Winsock protocol catalog providers (LSP DLL file names; the <c>Protocol_Catalog9</c> catalog
    /// that <c>WSCEnumProtocols</c> walks).</summary>
    [Sensitivity(Sensitivity.None)]
    public required Fact<IReadOnlyList<string>> WinsockProviders { get; init; }

    /// <summary>Non-Microsoft Winsock namespace providers (DLL file names; the <c>NameSpace_Catalog5</c> catalog that
    /// <c>WSCEnumNameSpaceProviders</c> walks), for example Bonjour's <c>mdnsNSP.dll</c>. Namespace providers are NOT in
    /// the protocol (LSP) catalog above.</summary>
    [Sensitivity(Sensitivity.None)]
    public required Fact<IReadOnlyList<string>> WinsockNamespaceProviders { get; init; }

    /// <summary>Windows Firewall rules that mention Riot or Vanguard executables.</summary>
    public required Fact<IReadOnlyList<FirewallRuleInfo>> RiotFirewallRules { get; init; }

    /// <summary>Windows Firewall profile state.</summary>
    public required Fact<FirewallProfiles> Firewall { get; init; }

    /// <summary>Wi-Fi link (WLAN API). NotApplicable when there is no Wi-Fi adapter.</summary>
    public required Fact<WlanInfo> Wlan { get; init; }

    /// <summary>Indicative ICMP/TCP probes to the KB's region endpoints and the gateway (optional, network collector).</summary>
    public required Fact<IReadOnlyList<PingProbe>> Probes { get; init; }

    /// <summary>All facts Unknown with one reason.</summary>
    /// <param name="reason">The reason.</param>
    public static NetworkState Unknown(UnknownReason reason) => new()
    {
        Adapters = Fact<IReadOnlyList<NetAdapter>>.Unknown(reason),
        Connectivity = Fact<ConnectivityInfo>.Unknown(reason),
        Proxy = Fact<ProxyInfo>.Unknown(reason),
        Ipv6 = Fact<Ipv6Info>.Unknown(reason),
        WinsockProviders = Fact<IReadOnlyList<string>>.Unknown(reason),
        WinsockNamespaceProviders = Fact<IReadOnlyList<string>>.Unknown(reason),
        RiotFirewallRules = Fact<IReadOnlyList<FirewallRuleInfo>>.Unknown(reason),
        Firewall = Fact<FirewallProfiles>.Unknown(reason),
        Wlan = Fact<WlanInfo>.Unknown(reason),
        Probes = Fact<IReadOnlyList<PingProbe>>.Unknown(reason),
    };
}

/// <summary>Adapter kind.</summary>
public enum AdapterKind
{
    /// <summary>Other.</summary>
    Other = 0,

    /// <summary>Wired Ethernet.</summary>
    Ethernet = 1,

    /// <summary>Wi-Fi.</summary>
    WiFi = 2,

    /// <summary>Virtual switch or virtual NIC (Hyper-V, WSL, VirtualBox, VMware).</summary>
    Virtual = 3,

    /// <summary>VPN or tunnel (WireGuard, OpenVPN TAP, ZeroTier, Radmin...).</summary>
    Tunnel = 4,

    /// <summary>Bluetooth PAN.</summary>
    Bluetooth = 5,

    /// <summary>Loopback.</summary>
    Loopback = 6,

    /// <summary>Mobile broadband.</summary>
    Cellular = 7,
}

/// <summary>Adapter operational status (IF_OPER_STATUS).</summary>
public enum AdapterOperStatus
{
    /// <summary>Unknown.</summary>
    Unknown = 0,

    /// <summary>Up.</summary>
    Up = 1,

    /// <summary>Down.</summary>
    Down = 2,

    /// <summary>Testing.</summary>
    Testing = 3,

    /// <summary>Dormant.</summary>
    Dormant = 4,

    /// <summary>Not present.</summary>
    NotPresent = 5,

    /// <summary>Lower layer down (cable unplugged).</summary>
    LowerLayerDown = 6,
}

/// <summary>A network adapter (design_B.md section 4.1). Its <see cref="InstanceId"/> binds fixes such as "disable unused adapter".</summary>
[Sensitivity(Sensitivity.None)]
public sealed record NetAdapter
{
    /// <summary>PnP instance id (stable; the binding key).</summary>
    [Sensitivity(Sensitivity.DeviceId)]
    public required string InstanceId { get; init; }

    /// <summary>Interface GUID.</summary>
    [Sensitivity(Sensitivity.Identifier)]
    public string? InterfaceGuid { get; init; }

    /// <summary>Driver-provided description, for example <c>Realtek Gaming 2.5GbE Family Controller</c>.</summary>
    public required string Description { get; init; }

    /// <summary>Connection name chosen by Windows or the user (for example <c>Ethernet 2</c>); free text.</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public string? Alias { get; init; }

    /// <summary>Kind.</summary>
    public required AdapterKind Kind { get; init; }

    /// <summary>Physical hardware (not virtual, not a tunnel).</summary>
    public required bool Physical { get; init; }

    /// <summary>Device enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Operational status.</summary>
    public required AdapterOperStatus Status { get; init; }

    /// <summary>Link speed (bits/s).</summary>
    public long? LinkSpeedBps { get; init; }

    /// <summary>Carries the IPv4 default route with the lowest metric (the active connection; a protected target, S4).</summary>
    public required bool IsDefaultRoute { get; init; }

    /// <summary>This interface has internet connectivity (NLM).</summary>
    public bool? HasInternet { get; init; }

    /// <summary>IPv4 interface metric.</summary>
    public int? Ipv4Metric { get; init; }

    /// <summary>Automatic metric.</summary>
    public bool? AutomaticMetric { get; init; }

    /// <summary>IPv6 bound to the adapter.</summary>
    public bool? Ipv6Bound { get; init; }

    /// <summary>Driver file, for example <c>rt25cx21x64.sys</c>.</summary>
    public string? DriverFile { get; init; }

    /// <summary>Driver version.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Driver date.</summary>
    public DateOnly? DriverDate { get; init; }

    /// <summary>Driver provider.</summary>
    public string? DriverProvider { get; init; }

    /// <summary>Advanced properties by registry keyword (<c>*EEE</c>, <c>*GreenEthernet</c>, <c>*InterruptModeration</c>,
    /// <c>AdvancedEEE</c>...) with their raw values. Keywords are invariant.</summary>
    public IReadOnlyDictionary<string, string> AdvancedProperties { get; init; } = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>"Allow the computer to turn off this device to save power" (<c>PnPCapabilities</c>).</summary>
    public bool? AllowPowerOff { get; init; }

    /// <summary>MAC address.</summary>
    [Sensitivity(Sensitivity.MacAddress)]
    public string? Mac { get; init; }

    /// <summary>IPv4 addresses.</summary>
    [Sensitivity(Sensitivity.IpAddress)]
    public IReadOnlyList<string> Ipv4Addresses { get; init; } = [];

    /// <summary>DNS servers.</summary>
    [Sensitivity(Sensitivity.IpAddress)]
    public IReadOnlyList<string> DnsServers { get; init; } = [];
}

/// <summary>Connectivity from the Network List Manager.</summary>
public sealed record ConnectivityInfo
{
    /// <summary>IPv4 internet.</summary>
    public required bool Ipv4Internet { get; init; }

    /// <summary>IPv6 internet.</summary>
    public bool? Ipv6Internet { get; init; }

    /// <summary>Captive portal or local-only network detected.</summary>
    public bool? LocalOnly { get; init; }
}

/// <summary>Proxy configuration flags (no host names).</summary>
public sealed record ProxyInfo
{
    /// <summary>WinHTTP machine proxy set.</summary>
    public required bool WinHttpProxy { get; init; }

    /// <summary>WinINet (per-user) proxy enabled.</summary>
    public required bool UserProxyEnabled { get; init; }

    /// <summary>A PAC / auto-config URL is set.</summary>
    public required bool AutoConfigUrl { get; init; }
}

/// <summary>IPv6 configuration.</summary>
public sealed record Ipv6Info
{
    /// <summary><c>Tcpip6\Parameters\DisabledComponents</c>.</summary>
    public int? DisabledComponents { get; init; }

    /// <summary>Teredo enabled.</summary>
    public bool? TeredoEnabled { get; init; }
}

/// <summary>Firewall rule direction.</summary>
public enum FirewallDirection
{
    /// <summary>Inbound.</summary>
    In = 0,

    /// <summary>Outbound.</summary>
    Out = 1,
}

/// <summary>Firewall rule action.</summary>
public enum FirewallAction
{
    /// <summary>Allow.</summary>
    Allow = 0,

    /// <summary>Block.</summary>
    Block = 1,
}

/// <summary>A firewall rule that targets a Riot or Vanguard executable.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record FirewallRuleInfo
{
    /// <summary>Rule name (may embed a user path; free text).</summary>
    [Sensitivity(Sensitivity.FreeText)]
    public required string Name { get; init; }

    /// <summary>Program image name, for example <c>VALORANT-Win64-Shipping.exe</c>.</summary>
    public required string ProgramImageName { get; init; }

    /// <summary>Direction.</summary>
    public required FirewallDirection Direction { get; init; }

    /// <summary>Action.</summary>
    public required FirewallAction Action { get; init; }

    /// <summary>Enabled.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Rule group (<c>VTDiag</c> for rules VTDiag created).</summary>
    public string? Group { get; init; }
}

/// <summary>Firewall profile state.</summary>
public sealed record FirewallProfiles
{
    /// <summary>Domain profile on.</summary>
    public required bool DomainOn { get; init; }

    /// <summary>Private profile on.</summary>
    public required bool PrivateOn { get; init; }

    /// <summary>Public profile on.</summary>
    public required bool PublicOn { get; init; }
}

/// <summary>Wi-Fi band.</summary>
public enum WifiBand
{
    /// <summary>Unknown.</summary>
    Unknown = 0,

    /// <summary>2.4 GHz.</summary>
    Ghz24 = 1,

    /// <summary>5 GHz.</summary>
    Ghz5 = 2,

    /// <summary>6 GHz.</summary>
    Ghz6 = 3,
}

/// <summary>Wi-Fi link quality.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record WlanInfo
{
    /// <summary>Connected.</summary>
    public required bool Connected { get; init; }

    /// <summary>Signal quality 0-100.</summary>
    public int? SignalQualityPercent { get; init; }

    /// <summary>RSSI (dBm).</summary>
    public int? RssiDbm { get; init; }

    /// <summary>Band.</summary>
    public WifiBand Band { get; init; }

    /// <summary>PHY type, for example <c>802.11ax</c>.</summary>
    public string? PhyType { get; init; }

    /// <summary>Link speed (Mbps).</summary>
    public int? LinkSpeedMbps { get; init; }

    /// <summary>SSID (hashed at share level).</summary>
    [Sensitivity(Sensitivity.Hashed)]
    public string? Ssid { get; init; }
}

/// <summary>What a probe targeted.</summary>
public enum ProbeTargetKind
{
    /// <summary>The default gateway (router).</summary>
    Gateway = 0,

    /// <summary>A KB region endpoint (indicative only: game servers move).</summary>
    Region = 1,

    /// <summary>A public DNS resolver.</summary>
    Dns = 2,
}

/// <summary>An indicative latency probe. Labelled approximate in the UI.</summary>
[Sensitivity(Sensitivity.None)]
public sealed record PingProbe
{
    /// <summary>Target label from the KB (for example <c>eu-central</c>) or <c>gateway</c>.</summary>
    public required string Target { get; init; }

    /// <summary>Target kind.</summary>
    public required ProbeTargetKind Kind { get; init; }

    /// <summary>Packets sent.</summary>
    public required int Sent { get; init; }

    /// <summary>Replies received.</summary>
    public required int Received { get; init; }

    /// <summary>Average RTT (ms).</summary>
    public double? RttAvgMs { get; init; }

    /// <summary>95th percentile RTT (ms).</summary>
    public double? RttP95Ms { get; init; }

    /// <summary>Jitter (ms, RFC 3550).</summary>
    public double? JitterMs { get; init; }
}
