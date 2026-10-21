using System.Text.RegularExpressions;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Policy;

/// <summary>One allowlisted registry location for <c>registry.setValue</c> / <c>registry.deleteValue</c>.</summary>
/// <param name="Hive">Hive.</param>
/// <param name="SubKey">Exact key path (compared ordinal ignore-case).</param>
/// <param name="Names">Allowed value names (empty: <paramref name="NamePattern"/> decides).</param>
/// <param name="NamePattern">Regex for value names when <paramref name="Names"/> is empty.</param>
/// <param name="Kinds">Allowed kinds for a set (<c>String</c>, <c>DWord</c>...).</param>
/// <param name="DataPattern">Regex the data must match for a set.</param>
/// <param name="AllowDelete">Whether <c>registry.deleteValue</c> may remove it.</param>
public sealed record RegistryAllowEntry(RegHive Hive, string SubKey, IReadOnlyList<string> Names, string? NamePattern, IReadOnlyList<string> Kinds, string DataPattern, bool AllowDelete);

/// <summary>
/// The registry roots the generic registry primitives may touch. They live in code, not in the KB (OpCatalog policy
/// notes): each entry is one exact key with the value names, kinds and data shapes a fix needs. Nothing here can lower
/// security (S5); <see cref="SecurityDenied"/> is checked first anyway as a second fence.
/// </summary>
public static class RegistryAllowlist
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Key fragments that are never written by a generic primitive (security settings, S5).</summary>
    public static IReadOnlyList<string> SecurityFragments { get; } =
    [
        @"\Policies\", @"Windows Defender", @"WindowsFirewall", @"FirewallPolicy", @"SharedAccess", @"\Lsa", @"DeviceGuard",
        @"SecureBoot", @"\CI\", @"HypervisorEnforcedCodeIntegrity", @"\TPM", @"BitLocker", @"FVE", @"SecurityProviders",
        @"Image File Execution Options", @"\Winlogon", @"\Run", @"\Services\", @"AppCompatFlags", @"SafeBoot", @"Session Manager\KnownDLLs",
        @"Session Manager\Environment", @"\Explorer\StartupApproved", @"UAC", @"SystemCertificates", @"\Cryptography",
    ];

    /// <summary>The allowlist.</summary>
    public static IReadOnlyList<RegistryAllowEntry> Entries { get; } =
    [
        // Game DVR / Game Bar capture off (fix.display.game_dvr_off).
        new(RegHive.User, @"System\GameConfigStore", ["GameDVR_Enabled", "GameDVR_FSEBehaviorMode", "GameDVR_FSEBehavior", "GameDVR_HonorUserFSEBehaviorMode", "GameDVR_DXGIHonorFSEWindowsCompatible"], null, ["DWord"], "^[0-2]$", false),
        new(RegHive.User, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", ["AppCaptureEnabled", "AudioCaptureEnabled", "HistoricalCaptureEnabled"], null, ["DWord"], "^[01]$", false),

        // Per-app GPU preference (fix.display.gpu_pref_high): value name is the executable's full path.
        new(RegHive.User, @"Software\Microsoft\DirectX\UserGpuPreferences", [], @"^[A-Za-z]:\\[^*?""<>|:]{1,1000}\.exe$", ["String"], "^GpuPreference=[0-2];$", true),

        // Hardware-accelerated GPU scheduling (fix.display.hags_off): 1 = off, 2 = on.
        new(RegHive.Machine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ["HwSchMode"], null, ["DWord"], "^[12]$", false),

        // Multiplane overlay test mode (fix.display.mpo_off); deleting it restores the default.
        new(RegHive.Machine, @"SOFTWARE\Microsoft\Windows\Dwm", ["OverlayTestMode"], null, ["DWord"], "^5$", true),

        // Enhance pointer precision (fix.input.mouse_accel_off).
        new(RegHive.User, @"Control Panel\Mouse", ["MouseSpeed", "MouseThreshold1", "MouseThreshold2"], null, ["String"], "^[0-9]{1,2}$", false),

        // Fast Startup (fix.os.fast_startup_off).
        new(RegHive.Machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", ["HiberbootEnabled"], null, ["DWord"], "^[01]$", false),
    ];

    /// <summary>True when the key path touches a security area (checked before the allowlist).</summary>
    /// <param name="subKey">Key path.</param>
    public static bool SecurityDenied(string subKey)
    {
        var padded = "\\" + subKey + "\\";
        return SecurityFragments.Any(f => padded.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds the entry that allows a value, or null.</summary>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path.</param>
    /// <param name="name">Value name.</param>
    public static RegistryAllowEntry? Find(RegHive hive, string subKey, string name)
    {
        foreach (var e in Entries)
        {
            if (e.Hive != hive || !string.Equals(e.SubKey, subKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (e.Names.Count > 0 ? e.Names.Contains(name, StringComparer.OrdinalIgnoreCase) : e.NamePattern is { } p && FullMatch(p, name))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>Whole-string regex match with a timeout.</summary>
    /// <param name="pattern">Pattern.</param>
    /// <param name="value">Value.</param>
    public static bool FullMatch(string pattern, string value)
    {
        var m = Regex.Match(value, pattern, RegexOptions.CultureInvariant, RegexTimeout);
        return m.Success && m.Index == 0 && m.Length == value.Length;
    }
}
