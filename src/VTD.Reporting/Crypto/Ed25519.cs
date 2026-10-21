using System.Numerics;
using System.Security.Cryptography;

namespace VTD.Reporting.Crypto;

/// <summary>
/// Pure C# Ed25519 (RFC 8032 section 5.1, the "PureEdDSA" variant) for <c>.vtdplan</c> signatures. The .NET 10 BCL has
/// no Ed25519, and adding a package is an AV/licence decision, so this follows the RFC's reference algorithm with
/// <see cref="BigInteger"/> field arithmetic. It is correct and small, not fast (a verify takes a few milliseconds) and
/// not constant-time: verification handles only public data. <see cref="Sign"/> exists for helper tooling and tests;
/// do not use it where a timing side channel on the private key matters.
/// </summary>
public static class Ed25519
{
    /// <summary>Public key length (bytes).</summary>
    public const int PublicKeySize = 32;

    /// <summary>Private key (seed) length (bytes).</summary>
    public const int PrivateKeySize = 32;

    /// <summary>Signature length (bytes).</summary>
    public const int SignatureSize = 64;

    // Field prime p = 2^255 - 19 and group order L = 2^252 + 27742317777372353535851937790883648493.
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point BasePoint = CreateBasePoint();

    /// <summary>Derives the public key from a 32-byte private key seed.</summary>
    /// <param name="privateKey">Seed.</param>
    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != PrivateKeySize)
        {
            throw new ArgumentException("An Ed25519 private key is 32 bytes.", nameof(privateKey));
        }

        var (a, _) = ExpandSecret(privateKey);
        return Encode(Multiply(a, BasePoint));
    }

    /// <summary>Signs a message.</summary>
    /// <param name="privateKey">32-byte seed.</param>
    /// <param name="message">Message bytes.</param>
    public static byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        if (privateKey.Length != PrivateKeySize)
        {
            throw new ArgumentException("An Ed25519 private key is 32 bytes.", nameof(privateKey));
        }

        var (a, prefix) = ExpandSecret(privateKey);
        var publicKey = Encode(Multiply(a, BasePoint));
        var r = HashToScalar(prefix, message);
        var rEncoded = Encode(Multiply(r, BasePoint));
        var h = HashToScalar(rEncoded, publicKey, message);
        var s = Mod(r + (h * a), L);
        var signature = new byte[SignatureSize];
        rEncoded.CopyTo(signature, 0);
        WriteLittleEndian(s, signature.AsSpan(32, 32));
        return signature;
    }

    /// <summary>Verifies a detached signature. Returns false (never throws) for malformed keys or signatures.</summary>
    /// <param name="publicKey">32-byte public key.</param>
    /// <param name="message">Message bytes.</param>
    /// <param name="signature">64-byte signature.</param>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        var a = Decode(publicKey);
        if (a is null)
        {
            return false;
        }

        var rBytes = signature[..32];
        var r = Decode(rBytes);
        if (r is null)
        {
            return false;
        }

        // Reject small-order (torsion) keys and R points, as libsodium does: with them a "signature" such as all zeros
        // over the all-zero key verifies for a fraction of messages, so anyone could fake a signed plan.
        if (IsSmallOrder(a.Value) || IsSmallOrder(r.Value))
        {
            return false;
        }

        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= L)
        {
            return false; // RFC 8032 5.1.7: reject non-canonical S (malleability).
        }

        var h = HashToScalar(rBytes, publicKey, message);
        var left = Multiply(s, BasePoint);
        var right = Add(r.Value, Multiply(h, a.Value));
        return AreEqual(left, right);
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Point CreateBasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, 0) ?? throw new InvalidOperationException("Ed25519 base point.");
        return new Point(x, y, 1, Mod(x * y));
    }

    private static BigInteger Mod(BigInteger value) => Mod(value, P);

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var r = BigInteger.Remainder(value, modulus);
        return r.Sign < 0 ? r + modulus : r;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static (BigInteger A, byte[] Prefix) ExpandSecret(ReadOnlySpan<byte> secret)
    {
        var h = SHA512.HashData(secret);
        var scalarBytes = h.AsSpan(0, 32).ToArray();
        scalarBytes[0] &= 248;
        scalarBytes[31] &= 127;
        scalarBytes[31] |= 64;
        var a = new BigInteger(scalarBytes, isUnsigned: true, isBigEndian: false);
        return (a, h.AsSpan(32, 32).ToArray());
    }

    private static BigInteger HashToScalar(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        sha.AppendData(first);
        sha.AppendData(second);
        sha.AppendData(third);
        var digest = sha.GetHashAndReset();
        return Mod(new BigInteger(digest, isUnsigned: true, isBigEndian: false), L);
    }

    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(BigInteger scalar, Point point)
    {
        var result = new Point(0, 1, 1, 0);
        var addend = point;
        while (scalar.Sign > 0)
        {
            if (!scalar.IsEven)
            {
                result = Add(result, addend);
            }

            addend = Add(addend, addend);
            scalar >>= 1;
        }

        return result;
    }

    private static bool IsSmallOrder(Point p)
    {
        var eight = Multiply(8, p);
        return Mod(eight.X).IsZero && Mod(eight.Y - eight.Z).IsZero;
    }

    private static bool AreEqual(Point p, Point q)
        => Mod((p.X * q.Z) - (q.X * p.Z)).IsZero && Mod((p.Y * q.Z) - (q.Y * p.Z)).IsZero;

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P)
        {
            return null;
        }

        var x2 = Mod(((y * y) - 1) * Inverse((D * y * y) + 1));
        if (x2.IsZero)
        {
            return sign == 0 ? BigInteger.Zero : null;
        }

        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod((x * x) - x2).IsZero)
        {
            x = Mod(x * SqrtM1);
        }

        if (!Mod((x * x) - x2).IsZero)
        {
            return null;
        }

        if ((int)(x & 1) != sign)
        {
            x = P - x;
        }

        return x;
    }

    private static byte[] Encode(Point point)
    {
        var zInv = Inverse(point.Z);
        var x = Mod(point.X * zInv);
        var y = Mod(point.Y * zInv);
        var bytes = new byte[32];
        WriteLittleEndian(y, bytes);
        if (!x.IsEven)
        {
            bytes[31] |= 0x80;
        }

        return bytes;
    }

    private static Point? Decode(ReadOnlySpan<byte> encoded)
    {
        Span<byte> copy = stackalloc byte[32];
        encoded.CopyTo(copy);
        var sign = copy[31] >> 7;
        copy[31] &= 0x7F;
        var y = new BigInteger(copy, isUnsigned: true, isBigEndian: false);
        var x = RecoverX(y, sign);
        return x is null ? null : new Point(x.Value, y, 1, Mod(x.Value * y));
    }

    private static void WriteLittleEndian(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        if (!value.TryWriteBytes(destination, out _, isUnsigned: true, isBigEndian: false))
        {
            throw new InvalidOperationException("Ed25519 scalar does not fit in 32 bytes.");
        }
    }
}
