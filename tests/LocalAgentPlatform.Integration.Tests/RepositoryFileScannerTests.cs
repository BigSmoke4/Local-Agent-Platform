using System.Runtime.InteropServices;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

public sealed class RepositoryFileScannerTests
{
    [Fact]
    public void Scanner_rejects_a_repository_root_that_is_a_symbolic_link()
    {
        var target = Directory.CreateTempSubdirectory("lap-scan-root-target-");
        var parent = Directory.CreateTempSubdirectory("lap-scan-root-link-");
        var link = Path.Combine(parent.FullName, "linked-root");
        try
        {
            try { Directory.CreateSymbolicLink(link, target.FullName); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return; // Some Windows/CI hosts disable unprivileged symlink creation.
            }
            var scanner = new RepositoryFileScanner();
            Assert.Throws<IOException>(() => scanner.EnumeratePaths(link).ToList());
        }
        finally
        {
            parent.Delete(recursive: true);
            target.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Indexing_does_not_follow_a_symlinked_directory()
    {
        var root = Directory.CreateTempSubdirectory("lap-scan-root-");
        var outside = Directory.CreateTempSubdirectory("lap-scan-outside-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "inside.cs"), "class Inside {}");
            await File.WriteAllTextAsync(Path.Combine(outside.FullName, "secret.cs"), "class Secret {}");
            if (!TryCreateDirectoryLink(Path.Combine(root.FullName, "linked"), outside.FullName)) return;

            var scanner = new RepositoryFileScanner();
            var paths = new List<string>();
            await foreach (var file in scanner.ScanAsync(root.FullName)) paths.Add(file.RelativePath);

            Assert.Contains("inside.cs", paths);
            Assert.DoesNotContain(paths, path => path.Contains("secret.cs", StringComparison.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void Strict_security_enumeration_fails_if_a_symlinked_directory_makes_coverage_ambiguous()
    {
        var root = Directory.CreateTempSubdirectory("lap-scan-strict-root-");
        var outside = Directory.CreateTempSubdirectory("lap-scan-strict-outside-");
        try
        {
            if (!TryCreateDirectoryLink(Path.Combine(root.FullName, "linked"), outside.FullName)) return;
            var scanner = new RepositoryFileScanner();
            Assert.Throws<InvalidOperationException>(() => scanner.EnumeratePaths(root.FullName).ToList());
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void Strict_security_enumeration_rejects_a_linux_fifo_as_incomplete_coverage()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("lap-scan-fifo-");
        try
        {
            var fifo = Path.Combine(root.FullName, "pipe.cs");
            Assert.Equal(0, NativeMkfifo(fifo, 0x180)); // 0600
            var scanner = new RepositoryFileScanner();
            var exception = Assert.Throws<InvalidOperationException>(() => scanner.EnumeratePaths(root.FullName).ToList());
            Assert.Contains("special file", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { root.Delete(recursive: true); }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int NativeMkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
