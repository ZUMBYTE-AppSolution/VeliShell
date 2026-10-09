using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VeliShell.Core;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal sealed record NativeWindow(
    nint Handle, string Title, string Executable, string ProcessName,
    string? AppUserModelId = null, WebAppShortcut? WebApp = null, int ProcessId = 0,
    string? DisplayName = null, string? IconTarget = null);

internal static class WindowCatalog
{
    internal static List<NativeWindow> Read()
    {
        var result = new List<NativeWindow>();
        var processPaths = new Dictionary<uint, string>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindow(hwnd, 4) != 0) return true;
                if ((NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExToolWindow) != 0) return true;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == Environment.ProcessId || pid == 0) return true;
                if (NativeMethods.DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var title = new StringBuilder(1024);
                NativeMethods.GetWindowText(hwnd, title, title.Capacity);
                if (title.Length == 0) return true;
                var className = new StringBuilder(128);
                NativeMethods.GetClassName(hwnd, className, className.Capacity);
                if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                if (!processPaths.TryGetValue(pid, out var path))
                {
                    path = GetProcessPath(pid);
                    processPaths[pid] = path;
                }
                // Inaccessible processes remain usable as individual window entries.
                var processName = path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : $"pid-{pid}";
                if (processName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase))
                {
                    path = SteamOwnerExecutable(path);
                    processName = path.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase)
                        ? "Steam" : processName;
                }
                if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
                    className.ToString().Equals("ApplicationFrameWindow", StringComparison.Ordinal))
                {
                    var packaged = PackagedAppService.ForFrame(hwnd, pid);
                    if (packaged is not null)
                    {
                        if (!processPaths.TryGetValue(packaged.ProcessId, out var appPath))
                        {
                            appPath = GetProcessPath(packaged.ProcessId);
                            processPaths[packaged.ProcessId] = appPath;
                        }
                        result.Add(new NativeWindow(hwnd, title.ToString(), appPath,
                            appPath.Length > 0 ? Path.GetFileNameWithoutExtension(appPath) : title.ToString(),
                            packaged.AppUserModelId, null, checked((int)packaged.ProcessId),
                            packaged.Name.Length > 0 ? packaged.Name : title.ToString(), packaged.Target));
                        return true;
                    }
                    // A frame can briefly exist without its app child during
                    // startup/shutdown; never present the host as an app name.
                    result.Add(new NativeWindow(hwnd, title.ToString(), "", title.ToString(),
                        ProcessId: checked((int)pid), DisplayName: title.ToString()));
                    return true;
                }
                var appId = IsBrowserProcess(processName) ? ReadWindowAppId(hwnd) : null;
                result.Add(new NativeWindow(hwnd, title.ToString(), path, processName,
                    appId, WebAppCatalog.Match(appId), checked((int)pid)));
            }
            catch (Exception) { /* A process may exit while it is being enumerated. */ }
            return true;
        }, 0);
        return result;
    }

    private static bool IsBrowserProcess(string processName) =>
        processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
        processName.EndsWith("_proxy", StringComparison.OrdinalIgnoreCase);

    private static string? ReadWindowAppId(nint hwnd)
    {
        NativeMethods.IPropertyStore? store = null;
        try
        {
            var interfaceId = typeof(NativeMethods.IPropertyStore).GUID;
            return NativeMethods.SHGetPropertyStoreForWindow(hwnd, ref interfaceId, out store) == 0 &&
                   store is not null ? NativeMethods.ReadAppUserModelId(store) : null;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException) { return null; }
        finally
        {
            if (store is not null && Marshal.IsComObject(store))
            {
                try { Marshal.ReleaseComObject(store); }
                catch (InvalidComObjectException) { }
            }
        }
    }

    // Chromium Embedded Framework creates Steam UI windows in a helper process.
    // Only fold them into Steam when steam.exe exists in the helper's own tree.
    internal static string SteamOwnerExecutable(string helperPath)
    {
        if (!string.Equals(Path.GetFileName(helperPath), "steamwebhelper.exe",
                StringComparison.OrdinalIgnoreCase)) return helperPath;
        var directory = Path.GetDirectoryName(helperPath);
        for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            var candidate = Path.Combine(directory, "steam.exe");
            var relative = Path.GetRelativePath(directory, helperPath);
            if (File.Exists(candidate) &&
                relative.StartsWith(@"bin\cef\", StringComparison.OrdinalIgnoreCase)) return candidate;
            directory = Path.GetDirectoryName(directory);
        }
        return helperPath;
    }

    internal static string GetProcessPath(uint pid)
    {
        var process = NativeMethods.OpenProcess(0x1000, false, pid);
        if (process == 0) return "";
        try
        {
            uint capacity = 32768;
            var text = new StringBuilder((int)capacity);
            return NativeMethods.QueryFullProcessImageName(process, 0, text, ref capacity) ? text.ToString() : "";
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    internal static bool Matches(NativeWindow window, Pin pin)
    {
        if (pin.Kind == PinKind.VirtualFolder) return false;
        var packagedAppId = PackagedAppService.AppIdFromTarget(pin.Target);
        if (packagedAppId is not null)
            return string.Equals(packagedAppId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase);
        var webAppPin = WebAppCatalog.TryRead(pin.Target);
        if (webAppPin is not null || window.WebApp is not null)
            return webAppPin is not null &&
                !string.IsNullOrWhiteSpace(webAppPin.AppUserModelId) &&
                string.Equals(webAppPin.AppUserModelId, window.AppUserModelId,
                    StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(pin.MatchProcess))
            return string.Equals(window.ProcessName, pin.MatchProcess, StringComparison.OrdinalIgnoreCase);
        var path = Environment.ExpandEnvironmentVariables(pin.Target);
        return string.Equals(window.Executable, path, StringComparison.OrdinalIgnoreCase)
            || (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(window.ProcessName, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase));
    }

    internal static bool Activate(nint hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindowAsync(hwnd, 9);
        return NativeMethods.SetForegroundWindow(hwnd);
    }

    internal static bool ForegroundIsFullscreenOnPrimary()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId || !NativeMethods.IsWindowVisible(hwnd)) return false;
        var cls = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        var monitor = NativeMethods.PrimaryMonitor().Monitor;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        return Math.Abs(rect.Left - monitor.Left) <= 2 && Math.Abs(rect.Top - monitor.Top) <= 2
            && Math.Abs(rect.Right - monitor.Right) <= 2 && Math.Abs(rect.Bottom - monitor.Bottom) <= 2;
    }
}
