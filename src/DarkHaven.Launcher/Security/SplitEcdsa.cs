using System.Numerics;
using System.Security.Cryptography;

namespace DarkHaven.Launcher.Security;

/// <summary>
/// ECDSA P-256 signing where the private key is never held as one value: it is stored as several shares
/// <c>d₁ + d₂ + … ≡ d (mod n)</c>, and a signature is computed from the shares directly
/// (<c>s = k⁻¹·(e + r·Σdᵢ) mod n</c>), so no variable and no run of bytes in the binary ever equals the key.
/// A build lays the shares out differently each time (see <see cref="LaunchSigningKey"/>), so a tool that
/// scrapes one build's binary finds nothing in the next. The result is an ordinary IEEE-P1363 signature the
/// server verifies with the matching public key like any other.
/// </summary>
/// <remarks>
/// This raises the cost of pulling the key out of a shipped launcher; it does not make it impossible — someone
/// who reverse-engineers a specific build can still recover it. The short-lived, one-time, server-bound proofs
/// are what limit what a recovered key is worth.
/// </remarks>
public static class SplitEcdsa
{
    // NIST P-256 domain parameters.
    private static readonly BigInteger P = Parse("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
    private static readonly BigInteger N = Parse("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
    private static readonly BigInteger A = P - 3;
    private static readonly BigInteger Gx = Parse("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296");
    private static readonly BigInteger Gy = Parse("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");

    /// <summary>Signs <paramref name="message"/> from the key shares, returning a 64-byte P-1363 (r‖s) signature.</summary>
    public static byte[] SignData(IReadOnlyList<byte[]> shares, ReadOnlySpan<byte> message)
    {
        var hash = SHA256.HashData(message.ToArray());
        return SignHash(shares, hash);
    }

    /// <summary>Signs a pre-computed 32-byte hash from the key shares.</summary>
    public static byte[] SignHash(IReadOnlyList<byte[]> shares, byte[] hash)
    {
        var e = ToScalar(hash);
        while (true)
        {
            var k = RandomScalar();
            var (rx, _) = Multiply(k, Gx, Gy);
            var r = rx % N;
            if (r.IsZero)
                continue;

            // acc = e + r·Σdᵢ (mod n), summed share by share so Σdᵢ is never formed on its own.
            var acc = e % N;
            foreach (var share in shares)
                acc = (acc + r * (ToScalar(share) % N)) % N;

            var s = ModInverse(k, N) * acc % N;
            if (s.IsZero)
                continue;

            var sig = new byte[64];
            WriteScalar(r, sig.AsSpan(0, 32));
            WriteScalar(s, sig.AsSpan(32, 32));
            return sig;
        }
    }

    /// <summary>The public key for a private scalar, as a DER SubjectPublicKeyInfo (what the server loads).</summary>
    public static byte[] PublicKeyFromScalar(BigInteger d)
    {
        var (x, y) = Multiply(d % N, Gx, Gy);
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = To32(x), Y = To32(y) },
        });
        return ecdsa.ExportSubjectPublicKeyInfo();
    }

    /// <summary>Splits a private scalar into <paramref name="count"/> shares that sum to it mod n.</summary>
    public static byte[][] Split(BigInteger d, int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));

        var shares = new byte[count][];
        var sum = BigInteger.Zero;
        for (var i = 0; i < count - 1; i++)
        {
            var share = RandomScalar();
            sum = (sum + share) % N;
            shares[i] = To32(share);
        }

        // The last share makes the total come out to d (mod n).
        var last = (d % N - sum % N + N) % N;
        shares[count - 1] = To32(last);
        return shares;
    }

    // --- scalar helpers ---

    private static BigInteger ToScalar(byte[] bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

    private static void WriteScalar(BigInteger value, Span<byte> dest) => To32(value).CopyTo(dest);

    private static byte[] To32(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == 32)
            return bytes;
        var padded = new byte[32];
        bytes.CopyTo(padded, 32 - bytes.Length);
        return padded;
    }

    private static BigInteger RandomScalar()
    {
        Span<byte> buffer = stackalloc byte[32];
        while (true)
        {
            RandomNumberGenerator.Fill(buffer);
            var value = new BigInteger(buffer, isUnsigned: true, isBigEndian: true);
            if (value >= BigInteger.One && value < N)
                return value;
        }
    }

    private static BigInteger ModInverse(BigInteger value, BigInteger modulus) =>
        BigInteger.ModPow(value % modulus, modulus - 2, modulus);

    // --- affine point arithmetic over P (one signature per launch, so plain and clear beats fast) ---

    private static (BigInteger X, BigInteger Y) Multiply(BigInteger scalar, BigInteger px, BigInteger py)
    {
        (BigInteger X, BigInteger Y)? result = null;
        var addend = (px, py);

        while (scalar > 0)
        {
            if (!(scalar & BigInteger.One).IsZero)
                result = result is null ? addend : Add(result.Value, addend);
            addend = Double(addend);
            scalar >>= 1;
        }

        return result ?? throw new InvalidOperationException("scalar was zero");
    }

    private static (BigInteger X, BigInteger Y) Double((BigInteger X, BigInteger Y) point)
    {
        var (x, y) = point;
        var lambda = (3 * x * x + A) % P * ModInverse(2 * y, P) % P;
        var rx = (lambda * lambda - 2 * x) % P;
        var ry = (lambda * (x - rx) - y) % P;
        return (Mod(rx, P), Mod(ry, P));
    }

    private static (BigInteger X, BigInteger Y) Add((BigInteger X, BigInteger Y) a, (BigInteger X, BigInteger Y) b)
    {
        if (a.X == b.X)
            return a.Y == b.Y ? Double(a) : throw new InvalidOperationException("point at infinity");

        var lambda = (b.Y - a.Y) % P * ModInverse((b.X - a.X + P) % P, P) % P;
        var rx = (lambda * lambda - a.X - b.X) % P;
        var ry = (lambda * (a.X - rx) - a.Y) % P;
        return (Mod(rx, P), Mod(ry, P));
    }

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var r = value % modulus;
        return r.Sign < 0 ? r + modulus : r;
    }

    private static BigInteger Parse(string hex) =>
        BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
}
