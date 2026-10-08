using Xunit;

namespace LocalAgentPlatform.LiveE2E.Fixture;

public sealed class DisposableWorkspaceTests
{
    [Fact]
    public void ExplicitDisposableWorkspaceMarkerIsPresent()
    {
        var markerPath = Path.Combine(AppContext.BaseDirectory, ".lap-live-e2e-disposable");
        var marker = File.ReadAllText(markerPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal("LOCAL AGENT PLATFORM LIVE E2E DISPOSABLE WORKSPACE\n", marker);
    }
}
