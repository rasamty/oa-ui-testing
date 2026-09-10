using System.Security.Cryptography;
using System.Text;

namespace BuildingBlocks.Auth.Totp;

/// <summary>
/// Time-based one-time passwords, RFC 6238 (the 6-digit codes an authenticator
/// app shows). Implemented directly so the library carries no extra dependency.
/// HMAC-SHA1, 30-second steps, 6 digits — the settings every authenticator app
/// assumes by default.
/// </summary>
public sealed class TotpService
{
    private const int Digits = 6;
    private const int PeriodSeconds = 30;

    /// <summary>A fresh random secret, Base32-encoded (what goes in the QR code and the DB).</summary>
    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20); // 160-bit, RFC 4226 recommendation
        return Base32Encode(bytes);
    }

    /// <summary>
    /// The <c>otpauth://</c> URI an authenticator app scans. Render it as a QR on the page.
    /// </summary>
    public string BuildOtpAuthUri(string issuer, string accountName, string base32Secret)
    {
        var iss = Uri.EscapeDataString(issuer);
        var acc = Uri.EscapeDataString(accountName);
        return $"otpauth://totp/{iss}:{acc}?secret={base32Secret}&issuer={iss}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    /// <summary>
    /// True if <paramref name="code"/> is valid for <paramref name="base32Secret"/> right now.
    /// <paramref name="stepSkew"/> allows codes from that many 30s windows either side, to
    /// tolerate clock drift (1 = accept the previous, current and next code).
    /// </summary>
    public bool Verify(string base32Secret, string code, int stepSkew = 1, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits) return false;
        code = code.Trim();

        byte[] key;
        try { key = Base32Decode(base32Secret); }
        catch { return false; }

        var counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / PeriodSeconds;
        for (var w = -stepSkew; w <= stepSkew; w++)
        {
            if (FixedTimeEquals(Compute(key, counter + w), code)) return true;
        }
        return false;
    }

    /// <summary>The current code for a secret — used by tests and by "show me a code" tooling, never in the login path.</summary>
    public string ComputeCurrent(string base32Secret, DateTimeOffset? now = null)
    {
        var counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / PeriodSeconds;
        return Compute(Base32Decode(base32Secret), counter);
    }

    private static string Compute(byte[] key, long counter)
    {
        Span<byte> ctr = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(ctr, counter);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, ctr, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString().PadLeft(Digits, '0');
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    // ---- Base32 (RFC 4648, no padding on output; tolerant on input) ----

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var bt in data)
        {
            buffer = (buffer << 8) | bt;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        input = input.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var ch in input)
        {
            var idx = Alphabet.IndexOf(ch);
            if (idx < 0) throw new FormatException("not Base32");
            buffer = (buffer << 5) | idx;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }
}
