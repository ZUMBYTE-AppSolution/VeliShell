using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal sealed record PackagedAppIdentity(uint ProcessId, string AppUserModelId, string Name, string Target);

/// <summary>Resolves the app hosted inside an ApplicationFrameWindow without treating its frame host as the app.</summary>
internal static class PackagedAppService
{
    private const string AppsFolderPrefix = "shell:AppsFolder\\";
    private const int ErrorInsufficientBuffer = 122;
    private static readonly ConcurrentDictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase);

    internal static PackagedAppIdentity? ForFrame(nint frame, uint frameProcessId)
    {
        PackagedAppIdentity? identity = null;
        NativeMethods.EnumChildWindows(frame, (child, unused) =>
        {
            var className = new StringBuilder(128);
            NativeMethods.GetClassName(child, className, className.Capacity);
            if (!className.ToString().Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal)) return true;

            NativeMethods.GetWindowThreadProcessId(child, out var appProcessId);
            if (appProcessId == 0 || appProcessId == frameProcessId) return true;
            var appId = ReadProcessAppId(appProcessId);
            if (appId is null) return true;

            var target = TargetForAppId(appId);
            var name = DisplayNames.GetOrAdd(appId, _ => ReadDisplayName(target) ?? "");
            // Do not cache a failed shell lookup permanently: an app may be
            // registering or updating while its window is already visible.
            if (name.Length == 0) DisplayNames.TryRemove(appId, out _);
            identity = new PackagedAppIdentity(appProcessId, appId, name, target);
            return false;
        }, 0);
        return identity;
    }

    internal static string TargetForAppId(string appId) => AppsFolderPrefix + appId;

    internal static string? AppIdFromTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var appId = target[AppsFolderPrefix.Length..];
        var separator = appId.IndexOf('!');
        return appId.Length is > 2 and <= 256 && separator > 0 && separator < appId.Length - 1 &&
               appId.IndexOfAny(['\\', '/']) < 0 && appId.All(character => !char.IsControl(character))
            ? appId : null;
    }

    internal static string? NameForAppId(string appId)
    {
        if (AppIdFromTarget(TargetForAppId(appId)) is null) return null;
        var name = DisplayNames.GetOrAdd(appId, _ => ReadDisplayName(TargetForAppId(appId)) ?? "");
        if (name.Length == 0) DisplayNames.TryRemove(appId, out _);
        return name.Length == 0 ? null : name;
    }

    private static string? ReadProcessAppId(uint processId)
    {
        var process = NativeMethods.OpenProcess(0x1000, false, processId);
        if (process == 0) return null;
        try
        {
            uint length = 0;
            if (NativeMethods.GetApplicationUserModelId(process, ref length, null) != ErrorInsufficientBuffer ||
                length is 0 or > 512) return null;
            var value = new StringBuilder((int)length);
            return NativeMethods.GetApplicationUserModelId(process, ref length, value) == 0 &&
                   AppIdFromTarget(TargetForAppId(value.ToString())) is not null ? value.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    private static string? ReadDisplayName(string target)
    {
        NativeMethods.IShellItem? item = null;
        nint name = 0;
        try
        {
            var interfaceId = typeof(NativeMethods.IShellItem).GUID;
            if (NativeMethods.SHCreateShellItemFromParsingName(target, 0, ref interfaceId, out item) < 0 ||
                item is null || item.GetDisplayName(0, out name) < 0 || name == 0) return null;
            return Marshal.PtrToStringUni(name)?.Trim();
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
                                          ArgumentException or InvalidOperationException) { return null; }
        finally
        {
            if (name != 0) Marshal.FreeCoTaskMem(name);
            if (item is not null && Marshal.IsComObject(item))
            {
                try { Marshal.ReleaseComObject(item); }
                catch (InvalidComObjectException) { }
            }
        }
    }
}
