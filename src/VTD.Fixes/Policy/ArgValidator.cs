using System.Globalization;
using System.Text.RegularExpressions;
using VTD.Contracts.Fixes;
using VTD.Fixes.Ports;

namespace VTD.Fixes.Policy;

/// <summary>
/// Validates resolved primitive arguments against the primitive's <see cref="OpArgSpec"/> list (types, enums, patterns)
/// before the policy or any port sees them. Values are invariant strings; anything unexpected is rejected.
/// </summary>
public static class ArgValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Path tokens the KB uses in entity ids (expanded by A3 from OS folders, never from the environment).</summary>
    public static IReadOnlyList<string> PathTokens { get; } = ["%SystemRoot%", "%WINDIR%", "%ProgramFiles%", "%ProgramFiles(x86)%", "%ProgramData%"];

    /// <summary>Validates <paramref name="values"/>; returns null when valid, otherwise a machine code.</summary>
    /// <param name="specs">Argument specs.</param>
    /// <param name="values">Values.</param>
    /// <param name="partial">Probe/verify use: required arguments may be missing (only the ones present are checked).</param>
    public static string? Validate(IReadOnlyList<OpArgSpec> specs, IReadOnlyDictionary<string, string> values, bool partial = false)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var name in values.Keys)
        {
            if (!specs.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
            {
                return "args.unknown:" + name;
            }
        }

        foreach (var spec in specs)
        {
            if (!values.TryGetValue(spec.Name, out var value))
            {
                if (spec.Required && !partial)
                {
                    return "args.missing:" + spec.Name;
                }

                continue;
            }

            if (Check(spec, value) is { } problem)
            {
                return problem + ":" + spec.Name;
            }
        }

        return null;
    }

    private static string? Check(OpArgSpec spec, string value)
    {
        if (value is null || value.Length > 2048 || value.Any(char.IsControl))
        {
            return "args.invalid";
        }

        var ok = spec.Type switch
        {
            OpArgType.String => true,
            OpArgType.Int => IsCanonicalInt(value),
            OpArgType.Bool => value is "true" or "false",
            OpArgType.Enum => spec.AllowedValues.Contains(value, StringComparer.Ordinal),
            OpArgType.InstanceId => IsInstanceId(value),
            OpArgType.ServiceName => IsServiceName(value),
            OpArgType.RegistryPath => RegistryPaths.TryParse(value, out _, out _),
            OpArgType.FilePath => IsFilePathArg(value),
            OpArgType.ImageName => IsImageName(value),
            OpArgType.Guid => Guid.TryParseExact(value, "D", out _),
            _ => false,
        };
        if (!ok)
        {
            return "args.invalid";
        }

        if (spec.Pattern is { } pattern)
        {
            var m = Regex.Match(value, pattern, RegexOptions.CultureInvariant, RegexTimeout);
            if (!m.Success || m.Index != 0 || m.Length != value.Length)
            {
                return "args.pattern";
            }
        }

        return null;
    }

    /// <summary>A canonical invariant integer (optional minus, digits, no padding).</summary>
    /// <param name="value">Value.</param>
    public static bool IsCanonicalInt(string value)
        => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
           && string.Equals(n.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal);

    /// <summary>A PnP instance id: printable ASCII, at least two segments, no empty or dot segments.</summary>
    /// <param name="value">Value.</param>
    public static bool IsInstanceId(string value)
    {
        if (value.Length is < 3 or > 400 || value.Any(c => c < 0x20 || c > 0x7E || c is '*' or '?' or '"' or '<' or '>' or '|' or '/'))
        {
            return false;
        }

        var parts = value.Split('\\');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p is not "." and not "..");
    }

    /// <summary>A service key name.</summary>
    /// <param name="value">Value.</param>
    public static bool IsServiceName(string value)
        => value.Length is >= 1 and <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '$' or '@' or ' ' or '+')
           && value.Trim().Length == value.Length;

    /// <summary>An executable image name (no path) ending in <c>.exe</c>.</summary>
    /// <param name="value">Value.</param>
    public static bool IsImageName(string value)
        => value.Length is >= 5 and <= 128
           && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && value.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0
           && value.Trim().Length == value.Length
           && !value.StartsWith('.');

    /// <summary>A file path argument: an absolute local path, or one that starts with a <see cref="PathTokens"/> token.</summary>
    /// <param name="value">Value.</param>
    public static bool IsFilePathArg(string value)
    {
        foreach (var token in PathTokens)
        {
            if (value.StartsWith(token + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return IsAbsoluteLocalPath("C:\\X" + value[token.Length..]);
            }
        }

        return IsAbsoluteLocalPath(value);
    }

    /// <summary>
    /// An absolute, canonical local path: drive letter + backslash, no device/UNC/<c>\\?\</c> prefixes, no alternate data
    /// streams, no wildcards, no <c>.</c>/<c>..</c> components, no trailing dots or spaces in a component.
    /// </summary>
    /// <param name="value">Value.</param>
    public static bool IsAbsoluteLocalPath(string value)
    {
        if (value.Length is < 4 or > 1024 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\')
        {
            return false;
        }

        if (value.IndexOfAny(['/', '*', '?', '"', '<', '>', '|']) >= 0 || value.IndexOf(':', 2) >= 0 || value.Any(char.IsControl))
        {
            return false;
        }

        var parts = value[3..].Split('\\');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.StartsWith(' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Expands a leading <see cref="PathTokens"/> token from OS-known folders; returns null when the path is not valid.</summary>
    /// <param name="value">Path argument.</param>
    /// <param name="folders">Known folders.</param>
    public static string? ExpandPath(string value, KnownFolders folders)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(folders);
        var expanded = value;
        foreach (var token in PathTokens)
        {
            if (value.StartsWith(token + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var root = token.ToUpperInvariant() switch
                {
                    "%SYSTEMROOT%" or "%WINDIR%" => folders.Windows,
                    "%PROGRAMFILES%" => folders.ProgramFiles,
                    "%PROGRAMFILES(X86)%" => folders.ProgramFilesX86,
                    _ => folders.ProgramData,
                };
                expanded = Path.TrimEndingDirectorySeparator(root) + value[token.Length..];
                break;
            }
        }

        return IsAbsoluteLocalPath(expanded) ? expanded : null;
    }
}

/// <summary>Registry path arguments: <c>HKLM\…</c> or <c>HKCU\…</c> (HKCU = the player's hive).</summary>
public static class RegistryPaths
{
    /// <summary>Parses a registry path argument.</summary>
    /// <param name="value">Value.</param>
    /// <param name="hive">Hive.</param>
    /// <param name="subKey">Key path relative to the hive.</param>
    public static bool TryParse(string? value, out RegHive hive, out string subKey)
    {
        hive = RegHive.Machine;
        subKey = string.Empty;
        if (value is null || value.Length is < 6 or > 512 || value.Any(char.IsControl))
        {
            return false;
        }

        if (value.StartsWith("HKLM\\", StringComparison.Ordinal))
        {
            hive = RegHive.Machine;
        }
        else if (value.StartsWith("HKCU\\", StringComparison.Ordinal))
        {
            hive = RegHive.User;
        }
        else
        {
            return false;
        }

        var rest = value[5..];
        if (!IsSubKey(rest))
        {
            return false;
        }

        subKey = rest;
        return true;
    }

    /// <summary>A relative key path: non-empty components separated by single backslashes, no <c>.</c>/<c>..</c>.</summary>
    /// <param name="subKey">Key path.</param>
    public static bool IsSubKey(string subKey)
    {
        if (string.IsNullOrEmpty(subKey) || subKey.Length > 512 || subKey.Any(char.IsControl))
        {
            return false;
        }

        return subKey.Split('\\').All(p => p.Length > 0 && p.Length <= 255 && p is not "." and not ".." && p.Trim().Length == p.Length);
    }
}
