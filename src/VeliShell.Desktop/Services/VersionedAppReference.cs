using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace VeliShell.Desktop.Services;

/// <summary>An executable within one installation, never a basename-only app guess.</summary>
internal sealed record VersionedAppReference(string Root, string ExecutableName)
{
    private static readonly Regex VersionDirectory = new(@"^app-(?<version>\d+(?:\.\d+){1,3})(?:-(?<pre>[a-z0-9.-]+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly ConcurrentDictionary<string, (long Until, VersionedAppReference? Reference)> RootLaunchers =
        new(StringComparer.OrdinalIgnoreCase);

    internal string RootExecutable => Path.Combine(Root, ExecutableName);
    private bool IsValid => Path.IsPathFullyQualified(Root) &&
        !string.Equals(Root.TrimEnd('\\'), Path.GetPathRoot(Root)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
        ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        ExecutableName.IndexOfAny(['\\', '/', ':', '"']) < 0 && !ExecutableName.Any(char.IsControl);

    internal static VersionedAppReference? FromExecutable(string? target)
    {
        var path = AppIdentityService.NormalizeExecutable(target);
        if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !TryVersion(Path.GetFileName(directory), out _, out _)) return null;
        var root = Path.GetDirectoryName(directory);
        return root is null ? null : new(root, Path.GetFileName(path));
    }

    internal static VersionedAppReference? FromRootLauncher(string? target)
    {
        var path = AppIdentityService.NormalizeExecutable(target);
        if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var now = Environment.TickCount64;
        if (RootLaunchers.TryGetValue(path, out var cached) && now < cached.Until) return cached.Reference;
        var root = Path.GetDirectoryName(path);
        VersionedAppReference? result = null;
        if (root is not null && IsPlainDirectory(root))
        {
            var possible = new VersionedAppReference(root, Path.GetFileName(path));
            // A generic updater or another installed app with the same name is
            // not sufficient. Require the exact versioned executable in this root.
            if (IsPlainFile(path) && possible.FindCurrentExecutable() is not null) result = possible;
        }
        if (RootLaunchers.Count >= 128) RootLaunchers.Clear();
        RootLaunchers[path] = (now + 2000, result);
        return result;
    }

    internal bool Matches(string? executable)
    {
        if (!IsValid) return false;
        var path = AppIdentityService.NormalizeExecutable(executable);
        if (path is null) return false;
        if (path.Equals(RootExecutable, StringComparison.OrdinalIgnoreCase)) return true;
        var other = FromExecutable(path);
        return other is not null && Root.Equals(other.Root, StringComparison.OrdinalIgnoreCase) &&
            ExecutableName.Equals(other.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    internal bool SameApp(VersionedAppReference other) => Root.Equals(other.Root, StringComparison.OrdinalIgnoreCase) &&
        ExecutableName.Equals(other.ExecutableName, StringComparison.OrdinalIgnoreCase);

    internal string? FindCurrentExecutable()
    {
        if (!IsValid || !IsPlainDirectory(Root)) return null;
        var candidates = new List<(Version Version, bool Stable, string Path)>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(Root, "app-*").Take(128))
            {
                if (!IsPlainDirectory(directory) || !TryVersion(Path.GetFileName(directory), out var version, out var stable)) continue;
                var executable = Path.Combine(directory, ExecutableName);
                if (IsPlainFile(executable)) candidates.Add((version, stable, executable));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        return candidates.OrderByDescending(candidate => candidate.Version).ThenByDescending(candidate => candidate.Stable)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).Select(candidate => candidate.Path).FirstOrDefault();
    }

    internal static bool IsPlainFile(string path) => HasPlainAttributes(path, directory: false);
    internal static bool IsPlainDirectory(string path) => HasPlainAttributes(path, directory: true);

    private static bool HasPlainAttributes(string path, bool directory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) == 0 &&
                ((attributes & FileAttributes.Directory) != 0) == directory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static bool TryVersion(string name, out Version version, out bool stable)
    {
        version = new Version(0, 0); stable = false;
        try
        {
            var match = VersionDirectory.Match(name);
            stable = !match.Groups["pre"].Success;
            return match.Success && Version.TryParse(match.Groups["version"].Value, out version!);
        }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
