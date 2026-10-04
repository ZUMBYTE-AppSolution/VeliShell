using System.IO;
using System.Security;

namespace VeliShell.Desktop.Services;

internal sealed record FolderBrowserEntry(
    string Name,
    string Path,
    bool IsDirectory,
    bool IsNavigable,
    bool IsOpenable);

internal sealed record FolderBrowserResult(
    IReadOnlyList<FolderBrowserEntry> Entries,
    bool IsTruncated,
    string? ErrorKey = null);

internal static class FolderBrowserService
{
    internal const int MaximumEntries = 120;
    internal const int MaximumDepth = 16;
    internal const int MaximumPathLength = 32767;

    internal static bool TryCreateRoot(string? path, out string root)
    {
        root = "";
        if (!TryNormalize(path, out var normalized) || !Directory.Exists(normalized)) return false;
        try
        {
            var attributes = File.GetAttributes(normalized);
            // A junction or symbolic-link root could make a string-confined
            // child resolve outside the folder the user pinned. Explorer can
            // still open such locations; the in-dock navigator stays literal.
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            root = Path.TrimEndingDirectorySeparator(normalized);
            return root.Length > 0;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return false;
        }
    }

    internal static bool TryResolveLocation(string root, string? candidate, out string location)
    {
        location = "";
        if (!TryNormalize(root, out var normalizedRoot) || !TryNormalize(candidate, out var normalizedCandidate) ||
            !IsWithinRoot(normalizedRoot, normalizedCandidate) ||
            RelativeDepth(normalizedRoot, normalizedCandidate) > MaximumDepth ||
            !HasSafeDirectoryChain(normalizedRoot, normalizedCandidate) ||
            !Directory.Exists(normalizedCandidate))
            return false;
        location = Path.TrimEndingDirectorySeparator(normalizedCandidate);
        return true;
    }

    internal static bool IsWithinRoot(string root, string candidate)
    {
        if (!TryNormalize(root, out var normalizedRoot) || !TryNormalize(candidate, out var normalizedCandidate))
            return false;
        normalizedRoot = Path.TrimEndingDirectorySeparator(normalizedRoot);
        normalizedCandidate = Path.TrimEndingDirectorySeparator(normalizedCandidate);
        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static Task<FolderBrowserResult> EnumerateAsync(
        string root,
        string location,
        CancellationToken cancellationToken) =>
        Task.Run(() => Enumerate(root, location, cancellationToken), cancellationToken);

    internal static bool CanOpenEntry(
        string root,
        string currentLocation,
        FolderBrowserEntry entry)
    {
        if (!entry.IsOpenable || !IsWithinRoot(root, entry.Path)) return false;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(entry.Path));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(currentLocation),
                Path.TrimEndingDirectorySeparator(parent ?? ""), StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            if (parent is null || !HasSafeDirectoryChain(root, parent)) return false;
            var attributes = File.GetAttributes(entry.Path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            return entry.IsDirectory
                ? (attributes & FileAttributes.Directory) != 0
                : (attributes & FileAttributes.Directory) == 0;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static FolderBrowserResult Enumerate(
        string root,
        string location,
        CancellationToken cancellationToken)
    {
        if (!TryResolveLocation(root, location, out var safeLocation))
            return new FolderBrowserResult([], false, "FolderPopover.Unavailable");

        var entries = new List<FolderBrowserEntry>(MaximumEntries);
        var truncated = false;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            };
            foreach (var path in Directory.EnumerateFileSystemEntries(safeLocation, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= MaximumEntries)
                {
                    truncated = true;
                    break;
                }
                if (!TryCreateEntry(root, safeLocation, path, out var entry)) continue;
                entries.Add(entry);
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            App.Log("Could not enumerate a pinned folder", exception);
            return new FolderBrowserResult([], false,
                exception is UnauthorizedAccessException or SecurityException
                    ? "FolderPopover.AccessDenied"
                    : "FolderPopover.Unavailable");
        }

        entries.Sort((left, right) =>
        {
            var kind = right.IsDirectory.CompareTo(left.IsDirectory);
            return kind != 0 ? kind : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        });
        return new FolderBrowserResult(entries, truncated);
    }

    private static bool TryCreateEntry(
        string root,
        string currentLocation,
        string path,
        out FolderBrowserEntry entry)
    {
        entry = default!;
        if (!TryNormalize(path, out var normalized) || !IsWithinRoot(root, normalized)) return false;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(normalized));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(currentLocation),
                Path.TrimEndingDirectorySeparator(parent ?? ""), StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var attributes = File.GetAttributes(normalized);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized));
            if (string.IsNullOrWhiteSpace(name)) return false;
            var navigable = isDirectory && !isReparsePoint &&
                            RelativeDepth(root, normalized) <= MaximumDepth;
            entry = new FolderBrowserEntry(name, normalized, isDirectory, navigable, !isReparsePoint);
            return true;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static int RelativeDepth(string root, string location)
    {
        var relative = Path.GetRelativePath(root, location);
        return relative == "."
            ? 0
            : relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static bool HasSafeDirectoryChain(string root, string location)
    {
        if (!TryNormalize(root, out var normalizedRoot) ||
            !TryNormalize(location, out var normalizedLocation) ||
            !IsWithinRoot(normalizedRoot, normalizedLocation))
            return false;
        try
        {
            if (!IsPlainDirectory(normalizedRoot)) return false;
            var relative = Path.GetRelativePath(normalizedRoot, normalizedLocation);
            if (relative == ".") return true;

            var cursor = normalizedRoot;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, segment);
                if (!IsPlainDirectory(cursor)) return false;
            }
            return true;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static bool IsPlainDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.Directory) != 0 &&
               (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || path.IndexOf('\0') >= 0)
            return false;
        try
        {
            normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            return Path.IsPathFullyQualified(normalized) && normalized.Length <= MaximumPathLength;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                          PathTooLongException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsExpectedFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException;
}
