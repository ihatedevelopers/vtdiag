using System.Globalization;
using System.Text.RegularExpressions;
using VTD.Platform.Sources;

namespace VTD.Collectors.Logs;

/// <summary>
/// Riot Client log safe extractor (critique section 3.8): Riot error codes surfaced by the client, Vanguard restart
/// requests and failures, patch failures, login/RSO and connectivity errors. Emits only <see cref="LogRecord"/> lines.
/// </summary>
public static partial class RiotClientLogExtractor
{
    /// <summary>Allowlisted pattern ids.</summary>
    public static IReadOnlyList<string> PatternIds { get; } =
    [
        "client.van_error_dialog", "client.val_error", "client.vanguard_restart_required", "client.vanguard_error",
        "client.patch_failed", "client.login_error", "client.rnet_not_connected", "client.crash",
    ];

    /// <summary>The stage-1 filter.</summary>
    public static LineFilter Filter { get; } = new("riotclient.v1", Map);

    /// <summary>Stage 1.</summary>
    /// <param name="line">Raw line.</param>
    public static string? Map(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length > 8192 || LogSafety.IsSensitive(line))
        {
            return null;
        }

        var codes = CodeNormalizer.Find(line).Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count > 0)
        {
            var van = codes.Where(c => c.StartsWith("VAN", StringComparison.Ordinal)).ToList();
            return van.Count > 0
                ? new LogRecord("client.van_error_dialog", null, van).Encode()
                : new LogRecord("client.val_error", null, codes).Encode();
        }

        if (line.Contains("restartRequired=true", StringComparison.Ordinal))
        {
            return new LogRecord("client.vanguard_restart_required", null, []).Encode();
        }

        if (VanguardError().IsMatch(line))
        {
            return new LogRecord("client.vanguard_error", null, []).Encode();
        }

        if (PatchFailed().IsMatch(line))
        {
            return new LogRecord("client.patch_failed", null, []).Encode();
        }

        if (line.Contains("RNET_NOT_CONNECTED", StringComparison.Ordinal))
        {
            return new LogRecord("client.rnet_not_connected", null, []).Encode();
        }

        if (LoginError().IsMatch(line))
        {
            return new LogRecord("client.login_error", null, []).Encode();
        }

        return line.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase) || CrashLine().IsMatch(line)
            ? new LogRecord("client.crash", null, []).Encode()
            : null;
    }

    [GeneratedRegex(@"\|\s*(ERROR|WARN)\|\s*vanguard:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VanguardError();

    [GeneratedRegex(@"\|\s*ERROR\|.{0,80}(login|rso|authenticat)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LoginError();

    [GeneratedRegex(@"\|\s*ERROR\|.{0,120}(Failed to start patching|patch(ing)? failed)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PatchFailed();

    [GeneratedRegex(@"\|\s*(ERROR|ALWAYS)\|.{0,80}(crashed|crash report|minidump written)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CrashLine();
}

/// <summary>
/// <c>Crashes\UECC-*\CrashContext.runtime-xml</c> safe extractor: only CrashType, a pattern id for ErrorMessage (never the
/// text) and the drive letter of BaseDir survive. CommandLine, MachineId, LoginId, UserName never leave the source.
/// </summary>
public static partial class CrashContextExtractor
{
    /// <summary>The stage-1 filter.</summary>
    public static LineFilter Filter { get; } = new("crashctx.v1", Map);

    /// <summary>Stage 1.</summary>
    /// <param name="line">Raw line.</param>
    public static string? Map(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length > 8192)
        {
            return null;
        }

        var m = Element().Match(line);
        if (!m.Success)
        {
            return null;
        }

        var value = m.Groups[2].Value;
        return m.Groups[1].Value switch
        {
            "CrashType" when SafeToken().IsMatch(value) => "type|" + value,
            "ErrorMessage" => "error|" + ErrorPattern(value),
            "BaseDir" => "drive|" + (Drive(value) ?? string.Empty),
            "EngineVersion" when SafeVersion().IsMatch(value) => "engine|" + value,
            _ => null,
        };
    }

    /// <summary>Maps an error message to an allowlisted pattern id.</summary>
    /// <param name="message">Message.</param>
    public static string ErrorPattern(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Contains("device being lost", StringComparison.OrdinalIgnoreCase) || message.Contains("0x887A0005", StringComparison.OrdinalIgnoreCase))
        {
            return "crash.d3d_device_lost";
        }

        if (message.Contains("0x887A0006", StringComparison.OrdinalIgnoreCase) || message.Contains("DEVICE_HUNG", StringComparison.OrdinalIgnoreCase))
        {
            return "crash.gpu_hung";
        }

        if (message.Contains("Out of video memory", StringComparison.OrdinalIgnoreCase))
        {
            return "crash.out_of_video_memory";
        }

        if (message.Contains("EXCEPTION_ACCESS_VIOLATION", StringComparison.OrdinalIgnoreCase) || message.Contains("0xc0000005", StringComparison.OrdinalIgnoreCase))
        {
            return "crash.access_violation";
        }

        if (message.Contains("Assertion failed", StringComparison.OrdinalIgnoreCase))
        {
            return "crash.assert";
        }

        if (message.Contains("Fatal error", StringComparison.OrdinalIgnoreCase))
        {
            return "crash.fatal_error";
        }

        return "crash.other";
    }

    private static string? Drive(string path)
    {
        var p = path.Trim();
        return p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' ? char.ToUpperInvariant(p[0]).ToString(CultureInfo.InvariantCulture) + ":" : null;
    }

    [GeneratedRegex(@"<(CrashType|ErrorMessage|BaseDir|EngineVersion)>([^<]{0,2048})</", RegexOptions.CultureInvariant)]
    private static partial Regex Element();

    [GeneratedRegex(@"^[A-Za-z]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeToken();

    [GeneratedRegex(@"^[0-9A-Za-z.+\-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersion();
}

/// <summary>GameUserSettings.ini whitelist filter (window mode, resolution, frame cap only; no monitor ids, no accounts).</summary>
public static partial class GameConfigExtractor
{
    /// <summary>Whitelisted keys.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "FullscreenMode", "PreferredFullscreenMode", "LastConfirmedFullscreenMode", "ResolutionSizeX", "ResolutionSizeY",
        "LastUserConfirmedResolutionSizeX", "LastUserConfirmedResolutionSizeY", "FrameRateLimit", "bUseVSync",
        "DesiredScreenWidth", "DesiredScreenHeight", "bShouldLetterbox", "bUseDynamicResolution",
    ];

    /// <summary>The stage-1 filter.</summary>
    public static LineFilter Filter { get; } = new("gameusersettings.v1", Map);

    /// <summary>Stage 1.</summary>
    /// <param name="line">Raw line.</param>
    public static string? Map(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Contains('\0', StringComparison.Ordinal))
        {
            return "corrupt|nul";
        }

        if (line.StartsWith('['))
        {
            return "section|";
        }

        var m = KeyValue().Match(line);
        return m.Success && Keys.Contains(m.Groups[1].Value, StringComparer.Ordinal) ? "kv|" + m.Groups[1].Value + "|" + m.Groups[2].Value : null;
    }

    [GeneratedRegex(@"^([A-Za-z]{1,48})=([0-9A-Za-z.\-]{0,24})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();
}
