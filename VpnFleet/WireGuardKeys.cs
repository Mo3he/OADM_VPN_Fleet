using System.Numerics;
using System.Security.Cryptography;

namespace VpnFleet;

/// <summary>A WireGuard key pair, base64 like <c>wg genkey</c> / <c>wg pubkey</c>.</summary>
public sealed record WireGuardKeyPair(string PrivateKey, string PublicKey);

/// <summary>
/// WireGuard keys (Curve25519, RFC 7748) made on the OADM server, so a camera's private key goes only to that camera.
/// Few keys are made at a time; a plain Montgomery ladder over <see cref="BigInteger"/> is fast enough and needs no
/// native library.
/// </summary>
public static class WireGuardKeys
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger A24 = 121665;

    public static WireGuardKeyPair Generate()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        Clamp(secret);
        return new WireGuardKeyPair(Convert.ToBase64String(secret), Convert.ToBase64String(PublicOf(secret)));
    }

    /// <summary>The public key of a private key (base64); throws <see cref="FormatException"/> for anything but 32 bytes.</summary>
    public static string PublicKeyOf(string privateKeyBase64) => Convert.ToBase64String(PublicOf(Decode(privateKeyBase64)));

    /// <summary>True for 32 bytes in base64, the form of every WireGuard key.</summary>
    public static bool IsKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Trim().Length != 44)
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(base64.Trim()).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>X25519(scalar, u) of RFC 7748 section 5, little-endian bytes in and out.</summary>
    public static byte[] X25519(byte[] scalar, byte[] u)
    {
        ArgumentNullException.ThrowIfNull(scalar);
        ArgumentNullException.ThrowIfNull(u);
        var k = (byte[])scalar.Clone();
        Clamp(k);
        var uBytes = (byte[])u.Clone();
        uBytes[31] &= 127;
        var x1 = new BigInteger(uBytes, isUnsigned: true, isBigEndian: false);
        var kn = new BigInteger(k, isUnsigned: true, isBigEndian: false);

        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        var swap = 0;
        for (var t = 254; t >= 0; t--)
        {
            var bit = (int)((kn >> t) & 1);
            swap ^= bit;
            if (swap == 1)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }

            swap = bit;
            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);
            x3 = Mod(BigInteger.Pow(da + cb, 2));
            z3 = Mod(x1 * BigInteger.Pow(da - cb, 2));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + (A24 * e)));
        }

        if (swap == 1)
        {
            (x2, x3) = (x3, x2);
            (z2, z3) = (z3, z2);
        }

        var result = Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
        var bytes = new byte[32];
        result.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        return bytes;
    }

    private static byte[] PublicOf(byte[] secret)
    {
        var basePoint = new byte[32];
        basePoint[0] = 9;
        return X25519(secret, basePoint);
    }

    private static byte[] Decode(string base64)
    {
        var bytes = Convert.FromBase64String(base64.Trim());
        return bytes.Length == 32 ? bytes : throw new FormatException("A WireGuard key is 32 bytes.");
    }

    private static void Clamp(byte[] k)
    {
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
    }

    private static BigInteger Mod(BigInteger v)
    {
        var r = v % P;
        return r.Sign < 0 ? r + P : r;
    }
}
