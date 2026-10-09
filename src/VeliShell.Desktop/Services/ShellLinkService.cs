using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal static class ShellLinkService
{
    internal sealed record ShortcutInfo(
        string Target, string Arguments, string? AppUserModelId, string? IconPath = null)
    {
        internal bool IsWebApp
        {
            get
            {
                var process = Path.GetFileNameWithoutExtension(Target);
                var browser = process.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                    process.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                    process.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
                    process.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
                    process.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
                    process.Equals("chrome_proxy", StringComparison.OrdinalIgnoreCase) ||
                    process.Equals("msedge_proxy", StringComparison.OrdinalIgnoreCase) ||
                    process.Equals("brave_proxy", StringComparison.OrdinalIgnoreCase);
                return browser && (Arguments.Contains("--app-id=", StringComparison.OrdinalIgnoreCase) ||
                    Arguments.Contains("--app=", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static readonly ConcurrentDictionary<string, (DateTime Modified, ShortcutInfo? Info)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static string? ResolveTarget(string shortcutPath) => Read(shortcutPath)?.Target;

    internal static ShortcutInfo? Read(string shortcutPath)
    {
        if (!File.Exists(shortcutPath) || !shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        DateTime modified;
        try { modified = File.GetLastWriteTimeUtc(shortcutPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        if (Cache.TryGetValue(shortcutPath, out var cached) && cached.Modified == modified)
            return cached.Info;
        var info = ReadUncached(shortcutPath);
        if (Cache.Count >= 512) Cache.Clear();
        Cache[shortcutPath] = (modified, info);
        return info;
    }

    private static ShortcutInfo? ReadUncached(string shortcutPath)
    {
        object? instance = null;
        try
        {
            instance = new ShellLinkComObject();
            ((IPersistFile)instance).Load(shortcutPath, 0);
            var path = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var iconPath = new StringBuilder(32768);
            var link = (IShellLinkW)instance;
            link.GetPath(path, path.Capacity, 0, 0);
            link.GetArguments(arguments, arguments.Capacity);
            try { link.GetIconLocation(iconPath, iconPath.Capacity, out _); }
            catch (COMException) { /* A missing custom icon does not invalidate the link. */ }
            string? appId = null;
            try
            {
                if (instance is NativeMethods.IPropertyStore store)
                    appId = NativeMethods.ReadAppUserModelId(store);
            }
            catch (COMException) { /* Older links may have no property store. */ }
            return path.Length == 0 ? null : new ShortcutInfo(
                Environment.ExpandEnvironmentVariables(path.ToString()), arguments.ToString(), appId,
                iconPath.Length == 0 ? null : Environment.ExpandEnvironmentVariables(iconPath.ToString()));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            App.Log("Could not resolve a shell link", ex);
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }
}
