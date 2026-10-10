using LocalAgentPlatform.Shared.Kernel.Security;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public sealed class ProductionConfigurationPolicyTests
{
    [Fact]
    public void Development_DoesNotRequireProductionDeploymentSettings()
    {
        Assert.Empty(ProductionConfigurationPolicy.Validate(
            isDevelopment: true,
            allowedHosts: "*",
            dataProtectionKeyRingPath: null,
            allowedWorkspaceRoots: Array.Empty<string>()));
    }

    [Fact]
    public void Production_RejectsWildcardLoopbackEphemeralAndMissingWorkspaceSettings()
    {
        var errors = ProductionConfigurationPolicy.Validate(
            isDevelopment: false,
            allowedHosts: "localhost;127.0.0.1",
            dataProtectionKeyRingPath: "relative/keyring",
            allowedWorkspaceRoots: new[] { "" });

        Assert.Contains(errors, error => error.Contains("non-loopback hostname", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("persistent directory", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("existing Repositories:AllowedRoots", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_AcceptsConcreteHostPersistentPathAndExistingWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"lap-production-root-{Guid.NewGuid():N}");
        var keyRing = Path.Combine(Path.GetTempPath(), $"lap-dp-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(keyRing);
        try
        {
            Assert.Empty(ProductionConfigurationPolicy.Validate(
                isDevelopment: false,
                allowedHosts: "agent.example.test",
                dataProtectionKeyRingPath: keyRing,
                allowedWorkspaceRoots: new[] { workspace }));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(keyRing, recursive: true);
        }
    }

    [Theory]
    [InlineData("localhost:8443")]
    [InlineData("127.0.0.1:8443")]
    [InlineData("[::1]")]
    [InlineData("[::1]:8443")]
    public void Production_RejectsLoopbackHostsWithExplicitPorts(string host)
    {
        var errors = ProductionConfigurationPolicy.Validate(
            isDevelopment: false,
            allowedHosts: host,
            dataProtectionKeyRingPath: null,
            allowedWorkspaceRoots: Array.Empty<string>());

        Assert.Contains(errors, error => error.Contains("non-loopback hostname", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_RejectsAbsoluteButNonexistentDataProtectionDirectory()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"lap-production-root-{Guid.NewGuid():N}");
        var missingKeyRing = Path.Combine(Path.GetTempPath(), $"lap-dp-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var errors = ProductionConfigurationPolicy.Validate(
                isDevelopment: false,
                allowedHosts: "agent.example.test",
                dataProtectionKeyRingPath: missingKeyRing,
                allowedWorkspaceRoots: new[] { workspace });

            Assert.Contains(errors, error => error.Contains("existing absolute persistent directory", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Production_RejectsFilesystemRootAsWorkspace()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath(Path.DirectorySeparatorChar.ToString()))!;
        var errors = ProductionConfigurationPolicy.Validate(
            isDevelopment: false,
            allowedHosts: "agent.example.test",
            dataProtectionKeyRingPath: Path.Combine(Path.GetTempPath(), "lap-dp-keys"),
            allowedWorkspaceRoots: new[] { filesystemRoot });

        Assert.Contains(errors, error => error.Contains("non-filesystem-root", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_RejectsAnyInvalidEntryEvenWhenAnotherWorkspaceIsUsable()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"lap-production-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var errors = ProductionConfigurationPolicy.Validate(
                isDevelopment: false,
                allowedHosts: "agent.example.test",
                dataProtectionKeyRingPath: Path.Combine(Path.GetTempPath(), "lap-dp-keys"),
                allowedWorkspaceRoots: new[] { workspace, Path.Combine(workspace, "missing") });

            Assert.Contains(errors, error => error.Contains("Every production", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
