using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>Hosts one rounded Windows Composition backdrop beneath a WPF surface.</summary>
internal sealed class ShellBackdropHost : IDisposable
{
    private const string GlassWindowClassName = "VeliShellCompositionGlass";
    private const uint SwpNoActivate = 0x0010;
    private static readonly NativeMethods.WindowProcedure GlassProcedure = GlassWindowProcedure;
    private static readonly Lazy<ushort> GlassClass = new(RegisterGlassClass);
    private readonly FrameworkElement _overlay;
    private readonly Window? _overlayWindow;
    private readonly FrameworkElement _plate;
    private readonly double _cornerRadius;
    private readonly DispatcherTimer _motionTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private CompositionGlassRenderer? _renderer;
    private nint _surfaceHandle;
    private NativeMethods.Rect _lastBounds;
    private bool _hasBounds;
    private bool _active;
    private bool _surfaceVisible;
    private bool _surfaceTopmost;
    private bool _disposed;
    private long _motionUntil;
    private string? _fallbackResource;

    internal ShellBackdropHost(FrameworkElement overlay, FrameworkElement plate, double cornerRadius)
    {
        _overlay = overlay;
        _overlayWindow = overlay as Window;
        _plate = plate;
        _cornerRadius = cornerRadius;
        if (_overlayWindow is not null)
        {
            _overlayWindow.LocationChanged += OnGeometryChanged;
            _overlayWindow.SizeChanged += OnGeometryChanged;
        }
        _overlay.IsVisibleChanged += OnVisibilityChanged;
        _overlay.LayoutUpdated += OnLayoutUpdated;
        if (!ReferenceEquals(_overlay, _plate)) _plate.LayoutUpdated += OnLayoutUpdated;
        _motionTimer.Tick += OnMotionTick;
    }

