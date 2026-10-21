using System.Globalization;
using VTD.Platform.Sources;

namespace VTD.Collectors.Core;

/// <summary>Invariant helpers over raw registry data.</summary>
public static class RegExtensions
{
    /// <summary>The key exists and was read.</summary>
    /// <param name="key">Key.</param>
    public static bool Ok(this RegKeyData key) => key.Status == ReadStatus.Ok;

    /// <summary>A value by name (case-insensitive like the registry).</summary>
    /// <param name="key">Key.</param>
    /// <param name="name">Value name.</param>
    public static RegValue? Value(this RegKeyData key, string name)
        => key.Values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>String data (any kind), null when absent.</summary>
    /// <param name="key">Key.</param>
    /// <param name="name">Value name.</param>
    public static string? Str(this RegKeyData key, string name) => key.Value(name)?.Data;

    /// <summary>Numeric data (DWORD/QWORD, or a decimal string), null when absent or not numeric.</summary>
    /// <param name="key">Key.</param>
    /// <param name="name">Value name.</param>
    public static long? Num(this RegKeyData key, string name)
    {
        var v = key.Value(name);
        if (v?.Data is null)
        {
            return null;
        }

        if (string.Equals(v.Kind, "Binary", StringComparison.Ordinal))
        {
            return null;
        }

        return ulong.TryParse(v.Data.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? unchecked((long)u) : null;
    }

    /// <summary>A key path joined with a child name.</summary>
    /// <param name="path">Parent.</param>
    /// <param name="child">Child.</param>
    public static string Sub(this string path, string child) => path + "\\" + child;
}

/// <summary>Invariant path helpers.</summary>
public static class PathText
{
    /// <summary>File name of a path (forward or back slashes), or the input.</summary>
    /// <param name="path">Path.</param>
    public static string FileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>
    /// The executable of a command line WITHOUT its arguments (arguments can carry tokens and are never kept):
    /// a quoted first token, else everything up to the first <c>.exe</c>, else the first token.
    /// </summary>
    /// <param name="commandLine">Command line.</param>
    public static string? Executable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }

        var best = -1;
        foreach (var ext in new[] { ".exe", ".sys", ".dll", ".com", ".bat", ".cmd", ".lnk" })
        {
            var from = 0;
            while (true)
            {
                var i = s.IndexOf(ext, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0)
                {
                    break;
                }

                var end = i + ext.Length;
                if (end == s.Length || char.IsWhiteSpace(s[end]) || s[end] is '"' or ',')
                {
                    if (best < 0 || end < best)
                    {
                        best = end;
                    }

                    break;
                }

                from = end;
            }
        }

        if (best > 0)
        {
            return s[..best];
        }

        var space = s.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? s[..space] : s;
    }

    /// <summary>
    /// Normalizes a kernel/service image path: strips arguments, <c>\??\</c>, expands <c>\SystemRoot\</c>,
    /// <c>%SystemRoot%</c>, <c>%windir%</c>, <c>%ProgramFiles%</c> and a bare <c>System32\</c> prefix.
    /// </summary>
    /// <param name="imagePath">Raw image path.</param>
    /// <param name="windowsDir">Windows directory.</param>
    /// <param name="programFiles">Program Files.</param>
    public static string? NormalizeImagePath(string? imagePath, string windowsDir, string programFiles)
    {
        var p = Executable(imagePath);
        if (string.IsNullOrEmpty(p))
        {
            return null;
        }

        if (p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            p = windowsDir + p[11..];
        }
        else if (p.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase))
        {
            p = windowsDir + p[12..];
        }
        else if (p.StartsWith("%windir%", StringComparison.OrdinalIgnoreCase))
        {
            p = windowsDir + p[8..];
        }
        else if (p.StartsWith("%ProgramFiles%", StringComparison.OrdinalIgnoreCase))
        {
            p = programFiles + p[14..];
        }
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
        {
            p = windowsDir + "\\" + p;
        }

        return p;
    }

    /// <summary>Drive letter with colon of an absolute path (forward or back slashes), uppercase.</summary>
    /// <param name="path">Path.</param>
    public static string? Drive(string? path)
        => path is { Length: >= 2 } && char.IsAsciiLetter(path[0]) && path[1] == ':' ? char.ToUpperInvariant(path[0]) + ":" : null;

    /// <summary>The path contains a non-ASCII character.</summary>
    /// <param name="path">Path.</param>
    public static bool HasNonAscii(string path) => path.Any(c => c > 127);
}

