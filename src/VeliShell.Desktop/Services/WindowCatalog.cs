using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VeliShell.Core;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal sealed record NativeWindow(nint Handle, string Title, string Executable, string ProcessName);

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
                result.Add(new NativeWindow(hwnd, title.ToString(), path, processName));
            }
            catch (Exception) { /* A process may exit while it is being enumerated. */ }
            return true;
        }, 0);
        return result;
    }

    private static string GetProcessPath(uint pid)
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
