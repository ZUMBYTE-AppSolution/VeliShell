using System.IO;
using Microsoft.Win32;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal sealed record SearchEntry(string Name, string Target, string Category, string? Description = null);

internal static class SearchCatalogService
{
    private const int MaximumEntries = 2400;
    private const int MaximumDepth = 7;

    internal static IReadOnlyList<SearchEntry> Discover()
    {
        var results = new List<SearchEntry>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var shortcutPaths = new List<string>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0 && results.Count < MaximumEntries)
            {
                var (path, depth) = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path))
                    {
                        if (results.Count >= MaximumEntries) break;
                        var extension = Path.GetExtension(file);
                        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                            !extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)) continue;
                        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                            shortcutPaths.Add(file);
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                        results.Add(new SearchEntry(name, file, "app"));
                    }
                    if (depth >= MaximumDepth) continue;
                    foreach (var directory in Directory.EnumerateDirectories(path))
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                            pending.Push((directory, depth + 1));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // One inaccessible Start-menu folder must not hide other apps.
                }
            }
        }
        // App Paths covers installed desktop programs without traversing all of
        // Program Files. Start-menu shortcuts above also include packaged apps.
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            if (results.Count >= MaximumEntries) break;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null) continue;
                foreach (var subkeyName in appPaths.GetSubKeyNames())
                {
                    if (results.Count >= MaximumEntries) break;
                    using var entry = appPaths.OpenSubKey(subkeyName);
                    var path = (entry?.GetValue("") as string)?.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                        !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileNameWithoutExtension(subkeyName);
                    if (!seen.Add(name)) continue;
                    results.Add(new SearchEntry(name, path, "app"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.Security.SecurityException)
            {
                // Registry permissions or a changing installation cannot stop search.
            }
        }
        // Some desktop applications register only their installed-program
        // entry. Include those only when DisplayIcon names a real executable,
        // never an uninstall command or a guessed path.
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            if (results.Count >= MaximumEntries) break;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var installed = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (installed is null) continue;
                foreach (var subkeyName in installed.GetSubKeyNames())
                {
                    if (results.Count >= MaximumEntries) break;
                    using var entry = installed.OpenSubKey(subkeyName);
                    var name = (entry?.GetValue("DisplayName") as string)?.Trim();
                    var path = ParseDisplayIconExecutable(entry?.GetValue("DisplayIcon") as string);
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 140 ||
                        path is null || !seen.Add(name)) continue;
                    results.Add(new SearchEntry(name, path, "app"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.Security.SecurityException)
            {
                // Inaccessible/uninstalling entries are simply excluded.
            }
        }
        foreach (var desktop in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        }.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(desktop, "*.lnk").Take(512))
                {
                    shortcutPaths.Add(file);
                    if (results.Count >= MaximumEntries) continue;
                    if (WebAppCatalog.TryRead(file) is not { } webApp || !seen.Add(webApp.Name)) continue;
                    results.Add(new SearchEntry(webApp.Name, file, "webapp"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        WebAppCatalog.Refresh(shortcutPaths);
        var webAppPaths = WebAppCatalog.Snapshot.Select(app => app.ShortcutPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < results.Count; index++)
            if (webAppPaths.Contains(results[index].Target))
                results[index] = results[index] with { Category = "webapp" };
        return results;
    }

    private static string? ParseDisplayIconExecutable(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = Environment.ExpandEnvironmentVariables(icon.Trim());
        var path = icon.StartsWith('"')
            ? icon[1..].Split('"', 2)[0]
            : icon[..Math.Max(0, icon.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4)];
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return null;
        var executable = Path.GetFileNameWithoutExtension(path);
        if (executable.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return null;
        return path;
    }

    internal static IReadOnlyList<SearchEntry> Match(
        string query, IEnumerable<Pin> pins, IReadOnlyList<SearchEntry> catalog, int limit = 12)
    {
        query = query.Trim();
        if (query.Length > 120) query = query[..120];
        var pinned = pins.Select(pin => new SearchEntry(
            LocalizationService.Current.DisplayPinName(pin), pin.Target, "pin"));
        return pinned.Concat(catalog)
            .Where(entry => query.Length == 0 ||
                            entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .DistinctBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(entry => query.Length == 0 ? (entry.Category == "pin" ? 0 : 1) :
                entry.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit, 1, 20))
            .ToArray();
    }

    internal static Uri WebSearchUri(string query)
    {
        query = query.Trim();
        if (query.Length is 0 or > 200) throw new ArgumentException("Invalid web search query", nameof(query));
        return new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
    }
}
