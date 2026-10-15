using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Keeps native acrylic confined to a small, rounded window immediately below
/// a layered WPF shell surface. Applying the accent to the WPF window itself
/// would blur its entire transparent rectangle, including space above the dock.
/// </summary>
internal sealed class ShellBackdropHost : IDisposable
{
    private const int AccentAcrylicBlurBehind = 4;
    private const int AccentPolicyAttribute = 19;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const uint SwpNoActivate = 0x0010;

    private readonly Window _overlay;
    private readonly FrameworkElement _plate;
    private readonly double _cornerRadius;
    private readonly DispatcherTimer _motionTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Window? _surface;
    private HwndSource? _source;
    private nint _surfaceHandle;
    private NativeMethods.Rect _lastBounds;
    private bool _hasBounds;
    private bool _active;
    private bool _disposed;
    private long _motionUntil;

    internal ShellBackdropHost(Window overlay, FrameworkElement plate, double cornerRadius)
    {
        _overlay = overlay;
        _plate = plate;
        _cornerRadius = cornerRadius;
        _overlay.LocationChanged += OnGeometryChanged;
        _overlay.SizeChanged += OnGeometryChanged;
        _overlay.IsVisibleChanged += OnVisibilityChanged;
        _plate.LayoutUpdated += OnLayoutUpdated;
        _motionTimer.Tick += OnMotionTick;
    }

    internal bool Apply()
    {
        if (_disposed) return false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
        {
            CloseSurface();
            return false;
        }
        if (Application.Current?.TryFindResource("ShellAcrylicTint") is not Color tint)
        {
            CloseSurface();
            return false;
        }
        if (_surface is null && !CreateSurface()) return false;
        if (!SetAccent(tint))
        {
            CloseSurface();
            return false;
        }
        _active = true;
        Sync();
        return true;
    }

