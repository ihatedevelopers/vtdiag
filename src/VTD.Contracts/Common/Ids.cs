using System.Security.Cryptography;
using System.Text;

namespace VTD.Contracts;

/// <summary>
/// Generates ids. Every id in VTDiag (snapshot, analysis, session, attempt, preview, token, plan, bundle, report) is a
/// 26-character Crockford base32 ULID string: sortable by creation time, culture-invariant, safe in file names.
/// Inject this (with a <see cref="TimeProvider"/>) so tests get deterministic ids (see VTD.Testing.Fakes).
/// </summary>
public interface IIdGenerator
{
    /// <summary>Returns a new ULID string.</summary>
    string NewId();
}

/// <summary>Default <see cref="IIdGenerator"/>: 48-bit millisecond timestamp + 80 random bits.</summary>
/// <param name="clock">Time source.</param>
public sealed class UlidGenerator(TimeProvider clock) : IIdGenerator
{
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc />
    public string NewId()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        return Ulid.Format(_clock.GetUtcNow(), random);
    }
}

/// <summary>ULID formatting and validation.</summary>
public static class Ulid
{
    /// <summary>Length of a ULID string.</summary>
    public const int Length = 26;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Formats a ULID from a timestamp and 10 bytes of entropy.</summary>
    /// <param name="timestamp">Creation time (millisecond precision is kept).</param>
    /// <param name="random">Exactly 10 bytes.</param>
    public static string Format(DateTimeOffset timestamp, ReadOnlySpan<byte> random)
    {
        if (random.Length != 10)
        {
            throw new ArgumentException("ULID entropy must be 10 bytes.", nameof(random));
        }

        var ms = (ulong)timestamp.ToUnixTimeMilliseconds();
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }

        random.CopyTo(bytes[6..]);

        // 128 bits -> 26 base32 chars (first char carries 3 bits).
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        Span<char> chars = stackalloc char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }

    /// <summary>True when <paramref name="value"/> is a syntactically valid ULID (uppercase Crockford base32).</summary>
    /// <param name="value">Candidate id.</param>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length || value[0] > '7')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The canonical hash used wherever two processes must agree on "the same thing": consent binding
/// (<c>bindingHash</c>, <c>previewHash</c>), state captures (bit-identical undo), and plan signatures.
/// Input is a string map; output is lowercase hex SHA-256 over <c>key=value\n</c> lines sorted by key (ordinal),
/// with <c>\</c>, <c>=</c> and newline in keys/values escaped as <c>\\</c>, <c>\=</c>, <c>\n</c>. UTF-8, no BOM.
/// </summary>
public static class CanonicalHash
{
    /// <summary>Hashes a string map canonically.</summary>
    /// <param name="values">The map. Keys are compared ordinally.</param>
    public static string Of(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sb = new StringBuilder();
        foreach (var key in values.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sb.Append(Escape(key)).Append('=').Append(Escape(values[key])).Append('\n');
        }

        return OfText(sb.ToString());
    }

    /// <summary>Hashes UTF-8 text.</summary>
    /// <param name="text">The text.</param>
    public static string OfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Hashes bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    public static string OfBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Escape(string s) => s
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("=", "\\=", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
