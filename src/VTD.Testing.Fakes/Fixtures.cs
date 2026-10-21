using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;

namespace VTD.Testing.Fakes;

/// <summary>
/// Loads the checked-in fixture snapshots (<c>tests/fixtures/snapshots/*.json</c>, embedded in this assembly) and lists
/// what each one is expected to diagnose. A2's accuracy gate and A6's report tests start from these.
/// </summary>
public static class Fixtures
{
    /// <summary>File name (no extension) of the Realtek 2.5GbE stutter fixture.</summary>
    public const string RealtekStutter = "realtek-2-5gbe-stutter";

    /// <summary>File name of the Nahimic black-screen fixture.</summary>
    public const string NahimicBlackScreen = "nahimic-black-screen";

    /// <summary>File name of the VAN 9003 Secure Boot off fixture (limited mode).</summary>
    public const string Van9003SecureBootOff = "van9003-secureboot-off";

    /// <summary>All fixture names.</summary>
    public static IReadOnlyList<string> Names { get; } = [RealtekStutter, NahimicBlackScreen, Van9003SecureBootOff];

    /// <summary>Expected top hypothesis (catalog id) per fixture: the label for accuracy tests.</summary>
    public static IReadOnlyDictionary<string, string> ExpectedTopHypothesis { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RealtekStutter] = "realtek-2-5gbe-nic-stutter",
        [NahimicBlackScreen] = "nahimic-audio-service-launch-hang",
        [Van9003SecureBootOff] = "secure-boot-not-detected-van9003",
    };

    /// <summary>Symptoms the player would pick for each fixture.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Symptoms { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [RealtekStutter] = ["stutter"],
        [NahimicBlackScreen] = ["launch_black_screen"],
        [Van9003SecureBootOff] = ["error_code"],
    };

    /// <summary>Raw JSON of a fixture.</summary>
    /// <param name="name">Fixture name.</param>
    public static string LoadJson(string name)
    {
        var resource = $"VTD.Fixtures.Snapshots.{name}.json";
        using var stream = typeof(Fixtures).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"Fixture '{name}' is not embedded. Known: {string.Join(", ", Names)}.", resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Deserializes a fixture strictly with the VTDiag JSON contract.</summary>
    /// <param name="name">Fixture name.</param>
    public static ScanSnapshot Load(string name) => VtdJson.Deserialize<ScanSnapshot>(LoadJson(name));

    /// <summary>Names of every embedded fixture resource.</summary>
    public static IReadOnlyList<string> EmbeddedResourceNames()
        => typeof(Fixtures).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("VTD.Fixtures.Snapshots.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
}
