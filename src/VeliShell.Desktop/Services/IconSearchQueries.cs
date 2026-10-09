using System.IO;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal static class IconSearchQueries
{
    internal static IReadOnlyList<string> ForPin(Pin pin, IReadOnlyList<NativeWindow> windows)
    {
        var candidates = new List<string>();
        candidates.AddRange(MacOsIconSearchCatalog.CreatePlans(pin).Take(1).Select(plan => plan.ExactName));

        var target = Environment.ExpandEnvironmentVariables(pin.Target ?? "").Trim().Trim('"');
        var executable = target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLinkService.ResolveTarget(target) : target;
        if (!string.IsNullOrWhiteSpace(executable))
            candidates.Add(Path.GetFileNameWithoutExtension(executable));

        var matchingWindows = windows.Where(window => WindowCatalog.Matches(window, pin) ||
            (!string.IsNullOrWhiteSpace(executable) &&
             string.Equals(window.Executable, executable, StringComparison.OrdinalIgnoreCase))).Take(1).ToArray();
        foreach (var window in matchingWindows)
        {
            // A document caption may include an app name on either side of the separator.
            var title = window.Title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (title.Length > 0) candidates.Add(title[^1]);
        }

        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.GetFileNameWithoutExtension(target));
        candidates.Add(pin.Name);
        candidates.Add(pin.MatchProcess ?? "");
        foreach (var window in matchingWindows)
        {
            var title = window.Title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (title.Length > 1) candidates.Add(title[0]);
        }

        return candidates.Select(value => value.Trim())
            .Where(value => value.Length is >= 2 and <= 100 && !value.Any(char.IsControl))
            .DistinctBy(MacOsIconSearchCatalog.Normalize, StringComparer.Ordinal)
            .Take(6).ToArray();
    }
}
