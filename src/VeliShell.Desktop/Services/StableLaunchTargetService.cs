using System.IO;
using System.Diagnostics;
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
        var normalizedExecutable = AppIdentityService.NormalizeExecutable(executable);
        if (normalizedExecutable is null) return executable;
        if (PreferPackagedIdentity(executable) is { } packagedTarget) return packagedTarget;
        var basename = Path.GetFileNameWithoutExtension(executable);
        var matchingName = displayName?.Trim();
        string? matchingExecutable = null;
        foreach (var path in EnumerateShortcuts())
        {
            var link = ShellLinkService.Read(path);
            if (link is null || link.IsWebApp) continue;
            if (string.Equals(AppIdentityService.NormalizeExecutable(link.Target),
                    normalizedExecutable, StringComparison.OrdinalIgnoreCase)) return path;
            // An update may change the version directory, never the application
            // family/install root. A shared EXE name is not sufficient evidence.
            if (matchingExecutable is null && basename.Length >= 5 &&
                AppIdentityService.SameExecutable(link.Target, executable) &&
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
        if (AppIdentityService.NormalizeExecutable(executable) is null) return null;
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
                    if (!AppIdentityService.SameExecutable(executable, runningPath)) continue;
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
