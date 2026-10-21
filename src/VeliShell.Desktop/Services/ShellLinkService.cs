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
            var target = path.Length > 0 ? Environment.ExpandEnvironmentVariables(path.ToString()) : null;
            if (target is null)
            {
                // AppsFolder links often have no filesystem GetPath at all.
                // Read the PIDL, but keep the .lnk itself as the launch target.
                nint idList = 0, parsingName = 0;
                NativeMethods.IPropertyStore? targetStore = null;
                try
                {
                    link.GetIDList(out idList);
                    if (idList != 0)
                    {
                        var iid = typeof(NativeMethods.IPropertyStore).GUID;
                        if (appId is null && NativeMethods.SHGetPropertyStoreFromIDList(idList, 0, ref iid, out targetStore) == 0 &&
                            targetStore is not null) appId = NativeMethods.ReadAppUserModelId(targetStore);
                        if (appId is null && NativeMethods.SHGetNameFromIDList(idList, 0x80028000, out parsingName) == 0)
                        {
                            var name = Marshal.PtrToStringUni(parsingName) ?? "";
                            appId = PackagedAppService.AppIdFromTarget(name);
                            // AppsFolder's absolute parsing name can be just the AUMID.
                            // Do not mistake arbitrary virtual folders for applications.
                            if (appId is null && PackagedAppService.AppIdFromTarget(PackagedAppService.TargetForAppId(name)) is { } bareId &&
                                (bareId.Contains('!') || PackagedAppService.NameForAppId(bareId) is not null)) appId = bareId;
                        }
                    }
                }
                catch (COMException) { /* A property-store identity still identifies an unresolvable PIDL. */ }
                finally
                {
                    if (parsingName != 0) Marshal.FreeCoTaskMem(parsingName);
                    if (idList != 0) Marshal.FreeCoTaskMem(idList);
                    if (targetStore is not null && Marshal.IsComObject(targetStore)) Marshal.ReleaseComObject(targetStore);
                }
                if (appId is not null) target = PackagedAppService.TargetForAppId(appId);
            }
            return target is null ? null : new ShortcutInfo(
                target, arguments.ToString(), appId,
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
