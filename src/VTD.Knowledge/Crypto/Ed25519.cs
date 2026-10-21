using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace VTD.Knowledge.Crypto;

/// <summary>
/// Ed25519 (RFC 8032, pure, no context) on <see cref="BigInteger"/> arithmetic. .NET has no built-in Ed25519 and no
/// package may be added, so this is the small reference algorithm. Verification is what the app needs (KB manifest,
/// revocation list, helper plans); signing exists for tests and tooling. Not constant-time: never sign with a
/// production key in this process (the release pipeline signs with <c>tools/kb_builder.py sign</c> offline).
/// </summary>
public static class Ed25519
{
    /// <summary>Public key size.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Signature size.</summary>
    public const int SignatureSize = 64;

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", NumberStyles.None, CultureInfo.InvariantCulture);
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point Identity = new(0, 1, 1, 0);
    private static readonly Point Base = CreateBase();

    /// <summary>Verifies a signature. Returns false (never throws) for malformed keys or signatures and small-order keys.</summary>
    /// <param name="publicKey">32-byte public key.</param>
    /// <param name="message">Signed bytes.</param>
    /// <param name="signature">64-byte signature.</param>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        var a = Decode(publicKey);
        var r = Decode(signature[..32]);
        if (a is null || r is null || IsSmallOrder(a.Value))
        {
            return false;
        }

        var s = FromLittleEndian(signature[32..]);
        if (s >= L)
        {
            return false;
        }

        var h = Mod(HashToScalar(signature[..32], publicKey, message), L);
        var sb = Multiply(s, Base);
        var rha = Add(r.Value, Multiply(h, a.Value));
        return AreEqual(sb, rha);
    }

    /// <summary>Derives the public key of a 32-byte seed.</summary>
    /// <param name="seed">Private seed.</param>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        var (a, _) = Expand(seed);
        return Encode(Multiply(a, Base));
    }

    /// <summary>Signs a message (tests and tooling only; not constant-time).</summary>
    /// <param name="seed">32-byte private seed.</param>
    /// <param name="message">Bytes to sign.</param>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (a, prefix) = Expand(seed);
        var publicKey = Encode(Multiply(a, Base));
        var r = Mod(HashToScalar(prefix, message, []), L);
        var rEncoded = Encode(Multiply(r, Base));
        var h = Mod(HashToScalar(rEncoded, publicKey, message), L);
        var s = Mod(r + (h * a), L);
        var signature = new byte[SignatureSize];
        rEncoded.CopyTo(signature, 0);
        ToLittleEndian(s, signature.AsSpan(32));
        return signature;
    }

    private static (BigInteger Scalar, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
        {
            throw new ArgumentException("An Ed25519 seed is 32 bytes.", nameof(seed));
        }

        var h = SHA512.HashData(seed);
        h[0] &= 248;
        h[31] &= 127;
        h[31] |= 64;
        return (FromLittleEndian(h.AsSpan(0, 32)), h[32..]);
    }

    private static BigInteger HashToScalar(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        sha.AppendData(a);
        sha.AppendData(b);
        sha.AppendData(c);
        return FromLittleEndian(sha.GetHashAndReset());
    }

    private static Point CreateBase()
    {
        var y = Mod(4 * Inv(5));
        var x = RecoverX(y, 0) ?? throw new InvalidOperationException("Ed25519 base point.");
        return new Point(x, y, 1, Mod(x * y));
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
        var q = Identity;
        while (scalar > 0)
        {
            if (!scalar.IsEven)
            {
                q = Add(q, point);
            }

            point = Add(point, point);
            scalar >>= 1;
        }

        return q;
    }

    private static bool AreEqual(Point p, Point q)
        => Mod(p.X * q.Z) == Mod(q.X * p.Z) && Mod(p.Y * q.Z) == Mod(q.Y * p.Z);

    private static bool IsSmallOrder(Point p) => AreEqual(Multiply(8, p), Identity);

    private static byte[] Encode(Point p)
    {
        var zInv = Inv(p.Z);
        var x = Mod(p.X * zInv);
        var y = Mod(p.Y * zInv);
        var bytes = new byte[32];
        ToLittleEndian(y, bytes);
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
        var y = FromLittleEndian(copy);
        if (y >= P)
        {
            return null;
        }

        var x = RecoverX(y, sign);
        return x is null ? null : new Point(x.Value, y, 1, Mod(x.Value * y));
    }

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        var x2 = Mod((y * y - 1) * Inv((D * y * y) + 1));
        if (x2.IsZero)
        {
            return sign == 0 ? BigInteger.Zero : null;
        }

        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod((x * x) - x2) != 0)
        {
            x = Mod(x * SqrtM1);
        }

        if (Mod((x * x) - x2) != 0)
        {
            return null;
        }

        if ((int)(x & 1) != sign)
        {
            x = P - x;
        }

        return x;
    }

    private static BigInteger Mod(BigInteger value) => Mod(value, P);

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var r = BigInteger.Remainder(value, modulus);
        return r.Sign < 0 ? r + modulus : r;
    }

    private static BigInteger Inv(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static void ToLittleEndian(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        if (!value.TryWriteBytes(destination, out _, isUnsigned: true, isBigEndian: false))
        {
            throw new InvalidOperationException("Ed25519 value does not fit in 32 bytes.");
        }
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);
}
