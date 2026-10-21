using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using VTD.Contracts.Assist;
using VTD.Contracts.Fixes;
using VTD.Contracts.Ipc;
using VTD.Contracts.Journaling;
using VTD.Contracts.Knowledge;
using VTD.Contracts.Monitoring;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Contracts.Sessions;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Contracts.Tests;

/// <summary>Structural guarantees the redactor, the KB loader and the RPC layer rely on.</summary>
public sealed class ContractShapeTests
{
    /// <summary>Types that leave the PC (export, report, plan, bundle) or sit in shared files.</summary>
    private static readonly Type[] ExportedRoots =
    [
        typeof(ScanSnapshot), typeof(Analysis.AnalysisResult), typeof(ReportModel), typeof(FixPlan), typeof(ScanBundle),
        typeof(SessionState), typeof(MonitorSummary), typeof(FixPreview), typeof(JournalRecord), typeof(PlanValidation),
        typeof(ExportResult), typeof(EngineHello), typeof(SessionEvent), typeof(EngineNotification),
    ];

    /// <summary>Every type that is serialized at all.</summary>
    private static readonly Type[] SerializedRoots =
    [
        .. ExportedRoots,
        typeof(RuleDef), typeof(SignalDef), typeof(CollectorDef), typeof(ConflictDef), typeof(ErrorCodeDef), typeof(FixDef),
        typeof(GuideDef), typeof(QuestionDef), typeof(SymptomDef), typeof(TableDef), typeof(KbManifest), typeof(KbRevocationList),
        typeof(ConsentToken), typeof(RestartToken), typeof(FixRunResult), typeof(UndoResult), typeof(RecoveryReport),
        typeof(MonitorOptions), typeof(MonitorTick), typeof(ExportRequest), typeof(ClientHello), typeof(RestartPrompt),
        typeof(Scanning.ScanRequest), typeof(Scanning.ScanProgress), typeof(ReportBundle), typeof(ReportOptions),
        typeof(EngineDiscovery), typeof(PlayerContext), typeof(SignedPlanEnvelope), typeof(OpSpec), typeof(Scanning.RawCaptureEntry),
    ];

    [Fact]
    public void Every_string_bearing_property_of_an_exported_type_declares_its_sensitivity()
    {
        var missing = new List<string>();
        foreach (var (owner, prop) in Walk(ExportedRoots))
        {
            if (!CarriesText(prop.PropertyType))
            {
                continue;
            }

            var declared = prop.GetCustomAttribute<SensitivityAttribute>() ?? prop.DeclaringType!.GetCustomAttribute<SensitivityAttribute>(inherit: true);
            if (declared is null)
            {
                missing.Add($"{owner.Name}.{prop.Name} ({prop.DeclaringType!.Name})");
            }
        }

        Assert.True(missing.Count == 0, "Add [Sensitivity] (property or type level):\n" + string.Join("\n", missing.Distinct()));
    }

    [Fact]
    public void Every_type_reachable_from_a_contract_root_resolves_in_the_json_context()
    {
        var unresolved = new List<string>();
        foreach (var type in Reachable(SerializedRoots))
        {
            try
            {
                _ = VtdJson.Options.GetTypeInfo(type);
            }
            catch (NotSupportedException)
            {
                unresolved.Add(type.ToString());
            }
        }

        Assert.True(unresolved.Count == 0, "Register in VtdJsonContext:\n" + string.Join("\n", unresolved.Distinct()));
    }

    [Fact]
    public void Contract_records_have_no_public_setters_and_no_mutable_collections()
    {
        var offenders = new List<string>();
        foreach (var (_, prop) in Walk(SerializedRoots))
        {
            var setter = prop.SetMethod;
            var isInit = setter?.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit") ?? false;
            if (setter is { IsPublic: true } && !isInit)
            {
                offenders.Add($"{prop.DeclaringType!.Name}.{prop.Name} has a public setter");
            }

            var t = prop.PropertyType;
            if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
            {
                offenders.Add($"{prop.DeclaringType!.Name}.{prop.Name} exposes a mutable collection");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Distinct()));
    }

