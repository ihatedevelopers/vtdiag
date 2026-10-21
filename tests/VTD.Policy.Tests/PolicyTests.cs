using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace VTD.Policy.Tests;

/// <summary>
/// Repository policy (DESIGN.md section 2: "Banned-API analyzers enforce this, plus a CI scan of the binary's import
/// table"). VTDiag binaries are managed, so the "import table" is the P/Invoke ImplMap in each assembly's metadata.
/// </summary>
public sealed partial class PolicyTests
{
    /// <summary>Never imported by any VTDiag binary (Vanguard coexistence S8, AV heuristics, timer resolution).</summary>
    private static readonly string[] ForbiddenImports =
    [
        "OpenProcess", "NtOpenProcess", "ZwOpenProcess", "ReadProcessMemory", "WriteProcessMemory", "NtReadVirtualMemory",
        "NtWriteVirtualMemory", "CreateRemoteThread", "CreateRemoteThreadEx", "NtCreateThreadEx", "RtlCreateUserThread",
        "SetWindowsHookEx", "SetWindowsHookExA", "SetWindowsHookExW", "NtQuerySystemInformation", "ZwQuerySystemInformation",
        "NtQueryObject", "DuplicateHandle", "VirtualAllocEx", "VirtualProtectEx", "QueueUserAPC", "SetThreadContext",
        "DebugActiveProcess", "MiniDumpWriteDump", "timeBeginPeriod", "NtSetTimerResolution", "ZwSetTimerResolution",
        "OpenThread", "SetWinEventHook",
    ];

    /// <summary>Expected banned-API layer per src project (Directory.Build.targets).</summary>
    private static readonly Dictionary<string, string> ExpectedLayers = new(StringComparer.Ordinal)
    {
        ["VTD.Contracts"] = "ReadOnly",
        ["VTD.Platform"] = "ReadOnly",
        ["VTD.Collectors"] = "ReadOnly",
        ["VTD.Knowledge"] = "ReadOnly",
        ["VTD.Analysis"] = "ReadOnly",
        ["VTD.Reporting"] = "ReadOnly",
        ["VTD.Monitor.Core"] = "ReadOnly",
        ["VTD.Monitor"] = "ReadOnly",
        ["VTD.Testing.Fakes"] = "ReadOnly",
        ["VTD.App"] = "Ui",
        ["VTD.Fixes"] = "Write",
        ["VTD.Journal"] = "Write",
        ["VTD.Session"] = "Write",
        ["VTD.Engine"] = "Write",
    };

    private static string Root { get; } = FindRoot();

    private static string Configuration { get; } = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    /// <summary>The only native functions the UI may import (all for verifying the engine, see banned/import-allowlist.txt).</summary>
    private static readonly string[] UiAllowedImports = ["GetNamedPipeServerProcessId", "NtQuerySystemInformation", "QueryDosDeviceW", "WinVerifyTrust"];

