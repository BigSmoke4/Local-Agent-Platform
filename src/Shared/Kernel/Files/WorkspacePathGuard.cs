using System.Runtime.InteropServices;

namespace LocalAgentPlatform.Shared.Kernel.Files;

/// <summary>
/// Canonical workspace containment checks shared by file tools, repository indexing,
/// context selection, and verification. Existing symbolic links/reparse points are
/// resolved before containment is checked. This is application-level defense in depth;
/// high-assurance hostile-code execution still needs an OS sandbox.
/// </summary>
public static class WorkspacePathGuard
{
    /// <summary>The root directory itself must exist as a real directory; a final
    /// symlink/junction, device, or missing path is not a valid workspace root. Symlinked
    /// ancestors are canonicalized separately for containment so conventional OS paths
    /// such as macOS's /tmp alias remain usable.</summary>
    public static bool IsSafeWorkspaceRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath)) return false;
            var attributes = File.GetAttributes(fullPath);
            return (attributes & FileAttributes.Directory) != 0 &&
                   (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Resolves an existing safe directory root to its physical path for an
    /// allowlist snapshot. The final directory itself may not be a link, but harmless
    /// symlinked ancestors are resolved so later comparisons are stable.</summary>
    public static string? ResolveSafeWorkspaceRoot(string path)
    {
        if (!IsSafeWorkspaceRoot(path)) return null;
        try
        {
            var resolved = ResolveExistingSegments(Path.GetFullPath(path));
            return resolved is not null && IsSafeWorkspaceRoot(resolved) ? Path.GetFullPath(resolved) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                return false;
            return !OperatingSystem.IsLinux() || IsLinuxRegularFile(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsLinuxRegularFile(string path)
    {
        // FileAttributes does not consistently distinguish Unix FIFOs/sockets/devices.
        // statx with AT_SYMLINK_NOFOLLOW provides the actual file type; an unavailable
        // syscall fails closed rather than opening a special file that could block.
        const int atFdcwd = -100;
        const int atSymlinkNoFollow = 0x100;
        const uint statxType = 0x1;
        const ushort sIfmt = 0xF000;
        const ushort sIfreg = 0x8000;
        const int statxBufferSize = 256;
        const int stxModeOffset = 28;

        var buffer = Marshal.AllocHGlobal(statxBufferSize);
        try
        {
            // The kernel fills struct statx on success, but zero it first so a partial
            // or unexpectedly old implementation cannot make stale allocator bytes look
            // like a regular-file mode.
            for (var i = 0; i < statxBufferSize; i++) Marshal.WriteByte(buffer, i, 0);
            if (NativeStatx(atFdcwd, path, atSymlinkNoFollow, statxType, buffer) != 0) return false;
            var returnedMask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            if ((returnedMask & statxType) == 0) return false;
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, stxModeOffset));
            return (mode & sIfmt) == sIfreg;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int NativeStatx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        IntPtr buffer);

    public static bool IsWithinWorkspace(string workspaceRootPath, string requestedPath)
    {
        try
        {
            var workspace = Path.GetFullPath(workspaceRootPath);
            var requested = Path.GetFullPath(Path.IsPathRooted(requestedPath)
                ? requestedPath
                : Path.Combine(workspaceRootPath, requestedPath));

            // Resolve existing segments for both paths, not only the requested suffix.
            // This catches a symlinked workspace root and dangling links, which
            // File.Exists/Directory.Exists report as absent and could otherwise be
            // followed by a later create operation.
            var resolvedWorkspace = ResolveExistingSegments(workspace);
            var resolvedRequested = ResolveExistingSegments(requested);
            if (resolvedWorkspace is null || resolvedRequested is null) return false;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var filesystemRoot = Path.GetPathRoot(resolvedWorkspace);
            if (!string.Equals(resolvedWorkspace, filesystemRoot, comparison))
                resolvedWorkspace = resolvedWorkspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var prefix = resolvedWorkspace.EndsWith(Path.DirectorySeparatorChar) || resolvedWorkspace.EndsWith(Path.AltDirectorySeparatorChar)
                ? resolvedWorkspace
                : resolvedWorkspace + Path.DirectorySeparatorChar;
            return resolvedRequested.Equals(resolvedWorkspace, comparison) ||
                   resolvedRequested.StartsWith(prefix, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? ResolveExistingSegments(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return null;

        var segments = fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = Path.Combine(current, segments[i]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(candidate);
            }
            catch (FileNotFoundException)
            {
                // A dangling symlink can also produce FileNotFoundException. Inspect
                // LinkTarget directly before treating this as an ordinary missing path.
                var linkResolution = ResolveLink(candidate);
                if (linkResolution.IsLink)
                {
                    if (linkResolution.TargetPath is null) return null;
                    current = linkResolution.TargetPath;
                    continue;
                }
                return AppendMissingSegments(current, segments, i);
            }
            catch (DirectoryNotFoundException)
            {
                var linkResolution = ResolveLink(candidate);
                if (linkResolution.IsLink)
                {
                    if (linkResolution.TargetPath is null) return null;
                    current = linkResolution.TargetPath;
                    continue;
                }
                return AppendMissingSegments(current, segments, i);
            }

            var info = (attributes & FileAttributes.Directory) != 0
                ? (FileSystemInfo)new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || !string.IsNullOrEmpty(info.LinkTarget))
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) return null;
                current = Path.GetFullPath(target.FullName);
            }
            else
            {
                current = candidate;
            }
        }

        return Path.GetFullPath(current);
    }

    private static (bool IsLink, string? TargetPath) ResolveLink(string path)
    {
        foreach (FileSystemInfo info in new FileSystemInfo[] { new FileInfo(path), new DirectoryInfo(path) })
        {
            string? linkTarget;
            try { linkTarget = info.LinkTarget; }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            if (string.IsNullOrEmpty(linkTarget)) continue;
            try
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                return (true, target is null ? null : Path.GetFullPath(target.FullName));
            }
            catch (FileNotFoundException) { return (true, null); }
            catch (DirectoryNotFoundException) { return (true, null); }
        }
        return (false, null);
    }

    private static string AppendMissingSegments(string current, IReadOnlyList<string> segments, int startIndex)
    {
        for (var i = startIndex; i < segments.Count; i++)
            current = Path.Combine(current, segments[i]);
        return Path.GetFullPath(current);
    }
}
