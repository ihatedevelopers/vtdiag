using VTD.Contracts.Reporting;

namespace VTD.Reporting;

/// <summary>A <c>.vtdscan</c> that cannot be written or read. <see cref="Code"/> is machine-readable.</summary>
public sealed class ScanBundleException : Exception
{
    /// <summary>Codes.</summary>
    public static class Codes
    {
        /// <summary>Not a zip, unknown format major, missing or unreadable manifest or part.</summary>
        public const string Format = "format";

        /// <summary>A part's SHA-256 or size does not match the manifest, or its compressed data is corrupt.</summary>
        public const string HashMismatch = "hash_mismatch";

        /// <summary>A size cap was exceeded (entry, total, entry count, compression ratio).</summary>
        public const string TooLarge = "too_large";

        /// <summary>An unknown, duplicate, unlisted or path-traversal entry name.</summary>
        public const string BadEntry = "bad_entry";

        /// <summary>A listed part is missing from the zip.</summary>
        public const string MissingPart = "missing_part";

        /// <summary>The snapshot is not share-level redacted.</summary>
        public const string NotRedacted = "not_redacted";

        /// <summary>The sweep found something that must not leave the PC (write only).</summary>
        public const string SweepBlocked = "sweep_blocked";
    }

    /// <summary>Creates the exception.</summary>
    public ScanBundleException()
        : this(Codes.Format, "Invalid .vtdscan bundle.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    public ScanBundleException(string message)
        : this(Codes.Format, message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Inner exception.</param>
    public ScanBundleException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = Codes.Format;
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="code">Code (see <see cref="Codes"/>).</param>
    /// <param name="message">Message (English, for logs).</param>
    /// <param name="innerException">Inner exception.</param>
    /// <param name="hits">Sweep hits (for <see cref="Codes.SweepBlocked"/>).</param>
    public ScanBundleException(string code, string message, Exception? innerException = null, IReadOnlyList<SweepHit>? hits = null)
        : base(message, innerException)
    {
        Code = code;
        SweepHits = hits ?? [];
    }

    /// <summary>Machine code: <c>format</c>, <c>hash_mismatch</c>, <c>too_large</c>, <c>bad_entry</c>, <c>missing_part</c>,
    /// <c>not_redacted</c>, <c>sweep_blocked</c>.</summary>
    public string Code { get; }

    /// <summary>Sweep hits when <see cref="Code"/> is <c>sweep_blocked</c>.</summary>
    public IReadOnlyList<SweepHit> SweepHits { get; } = [];
}
