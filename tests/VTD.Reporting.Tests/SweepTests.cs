using System.Text.Json.Nodes;
using VTD.Contracts.Reporting;
using VTD.Contracts.Serialization;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary>The export-time sweep: what blocks, what must not.</summary>
public sealed class SweepTests
{
    private static string Json(string value) => VtdJson.Serialize(value);

    [Theory]
    [InlineData(CanaryTokens.RemotingAuthToken, "canary")]
    [InlineData(CanaryTokens.RemotingAuthToken, "auth_token")]
    [InlineData(CanaryTokens.Jwt, "jwt")]
    [InlineData(CanaryTokens.UserProfilePath, "userprofile")]
    [InlineData(CanaryTokens.UserName, "canary")]
    [InlineData(CanaryTokens.ComputerName, "canary")]
    [InlineData(CanaryTokens.Email, "email")]
    [InlineData(CanaryTokens.Sid, "sid")]
    [InlineData(CanaryTokens.Puuid, "id_next_to_key")]
    [InlineData("C:/Users/JohnSmith/Documents", "userprofile")]
    [InlineData("\\Device\\HarddiskVolume3\\Users\\Ayse\\AppData", "userprofile")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234", "auth_token")]
    [InlineData("LoginId=12345678", "id_next_to_key")]
    [InlineData("CommandLine=\"RiotClientServices.exe\" --launch-product=valorant", "auth_token")]
    [InlineData("MachineId: 4c4c4544-0032-3010-8051-b4c04f4e3332", "id_next_to_key")]
    public void Each_rule_fires(string text, string rule)
    {
        var hits = Redactor().Sweep(Json(text));
        Assert.Contains(hits, h => h.Rule == rule);
    }

    [Theory]
    [InlineData("HKLM:{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver")]
    [InlineData("{4d36e972-e325-11ce-bfc1-08002be10318}")]
    [InlineData("01JB6R7X3K9M2N4P5Q6R7S8T9V")]
    [InlineData("3f79bb7b435b05321651daefd374cdc681dc06faa65e374e38337b88ca046dea")]
    [InlineData("rt25cx21x64.sys")]
    [InlineData("10.68.815.2023")]
    [InlineData("release-11.08-shipping-14-3988147")]
    [InlineData("PCI\\VEN_10EC&DEV_8125&SUBSYS_87D71043&REV_05\\h:0123456789abcdef")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("C:\\Users\\Public\\Desktop")]
    [InlineData("C:\\Users\\Default\\NTUSER.DAT")]
    [InlineData("C:\\Users\\<user>\\AppData\\Local\\Discord")]
    [InlineData("%USERPROFILE%\\AppData\\Local\\VALORANT")]
    [InlineData("--remoting-auth-token=[redacted]")]
    [InlineData("puuid=[redacted]")]
    [InlineData("CommandLine=[redacted]")]
    [InlineData("\\SystemRoot\\System32\\DriverStore\\FileRepository\\nv_dispi.inf_amd64_0a1b2c3d4e5f6a7b\\nvlddmkm.sys")]
    [InlineData("Microsoft Windows Hardware Compatibility Publisher")]
    [InlineData("Realtek Gaming 2.5GbE Family Controller")]
    [InlineData("mk1:7f3c2a9e41d05b6c")]
    [InlineData("04:42:1A:h:0123456789ab")]
    [InlineData("VAN 9003")]
    [InlineData("0xc0000005")]
    [InlineData("Hardware: Independent Flip")]
    public void Legit_data_does_not_hit(string text)
        => Assert.Empty(Redactor().Sweep(Json(text)));

    [Fact]
    public void Real_fixtures_and_canned_analyses_do_not_hit_after_share_redaction()
    {
        foreach (var name in Fixtures.Names)
        {
            var snapshot = TestSupport.Snapshot(name);
            var redactor = Redactor();
            var redacted = redactor.Redact(snapshot, RedactionPolicy.Share);
            Assert.Empty(redacted.SweepHits);
            Assert.Empty(redactor.Sweep(VtdJson.Serialize(redactor.Redact(CannedAnalyses.For(snapshot), RedactionPolicy.Share).Value)));
        }
    }

    [Fact]
    public void Property_names_are_swept_masked_and_the_hit_names_the_snapshot_section()
    {
        var node = new JsonObject
        {
            ["snapshot"] = new JsonObject
            {
                ["apps"] = new JsonObject { ["status"] = "known", ["value"] = new JsonArray(new JsonObject { [CanaryTokens.Email] = "x" }) },
            },
        };
        var hits = Redactor().Sweep(node.ToJsonString());
        var hit = Assert.Single(hits, h => h.Rule == "email");
        Assert.Equal("apps", hit.Section);
        Assert.Equal("/snapshot/apps/value/0/*", hit.Path);
        Assert.All(hits, h => Assert.DoesNotContain("vtdcanary", h.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Hits_never_carry_the_matched_text()
    {
        IReadOnlyDictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal) { ["note"] = CanaryTokens.Jwt + " " + CanaryTokens.Email };
        var json = VtdJson.Serialize(map);
        foreach (var hit in Redactor().Sweep(json))
        {
            Assert.Equal("/note", hit.Path);
            Assert.Equal("note", hit.Section);
        }
    }

    [Fact]
    public void Invalid_json_blocks()
        => Assert.Equal("invalid_json", Assert.Single(Redactor().Sweep("{not json")).Rule);

    private static Redactor Redactor() => TestSupport.Redactor();
}
