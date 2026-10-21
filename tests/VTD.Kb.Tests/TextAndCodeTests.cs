using System.Globalization;
using VTD.Contracts;
using VTD.Knowledge;

namespace VTD.Kb.Tests;

/// <summary>Code normalization and lookup, and the string formatter.</summary>
public sealed class TextAndCodeTests
{
    [Theory]
    [InlineData("van9003", "VAN 9003")]
    [InlineData("VAN-9003", "VAN 9003")]
    [InlineData("VAN: 9003", "VAN 9003")]
    [InlineData("  van 9003 ", "VAN 9003")]
    [InlineData("VAN -81", "VAN -81")]
    [InlineData("VAN: -81", "VAN -81")]
    [InlineData("VAN - 81", "VAN -81")]
    [InlineData("VAN: RESTRICTION: 1", "VAN RESTRICTION 1")]
    [InlineData("Error code: VAN 152", "VAN 152")]
    [InlineData("val5", "VAL 5")]
    public void Codes_normalize_culture_invariantly(string raw, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); // the dotted-I trap must not matter
            Assert.Equal(expected, CodeNormalizer.Normalize(raw));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Lookup_is_exact_or_marked_fuzzy()
    {
        var kb = Repo.Real;
        var exact = kb.FindCode("van 9003")!;
        Assert.True(exact.Exact);
        Assert.Equal("van-9003", exact.Def.Id);

        var signless = kb.FindCode("VAN 104")!; // players drop the minus
        Assert.False(signless.Exact);
        Assert.Equal("VAN -104", signless.Normalized);

        var alias = kb.FindCode("VAN 81")!; // community spelling of VAN -81 is an explicit alias
        Assert.Contains("nahimic-audio-service-launch-hang", alias.Def.PointsTo.Select(p => p.Hypothesis));

        Assert.Equal("van-restriction-1", kb.FindCode("VAN: RESTRICTION: 1")!.Def.Id);
        Assert.Equal("val-8-group", kb.FindCode("VAL 12")!.Def.Id);
        Assert.Null(kb.FindCode("VAL 9")); // "9 and 19 not on page"
        Assert.Null(kb.FindCode("hello"));
    }

    [Fact]
    public void Text_falls_back_to_english_then_key()
    {
        var kb = Repo.Real;
        Assert.Equal("Turn on Secure Boot", kb.Text("guide.secureboot.enable.title", "en"));
        Assert.NotEqual(kb.Text("guide.secureboot.enable.title", "en"), kb.Text("guide.secureboot.enable.title", "tr"));
        Assert.Equal(kb.Text("guide.secureboot.enable.title", "en"), kb.Text("guide.secureboot.enable.title", "de"));
        Assert.Equal("no.such.key", kb.Text("no.such.key", "tr"));
        Assert.True(kb.HasText("answer.yes", "tr"));
    }

    [Fact]
    public void Formatter_handles_placeholders_plurals_and_display_culture()
    {
        var args = new Dictionary<string, object>(StringComparer.Ordinal) { ["count"] = 1234.5, ["name"] = "Nahimic" };
        Assert.Equal("1,234.5 from Nahimic", MessageFormatter.Format("{count} from {name}", "en", args));
        Assert.Equal("1.234,5 from Nahimic", MessageFormatter.Format("{count} from {name}", "tr", args));
        var plural = "{n, plural, one {# reset} other {# resets}}";
        Assert.Equal("1 reset", MessageFormatter.Format(plural, "en", new Dictionary<string, object>(StringComparer.Ordinal) { ["n"] = 1 }));
        Assert.Equal("14 resets", MessageFormatter.Format(plural, "en", new Dictionary<string, object>(StringComparer.Ordinal) { ["n"] = 14 }));
        Assert.Equal("missing {x}", MessageFormatter.Format("missing {x}", "en", args));
        Assert.Equal(new SortedSet<string>(StringComparer.Ordinal) { "count", "name" }, MessageFormatter.PlaceholderNames("{count} from {name}"));
    }

    [Fact]
    public void Real_strings_render_with_args()
    {
        var text = Repo.Real.Text("signal.events.realtek_nic_resets.ev", Languages.Turkish,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["count"] = "14", ["days"] = "14" });
        Assert.Contains("14", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
    }
}
