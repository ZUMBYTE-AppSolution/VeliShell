using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal static class WindowShapeService
{
    internal static void ApplyRoundedRegion(Window window, double cornerRadius)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        if (window.WindowState == WindowState.Maximized)
        {
            NativeMethods.SetWindowRgn(handle, 0, true);
            return;
        }
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 2 || height < 2) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var diameterX = Math.Max(2, (int)Math.Round(cornerRadius * 2 * dpi.DpiScaleX));
        var diameterY = Math.Max(2, (int)Math.Round(cornerRadius * 2 * dpi.DpiScaleY));
        var region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, diameterX, diameterY);
        if (region != 0 && NativeMethods.SetWindowRgn(handle, region, true) == 0)
            NativeMethods.DeleteObject(region); // A successful call transfers ownership to Windows.
    }
}
