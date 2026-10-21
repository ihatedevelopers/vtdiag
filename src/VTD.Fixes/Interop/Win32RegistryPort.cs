using System.Globalization;
using System.ComponentModel;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using VTD.Contracts;
using VTD.Fixes.Ports;
using static VTD.Fixes.Interop.NativeMethods;

namespace VTD.Fixes.Interop;

/// <summary>
/// Registry access that never follows a registry symbolic link: every key component is opened relative to its parent's
/// handle with <c>REG_OPTION_OPEN_LINK</c> and refused when it is a link, so the final handle is the real key (no
/// check-then-use gap). <see cref="RegHive.User"/> is <c>HKU\&lt;player SID&gt;</c> from the session, never the engine's own
/// HKCU. Values are read and written as raw type + bytes.
/// </summary>
public sealed class Win32RegistryPort : IRegistryPort
{
    private readonly Func<PlayerContext?> _player;
    private readonly string? _sandboxRoot;

    /// <summary>Creates the production port.</summary>
    /// <param name="player">The current player (from the session).</param>
    public Win32RegistryPort(Func<PlayerContext?> player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    private Win32RegistryPort(string sandboxRoot)
    {
        _player = () => null;
        _sandboxRoot = sandboxRoot;
    }

    /// <summary>
    /// A test port that maps BOTH hives under <c>HKCU\&lt;sandboxRoot&gt;\HKLM</c> and <c>…\HKU</c> of the current user, so the
    /// real code path (no-follow opens, raw values) is exercised without touching anything outside the sandbox key.
    /// </summary>
    /// <param name="sandboxRoot">Key under HKCU, for example <c>Software\VTDiag.Tests\&lt;guid&gt;</c>.</param>
    public static Win32RegistryPort ForSandbox(string sandboxRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxRoot);
        if (!sandboxRoot.StartsWith(@"Software\VTDiag.Tests\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(@"A sandbox root must live under HKCU\Software\VTDiag.Tests\.", nameof(sandboxRoot));
        }

        return new Win32RegistryPort(sandboxRoot);
    }

    /// <inheritdoc />
    public RegValue? GetValue(RegHive hive, string subKey, string name)
    {
        using var key = OpenPath(hive, subKey, KeyQueryValue, create: false);
        if (key is null)
        {
            return null;
        }

        var size = 0;
        var rc = RegQueryValueEx(key, name, IntPtr.Zero, out var type, null, ref size);
        if (rc == ErrorFileNotFound)
        {
            return null;
        }

        if (rc != ErrorSuccess && rc != ErrorMoreData)
        {
            throw new Win32Exception(rc);
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var data = new byte[size];
            rc = RegQueryValueEx(key, name, IntPtr.Zero, out type, data, ref size);
            if (rc == ErrorSuccess)
            {
                return new RegValue(type, size == data.Length ? data : data[..size]);
            }

            if (rc == ErrorFileNotFound)
            {
                return null;
            }

            if (rc != ErrorMoreData)
            {
                throw new Win32Exception(rc);
            }
        }

        throw new IOException("The registry value kept changing size while it was read.");
    }

    /// <inheritdoc />
    public bool KeyExists(RegHive hive, string subKey)
    {
        using var key = OpenPath(hive, subKey, KeyQueryValue, create: false);
        return key is not null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, string subKey)
    {
        using var handle = OpenPath(hive, subKey, KeyEnumerateSubKeys | KeyQueryValue, create: false);
        if (handle is null)
        {
            return [];
        }

        using var key = RegistryKey.FromHandle(handle, RegistryView.Registry64);
        return key.GetSubKeyNames();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetValueNames(RegHive hive, string subKey)
    {
        using var handle = OpenPath(hive, subKey, KeyQueryValue, create: false);
        if (handle is null)
        {
            return [];
        }

        using var key = RegistryKey.FromHandle(handle, RegistryView.Registry64);
        return key.GetValueNames();
    }

    /// <inheritdoc />
    public void SetValue(RegHive hive, string subKey, string name, RegValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Type == RegLink)
        {
            throw new PortRefusedException("registry.link");
        }

        using var key = OpenPath(hive, subKey, KeySetValue | KeyQueryValue, create: true)!;
        var rc = RegSetValueEx(key, name, 0, value.Type, value.Data, value.Data.Length);
        if (rc != ErrorSuccess)
        {
            throw new Win32Exception(rc);
        }
    }

    /// <inheritdoc />
    public bool DeleteValue(RegHive hive, string subKey, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var key = OpenPath(hive, subKey, KeySetValue | KeyQueryValue, create: false);
        if (key is null)
        {
            return false;
        }

        var rc = RegDeleteValue(key, name);
        if (rc == ErrorFileNotFound)
        {
            return false;
        }

        return rc == ErrorSuccess ? true : throw new Win32Exception(rc);
    }

    /// <summary>
    /// Opens <paramref name="subKey"/> component by component without following links. Missing components are created
    /// when <paramref name="create"/> (a component that already existed is reopened with OPEN_LINK and checked).
    /// Returns null when a component is missing and <paramref name="create"/> is false.
    /// </summary>
    private SafeRegistryHandle? OpenPath(RegHive hive, string subKey, int access, bool create)
    {
        ArgumentNullException.ThrowIfNull(subKey);
        if (!Policy.RegistryPaths.IsSubKey(subKey))
        {
            throw new ArgumentException("Invalid registry key path.", nameof(subKey));
        }

        subKey = ResolveCurrentControlSet(hive, subKey);
        var (root, prefix) = Base(hive);
        var parts = (prefix is null ? subKey : prefix + "\\" + subKey).Split('\\');
        var current = root;
        var ownsCurrent = false;
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                var last = i == parts.Length - 1;
                var want = (last ? access : KeyQueryValue | KeyEnumerateSubKeys | (create ? KeyCreateSubKey : 0)) | KeyWow64_64Key;
                var rc = RegOpenKeyEx(current, parts[i], RegOptionOpenLink, want | KeyQueryValue, out var next);
                if (rc == ErrorAccessDenied && !last && create)
                {
                    // No create right on an existing ancestor: fine as long as nothing below it has to be created.
                    next.Dispose();
                    want = KeyQueryValue | KeyEnumerateSubKeys | KeyWow64_64Key;
                    rc = RegOpenKeyEx(current, parts[i], RegOptionOpenLink, want, out next);
                }
                if (rc == ErrorFileNotFound)
                {
                    next.Dispose();
                    if (!create)
                    {
                        return null;
                    }

                    rc = RegCreateKeyEx(current, parts[i], 0, null, RegOptionNonVolatile, want | KeyQueryValue, IntPtr.Zero, out next, out var disposition);
                    if (rc != ErrorSuccess)
                    {
                        next.Dispose();
                        throw new Win32Exception(rc);
                    }

                    if (disposition != RegCreatedNewKey)
                    {
                        // Someone created it between our open and create: the create may have followed a link. Reopen safely.
                        next.Dispose();
                        rc = RegOpenKeyEx(current, parts[i], RegOptionOpenLink, want | KeyQueryValue, out next);
                        if (rc != ErrorSuccess)
                        {
                            next.Dispose();
                            throw new Win32Exception(rc);
                        }
                    }
                }
                else if (rc != ErrorSuccess)
                {
                    next.Dispose();
                    throw new Win32Exception(rc);
                }

                if (IsLink(next))
                {
                    next.Dispose();
                    throw new PortRefusedException("registry.link");
                }

                if (ownsCurrent)
                {
                    current.Dispose();
                }

                current = next;
                ownsCurrent = true;
            }

            ownsCurrent = false;
            return current;
        }
        finally
        {
            if (ownsCurrent)
            {
                current.Dispose();
            }

            root.Dispose();
        }
    }

    private const string CurrentControlSet = @"SYSTEM\CurrentControlSet";

    /// <summary>
    /// HKLM\SYSTEM\CurrentControlSet is the one OS-managed registry link: resolve it to the real ControlSetNNN (from
    /// SYSTEM\Select\Current) so the no-links rule can stay absolute for every other component.
    /// </summary>
    private string ResolveCurrentControlSet(RegHive hive, string subKey)
    {
        if (_sandboxRoot is not null || hive != RegHive.Machine
            || !(subKey.Equals(CurrentControlSet, StringComparison.OrdinalIgnoreCase) || subKey.StartsWith(CurrentControlSet + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            return subKey;
        }

        using var select = OpenPathCore(hive, @"SYSTEM\Select");
        if (select is null)
        {
            return subKey;
        }

        var size = 4;
        var buffer = new byte[4];
        if (RegQueryValueEx(select, "Current", IntPtr.Zero, out var type, buffer, ref size) != ErrorSuccess || type != 4 || size != 4)
        {
            return subKey;
        }

        var n = BitConverter.ToInt32(buffer, 0);
        return n is < 1 or > 999 ? subKey : string.Create(CultureInfo.InvariantCulture, $"SYSTEM\\ControlSet{n:D3}") + subKey[CurrentControlSet.Length..];
    }

    private SafeRegistryHandle? OpenPathCore(RegHive hive, string subKey) => OpenPath(hive, subKey, KeyQueryValue, create: false);

    private static bool IsLink(SafeRegistryHandle key)
    {
        var size = 0;
        var rc = RegQueryValueEx(key, "SymbolicLinkValue", IntPtr.Zero, out var type, null, ref size);
        return (rc == ErrorSuccess || rc == ErrorMoreData) && type == RegLink;
    }

    private (SafeRegistryHandle Root, string? Prefix) Base(RegHive hive)
    {
        if (_sandboxRoot is not null)
        {
            return (Predefined(0x80000001), _sandboxRoot + (hive == RegHive.Machine ? "\\HKLM" : "\\HKU")); // HKEY_CURRENT_USER
        }

        if (hive == RegHive.Machine)
        {
            return (Predefined(0x80000002), null); // HKEY_LOCAL_MACHINE
        }

        var player = _player() ?? throw new PortRefusedException("player_unknown");
        if (!System.Text.RegularExpressions.Regex.IsMatch(player.Sid, @"^S-1-5-21(-[0-9]{1,10}){4}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            throw new PortRefusedException("player_sid_invalid");
        }

        return (Predefined(0x80000003), player.Sid); // HKEY_USERS
    }

    /// <summary>A predefined HKEY_* pseudo handle (sign-extended on x64; never closed).</summary>
    private static SafeRegistryHandle Predefined(uint hkey) => new(new IntPtr(unchecked((int)hkey)), ownsHandle: false);
}