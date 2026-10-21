using System.Globalization;
using System.Security;
using Microsoft.Win32;
using VTD.Platform.Sources;

namespace VTD.Platform.Registry;

/// <summary>Live registry reads. Keys are only ever opened with <c>OpenSubKey(string)</c> (read access).</summary>
public sealed class RegistrySource : IRegistrySource
{
    private const int MaxValues = 4096;

    /// <inheritdoc />
    public RegKeyData ReadKey(RegHive hive, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == RegHive.Users ? RegistryHive.Users : RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = path.Length == 0 ? root.OpenSubKey(string.Empty) : root.OpenSubKey(path);
            if (key is null)
            {
                return new RegKeyData { Status = ReadStatus.NotFound };
            }

            var values = new List<RegValue>();
            foreach (var name in key.GetValueNames().Take(MaxValues))
            {
                values.Add(ReadValue(key, name));
            }

            string[] subKeys;
            try
            {
                subKeys = key.GetSubKeyNames();
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
                // A key whose subkeys cannot be listed is reported as unreadable: an empty list would read as "none".
                return new RegKeyData { Status = ReadStatus.AccessDenied, Values = values, Error = "subkeys_" + e.GetType().Name };
            }

            return new RegKeyData { Status = ReadStatus.Ok, Values = values, SubKeys = subKeys };
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            return new RegKeyData { Status = ReadStatus.AccessDenied, Error = e.GetType().Name };
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return new RegKeyData { Status = ReadStatus.Failed, Error = e.GetType().Name };
        }
    }

    private static RegValue ReadValue(RegistryKey key, string name)
    {
        try
        {
            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new RegValue { Name = name, Kind = kind.ToString(), Data = Format(raw) };
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new RegValue { Name = name, Kind = "None", Data = null };
        }
    }

    internal static string? Format(object? raw) => raw switch
    {
        null => null,
        string s => s,
        string[] m => string.Join('\n', m),
        int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
        long l => unchecked((ulong)l).ToString(CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexStringLower(b),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
    };
}
