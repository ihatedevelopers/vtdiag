using System.Diagnostics;

namespace VTD.App.Services;

/// <summary>
/// Opens an official https link in the default browser (allowlisted RS0030 exception, banned/rs0030-allowlist.txt).
/// Only https URLs on hosts the KB uses for official help; helper-supplied links are never passed here (they are shown
/// as plain text "link from helper").
/// </summary>
public static class LinkOpener
{
    private static readonly string[] AllowedHosts =
    [
        "support.riotgames.com", "support-valorant.riotgames.com", "playvalorant.com", "aka.ms", "support.microsoft.com",
        "learn.microsoft.com", "github.com",
    ];

    /// <summary>True when the URL is https on an allowlisted host.</summary>
    /// <param name="url">URL.</param>
    public static bool IsAllowed(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
           && string.IsNullOrEmpty(uri.UserInfo)
           && uri.IsDefaultPort
           && AllowedHosts.Any(h => string.Equals(uri.Host, h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens the link when allowed.</summary>
    /// <param name="url">URL.</param>
    /// <returns>True when a browser was asked to open it.</returns>
    public static bool Open(string url)
    {
        if (!IsAllowed(url))
        {
            return false;
        }

        var uri = new Uri(url, UriKind.Absolute);
#pragma warning disable RS0030 // Audited: ShellExecute of a validated https URL (allowlisted file).
        using var process = Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
#pragma warning restore RS0030
        return true;
    }
}
