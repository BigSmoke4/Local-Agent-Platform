using LocalAgentPlatform.Shared.Kernel.Security;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public sealed class BootstrapTokenVerifierTests
{
    [Fact]
    public void Verify_AcceptsMatchingHighEntropyConfiguredToken()
    {
        const string token = "c2737a24f4496eaa54dcba19e94d8f3b8cbe667dce2a441c34b0d831325496ee";

        Assert.True(BootstrapTokenVerifier.Verify(token, token));
    }

    [Theory]
    [InlineData(null, "c2737a24f4496eaa54dcba19e94d8f3b8cbe667dce2a441c34b0d831325496ee")]
    [InlineData("", "c2737a24f4496eaa54dcba19e94d8f3b8cbe667dce2a441c34b0d831325496ee")]
    [InlineData("short", "short")]
    [InlineData("c2737a24f4496eaa54dcba19e94d8f3b8b", "c2737a24f4496eaa54dcba19e94d8f3b8c")]
    public void Verify_RejectsMissingShortOrIncorrectTokens(string? configured, string? supplied)
    {
        Assert.False(BootstrapTokenVerifier.Verify(configured, supplied));
    }

    [Fact]
    public void Verify_DoesNotTrimOrAcceptOverlongInput()
    {
        const string token = "c2737a24f4496eaa54dcba19e94d8f3b8cbe667dce2a441c34b0d831325496ee";

        Assert.False(BootstrapTokenVerifier.Verify(token, $" {token}"));
        Assert.False(BootstrapTokenVerifier.Verify(token, new string('x', 513)));
    }
}
