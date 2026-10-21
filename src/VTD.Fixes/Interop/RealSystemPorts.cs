using VTD.Fixes.Ports;

namespace VTD.Fixes.Interop;

/// <summary>Builds the real Win32 ports for the elevated engine (A4 wires this; tests use fakes or sandboxed ports).</summary>
public static class RealSystemPorts
{
    /// <summary>Creates the production port set.</summary>
    /// <param name="session">Session context (the player for HKCU).</param>
    /// <param name="dataRoot">VTDiag data root, normally <c>%ProgramData%\VTDiag</c>.</param>
    /// <param name="clock">Clock.</param>
    public static SystemPorts Create(IFixSessionContext session, string dataRoot, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);
        var registry = new Win32RegistryPort(() => session.Player);
        var devices = new CfgMgrDevicePort();
        return new SystemPorts
        {
            Services = new Win32ServicePort(clock),
            Devices = devices,
            Network = new NetworkPort(devices, registry),
            Registry = registry,
            Power = new PowrProfPowerPort(),
            Firewall = new ComFirewallPort(),
            Defender = new MpPreferenceDefenderPort(),
            Bcd = new WmiBcdPort(),
            Processes = new ToolHelpProcessPort(),
            Files = new ReparseSafeFilePort(ReparseSafeFilePort.SystemFolders(), dataRoot, adminOnly: true),
            Tools = new SystemToolRunner(clock),
            SystemProtection = new SystemRestorePort(registry),
            Boot = new TickBootClock(clock),
        };
    }

    /// <summary>The default data root: <c>%ProgramData%\VTDiag</c>.</summary>
    public static string DefaultDataRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VTDiag");
}
