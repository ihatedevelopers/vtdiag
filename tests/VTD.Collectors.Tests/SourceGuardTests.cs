using System.Text.RegularExpressions;
using VTD.Collectors.Tests.Support;

namespace VTD.Collectors.Tests;

/// <summary>
/// Source-level guards for A1's rules that the banned-API analyzers cannot express: per-user data only through the
/// player (never HKCU or the engine's own profile), no Process objects at all (names come from ToolHelp), no localized
/// display-name matching helpers, no expansion of the engine's environment.
/// </summary>
public sealed partial class SourceGuardTests
{
    private static IEnumerable<(string Path, string Code)> Sources(params string[] projects)
        => projects.SelectMany(p => Directory.EnumerateFiles(Path.Combine(TestPaths.Root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(f => (f, Comments().Replace(File.ReadAllText(f), string.Empty)));

    [Theory]
    [InlineData(@"Registry\.CurrentUser")]
    [InlineData(@"RegistryHive\.CurrentUser")]
    [InlineData(@"\bProcess\.")]
    [InlineData(@"new Process\(")]
    [InlineData(@"ExpandEnvironmentVariables")]
    [InlineData(@"GetEnvironmentVariable\(")]
    [InlineData(@"SystemParametersInfo")]
    [InlineData(@"\bnetsh\b|\bpowercfg\b|\bbcdedit\b|\btpmtool\b")]
    public void Read_side_never_uses(string pattern)
    {
        var hits = Sources("VTD.Platform", "VTD.Collectors")
            .Where(s => Regex.IsMatch(s.Code, pattern))
            .Select(s => Path.GetRelativePath(TestPaths.Root, s.Path))
            .ToList();
        Assert.True(hits.Count == 0, pattern + " found in: " + string.Join(", ", hits));
    }

    [Fact]
    public void Engine_profile_folders_are_read_only_in_PlayerContexts()
    {
        var hits = Sources("VTD.Platform", "VTD.Collectors")
            .Where(s => Regex.IsMatch(s.Code, @"SpecialFolder\.(LocalApplicationData|ApplicationData|UserProfile)"))
            .Select(s => Path.GetFileName(s.Path))
            .ToList();
        Assert.Equal(["LiveSources.cs"], hits);
    }

    [Fact]
    public void Only_the_three_allowlisted_platform_files_suppress_banned_apis()
    {
        var hits = Sources("VTD.Platform", "VTD.Collectors")
            .Where(s => File.ReadAllText(s.Path).Contains(SuppressionNeedle, StringComparison.Ordinal))
            .Select(s => Path.GetFileName(s.Path))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["ReadOnlyWmiMethods.cs", "SharedReadFile.cs"], hits);
    }

    // Built at runtime so this file does not itself look like a suppression to tests/VTD.Policy.Tests.
    private static readonly string SuppressionNeedle = string.Concat("#pragma warning ", "disable RS", "0030");

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();
}
