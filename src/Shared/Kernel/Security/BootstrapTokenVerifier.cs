using System.Security.Cryptography;
using System.Text;

namespace LocalAgentPlatform.Shared.Kernel.Security;

/// <summary>Verifies the one-time deployment secret used to bootstrap the first
/// production administrator. The token is never persisted or emitted in diagnostics.</summary>
public static class BootstrapTokenVerifier
{
    private const int MinimumTokenBytes = 32;
    private const int MaximumInputCharacters = 512;

    public static bool Verify(string? configuredToken, string? suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(configuredToken) ||
            string.IsNullOrWhiteSpace(suppliedToken) ||
            configuredToken.Length > MaximumInputCharacters ||
            suppliedToken.Length > MaximumInputCharacters)
            return false;

        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        if (configuredBytes.Length < MinimumTokenBytes) return false;

        var configuredDigest = SHA256.HashData(configuredBytes);
        var suppliedDigest = SHA256.HashData(suppliedBytes);
        return CryptographicOperations.FixedTimeEquals(configuredDigest, suppliedDigest);
    }
}