/// <summary>Expands environment variables for the PLAYER (never the engine's own environment).</summary>
public static class PlayerPaths
{
    /// <summary>Expands the well-known variables; unknown ones are left as they are.</summary>
    /// <param name="env">Environment.</param>
    /// <param name="value">Value.</param>
    public static string Expand(ScanEnvironment env, string value)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        (string Name, string Value)[] map =
        [
            ("%USERPROFILE%", env.Player.ProfilePath), ("%LOCALAPPDATA%", env.Player.LocalAppData), ("%APPDATA%", env.Player.RoamingAppData),
            ("%SystemRoot%", env.WindowsDir), ("%windir%", env.WindowsDir), ("%SystemDrive%", env.SystemDrive),
            ("%ProgramFiles(x86)%", env.Env.ProgramFilesX86), ("%ProgramW6432%", env.Env.ProgramFiles), ("%ProgramFiles%", env.Env.ProgramFiles),
            ("%CommonProgramFiles%", Path.Combine(env.Env.ProgramFiles, "Common Files")), ("%ProgramData%", env.Env.ProgramData), ("%ALLUSERSPROFILE%", env.Env.ProgramData),
            ("%TEMP%", Path.Combine(env.Player.LocalAppData, "Temp")), ("%TMP%", Path.Combine(env.Player.LocalAppData, "Temp")),
        ];
        foreach (var (name, replacement) in map)
        {
            value = value.Replace(name, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }
}

/// <summary>Invariant parsing.</summary>
public static class Parse
{
    /// <summary>Int or null.</summary>
    /// <param name="s">Text.</param>
    public static int? Int(string? s) => int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Long or null.</summary>
    /// <param name="s">Text.</param>
    public static long? Long(string? s) => long.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Bool (<c>true</c>/<c>false</c>/1/0) or null.</summary>
    /// <param name="s">Text.</param>
    public static bool? Bool(string? s) => s?.Trim() switch
    {
        "true" or "True" or "1" => true,
        "false" or "False" or "0" => false,
        _ => null,
    };

    /// <summary>Comma-joined ints.</summary>
    /// <param name="s">Text.</param>
    public static IReadOnlyList<int> IntList(string? s)
        => string.IsNullOrWhiteSpace(s) ? [] : s.Split(',').Select(Int).Where(i => i is not null).Select(i => i!.Value).ToList();

    /// <summary>CIM_DATETIME (<c>yyyymmddHHMMSS.mmmmmmsUUU</c>) to a date, or null.</summary>
    /// <param name="s">Text.</param>
    public static DateOnly? CimDate(string? s)
    {
        if (s is null || s.Length < 8)
        {
            return null;
        }

        return DateOnly.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>CIM_DATETIME to UTC, or null.</summary>
    /// <param name="s">Text.</param>
    public static DateTimeOffset? CimDateTime(string? s)
    {
        if (s is null || s.Length < 14 || !DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var offsetMinutes = 0;
        if (s.Length >= 25 && (s[21] == '+' || s[21] == '-') && int.TryParse(s[22..25], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
        {
            offsetMinutes = s[21] == '-' ? -m : m;
        }

        return new DateTimeOffset(local, TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();
    }

    /// <summary>Hex string without separators, or null.</summary>
    /// <param name="s">Text.</param>
    public static byte[]? Hex(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
