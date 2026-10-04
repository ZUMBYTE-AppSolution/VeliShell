using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Controls;

public class VeliShellWindow : Window
{
    private HwndSource? _source;
    public VeliShellWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "WindowSurface");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 50, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false
        });
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowHook);
            var rounded = 2;
            NativeMethods.DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        };
        Closed += (_, _) => _source?.RemoveHook(WindowHook);
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x24) // WM_GETMINMAXINFO: preserve the Windows taskbar on maximize.
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var value = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
                value.MaxPosition.X = info.Work.Left - info.Monitor.Left;
                value.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
                value.MaxSize.X = info.Work.Right - info.Work.Left;
                value.MaxSize.Y = info.Work.Bottom - info.Work.Top;
                Marshal.StructureToPtr(value, lParam, false);
                handled = true;
            }
        }
        return 0;
    }
}
