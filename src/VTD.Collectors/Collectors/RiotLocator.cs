using System.Text.RegularExpressions;
using VTD.Collectors.Core;
using VTD.Platform.Sources;

namespace VTD.Collectors.Collectors;

/// <summary>Where the Riot Client, VALORANT and Vanguard live on this PC (memoized per scan).</summary>
/// <param name="ClientExe">RiotClientServices.exe path.</param>
/// <param name="ValorantDir">VALORANT install folder (<c>...\VALORANT\live</c>).</param>
/// <param name="VanguardDir">Riot Vanguard folder.</param>
/// <param name="InstallsStatus">Read status of RiotClientInstalls.json.</param>
/// <param name="SettingsStatus">Read status of the VALORANT product settings.</param>
public sealed record RiotLocation(string? ClientExe, string? ValorantDir, string VanguardDir, ReadStatus InstallsStatus, ReadStatus SettingsStatus)
{
    /// <summary>Riot Client folder.</summary>
    public string? ClientDir => ClientExe is null ? null : Path.GetDirectoryName(ClientExe);

    /// <summary>Both metadata files were read or are absent (a missing install is then a definite answer).</summary>
    public bool MetadataConclusive => InstallsStatus is ReadStatus.Ok or ReadStatus.NotFound && SettingsStatus is ReadStatus.Ok or ReadStatus.NotFound;

    /// <summary>Drive that hosts the game (or the client when the game is not installed).</summary>
    public string? RiotDrive => PathText.Drive(ValorantDir) ?? PathText.Drive(ClientExe);
}

/// <summary>Locates Riot installs from <c>%ProgramData%\Riot Games</c> metadata and the vgc service key.</summary>
public static partial class RiotLocator
{
    /// <summary>Filter for RiotClientInstalls.json: only quoted absolute paths and their keys survive.</summary>
    public static LineFilter InstallsFilter { get; } = new("riotinstalls.v1", line =>
    {
        var m = QuotedPathPair().Match(line);
        return m.Success ? m.Groups[1].Value + "|" + m.Groups[2].Value : null;
    });

    /// <summary>Filter for product_settings.yaml: only the install path line.</summary>
    public static LineFilter ProductSettingsFilter { get; } = new("productsettings.v1", line =>
    {
        var t = line.Trim();
        return t.StartsWith("product_install_full_path:", StringComparison.Ordinal) ? t["product_install_full_path:".Length..].Trim().Trim('"') : null;
    });

    /// <summary>Locates (memoized in the environment).</summary>
    /// <param name="env">Environment.</param>
    public static RiotLocation Locate(ScanEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return env.Memo("riot.location", () => LocateCore(env));
    }

    private static RiotLocation LocateCore(ScanEnvironment env)
    {
        var riotData = Path.Combine(env.Env.ProgramData, "Riot Games");
        string? clientExe = null;
        string? valorant = null;
        var installs = env.Sources.Files.ReadLines(Path.Combine(riotData, "RiotClientInstalls.json"), InstallsFilter, 1 << 20, 64);
        if (installs.Status == ReadStatus.Ok)
        {
            foreach (var pair in installs.Lines)
            {
                var parts = pair.Split('|');
                if (parts.Length != 2)
                {
                    continue;
                }

                if (parts[0] is "rc_default" or "rc_live" && clientExe is null)
                {
                    clientExe = Win(parts[1]);
                }
                else if (parts[0].Contains("VALORANT", StringComparison.OrdinalIgnoreCase) && valorant is null)
                {
                    valorant = Win(parts[0]).TrimEnd('\\');
                }
            }
        }

        var settings = env.Sources.Files.ReadLines(Path.Combine(riotData, "Metadata", "valorant.live", "valorant.live.product_settings.yaml"), ProductSettingsFilter, 1 << 20, 4);
        if (settings.Status == ReadStatus.Ok && settings.Lines.Count > 0 && settings.Lines[0].Length > 2)
        {
            valorant = Win(settings.Lines[0]).TrimEnd('\\');
        }

        var vgc = env.Hklm(@"SYSTEM\CurrentControlSet\Services\vgc");
        var vgcImage = PathText.NormalizeImagePath(vgc.Str("ImagePath"), env.WindowsDir, env.Env.ProgramFiles);
        var vanguardDir = vgcImage is not null ? Path.GetDirectoryName(vgcImage) ?? Path.Combine(env.Env.ProgramFiles, "Riot Vanguard") : Path.Combine(env.Env.ProgramFiles, "Riot Vanguard");
        return new RiotLocation(clientExe, valorant, vanguardDir, installs.Status, settings.Status);
    }

    private static string Win(string path) => path.Replace('/', '\\');

    [GeneratedRegex(@"""([^""]{1,260})""\s*:\s*""([A-Za-z]:[\\/][^""]{1,260})""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPathPair();
}
