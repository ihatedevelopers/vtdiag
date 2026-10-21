using VTD.Contracts.Serialization;
using VTD.Contracts.Snapshots;
using VTD.Testing.Fakes;

namespace VTD.Contracts.Tests;

/// <summary>The three hand-made fixture snapshots deserialize strictly, round-trip, and are valid against the schema.</summary>
public sealed class FixtureTests
{
    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Fixtures.Names)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Checked_in_fixture_matches_its_builder(string name)
    {
        var path = Path.Combine(RepoPaths.FixtureDir, name + ".json");
        var built = RepoPaths.Normalize(VtdJson.Serialize(FixtureSnapshots.All[name](), indented: true));
        if (RepoPaths.UpdateFixtures)
        {
            Directory.CreateDirectory(RepoPaths.FixtureDir);
            File.WriteAllText(path, built);
        }

        Assert.True(File.Exists(path), $"{path} is missing. Run the tests once with VTD_UPDATE_FIXTURES=1.");
        Assert.True(string.Equals(built, RepoPaths.Normalize(File.ReadAllText(path)), StringComparison.Ordinal), $"{name}.json differs from FixtureSnapshots.{name}. Re-run with VTD_UPDATE_FIXTURES=1 and review the diff.");
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_deserializes_strictly_and_round_trips(string name)
    {
        var json = RepoPaths.Normalize(File.ReadAllText(Path.Combine(RepoPaths.FixtureDir, name + ".json")));
        var snapshot = VtdJson.Deserialize<ScanSnapshot>(json);
        Assert.Equal(ScanSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.True(Ulid.IsValid(snapshot.Meta.SnapshotId));
        Assert.Equal(json, RepoPaths.Normalize(VtdJson.Serialize(snapshot, indented: true)));
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_is_valid_against_snapshot_schema(string name)
    {
        var json = File.ReadAllText(Path.Combine(RepoPaths.FixtureDir, name + ".json"));
        var errors = SchemaValidation.Validate("snapshot", json);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(20)));
    }

    [Fact]
    public void Fixtures_are_embedded_in_the_fakes_and_load_by_name()
    {
        Assert.Equal(Fixtures.Names.Count, Fixtures.EmbeddedResourceNames().Count);
        foreach (var name in Fixtures.Names)
        {
            var loaded = Fixtures.Load(name);
            Assert.Equal(FixtureSnapshots.All[name]().Meta.SnapshotId, loaded.Meta.SnapshotId);
            Assert.True(Fixtures.ExpectedTopHypothesis.ContainsKey(name));
        }
    }

    [Fact]
    public void Realtek_fixture_has_an_unused_enabled_realtek_adapter_and_resets()
    {
        var s = Fixtures.Load(Fixtures.RealtekStutter);
        var realtek = Assert.Single(s.Network.Adapters.Value, a => a.InstanceId == FixtureSnapshots.RealtekAdapterInstanceId);
        Assert.True(realtek.Enabled);
        Assert.False(realtek.IsDefaultRoute);
        Assert.Contains(s.Network.Adapters.Value, a => a.IsDefaultRoute && a.Kind == AdapterKind.WiFi && a.HasInternet == true);
        Assert.Equal(14, Assert.Single(s.Events.Hits.Value).Count);
        Assert.Equal("tr-TR", s.Meta.OsUiCulture);
    }

    [Fact]
    public void Nahimic_fixture_has_service_running_and_productinfo_crash()
    {
        var s = Fixtures.Load(Fixtures.NahimicBlackScreen);
        Assert.Contains(s.Services.Value, v => v.Name == "NahimicService" && v.State == ServiceState.Running);
        Assert.All(s.Events.AppCrashes.Value, c => Assert.Equal("ProductInfo.dll", c.Module));
    }

    [Fact]
    public void Van9003_fixture_is_limited_mode_with_explicit_unknowns()
    {
        var s = Fixtures.Load(Fixtures.Van9003SecureBootOff);
        Assert.Equal(PrivilegeMode.Limited, s.Meta.Privilege);
        Assert.Equal(Tri.False, s.Security.SecureBootEnabled.ToTri());
        Assert.Equal(FactStatus.Unknown, s.Security.SecureBootSetupMode.Status);
        Assert.Equal(UnknownReason.NeedsAdmin, s.Security.SecureBootSetupMode.Reason);
        Assert.Equal(Tri.Unknown, s.Security.SecureBootSetupMode.ToTri());
        Assert.Contains(s.Riot.DetectedCodes.Value, c => c.Code == "VAN 9003");
    }
}
