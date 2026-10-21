namespace VTD.Reporting;

/// <summary>
/// <c>kb/schema/plan.schema.json</c> as published (the file is linked into this assembly as a resource, never copied or
/// edited). The "Copy for AI" digest embeds it so a helper's AI writes plans against exactly the schema the player's
/// app validates.
/// </summary>
public static class PlanSchema
{
    private const string ResourceName = "VTD.Reporting.Schemas.plan.schema.json";
    private static readonly Lazy<string> Text = new(Load);

    /// <summary>The schema text.</summary>
    public static string Json => Text.Value;

    private static string Load()
    {
        using var stream = typeof(PlanSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("plan.schema.json is not embedded in VTD.Reporting.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
