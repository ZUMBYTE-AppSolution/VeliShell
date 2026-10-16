using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Places native acrylic in pixel-row bands matching the rounded WPF plate.
/// The accent compositor ignores a Win32 window region on some systems, so a
/// single inset HWND leaves a rectangular hole and a single full HWND leaks
/// into the transparent corners. Rectangular bands avoid both failure modes.
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
    private readonly List<AcrylicTile> _tiles = new();
    private Color _tint;
    private bool _active;
    private bool _surfaceTopmost;
    private bool _disposed;
    private long _motionUntil;

    private sealed class AcrylicTile
    {
        internal required nint Handle { get; init; }
        internal NativeMethods.Rect Bounds;
        internal bool HasBounds;
        internal bool Visible;
    }

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
        _tint = tint;
        if (!EnsureTileCount(1) || _tiles.Any(tile => !SetAccent(tile.Handle, tint)))
        {
            CloseSurface();
            return false;
        }
        _active = true;
        Sync();
        return _active;
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
        if (!_active || _disposed || _tiles.Count == 0) return;
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
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < 2 || height < 2)
        {
            HideSurface();
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(_overlay);
        var radiusX = Math.Min((int)Math.Round(_cornerRadius * dpi.DpiScaleX), (width - 2) / 2);
        var radiusY = Math.Min((int)Math.Round(_cornerRadius * dpi.DpiScaleY), height / 2);
        var bands = BuildBands(bounds, radiusX, radiusY);
        if (!EnsureTileCount(bands.Count))
        {
            CloseSurface();
            return;
        }
        var topmost = OverlayTopmost(overlayHandle);
        var topmostChanged = _surfaceTopmost != topmost;
        _surfaceTopmost = topmost;
        for (var index = 0; index < _tiles.Count; index++)
        {
            var tile = _tiles[index];
            if (index >= bands.Count)
            {
                if (tile.Visible) NativeMethods.ShowWindow(tile.Handle, NativeMethods.SwHide);
                tile.Visible = false;
                continue;
            }
            var band = bands[index];
            var wasVisible = tile.Visible;
            if (!wasVisible)
            {
                NativeMethods.ShowWindow(tile.Handle, NativeMethods.SwShowNoActivate);
                tile.Visible = true;
            }
            var changed = !tile.HasBounds || band.Left != tile.Bounds.Left || band.Top != tile.Bounds.Top ||
                          band.Right != tile.Bounds.Right || band.Bottom != tile.Bounds.Bottom;
            if (changed || !wasVisible || topmostChanged)
            {
                if (!NativeMethods.SetWindowPos(tile.Handle, overlayHandle,
                        band.Left, band.Top, band.Right - band.Left, band.Bottom - band.Top, SwpNoActivate))
                {
                    CloseSurface();
                    return;
                }
                tile.Bounds = band;
                tile.HasBounds = true;
            }
        }
    }

    private static List<NativeMethods.Rect> BuildBands(NativeMethods.Rect bounds, int radiusX, int radiusY)
    {
        var height = bounds.Bottom - bounds.Top;
        var bands = new List<NativeMethods.Rect>();
        var firstRow = 0;
        var currentInset = -1;
        for (var row = 0; row <= height; row++)
        {
            var inset = row == height ? -1 : RoundedRowInset(radiusX, radiusY, Math.Min(row, height - 1 - row));
            if (row == 0) { currentInset = inset; continue; }
            if (inset == currentInset) continue;
            bands.Add(new NativeMethods.Rect
            {
                Left = bounds.Left + currentInset,
                Top = bounds.Top + firstRow,
                Right = bounds.Right - currentInset,
                Bottom = bounds.Top + row
            });
            firstRow = row;
            currentInset = inset;
        }
        return bands;
    }

    internal static int RoundedRowInset(int radiusX, int radiusY, int row)
    {
        if (radiusX <= 0 || radiusY <= 0 || row >= radiusY) return 0;
        var normalizedY = (radiusY - row - 0.5) / radiusY;
        return (int)Math.Ceiling(radiusX * (1 - Math.Sqrt(1 - normalizedY * normalizedY)));
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
        if (!_active || _disposed || _tiles.Count == 0) return;
        var overlayHandle = OverlayHandle();
        if (overlayHandle == 0) return;
        foreach (var tile in _tiles.Where(tile => tile.Visible))
            NativeMethods.SetWindowPos(tile.Handle, overlayHandle,
                0, 0, 0, 0, 0x0001 | 0x0002 | SwpNoActivate);
    }

    private bool EnsureTileCount(int count)
    {
        while (_tiles.Count < count)
        {
            var handle = CreateSurface();
            if (handle == 0) return false;
            _tiles.Add(new AcrylicTile { Handle = handle });
            if (!SetAccent(handle, _tint)) return false;
        }
        return true;
    }

    private static nint CreateSurface()
    {
        if (AcrylicClass.Value == 0) return 0;
        return NativeMethods.CreateWindowEx(
            (int)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate |
                  NativeMethods.WsExTransparent),
            AcrylicWindowClassName, "", unchecked((int)0x80000000),
            -10000, -10000, 2, 2, 0, 0, NativeMethods.GetModuleHandle(null), 0);
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

    private static bool SetAccent(nint handle, Color tint)
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
            return NativeMethods.SetWindowCompositionAttribute(handle, ref data);
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
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
        foreach (var tile in _tiles)
            NativeMethods.DestroyWindow(tile.Handle);
        _tiles.Clear();
    }

    private void HideSurface()
    {
        foreach (var tile in _tiles.Where(tile => tile.Visible))
        {
            NativeMethods.ShowWindow(tile.Handle, NativeMethods.SwHide);
            tile.Visible = false;
        }
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