    internal bool Apply()
    {
        if (_disposed) return false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast ||
            Application.Current?.TryFindResource("ShellAcrylicTint") is not Color tint)
        {
            CloseSurface();
            return false;
        }
        try
        {
            if (_surfaceHandle == 0 && !CreateSurface()) return false;
            var gloss = Application.Current.TryFindResource("ShellGlassGloss") is double g ? g : 50;
            var transparency = Application.Current.TryFindResource("ShellGlassTransparency") is double t ? t : 50;
            _renderer!.Apply(tint, gloss, transparency);
            _active = true;
            Sync();
            return _active;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
                                          PlatformNotSupportedException or TypeLoadException or
                                          EntryPointNotFoundException or DllNotFoundException)
        {
            App.Log("Windows Composition backdrop unavailable", exception);
            CloseSurface();
            return false;
        }
    }

    internal bool ApplyMaterial(string acrylicResource, string fallbackResource)
    {
        _fallbackResource = fallbackResource;
        var active = Apply();
        var resource = active ? acrylicResource : fallbackResource;
        SetPlateMaterial(resource);
        return active;
    }

    private void SetPlateMaterial(string resource)
    {
        if (_plate is Border border) border.SetResourceReference(Border.BackgroundProperty, resource);
        else if (_plate is Control control) control.SetResourceReference(Control.BackgroundProperty, resource);
    }

    internal void Sync()
    {
        if (!_active || _disposed || _surfaceHandle == 0) return;
        if (!_overlay.IsVisible || !_plate.IsVisible || _plate.ActualWidth <= 0 || _plate.ActualHeight <= 0)
        {
            HideSurface();
            return;
        }
        var topLeft = _plate.PointToScreen(new Point());
        var bottomRight = _plate.PointToScreen(new Point(_plate.ActualWidth, _plate.ActualHeight));
        var overlayHandle = OverlayHandle();
        if (overlayHandle == 0 || !NativeMethods.GetWindowRect(overlayHandle, out var overlayBounds)) return;
        var bounds = new NativeMethods.Rect
        {
            Left = Math.Max((int)Math.Round(topLeft.X), overlayBounds.Left),
            Top = Math.Max((int)Math.Round(topLeft.Y), overlayBounds.Top),
            Right = Math.Min((int)Math.Round(bottomRight.X), overlayBounds.Right),
            Bottom = Math.Min((int)Math.Round(bottomRight.Y), overlayBounds.Bottom)
        };
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < 2 || height < 2) { HideSurface(); return; }
        var changed = !_hasBounds || bounds.Left != _lastBounds.Left || bounds.Top != _lastBounds.Top ||
                      bounds.Right != _lastBounds.Right || bounds.Bottom != _lastBounds.Bottom;
        var topmost = OverlayTopmost(overlayHandle);
        var zOrderChanged = _surfaceTopmost != topmost;
        _surfaceTopmost = topmost;
        var wasVisible = _surfaceVisible;
        if (changed)
        {
            var dpi = VisualTreeHelper.GetDpi(_overlay);
            try
            {
                _renderer!.Resize(width, height, (float)(_cornerRadius * dpi.DpiScaleX),
                    (float)(_cornerRadius * dpi.DpiScaleY));
            }
            catch (COMException exception)
            {
                App.Log("Windows Composition backdrop lost its graphics device", exception);
                CloseSurface();
                return;
            }
        }
        if (!wasVisible)
        {
            NativeMethods.ShowWindow(_surfaceHandle, NativeMethods.SwShowNoActivate);
            _surfaceVisible = true;
        }
        if (changed || !wasVisible || zOrderChanged)
        {
            if (!NativeMethods.SetWindowPos(_surfaceHandle, overlayHandle,
                    bounds.Left, bounds.Top, width, height, SwpNoActivate))
            {
                CloseSurface();
                return;
            }
            _lastBounds = bounds;
            _hasBounds = true;
        }
    }

    internal void FollowMotion(int durationMilliseconds)
    {
        if (!_active) return;
        _motionUntil = Environment.TickCount64 + durationMilliseconds + 50;
        _motionTimer.Start();
        Sync();
    }

    internal void AlignZOrder()
    {
        if (!_active || _disposed || _surfaceHandle == 0) return;
        var overlayHandle = OverlayHandle();
        if (overlayHandle != 0)
            NativeMethods.SetWindowPos(_surfaceHandle, overlayHandle, 0, 0, 0, 0, 0x0001 | 0x0002 | SwpNoActivate);
    }

    private bool CreateSurface()
    {
        if (GlassClass.Value == 0) return false;
        _surfaceHandle = NativeMethods.CreateWindowEx(
            (int)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate |
                  NativeMethods.WsExTransparent | 0x00200000), // WS_EX_NOREDIRECTIONBITMAP
            GlassWindowClassName, "", unchecked((int)0x80000000),
            -10000, -10000, 2, 2, 0, 0, NativeMethods.GetModuleHandle(null), 0);
        if (_surfaceHandle == 0) return false;
        _renderer = new CompositionGlassRenderer(_surfaceHandle);
        return true;
    }

    private static ushort RegisterGlassClass()
    {
        var registration = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(GlassProcedure),
            Instance = NativeMethods.GetModuleHandle(null), Name = GlassWindowClassName
        };
        return NativeMethods.RegisterClass(ref registration);
    }

    private static nint GlassWindowProcedure(nint window, int message, nint wParam, nint lParam)
    {
        if (message == 0x0084) return -1; // HTTRANSPARENT
        if (message == 0x0021) return 3; // MA_NOACTIVATE
        if (message == 0x0014) return 1; // WM_ERASEBKGND
        if (message == 0x000F) { NativeMethods.ValidateRect(window, 0); return 0; }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private nint OverlayHandle() => (PresentationSource.FromVisual(_overlay) as HwndSource)?.Handle ?? 0;
    private bool OverlayTopmost(nint handle) => _overlayWindow?.Topmost ??
        (NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExTopmost) != 0;
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
        try { _renderer?.Dispose(); }
        finally
        {
            _renderer = null;
            if (_surfaceHandle != 0) NativeMethods.DestroyWindow(_surfaceHandle);
            _surfaceHandle = 0;
        }
        _surfaceVisible = false;
        _hasBounds = false;
        if (!_disposed && _fallbackResource is not null) SetPlateMaterial(_fallbackResource);
    }

    private void HideSurface()
    {
        if (_surfaceVisible && _surfaceHandle != 0)
            NativeMethods.ShowWindow(_surfaceHandle, NativeMethods.SwHide);
        _surfaceVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_overlayWindow is not null)
        {
            _overlayWindow.LocationChanged -= OnGeometryChanged;
            _overlayWindow.SizeChanged -= OnGeometryChanged;
        }
        _overlay.IsVisibleChanged -= OnVisibilityChanged;
        _overlay.LayoutUpdated -= OnLayoutUpdated;
        if (!ReferenceEquals(_overlay, _plate)) _plate.LayoutUpdated -= OnLayoutUpdated;
        _motionTimer.Tick -= OnMotionTick;
        CloseSurface();
    }
}
