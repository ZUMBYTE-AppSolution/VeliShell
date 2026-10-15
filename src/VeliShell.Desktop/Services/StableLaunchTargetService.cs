using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VeliShell.Desktop.Services;

/// <summary>Finds a persistent shell link before an updater-owned executable is pinned.</summary>
internal static class StableLaunchTargetService
{
    private static readonly string[] BlockedSchemes =
        ["file", "http", "https", "javascript", "data", "shell", "ms-msdt"];

    internal static bool IsRegisteredProtocol(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 2048 ||
            target.Any(char.IsControl) || !Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            BlockedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(uri.Scheme);
            return key?.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException) { return false; }
    }

    internal static string PreferShortcut(string executable, string? displayName = null)
    {
        if (!Path.IsPathFullyQualified(executable) ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return executable;
        if (PreferPackagedIdentity(executable) is { } packagedTarget) return packagedTarget;
        var basename = Path.GetFileNameWithoutExtension(executable);
        var matchingName = displayName?.Trim();
        string? matchingExecutable = null;
        foreach (var path in EnumerateShortcuts())
        {
            var link = ShellLinkService.Read(path);
            if (link is null || link.IsWebApp) continue;
            if (string.Equals(link.Target, executable, StringComparison.OrdinalIgnoreCase)) return path;
            // Only fall back to the executable's stable name when it is also
            // supported by the shortcut's display name. "Update.exe" must
            // never bind a pin to a different application's updater.
            if (matchingExecutable is null && basename.Length >= 5 &&
                string.Equals(Path.GetFileNameWithoutExtension(link.Target), basename,
                    StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(matchingName) ||
                 Path.GetFileNameWithoutExtension(path).Contains(matchingName,
                     StringComparison.OrdinalIgnoreCase) ||
                 matchingName.Contains(Path.GetFileNameWithoutExtension(path),
                     StringComparison.OrdinalIgnoreCase)))
                matchingExecutable = path;
        }
        return matchingExecutable ?? executable;
    }

    internal static string? PreferPackagedIdentity(string executable)
    {
        var family = PackageFamilyFromPath(executable);
        if (family is null) return null;
        Process[] processes;
        try { processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        { return null; }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var runningPath = WindowCatalog.GetProcessPath(checked((uint)process.Id));
                    if (!string.Equals(family, PackageFamilyFromPath(runningPath),
                            StringComparison.OrdinalIgnoreCase)) continue;
                    var identity = PackagedAppService.ForProcess(checked((uint)process.Id));
                    if (identity is not null) return identity.Target;
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                 System.ComponentModel.Win32Exception or
                                                 UnauthorizedAccessException or ArgumentException)
                { /* A process can exit during the lookup. */ }
            }
        }
        return null;
    }

    private static string? PackageFamilyFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var marker = path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var packageDirectory = path[(marker + "\\WindowsApps\\".Length)..].Split('\\', 2)[0];
        if (packageDirectory.Length is < 8 or > 256) return null;
        var match = Regex.Match(packageDirectory,
            @"^(.+?)_\d+\.\d+\.\d+\.\d+_.+__(.+)$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value + "_" + match.Groups[2].Value : null;
    }

    private static IEnumerable<string> EnumerateShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        var visited = 0;
        foreach (var root in roots.Where(Directory.Exists))
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0 && visited < 2500)
            {
                var (directory, depth) = pending.Pop();
                string[] files;
                string[] children;
                try
                {
                    files = Directory.GetFiles(directory, "*.lnk");
                    children = depth >= 6 ? [] : Directory.GetDirectories(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // An inaccessible Start menu folder does not invalidate other shortcuts.
                    continue;
                }
                foreach (var file in files)
                {
                    if (++visited > 2500) yield break;
                    yield return file;
                }
                foreach (var child in children)
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Push((child, depth + 1));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }
}
