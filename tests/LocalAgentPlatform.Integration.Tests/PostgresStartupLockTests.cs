using LocalAgentPlatform.Shared.Data;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

[Collection("Postgres")]
public sealed class PostgresStartupLockTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresStartupLockTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StartupLock_SerializesIndependentContexts_AndReleasesAfterFailure()
    {
        await using var first = _fixture.CreateContext();
        await using var second = _fixture.CreateContext();
        var active = 0;
        var maximumConcurrentActions = 0;

        async Task StartupWork(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumConcurrentActions, current);
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(
            PostgresStartupLock.ExecuteAsync(first, StartupWork),
            PostgresStartupLock.ExecuteAsync(second, StartupWork));

        Assert.Equal(1, maximumConcurrentActions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgresStartupLock.ExecuteAsync(
            first,
            static _ => Task.FromException(new InvalidOperationException("startup fixture failure"))));
        await PostgresStartupLock.ExecuteAsync(second, static _ => Task.CompletedTask);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (observed >= candidate) return;
        } while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
    }
}
