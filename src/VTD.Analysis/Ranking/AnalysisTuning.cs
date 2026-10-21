namespace VTD.Analysis.Ranking;

/// <summary>
/// Analysis-internal tuning (the shared thresholds live in <see cref="VTD.Contracts.RankingConstants"/>). Priors are the
/// flat smoothed share of a rule inside its catalog category, ((threads + 1) / (category threads + category rules)),
/// times how relevant the category is to the chosen symptom chips. Chips set priors; they never veto.
/// </summary>
public static class AnalysisTuning
{
    /// <summary>Relevance when a chosen chip is one of the rule's own symptoms.</summary>
    public const double SymptomMatch = 0.5;

    /// <summary>Relevance when a chosen chip selects the rule's category (but is not one of its symptoms).</summary>
    public const double CategoryMatch = 0.25;

    /// <summary>Relevance when chips were chosen and none points at this rule.</summary>
    public const double NoMatch = 0.04;

    /// <summary>Relevance when no chip was chosen (CLI scan, helper import).</summary>
    public const double NoSymptoms = 0.15;

    /// <summary>
    /// Smoothing pseudo-count added to every category's denominator: the "other causes we don't know yet" inside a
    /// category, so a category with a single rule does not hand it a near-certain prior.
    /// </summary>
    public const int CategorySmoothing = 20;

    /// <summary>Generated (conflict) rules count as zero-thread rules in a category of at least this many threads.</summary>
    public const int GeneratedDenominatorFloor = 100;

    /// <summary>Prior probability bounds.</summary>
    public const double MinPrior = 1e-4;

    /// <summary>Prior probability bounds.</summary>
    public const double MaxPrior = 0.6;

    /// <summary>Total log-odds an error code table can add to one hypothesis.</summary>
    public const double MaxCodeLogOdds = 5.0;

    /// <summary>Negative evidence per verified fix whose outcome was NotFixed, scaled by P(fix works | cause).</summary>
    public const double FailedFixPenalty = 2.5;

    /// <summary>Hypotheses below this posterior are not listed (they still count for the unknown-cause share).</summary>
    public const double ListMinPosterior = 0.01;

    /// <summary>At most this many hypotheses are listed.</summary>
    public const int MaxListed = 30;

    /// <summary>"Could not check" covers hypotheses at or above this posterior (or the top 10).</summary>
    public const double CouldNotCheckMinPosterior = 0.05;

    /// <summary>A question must move the two leaders apart by at least this many log-odds to be worth asking.</summary>
    public const double MinQuestionSeparation = 0.5;

    /// <summary>Evidence below this |weight| does not make a "could not check" entry.</summary>
    public const double CouldNotCheckMinWeight = 0.5;
}
