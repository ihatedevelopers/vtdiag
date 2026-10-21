using VTD.Contracts.Fixes;
using VTD.Contracts.Scanning;
using VTD.Fixes.Ops;
using VTD.Fixes.Policy;
using VTD.Fixes.Ports;

namespace VTD.Fixes;

/// <summary>
/// The audited primitives: exactly the <see cref="OpCatalog"/> entries (a test asserts the metadata matches). Nothing else
/// can be registered; the KB and plans only compose these.
/// </summary>
public sealed class OperationRegistry : IOperationRegistry
{
    private readonly Dictionary<string, OperationBase> _ops;

    /// <summary>Creates the registry over the given ports.</summary>
    /// <param name="ports">System ports (real in the engine, fakes in tests).</param>
    /// <param name="session">Session context.</param>
    /// <param name="clock">Clock.</param>
    /// <param name="factProbe">Live probes.</param>
    public OperationRegistry(SystemPorts ports, IFixSessionContext session, TimeProvider clock, IFactProbe factProbe)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(factProbe);
        Policy = new ProtectedTargetPolicy(ports, session);
        var ctx = new OperationContext(ports, Policy, session, clock, factProbe);
        OperationBase[] all =
        [
            new ServiceSetStartTypeOp(ctx), new ServiceStopOp(ctx), new ServiceStartOp(ctx),
            new DeviceDisableOp(ctx), new DeviceEnableOp(ctx),
            new NetAdapterSetAdvancedPropertyOp(ctx), new NetAdapterSetMetricOp(ctx), new NetAdapterUnbindIpv6Op(ctx),
            new RegistrySetValueOp(ctx), new RegistryDeleteValueOp(ctx),
            new PowerCfgSetValueOp(ctx), new PowerCfgSetActiveSchemeOp(ctx),
            new AppCompatSetLayersOp(ctx), new AppCompatRemoveLayersOp(ctx),
            new IfeoResetMitigationsOp(ctx), new IfeoRemovePerfOptionsOp(ctx),
            new FileQuarantineOp(ctx),
            new FirewallAddRuleOp(ctx), new FirewallRemoveRuleOp(ctx),
            new DefenderAddExclusionOp(ctx),
            new BcdSetValueOp(ctx),
            new TimeResyncOp(ctx), new DnsFlushOp(ctx),
            new ProcessCloseGracefulOp(ctx),
            new StartupDisableOp(ctx),
            new SystemProtectionEnableOp(ctx),
            new WinsockResetOp(ctx), new IpResetOp(ctx), new SfcDismOp(ctx),
        ];
        _ops = all.ToDictionary(o => o.Op, StringComparer.Ordinal);
        var missing = OpCatalog.All.Select(s => s.Op).Where(id => !_ops.ContainsKey(id)).ToList();
        var extra = _ops.Keys.Where(id => OpCatalog.Find(id) is null).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            throw new InvalidOperationException("The primitive registry does not match OpCatalog: missing [" + string.Join(", ", missing) + "], extra [" + string.Join(", ", extra) + "].");
        }
    }

    /// <summary>The policy the primitives use.</summary>
    public ProtectedTargetPolicy Policy { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<IOperation> All => _ops.Values;

    /// <inheritdoc />
    public IOperation? Find(string op) => op is not null && _ops.TryGetValue(op, out var o) ? o : null;

    /// <summary>Finds a primitive with its extended surface (controlled keys, preconditions).</summary>
    /// <param name="op">Primitive id.</param>
    public OperationBase? FindBase(string op) => op is not null && _ops.TryGetValue(op, out var o) ? o : null;
}
