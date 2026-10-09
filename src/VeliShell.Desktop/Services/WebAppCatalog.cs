using System.IO;

namespace VeliShell.Desktop.Services;

internal sealed record WebAppShortcut(string Name, string ShortcutPath, string? AppUserModelId);

// Installed browser apps are represented by their Windows shortcuts. The
// shortcut owns both the displayed name and the per-site icon; the AUMID
// distinguishes its window from ordinary tabs in the same browser process.
internal static class WebAppCatalog
{
    private static IReadOnlyList<WebAppShortcut> _snapshot = [];

    internal static IReadOnlyList<WebAppShortcut> Snapshot => Volatile.Read(ref _snapshot);

    internal static void Refresh(IEnumerable<string> shortcutPaths)
    {
        var apps = shortcutPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(TryRead)
            .Where(app => app is not null)
            .Cast<WebAppShortcut>()
            .DistinctBy(app => app.AppUserModelId ?? app.ShortcutPath, StringComparer.OrdinalIgnoreCase)
            .Take(512)
            .ToArray();
        Volatile.Write(ref _snapshot, apps);
    }

    internal static WebAppShortcut? TryRead(string shortcutPath)
    {
        if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        var link = ShellLinkService.Read(shortcutPath);
        if (link?.IsWebApp != true) return null;
        var name = Path.GetFileNameWithoutExtension(shortcutPath);
        return name.Length is > 0 and <= 160
            ? new WebAppShortcut(name, shortcutPath, link.AppUserModelId)
            : null;
    }

    internal static WebAppShortcut? Match(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return null;
        return Snapshot.FirstOrDefault(app =>
            string.Equals(app.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase));
    }
}
