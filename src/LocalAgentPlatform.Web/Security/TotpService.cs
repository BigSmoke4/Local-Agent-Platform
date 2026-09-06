using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LocalAgentPlatform.Web.Security;

public sealed class TotpService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret(int bytes = 20)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        return Base32Encode(data);
    }

    public bool Verify(string base32Secret, string code, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsDigit)) return false;
        var secret = Base32Decode(base32Secret);
        var counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        for (var offset = -1; offset <= 1; offset++)
            if (FixedTimeEquals(Compute(secret, counter + offset), code)) return true;
        return false;
    }

    private static string Compute(byte[] secret, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));

    private static string Base32Encode(byte[] data)
    {
        var output = new System.Text.StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) output.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string text)
    {
        int buffer = 0, bitsLeft = 0;
        var bytes = new List<byte>();
        foreach (var ch in text.Trim().TrimEnd('=').ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(ch);
            if (value < 0) throw new FormatException("Invalid Base32 MFA secret.");
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 255));
                bitsLeft -= 8;
            }
        }
        return bytes.ToArray();
    }
}
