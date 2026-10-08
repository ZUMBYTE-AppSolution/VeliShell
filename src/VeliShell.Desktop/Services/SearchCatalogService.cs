using System.IO;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal sealed record SearchEntry(string Name, string Target, string Category);

internal static class SearchCatalogService
{
    private const int MaximumEntries = 2400;
    private const int MaximumDepth = 7;

    internal static IReadOnlyList<SearchEntry> Discover()
    {
        var results = new List<SearchEntry>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
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
        return results;
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