    internal void Sync()
    {
        if (!_active || _disposed || _surface is null) return;
        if (!_overlay.IsVisible || !_plate.IsVisible || _plate.ActualWidth <= 0 || _plate.ActualHeight <= 0)
        {
            if (_surface.IsVisible) _surface.Hide();
            return;
        }
        var topLeft = _plate.PointToScreen(new Point());
        var bottomRight = _plate.PointToScreen(new Point(_plate.ActualWidth, _plate.ActualHeight));
        var bounds = new NativeMethods.Rect
        {
            Left = (int)Math.Round(topLeft.X),
            Top = (int)Math.Round(topLeft.Y),
            Right = (int)Math.Round(bottomRight.X),
            Bottom = (int)Math.Round(bottomRight.Y)
        };
        var overlayHandle = new WindowInteropHelper(_overlay).Handle;
        if (!NativeMethods.GetWindowRect(overlayHandle, out var overlayBounds)) return;
        bounds.Left = Math.Max(bounds.Left, overlayBounds.Left);
        bounds.Top = Math.Max(bounds.Top, overlayBounds.Top);
        bounds.Right = Math.Min(bounds.Right, overlayBounds.Right);
        bounds.Bottom = Math.Min(bounds.Bottom, overlayBounds.Bottom);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < 2 || height < 2)
        {
            if (_surface.IsVisible) _surface.Hide();
            return;
        }
        var wasVisible = _surface.IsVisible;
        if (!wasVisible) _surface.Show();
        var topmostChanged = _surface.Topmost != _overlay.Topmost;
        if (topmostChanged) _surface.Topmost = _overlay.Topmost;
        var changed = !_hasBounds || bounds.Left != _lastBounds.Left || bounds.Top != _lastBounds.Top ||
                      bounds.Right != _lastBounds.Right || bounds.Bottom != _lastBounds.Bottom;
        if (changed)
        {
            if (!NativeMethods.SetWindowPos(_surfaceHandle, overlayHandle,
                    bounds.Left, bounds.Top, width, height, SwpNoActivate)) return;
            if (!_hasBounds || width != _lastBounds.Right - _lastBounds.Left ||
                height != _lastBounds.Bottom - _lastBounds.Top)
                ApplyRegion(width, height);
            _lastBounds = bounds;
            _hasBounds = true;
        }
        else if (!wasVisible || topmostChanged)
        {
            // A Topmost change can move the material above the interactive WPF window.
            NativeMethods.SetWindowPos(_surfaceHandle, overlayHandle,
                0, 0, 0, 0, 0x0001 | 0x0002 | SwpNoActivate);
        }
    }

    internal void FollowMotion(int durationMilliseconds)
    {
        if (!_active) return;
        _motionUntil = Environment.TickCount64 + durationMilliseconds + 50;
        _motionTimer.Start();
        Sync();
    }

    private bool CreateSurface()
    {
        try
        {
            var surface = new Window
            {
                Width = 2, Height = 2, Left = -10000, Top = -10000,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = _overlay.Topmost,
                Content = new Grid { Background = Brushes.Transparent }
            };
            _surface = surface;
            surface.SourceInitialized += (_, _) =>
            {
                _surfaceHandle = new WindowInteropHelper(surface).Handle;
                var style = NativeMethods.GetWindowLongPtr(_surfaceHandle, NativeMethods.GwlExStyle).ToInt64();
                NativeMethods.SetWindowLongPtr(_surfaceHandle, NativeMethods.GwlExStyle,
                    (nint)(style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate |
                           NativeMethods.WsExTransparent));
                _source = HwndSource.FromHwnd(_surfaceHandle);
                _source?.AddHook(WindowHook);
            };
            surface.Closed += (_, _) =>
            {
                _active = false;
                _source?.RemoveHook(WindowHook);
                _source = null;
                _surfaceHandle = 0;
                _hasBounds = false;
                if (ReferenceEquals(_surface, surface)) _surface = null;
            };
            surface.Show();
            return _surfaceHandle != 0;
        }
        catch (InvalidOperationException)
        {
            CloseSurface();
            return false;
        }
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmNcHitTest) { handled = true; return HtTransparent; }
        if (message == WmMouseActivate) { handled = true; return MaNoActivate; }
        return 0;
    }

    private bool SetAccent(Color tint)
    {
        var policy = new NativeMethods.AccentPolicy
        {
            State = AccentAcrylicBlurBehind,
            GradientColor = (uint)tint.A << 24 | (uint)tint.B << 16 | (uint)tint.G << 8 | tint.R
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = AccentPolicyAttribute,
                Data = memory,
                Size = Marshal.SizeOf<NativeMethods.AccentPolicy>()
            };
            return NativeMethods.SetWindowCompositionAttribute(_surfaceHandle, ref data);
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private void ApplyRegion(int width, int height)
    {
        if (_cornerRadius <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(_overlay);
        var roundX = Math.Max(2, (int)Math.Round(_cornerRadius * 2 * dpi.DpiScaleX));
        var roundY = Math.Max(2, (int)Math.Round(_cornerRadius * 2 * dpi.DpiScaleY));
        var region = NativeMethods.CreateRoundRectRgn(0, 0, width, height, roundX, roundY);
        if (region != 0 && NativeMethods.SetWindowRgn(_surfaceHandle, region, true) == 0)
            NativeMethods.DeleteObject(region); // On success Windows owns the region.
    }

    private void OnGeometryChanged(object? sender, EventArgs e) => Sync();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Sync();
    private void OnLayoutUpdated(object? sender, EventArgs e) => Sync();

    private void OnMotionTick(object? sender, EventArgs e)
    {
        Sync();
        if (Environment.TickCount64 >= _motionUntil) _motionTimer.Stop();
    }

    private void CloseSurface()
    {
        _active = false;
        _motionTimer.Stop();
        var surface = _surface;
        _surface = null;
        if (surface is not null)
        {
            try { surface.Close(); }
            catch (InvalidOperationException) { /* WPF is already closing the surface. */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _overlay.LocationChanged -= OnGeometryChanged;
        _overlay.SizeChanged -= OnGeometryChanged;
        _overlay.IsVisibleChanged -= OnVisibilityChanged;
        _plate.LayoutUpdated -= OnLayoutUpdated;
        _motionTimer.Tick -= OnMotionTick;
        CloseSurface();
    }
}
