namespace VTD.Monitor;

/// <summary>
/// M0 placeholder for VTDiag.Monitor.exe (A5, M3): low-priority (BelowNormal + EcoQoS) child of the engine that
/// serves <c>IMonitorService</c> over a pipe. Empty in M0 by DESIGN.md section 5.
/// </summary>
public static class Program
{
    /// <summary>Entry point. Placeholder: exits with 0.</summary>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return 0;
    }
}
