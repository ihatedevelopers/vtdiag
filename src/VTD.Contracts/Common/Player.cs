namespace VTD.Contracts;

/// <summary>
/// The interactive player, established by the ENGINE from the pipe client's token (never a client claim). The elevated
/// engine may run under a different account (standard user + separate admin), so every per-user read (Riot Client logs,
/// ShooterGame.log, HKCU uninstall keys, AppCompat layers, Startup folder, <c>%PlayerLocalAppData%</c>) goes through this,
/// never through the engine's own profile or <c>HKEY_CURRENT_USER</c>.
/// </summary>
[Sensitivity(Sensitivity.Path)]
public sealed record PlayerContext
{
    /// <summary>Player SID (registry access via <c>HKU\&lt;Sid&gt;</c>).</summary>
    [Sensitivity(Sensitivity.Identifier)]
    public required string Sid { get; init; }

    /// <summary>Profile folder (<c>C:\Users\&lt;name&gt;</c>).</summary>
    public required string ProfilePath { get; init; }

    /// <summary>Local AppData folder.</summary>
    public required string LocalAppData { get; init; }

    /// <summary>Roaming AppData folder.</summary>
    public required string RoamingAppData { get; init; }

    /// <summary>The player is a member of Administrators.</summary>
    public required bool IsAdmin { get; init; }
}

/// <summary>
/// Formats <see cref="EntityRef.Id"/> values so every component builds the same id for the same thing.
/// </summary>
public static class EntityIds
{
    /// <summary>App id: <c>HKLM:&lt;key&gt;</c>, <c>HKLM32:&lt;key&gt;</c> or <c>HKCU:&lt;key&gt;</c>.</summary>
    /// <param name="hive">Uninstall hive.</param>
    /// <param name="keyName">Uninstall subkey name.</param>
    public static string App(Snapshots.AppHive hive, string keyName) => hive switch
    {
        Snapshots.AppHive.Hklm => "HKLM:" + keyName,
        Snapshots.AppHive.Hklm32 => "HKLM32:" + keyName,
        _ => "HKCU:" + keyName,
    };

    /// <summary>Registry value id: <c>&lt;HKLM|HKCU&gt;\&lt;path&gt;|&lt;valueName&gt;</c> (HKCU = the player's hive).</summary>
    /// <param name="hive"><c>HKLM</c> or <c>HKCU</c>.</param>
    /// <param name="path">Key path without hive.</param>
    /// <param name="valueName">Value name (empty for the default value).</param>
    public static string RegistryValue(string hive, string path, string valueName) => hive + "\\" + path + "|" + valueName;

    /// <summary>Startup item id: <c>&lt;location&gt;|&lt;name&gt;</c>.</summary>
    /// <param name="location">Location.</param>
    /// <param name="name">Entry name.</param>
    public static string StartupItem(Snapshots.StartupLocation location, string name) => location.ToString() + "|" + name;

    /// <summary>Power setting id: <c>&lt;subgroup guid&gt;/&lt;setting guid&gt;</c> (lowercase).</summary>
    /// <param name="subgroup">Subgroup GUID.</param>
    /// <param name="setting">Setting GUID.</param>
    public static string PowerSetting(Guid subgroup, Guid setting)
        => subgroup.ToString("D", System.Globalization.CultureInfo.InvariantCulture) + "/" + setting.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