    [Fact]
    public void Serialization_is_culture_invariant()
    {
        var snapshot = FixtureSnapshots.RealtekStutter();
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var tr = VtdJson.Serialize(snapshot);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var inv = VtdJson.Serialize(snapshot);
            Assert.Equal(inv, tr);
            Assert.DoesNotContain("İ", tr, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Enums_are_camel_case_strings_and_catalog_names_are_kept()
    {
        Assert.Equal("\"fps_performance\"", VtdJson.Serialize(ProblemCategory.FpsPerformance));
        Assert.Equal("\"needsAdmin\"", VtdJson.Serialize(UnknownReason.NeedsAdmin));
        Assert.Equal("\"rgb_vendor\"", VtdJson.Serialize(ConflictKind.RgbVendor));
        Assert.Throws<System.Text.Json.JsonException>(() => VtdJson.Deserialize<UnknownReason>("1"));
    }

    [Fact]
    public void Polymorphic_contracts_round_trip_with_their_discriminator()
    {
        SessionEvent e = new ConsentGiven
        {
            EventId = "01JB6R7X3K9M2N4P5Q6R7S8TC0",
            Utc = FakeClock.DefaultStart,
            Token = new ConsentToken
            {
                TokenId = "01JB6R7X3K9M2N4P5Q6R7S8TC1",
                SessionId = "01JB6R7X3K9M2N4P5Q6R7S8TC2",
                PreviewId = "01JB6R7X3K9M2N4P5Q6R7S8TC3",
                FixId = "fix.net.disable_unused_adapter",
                BindingHash = "b",
                PreviewHash = "p",
                Nonce = "n",
                IssuedUtc = FakeClock.DefaultStart,
            },
            Origin = EventOrigin.Ui,
        };
        var json = VtdJson.Serialize(e);
        Assert.StartsWith("{\"type\":\"consentGiven\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("origin", json, StringComparison.OrdinalIgnoreCase); // never on the wire
        var back = Assert.IsType<ConsentGiven>(VtdJson.Deserialize<SessionCommand>(json));
        Assert.Equal(EventOrigin.Unspecified, back.Origin);

        // An engine-only event cannot be smuggled in as a client command.
        var engineOnly = VtdJson.Serialize<SessionEvent>(new ScanCompleted { EventId = "x", Utc = FakeClock.DefaultStart, SnapshotId = FixtureSnapshots.RealtekId });
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => VtdJson.Deserialize<SessionCommand>(engineOnly));

        CollectorDef c = new EventLogCollectorDef { Id = "x", Log = "System" };
        Assert.IsType<EventLogCollectorDef>(VtdJson.Deserialize<CollectorDef>(VtdJson.Serialize(c)));

        // Discriminator after other properties is accepted (hand-written KB files).
        Assert.IsType<FileExistsCollectorDef>(VtdJson.Deserialize<CollectorDef>("""{"id":"f","path":"%ProgramData%\\x","kind":"fileExists"}"""));
    }

    [Fact]
    public void Session_events_split_exactly_into_client_commands_and_engine_events()
    {
        static HashSet<Type> Derived(Type t) => t.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).Select(a => a.DerivedType).ToHashSet();
        var all = Derived(typeof(SessionEvent));
        var commands = Derived(typeof(SessionCommand));
        var engine = Derived(typeof(EngineEvent));
        Assert.Empty(commands.Intersect(engine));
        Assert.True(all.SetEquals(commands.Union(engine)), "SessionEvent must list exactly the SessionCommand and EngineEvent types.");
        Assert.All(commands, t => Assert.True(t.IsSubclassOf(typeof(SessionCommand)), t.Name));
        Assert.All(engine, t => Assert.True(t.IsSubclassOf(typeof(EngineEvent)), t.Name));
        var concrete = typeof(SessionEvent).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(SessionEvent)) && !t.IsAbstract).ToHashSet();
        Assert.True(concrete.SetEquals(all), "Every concrete SessionEvent must be registered for JSON.");
    }

    [Fact]
    public void Numbers_are_never_nan()
    {
        var tick = new MonitorTick { Seq = 1, Utc = FakeClock.DefaultStart, GameRunning = true, SelfCpuPct = double.NaN };
        Assert.ThrowsAny<Exception>(() => VtdJson.Serialize(tick));
    }

    [Fact]
    public void Lenient_options_skip_unknown_properties_strict_ones_do_not()
    {
        const string Newer = """{"id":"x","titleKey":"t","newFieldFromAFutureKb":1}""";
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => VtdJson.Deserialize<SymptomDef>(Newer));
        Assert.Equal("x", VtdJson.DeserializeLenient<SymptomDef>(Newer).Id);
    }

    [Fact]
    public void Primitive_catalog_is_consistent_and_checked_in()
    {
        Assert.Equal(OpCatalog.All.Count, OpCatalog.All.Select(o => o.Op).Distinct(StringComparer.Ordinal).Count());
        var constants = typeof(OpIds).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!).ToHashSet(StringComparer.Ordinal);
        Assert.True(constants.SetEquals(OpCatalog.All.Select(o => o.Op)), "OpIds and OpCatalog must list the same primitives.");
        Assert.All(OpCatalog.All.Where(o => o.Reversibility == Reversibility.OneWay), o => Assert.True(o.RiskFloor >= RiskLevel.Low));

        var path = Path.Combine(RepoPaths.SchemaDir, "ops.json");
        var generated = RepoPaths.Normalize(VtdJson.Serialize(OpCatalog.All, indented: true));
        if (RepoPaths.UpdateSchemas)
        {
            File.WriteAllText(path, generated);
        }

        Assert.True(File.Exists(path) && string.Equals(generated, RepoPaths.Normalize(File.ReadAllText(path)), StringComparison.Ordinal), "kb/schema/ops.json is out of date. Re-run with VTD_UPDATE_SCHEMAS=1.");
    }

    [Fact]
    public void Ranking_helpers_follow_the_documented_formula()
    {
        Assert.Equal(RankingConstants.UnknownCauseFloor, RankingConstants.UnknownCauseProbability([0.93]));
        Assert.Equal(0.5 * 0.5, RankingConstants.UnknownCauseProbability([0.5, 0.5]), 9);
        Assert.Equal(1.0, RankingConstants.UnknownCauseProbability([]));
        Assert.Equal(ConfidenceBand.VeryLikely, RankingConstants.BandOf(0.8));
        Assert.Equal(ConfidenceBand.LongShot, RankingConstants.BandOf(0.19));
    }

    [Fact]
    public void Guard_ids_and_protected_process_names_are_unique_and_lowercase_dotted()
    {
        Assert.Equal(GuardIds.All.Count, GuardIds.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(GuardIds.All, id => Assert.Matches("^[a-z]+(\\.[a-z_]+)+$", id));
        Assert.True(RiotProcessNames.IsProtected("valorant-win64-shipping.EXE"));
        Assert.False(RiotProcessNames.IsProtected("NahimicSvc64.exe"));
    }

    private static bool CarriesText(Type t)
    {
        if (t == typeof(string))
        {
            return true;
        }

        if (!t.IsGenericType)
        {
            return false;
        }

        var def = t.GetGenericTypeDefinition();
        var args = t.GetGenericArguments();
        if (def == typeof(IReadOnlyDictionary<,>) && args[0] == typeof(string))
        {
            return true;
        }

        return args.Any(CarriesText) && (def == typeof(IReadOnlyList<>) || def == typeof(Fact<>) || def == typeof(IReadOnlyDictionary<,>));
    }

    private static IEnumerable<(Type Owner, PropertyInfo Prop)> Walk(IEnumerable<Type> roots)
    {
        foreach (var type in Reachable(roots).Where(IsContractType))
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null || prop.GetIndexParameters().Length > 0 || prop.Name == "EqualityContract")
                {
                    continue;
                }

                yield return (type, prop);
            }
        }
    }

    private static HashSet<Type> Reachable(IEnumerable<Type> roots)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(roots);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t))
            {
                continue;
            }

            foreach (var derived in t.GetCustomAttributes<JsonDerivedTypeAttribute>())
            {
                queue.Enqueue(derived.DerivedType);
            }

            if (t.IsGenericType)
            {
                foreach (var a in t.GetGenericArguments())
                {
                    queue.Enqueue(a);
                }
            }

            if (Nullable.GetUnderlyingType(t) is { } underlying)
            {
                queue.Enqueue(underlying);
            }

            if (IsContractType(t))
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetCustomAttribute<JsonIgnoreAttribute>() is null && p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
                    {
                        queue.Enqueue(p.PropertyType);
                    }
                }
            }
        }

        return seen;
    }

    private static bool IsContractType(Type t) => t.Namespace?.StartsWith("VTD.Contracts", StringComparison.Ordinal) == true
        && !t.IsGenericType && (t.IsClass || (t.IsValueType && !t.IsEnum));
}