    /// <summary>(repo-relative file, function) pairs from banned/import-allowlist.txt.</summary>
    private static IReadOnlyList<(string File, string Function)> ImportAllowlist { get; } = File.ReadAllLines(Path.Combine(FindRoot(), "banned", "import-allowlist.txt"))
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split('|'))
        .Select(p => (p[0].Trim(), p[1].Trim()))
        .ToList();

    [Fact]
    public void No_source_file_declares_a_forbidden_import()
    {
        var hits = new List<string>();
        foreach (var file in SourceFiles("src"))
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (var name in ForbiddenImports)
            {
                var allowed = ImportAllowlist.Any(a => string.Equals(a.File, Rel(file), StringComparison.OrdinalIgnoreCase) && string.Equals(a.Function, name, StringComparison.Ordinal));
                if (!allowed && Regex.IsMatch(code, $@"\b{name}\b"))
                {
                    hits.Add($"{Rel(file)}: {name}");
                }
            }
        }

        Assert.True(hits.Count == 0, "Forbidden native imports (see banned/import-allowlist.txt):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void No_built_vtdiag_binary_imports_a_forbidden_function()
    {
        var binaries = BuiltBinaries().ToList();
        Assert.True(binaries.Count >= ExpectedLayers.Count, $"Expected the {Configuration} build of every src project; found {binaries.Count} binaries. Build the solution first.");
        var hits = new List<string>();
        foreach (var path in binaries)
        {
            var project = ProjectOf(path);
            foreach (var (module, entry) in PInvokes(path))
            {
                var forbidden = ForbiddenImports.Any(f => string.Equals(f, entry, StringComparison.OrdinalIgnoreCase));
                var allowed = ImportAllowlist.Any(a => a.File.StartsWith("src/" + project + "/", StringComparison.OrdinalIgnoreCase) && string.Equals(a.Function, entry, StringComparison.Ordinal));
                if (forbidden && !allowed)
                {
                    hits.Add($"{Path.GetFileName(path)} imports {module}!{entry}");
                }
            }
        }

        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }

    [Fact]
    public void Ui_binary_imports_only_the_engine_verification_functions()
    {
        var app = BuiltBinaries().Single(p => string.Equals(Path.GetFileName(p), "VTDiag.dll", StringComparison.OrdinalIgnoreCase));
        var extra = PInvokes(app).Where(p => !UiAllowedImports.Contains(p.Entry, StringComparer.Ordinal)).ToList();
        Assert.True(extra.Count == 0, "The UI may only P/Invoke " + string.Join(", ", UiAllowedImports) + ": " + string.Join(", ", extra));
    }

    [Fact]
    public void Every_project_has_the_designed_banned_api_layer()
    {
        foreach (var path in BuiltBinaries())
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            var name = md.GetString(md.GetAssemblyDefinition().Name);
            var project = name switch
            {
                "VTDiag" => "VTD.App",
                "VTDiag.Engine" => "VTD.Engine",
                "VTDiag.Monitor" => "VTD.Monitor",
                _ => name,
            };
            Assert.True(ExpectedLayers.TryGetValue(project, out var expected), $"Unexpected binary {name}");
            Assert.Equal(expected, AssemblyMetadata(md, "VtdLayer"));
        }
    }

    [Fact]
    public void Rs0030_is_suppressed_only_in_allowlisted_files()
    {
        var allow = File.ReadAllLines(Path.Combine(Root, "banned", "rs0030-allowlist.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits = SourceFiles("src").Concat(SourceFiles("tests"))
            .Where(f => Rs0030Suppression().IsMatch(File.ReadAllText(f)))
            .Select(Rel)
            .Where(r => !allow.Contains(r))
            .ToList();
        Assert.True(hits.Count == 0, "RS0030 suppressed outside banned/rs0030-allowlist.txt:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Project_files_do_not_opt_out_of_analyzers_or_layers()
    {
        string[] forbidden = ["TreatWarningsAsErrors", "WarningsNotAsErrors", "VtdLayer", "BannedSymbols", "AdditionalFiles", "RunAnalyzers", "EnableNETAnalyzers", "AnalysisLevel", "AnalysisMode", "CodeAnalysisRuleSet", "GlobalAnalyzerConfigFiles", "EditorConfigFiles", "SuppressMessage"];
        string[] protectedRules = ["RS0030", "RS0031", "CA1304", "CA1305", "CA1309", "CA1310", "CA1311", "CA1416", "NU1902", "NU1903", "NU1904"];
        var hits = new List<string>();
        foreach (var proj in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(Root, "tests"), "*.csproj", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(proj);
            hits.AddRange(forbidden.Where(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)).Select(f => $"{Rel(proj)}: {f}"));
            foreach (Match m in NoWarn().Matches(text))
            {
                hits.AddRange(protectedRules.Where(r => m.Value.Contains(r, StringComparison.OrdinalIgnoreCase)).Select(r => $"{Rel(proj)}: NoWarn {r}"));
            }

            if (PackageVersionAttr().IsMatch(text))
            {
                hits.Add($"{Rel(proj)}: PackageReference with Version (use Directory.Packages.props)");
            }
        }

        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }

    [Fact]
    public void Build_and_analyzer_config_exists_only_at_the_root()
    {
        string[] names = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", ".editorconfig", ".globalconfig", "global.json", "NuGet.config", "nuget.config"];
        var hits = new[] { "src", "tests", "tools", "kb" }
            .Select(d => Path.Combine(Root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(f) && (names.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase) || f.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase)))
            .Select(Rel)
            .ToList();
        Assert.True(hits.Count == 0, "Only the repository root may hold build/analyzer config:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Every_project_is_in_the_solution()
    {
        var sln = File.ReadAllText(Path.Combine(Root, "VTDiag.sln"));
        var missing = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Where(p => !IsBuildOutput(p))
            .Select(p => Path.GetFileName(p))
            .Where(n => !sln.Contains(n, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(missing.Count == 0, "Add to VTDiag.sln (lead): " + string.Join(", ", missing));
    }

    private static IEnumerable<string> BuiltBinaries()
    {
        foreach (var projectDir in Directory.EnumerateDirectories(Path.Combine(Root, "src")))
        {
            var outDir = Path.Combine(projectDir, "bin", Configuration, "net10.0-windows");
            if (!Directory.Exists(outDir))
            {
                continue;
            }

            var project = Path.GetFileName(projectDir);
            var assemblyName = project switch
            {
                "VTD.App" => "VTDiag",
                "VTD.Engine" => "VTDiag.Engine",
                "VTD.Monitor" => "VTDiag.Monitor",
                _ => project,
            };
            var dll = Path.Combine(outDir, assemblyName + ".dll");
            if (File.Exists(dll))
            {
                yield return dll;
            }
        }
    }

    private static string ProjectOf(string binaryPath)
    {
        var src = Path.Combine(Root, "src") + Path.DirectorySeparatorChar;
        return Path.GetRelativePath(src, binaryPath).Split(Path.DirectorySeparatorChar)[0];
    }

    private static IEnumerable<(string Module, string Entry)> PInvokes(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var list = new List<(string, string)>();
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            var import = method.GetImport();
            if (import.Module.IsNil)
            {
                continue;
            }

            var module = md.GetString(md.GetModuleReference(import.Module).Name);
            var entry = import.Name.IsNil ? md.GetString(method.Name) : md.GetString(import.Name);
            list.Add((module, entry));
        }

        return list;
    }

    private static string? AssemblyMetadata(MetadataReader md, string key)
    {
        foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var ctor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            var type = md.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (md.GetString(type.Name) != "AssemblyMetadataAttribute")
            {
                continue;
            }

            var blob = md.GetBlobReader(attribute.Value);
            blob.ReadUInt16(); // prolog
            var k = blob.ReadSerializedString();
            var v = blob.ReadSerializedString();
            if (string.Equals(k, key, StringComparison.Ordinal))
            {
                return v;
            }
        }

        return null;
    }

    private static IEnumerable<string> SourceFiles(string folder)
        => Directory.EnumerateFiles(Path.Combine(Root, folder), "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f));

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase) || path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripComments(string code) => Comments().Replace(code, string.Empty);

    private static string Rel(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VTDiag.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("VTDiag.sln not found.");
    }

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"#pragma\s+warning\s+(disable|restore)[^\n]*RS003[01]|SuppressMessage\([^)]*RS003[01]|SuppressMessage\([^)]*BannedApi", RegexOptions.IgnoreCase)]
    private static partial Regex Rs0030Suppression();

    [GeneratedRegex(@"<NoWarn>[^<]*</NoWarn>", RegexOptions.IgnoreCase)]
    private static partial Regex NoWarn();

    [GeneratedRegex(@"<PackageReference[^>]*\sVersion\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex PackageVersionAttr();
}
