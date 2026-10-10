using LocalAgentPlatform.Shared.Kernel.Security;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("api_key=supersecretvalue", "supersecretvalue")]
    [InlineData("password: \"a value with spaces\"", "a value with spaces")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("postgres://user:secret@localhost/db", "secret")]
    public void Redacts_common_credential_forms(string input, string secret)
    {
        var redacted = SecretRedactor.Redact(input);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_multiline_private_keys()
    {
        var privateKey = string.Join(Environment.NewLine,
            "-----BEGIN RSA PRIVATE KEY-----", "private-key-material", "-----END RSA PRIVATE KEY-----");
        var redacted = SecretRedactor.Redact("before" + Environment.NewLine + privateKey + Environment.NewLine + "after");
        Assert.DoesNotContain("private-key-material", redacted, StringComparison.Ordinal);
        Assert.Contains("[PRIVATE KEY REDACTED]", redacted, StringComparison.Ordinal);
    }
}
