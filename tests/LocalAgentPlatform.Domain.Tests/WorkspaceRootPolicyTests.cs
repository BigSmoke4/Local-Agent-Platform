using System.Runtime.InteropServices;
using LocalAgentPlatform.Shared.Kernel.Files;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public sealed class WorkspaceRootPolicyTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "lap-workspace-policy-" + Guid.NewGuid().ToString("N"));
    private readonly string _allowedRoot;

    public WorkspaceRootPolicyTests()
    {
        _allowedRoot = Path.Combine(_sandbox, "workspace");
        Directory.CreateDirectory(_allowedRoot);
    }

    [Fact]
    public void Empty_allowlist_denies_every_repository()
    {
        var policy = new WorkspaceRootPolicy(Array.Empty<string>());
        Assert.False(policy.IsAllowed(_allowedRoot));
    }

    [Fact]
    public void Allowlist_accepts_descendants_but_not_sibling_prefixes_or_parent_paths()
    {
        var policy = new WorkspaceRootPolicy(new[] { _allowedRoot });
        var repository = Path.Combine(_allowedRoot, "project");
        Directory.CreateDirectory(repository);
        var similarlyNamedSibling = _allowedRoot + "-private";
        var outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(similarlyNamedSibling);
        Directory.CreateDirectory(outside);

        Assert.True(policy.IsAllowed(repository));
        Assert.True(policy.IsAllowed(_allowedRoot));
        Assert.False(policy.IsAllowed(similarlyNamedSibling));
        Assert.False(policy.IsAllowed(outside));
        Assert.False(policy.IsAllowed(Path.Combine(_allowedRoot, "missing-repository")));
    }

    [Fact]
    public void Nonexistent_allowlist_roots_are_not_authorized()
    {
        var policy = new WorkspaceRootPolicy(new[] { Path.Combine(_sandbox, "missing-allowlist") });
        Assert.Empty(policy.AllowedRoots);
        Assert.False(policy.IsAllowed(_allowedRoot));
    }

    [Fact]
    public void Symlinked_allowlist_roots_fail_closed()
    {
        var target = Path.Combine(_sandbox, "real-allowlist");
        var linkedRoot = Path.Combine(_sandbox, "linked-allowlist");
        var repository = Path.Combine(target, "repository");
        Directory.CreateDirectory(repository);
        try { Directory.CreateSymbolicLink(linkedRoot, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Some Windows/CI hosts disable unprivileged symlink creation.
        }

        var policy = new WorkspaceRootPolicy(new[] { linkedRoot });
        Assert.False(policy.IsAllowed(repository));
    }

    [Fact]
    public void Symlinked_ancestor_of_allowlist_is_canonicalized_before_containment_checks()
    {
        var realParent = Path.Combine(_sandbox, "real-parent");
        var linkedParent = Path.Combine(_sandbox, "linked-parent");
        var realRoot = Path.Combine(realParent, "workspace");
        var linkedRoot = Path.Combine(linkedParent, "workspace");
        Directory.CreateDirectory(realRoot);
        try { Directory.CreateSymbolicLink(linkedParent, realParent); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var policy = new WorkspaceRootPolicy(new[] { linkedRoot });
        Assert.True(policy.IsAllowed(realRoot));
        Assert.Equal(WorkspacePathGuard.ResolveSafeWorkspaceRoot(realRoot), Assert.Single(policy.AllowedRoots));
    }

    [Fact]
    public void Repository_root_with_a_symlinked_ancestor_is_canonicalized_for_containment()
    {
        var repository = Path.Combine(_allowedRoot, "repository-via-link");
        var linkedParent = Path.Combine(_sandbox, "linked-workspace");
        Directory.CreateDirectory(repository);
        try { Directory.CreateSymbolicLink(linkedParent, _allowedRoot); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var policy = new WorkspaceRootPolicy(new[] { _allowedRoot });
        Assert.True(policy.IsAllowed(Path.Combine(linkedParent, "repository-via-link")));
    }

    [Fact]
    public void Repository_root_symlink_is_denied_even_when_its_target_is_inside_the_allowlist()
    {
        var target = Path.Combine(_allowedRoot, "real-repository");
        var link = Path.Combine(_allowedRoot, "linked-repository");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Some Windows/CI hosts disable unprivileged symlink creation.
        }

        var policy = new WorkspaceRootPolicy(new[] { _allowedRoot });
        Assert.False(policy.IsAllowed(link));
    }

    [Fact]
    public void Repository_root_symlink_to_outside_target_is_denied()
    {
        var outside = Path.Combine(_sandbox, "outside");
        var link = Path.Combine(_allowedRoot, "linked-repo");
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Some Windows/CI hosts disable unprivileged symlink creation.
        }

        var policy = new WorkspaceRootPolicy(new[] { _allowedRoot });
        Assert.False(policy.IsAllowed(link));
    }

    [Fact]
    public void Dangling_repository_symlink_to_outside_is_denied()
    {
        var outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_allowedRoot, "dangling-repo");
        var missingTarget = Path.Combine(outside, "not-created");
        try { Directory.CreateSymbolicLink(link, missingTarget); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var policy = new WorkspaceRootPolicy(new[] { _allowedRoot });
        Assert.False(policy.IsAllowed(Path.Combine(link, "repo")));
    }

    [Fact]
    public void Linux_fifo_is_not_a_regular_file()
    {
        if (!OperatingSystem.IsLinux()) return;

        var fifo = Path.Combine(_allowedRoot, "pipe");
        Assert.Equal(0, NativeMkfifo(fifo, 0x180)); // 0600
        Assert.False(WorkspacePathGuard.IsRegularFile(fifo));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int NativeMkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    public void Dispose()
    {
        if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true);
    }
}
