using VTD.Contracts;
using VTD.Contracts.Ipc;
using VTD.Testing.Fakes;

namespace VTD.Reporting.Tests;

/// <summary>Writes sample renderings (canaries planted, engine redactor) for manual review when <c>VTD_DUMP_DIR</c> is set.</summary>
public sealed class ReportSampleDump
{
    [Fact]
    public void Dump_samples_for_review()
    {
        var dir = Environment.GetEnvironmentVariable("VTD_DUMP_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            Assert.Skip("Set VTD_DUMP_DIR to write sample reports.");
        }

        Directory.CreateDirectory(dir);
        foreach (var fixture in Fixtures.Names)
        {
            foreach (var language in Languages.All)
            {
                foreach (var (kind, suffix) in new[] { (ExportKind.ReportHtml, "html"), (ExportKind.ReportMarkdown, "md"), (ExportKind.AiDigest, "ai.md") })
                {
                    var result = TestSupport.Exporter(TestSupport.EngineRedactor()).Export(new ExportRequest { Kind = kind, Language = language }, CanaryTests.Planted(fixture));
                    File.WriteAllText(Path.Combine(dir, fixture + "." + language + "." + suffix), result.TextContent);
                }
            }
        }
    }
}
