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
    private const string AcrylicWindowClassName = "VeliShellAcrylicBackdrop";
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const uint SwpNoActivate = 0x0010;
    private static readonly NativeMethods.WindowProcedure AcrylicProcedure = AcrylicWindowProcedure;
    private static readonly Lazy<ushort> AcrylicClass = new(RegisterAcrylicClass);

    private readonly FrameworkElement _overlay;
    private readonly Window? _overlayWindow;
    private readonly FrameworkElement _plate;
    private readonly double _cornerRadius;
    private readonly DispatcherTimer _motionTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private nint _surfaceHandle;
    private NativeMethods.Rect _lastBounds;
    private bool _hasBounds;
    private bool _active;
    private bool _surfaceVisible;
    private bool _surfaceTopmost;
    private bool _disposed;
    private long _motionUntil;

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
        if (_surfaceHandle == 0 && !CreateSurface()) return false;
        if (!SetAccent(tint))
        {
            CloseSurface();
            return false;
        }
        _active = true;
        Sync();
        return true;
    }

    internal bool ApplyMaterial(string acrylicResource, string fallbackResource)
    {
        var active = Apply();
        var resource = active ? acrylicResource : fallbackResource;
        if (_plate is Border border)
            border.SetResourceReference(Border.BackgroundProperty, resource);
        else if (_plate is Control control)
            control.SetResourceReference(Control.BackgroundProperty, resource);
        return active;
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
        var bounds = new NativeMethods.Rect
        {
            Left = (int)Math.Round(topLeft.X),
            Top = (int)Math.Round(topLeft.Y),
            Right = (int)Math.Round(bottomRight.X),
            Bottom = (int)Math.Round(bottomRight.Y)
        };
        var overlayHandle = OverlayHandle();
        if (overlayHandle == 0) return;
        if (!NativeMethods.GetWindowRect(overlayHandle, out var overlayBounds)) return;
        bounds.Left = Math.Max(bounds.Left, overlayBounds.Left);
        bounds.Top = Math.Max(bounds.Top, overlayBounds.Top);
        bounds.Right = Math.Min(bounds.Right, overlayBounds.Right);
        bounds.Bottom = Math.Min(bounds.Bottom, overlayBounds.Bottom);
        var insetX = 0;
        var insetY = 0;
        if (_cornerRadius > 0)
        {
            // The legacy accent can paint the full rectangular HWND despite
            // SetWindowRgn. Keep even that rectangle inside the WPF round-rect:
            // at its nearest corner, 2 * (radius - inset)^2 <= radius^2.
            // Two extra physical pixels cover antialiasing and DPI rounding.
            var dpi = VisualTreeHelper.GetDpi(_overlay);
            insetX = SafeBackdropInset(_cornerRadius, dpi.DpiScaleX);
            insetY = SafeBackdropInset(_cornerRadius, dpi.DpiScaleY);
            bounds.Left += insetX;
            bounds.Top += insetY;
            bounds.Right -= insetX;
            bounds.Bottom -= insetY;
        }
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < 2 || height < 2)
        {
            HideSurface();
            return;
        }
        var wasVisible = _surfaceVisible;
        if (!wasVisible)
        {
            NativeMethods.ShowWindow(_surfaceHandle, NativeMethods.SwShowNoActivate);
            _surfaceVisible = true;
        }
        var topmost = OverlayTopmost(overlayHandle);
        var topmostChanged = _surfaceTopmost != topmost;
        _surfaceTopmost = topmost;
        var changed = !_hasBounds || bounds.Left != _lastBounds.Left || bounds.Top != _lastBounds.Top ||
                      bounds.Right != _lastBounds.Right || bounds.Bottom != _lastBounds.Bottom;
        if (changed)
        {
            if (!NativeMethods.SetWindowPos(_surfaceHandle, overlayHandle,
                    bounds.Left, bounds.Top, width, height, SwpNoActivate)) return;
            // Restore the rounded clipping whenever the backdrop window's
            // bounds change or it is moved between monitors.
            ApplyRegion(width, height, insetX, insetY);
            _lastBounds = bounds;
            _hasBounds = true;
        }
        else if (!wasVisible || topmostChanged)
        {
            // A Topmost change can move the material above the interactive WPF window.
            NativeMethods.SetWindowPos(_surfaceHandle, overlayHandle,
                0, 0, 0, 0, 0x0001 | 0x0002 | SwpNoActivate);
            if (!wasVisible) ApplyRegion(width, height, insetX, insetY);
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
        if (overlayHandle == 0) return;
        NativeMethods.SetWindowPos(_surfaceHandle, overlayHandle,
            0, 0, 0, 0, 0x0001 | 0x0002 | SwpNoActivate);
    }

    private bool CreateSurface()
    {
        if (AcrylicClass.Value == 0) return false;
        _surfaceHandle = NativeMethods.CreateWindowEx(
            (int)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate |
                  NativeMethods.WsExTransparent),
            AcrylicWindowClassName, "", unchecked((int)0x80000000),
            -10000, -10000, 2, 2, 0, 0, NativeMethods.GetModuleHandle(null), 0);
        return _surfaceHandle != 0;
    }

    private static ushort RegisterAcrylicClass()
    {
        var registration = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(AcrylicProcedure),
            Instance = NativeMethods.GetModuleHandle(null),
            Name = AcrylicWindowClassName
        };
        return NativeMethods.RegisterClass(ref registration);
    }

    private static nint AcrylicWindowProcedure(nint window, int message, nint wParam, nint lParam)
    {
        if (message == WmNcHitTest) return HtTransparent;
        if (message == WmMouseActivate) return MaNoActivate;
        if (message == 0x0014) return 1; // WM_ERASEBKGND: leave the acrylic material visible.
        if (message == 0x000F) { NativeMethods.ValidateRect(window, 0); return 0; } // WM_PAINT
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private nint OverlayHandle() =>
        (PresentationSource.FromVisual(_overlay) as HwndSource)?.Handle ?? 0;

    private bool OverlayTopmost(nint handle) => _overlayWindow?.Topmost ??
        (handle != 0 && (NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64() &
                         NativeMethods.WsExTopmost) != 0);

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

    internal static int SafeBackdropInset(double radius, double dpiScale) =>
        Math.Max(2, (int)Math.Ceiling(radius * dpiScale * (1 - 1 / Math.Sqrt(2))) + 2);

    private void ApplyRegion(int width, int height, int insetX, int insetY)
    {
        if (_cornerRadius <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(_overlay);
        var roundX = Math.Max(2, (int)Math.Round((_cornerRadius * dpi.DpiScaleX - insetX) * 2));
        var roundY = Math.Max(2, (int)Math.Round((_cornerRadius * dpi.DpiScaleY - insetY) * 2));
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
        var handle = _surfaceHandle;
        _surfaceHandle = 0;
        _surfaceVisible = false;
        _hasBounds = false;
        if (handle != 0) NativeMethods.DestroyWindow(handle);
    }

    private void HideSurface()
    {
        if (!_surfaceVisible || _surfaceHandle == 0) return;
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
