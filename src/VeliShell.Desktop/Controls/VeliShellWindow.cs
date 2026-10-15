using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Controls;

public class VeliShellWindow : Window
{
    private HwndSource? _source;
    private CornerRadius? _normalCornerRadius;
    private Thickness? _normalBorderThickness;
    private double? _normalClipRadius;
    public VeliShellWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "TextPrimary");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 50, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowHook);
        };
        // A Win32 window region has hard, pixel-stepped edges. Layered WPF
        // transparency uses the actual antialiased border alpha instead, even
        // where DWM window rounding is unavailable (for example in a VM).
        StateChanged += (_, _) => UpdateMaximizedShape();
        Closed += (_, _) => _source?.RemoveHook(WindowHook);
    }

    private void UpdateMaximizedShape()
    {
        if (Content is not Border plate) return;
        _normalCornerRadius ??= plate.CornerRadius;
        _normalBorderThickness ??= plate.BorderThickness;
        if (plate.Child is FrameworkElement clipped)
            _normalClipRadius ??= RoundedClip.GetRadius(clipped);
        var maximized = WindowState == WindowState.Maximized;
        plate.CornerRadius = maximized ? new CornerRadius(0) : _normalCornerRadius.Value;
        plate.BorderThickness = maximized ? new Thickness(0) : _normalBorderThickness.Value;
        if (plate.Child is FrameworkElement view && _normalClipRadius is double radius)
            RoundedClip.SetRadius(view, maximized ? 0 : radius);
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
