using System.IO;

namespace VeliShell.Desktop.Services;

internal static class FolderShortcutService
{
    internal static string? ResolveFolderTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Directory.Exists(path)) return path;
            if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

            var target = ShellLinkService.ResolveTarget(path);
            return !string.IsNullOrWhiteSpace(target) && Directory.Exists(target) ? target : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
