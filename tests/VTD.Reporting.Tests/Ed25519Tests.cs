using VTD.Reporting.Crypto;

namespace VTD.Reporting.Tests;

/// <summary>RFC 8032 section 7.1 test vectors (TEST 1, 2, 3 and TEST SHA(abc)).</summary>
public sealed class Ed25519Tests
{
    public static TheoryData<string, string, string, string> Vectors => new()
    {
        {
            "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        },
        {
            "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
        },
        {
            "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"
        },
        {
            "833fe62409237b9d62ec77587520911e9a759cec1d19755b7da901b96dca3d42",
            "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704"
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Rfc8032_vector_public_key_signature_and_verify(string secret, string publicKey, string message, string signature)
    {
        var sk = Convert.FromHexString(secret);
        var msg = Convert.FromHexString(message);
        Assert.Equal(publicKey, Convert.ToHexStringLower(Ed25519.GetPublicKey(sk)));
        Assert.Equal(signature, Convert.ToHexStringLower(Ed25519.Sign(sk, msg)));
        Assert.True(Ed25519.Verify(Convert.FromHexString(publicKey), msg, Convert.FromHexString(signature)));
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Any_flipped_bit_fails_verification(string secret, string publicKey, string message, string signature)
    {
        _ = secret;
        var pk = Convert.FromHexString(publicKey);
        var msg = Convert.FromHexString(message);
        var sig = Convert.FromHexString(signature);

        var badSig = (byte[])sig.Clone();
        badSig[5] ^= 0x01;
        Assert.False(Ed25519.Verify(pk, msg, badSig));

        var badS = (byte[])sig.Clone();
        badS[40] ^= 0x80;
        Assert.False(Ed25519.Verify(pk, msg, badS));

        var badMsg = msg.Length == 0 ? new byte[] { 0 } : (byte[])msg.Clone();
        if (msg.Length > 0)
        {
            badMsg[0] ^= 0x01;
        }

        Assert.False(Ed25519.Verify(pk, badMsg, sig));

        var badPk = (byte[])pk.Clone();
        badPk[3] ^= 0x10;
        Assert.False(Ed25519.Verify(badPk, msg, sig));
    }

    [Fact]
    public void Malformed_inputs_return_false_instead_of_throwing()
    {
        Assert.False(Ed25519.Verify(new byte[31], [], new byte[64]));
        Assert.False(Ed25519.Verify(new byte[32], [], new byte[63]));
        var sig = new byte[64];
        Array.Fill(sig, (byte)0xFF); // S >= L: non-canonical
        Assert.False(Ed25519.Verify(Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"), [], sig));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")] // y = 0, order 4
    [InlineData("0100000000000000000000000000000000000000000000000000000000000000")] // identity
    [InlineData("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")] // y = -1, order 2
    [InlineData("c7176a703d4dd84fba3c0b760d10670f2a2053fa2c39ccc64ec7fd7792ac037a")] // order 8
    public void Small_order_keys_and_R_never_verify(string smallOrderHex)
    {
        // With a torsion key and R, an all-zero-S "signature" verifies for a fraction of messages under plain RFC 8032
        // cofactorless checks; a forged "signed" plan must never pass.
        var point = Convert.FromHexString(smallOrderHex);
        for (var i = 0; i < 16; i++)
        {
            var msg = new[] { (byte)i };
            Assert.False(Ed25519.Verify(point, msg, [.. point, .. new byte[32]]));
            Assert.False(Ed25519.Verify(point, msg, new byte[64]));
        }
    }
}
