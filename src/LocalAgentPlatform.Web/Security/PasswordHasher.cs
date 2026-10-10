using System.Security.Cryptography;

namespace LocalAgentPlatform.Web.Security;

/// <summary>
/// PBKDF2-SHA256 password hashing with a random salt. New hashes use 600,000
/// iterations; the iteration count is embedded so existing 100,000-iteration hashes
/// remain verifiable. Verify rejects malformed or unreasonable stored parameters before
/// doing expensive work.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const int MinimumAcceptedIterations = 100_000;
    private const int MaximumAcceptedIterations = 2_000_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string storedHash)
    {
        if (password is null || string.IsNullOrWhiteSpace(storedHash) || storedHash.Length > 256) return false;
        try
        {
            var parts = storedHash.Split('.');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations) ||
                iterations < MinimumAcceptedIterations || iterations > MaximumAcceptedIterations)
                return false;

            var salt = Convert.FromBase64String(parts[1]);
            var expectedKey = Convert.FromBase64String(parts[2]);
            if (salt.Length != SaltSize || expectedKey.Length != KeySize) return false;

            var actualKey = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, expectedKey.Length);
            return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
