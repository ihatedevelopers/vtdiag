using VTD.App.Localization;
using VTD.Contracts;
using VTD.Contracts.Analysis;
using VTD.Contracts.Fixes;
using VTD.Contracts.Sessions;

namespace VTD.App.ViewModels;

/// <summary>Plain-language words for engine enums (one place, so every screen says the same thing).</summary>
public static class Words
{
    private static Loc L => Loc.Instance;

    /// <summary>Confidence band ("Very likely"). Players see bands, never fake-precise percentages.</summary>
    /// <param name="band">Band.</param>
    public static string Band(ConfidenceBand band) => L.Get("Band." + band);

    /// <summary>Honesty bucket name ("Fixable here").</summary>
    /// <param name="bucket">Bucket.</param>
    public static string Bucket(HonestyBucket bucket) => L.Get("Bucket." + bucket);

    /// <summary>Honesty bucket sentence ("This is outside your PC...").</summary>
    /// <param name="bucket">Bucket.</param>
    public static string BucketExplain(HonestyBucket bucket) => L.Get("Bucket." + bucket + ".Explain");

    /// <summary>Why something couldn't be checked.</summary>
    /// <param name="reason">Reason.</param>
    public static string Unknown(UnknownReason? reason) => L.Get("Unknown." + (reason ?? UnknownReason.Failed));

    /// <summary>Health status word.</summary>
    /// <param name="status">Status.</param>
    public static string Health(HealthStatus status) => L.Get("Health.Status." + status);

    /// <summary>Attempt status in plain words.</summary>
    /// <param name="status">Status.</param>
    public static string Attempt(AttemptStatus status) => L.Get("Attempt." + status);

    /// <summary>Outcome in plain words.</summary>
    /// <param name="outcome">Outcome.</param>
    public static string Outcome(FixOutcome outcome) => L.Get("Outcome." + outcome);

    /// <summary>Risk badge.</summary>
    /// <param name="risk">Risk.</param>
    public static BadgeViewModel Risk(RiskLevel risk) => new(L.Get("Badge.Risk." + risk), risk switch
    {
        RiskLevel.Low => BadgeTone.Good,
        RiskLevel.Medium => BadgeTone.Caution,
        _ => BadgeTone.Risk,
    });

    /// <summary>Reversibility badge.</summary>
    /// <param name="r">Reversibility.</param>
    public static BadgeViewModel Reversible(Reversibility r) => new(L.Get("Badge.Undo." + r), r switch
    {
        Reversibility.Exact or Reversibility.SessionOnly => BadgeTone.Good,
        Reversibility.BestEffort => BadgeTone.Neutral,
        _ => BadgeTone.Risk,
    });

    /// <summary>Reboot badge.</summary>
    /// <param name="r">Reboot need.</param>
    public static BadgeViewModel Reboot(RebootNeed r) => new(L.Get("Badge.Reboot." + r), r == RebootNeed.Required ? BadgeTone.Caution : BadgeTone.Good);

    /// <summary>Time badge ("about 10 s").</summary>
    /// <param name="minutes">Estimated minutes.</param>
    public static BadgeViewModel Time(double minutes)
        => minutes < 1
            ? new(L.Format("Badge.Seconds", ("n", Math.Max(5, (int)Math.Round(minutes * 60 / 5) * 5))), BadgeTone.Neutral)
            : new(L.Format("Badge.Minutes", ("n", (int)Math.Ceiling(minutes))), BadgeTone.Neutral);

    /// <summary>Who proposed a fix, for the consent badge. Null for the player's own pick.</summary>
    /// <param name="p">Proposer.</param>
    /// <param name="planHelper">Helper name when the step came from an imported plan.</param>
    /// <param name="verified">The plan is signed by a key on the KB's trusted-helper list.</param>
    public static string? Proposer(Proposer? p, string? planHelper = null, bool verified = false)
    {
        if (planHelper is not null || p?.PlanId is not null)
        {
            var name = planHelper ?? p?.Label;
            return string.IsNullOrWhiteSpace(name) ? L.Get("Proposer.PlanNoName")
                : verified ? L.Format("Proposer.PlanVerified", ("name", Clip(name, 40)))
                : L.Format("Proposer.Plan", ("name", Clip(name, 40)));
        }

        return p?.Origin switch
        {
            EventOrigin.Mcp => string.IsNullOrWhiteSpace(p.Label) ? L.Get("Proposer.Mcp") : L.Format("Proposer.McpLabel", ("label", Clip(p.Label, 60))),
            EventOrigin.Cli => L.Get("Proposer.Cli"),
            _ => null,
        };
    }

    /// <summary>Shortens untrusted text for display.</summary>
    /// <param name="text">Text.</param>
    /// <param name="max">Max chars.</param>
    public static string Clip(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
    }

    /// <summary>A date for the screen.</summary>
    /// <param name="utc">UTC time.</param>
    public static string Date(DateTimeOffset utc) => utc.ToLocalTime().ToString("g", L.Culture);
}

/// <summary>Tone of a badge (colour).</summary>
public enum BadgeTone
{
    /// <summary>Neutral.</summary>
    Neutral = 0,

    /// <summary>Reassuring (undoable, low risk, no restart).</summary>
    Good = 1,

    /// <summary>Worth a look (restart needed, medium risk).</summary>
    Caution = 2,

    /// <summary>Serious (one-way, high risk).</summary>
    Risk = 3,
}

/// <summary>A small badge.</summary>
/// <param name="Text">Text.</param>
/// <param name="Tone">Tone.</param>
public sealed record BadgeViewModel(string Text, BadgeTone Tone);

/// <summary>One line with a status (checklists, step lists).</summary>
/// <param name="Text">Text.</param>
/// <param name="Status">Status (Ok, Warn, Fail, Unknown, NotApplicable; or for steps: Ok = done, Unknown = pending, Warn = running).</param>
/// <param name="Detail">Secondary text.</param>
public sealed record LineViewModel(string Text, HealthStatus Status, string? Detail = null)
{
    /// <summary>Has detail.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}
