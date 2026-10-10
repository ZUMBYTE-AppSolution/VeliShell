using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Native;
using VeliShell.Core;

namespace VeliShell.Desktop.Views;

internal sealed class DragGhostWindow : Window, IDisposable
{
    private nint _handle;
    private bool _disposed;
    private bool _snapped;

    internal DragGhostWindow(Window owner, ImageSource source, double iconSize) :
        this(owner, source, iconSize, freeform: false) { }

    internal DragGhostWindow(Window owner, ImageSource source, double iconSize, bool freeform) :
        this(owner, new AppIconSurface(source, Math.Clamp(iconSize, 32, 96), freeform), iconSize) { }

    internal DragGhostWindow(Window owner, Pin folder, double iconSize) :
        this(owner, CreateFolderArtwork(folder, iconSize), iconSize) { }

    private DragGhostWindow(Window owner, FrameworkElement artwork, double iconSize)
    {
        Owner = owner;
        Title = LocalizationService.Current.Get("Product.Name");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;

        var size = Math.Clamp(iconSize, 32, 96);
        Width = size + 20;
        Height = size + 20;
        Opacity = 0.72;

        var shadowHost = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 5,
                Opacity = 0.48,
                Color = Colors.Black
            },
            Children = { artwork }
        };

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { shadowHost }
        };

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                _handle,
                NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent));
        };
        Closed += (_, _) => _handle = 0;
    }

    private static FrameworkElement CreateFolderArtwork(Pin folder, double iconSize)
    {
        var size = Math.Clamp(iconSize, 32, 96);
        if (folder.Icon is not null)
            return new AppIconSurface(IconService.For(
                    folder.Kind == PinKind.VirtualFolder ? "virtual-folder" : folder.Id,
                    folder.Target, folder.Icon), size,
                freeform: true);
        var preview = new DockFolderPreview(size);
        preview.UpdatePin(folder);
        return preview;
    }

    internal void ShowAtCursor()
    {
        if (_disposed) return;
        if (!IsVisible) Show();
        _snapped = false;
        MoveToCursor();
    }

    internal void MoveToCursor()
    {
        if (_disposed || _snapped || _handle == 0 || !NativeMethods.GetCursorPos(out var cursor)) return;

        var monitor = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        var monitorHandle = NativeMethods.MonitorFromPoint(cursor, 2); // MONITOR_DEFAULTTONEAREST
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitor))
            monitor.Work = monitor.Monitor = new NativeMethods.Rect { Right = 1920, Bottom = 1080 };

        var width = 96;
        var height = 96;
        if (NativeMethods.GetWindowRect(_handle, out var bounds))
        {
            width = Math.Max(1, bounds.Right - bounds.Left);
            height = Math.Max(1, bounds.Bottom - bounds.Top);
        }

        var x = Math.Clamp(cursor.X + 18, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        var y = Math.Clamp(cursor.Y + 20, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        NativeMethods.SetWindowPos(_handle, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    internal void FollowCursor()
    {
        if (_disposed) return;
        _snapped = false;
        if (!IsVisible) Show();
        MoveToCursor();
    }

    internal bool SnapTo(FrameworkElement anchor)
    {
        if (_disposed || _handle == 0) return false;
        try
        {
            var center = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
            var cursor = new NativeMethods.Point
            {
                X = (int)Math.Round(center.X),
                Y = (int)Math.Round(center.Y)
            };
            var monitor = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            var monitorHandle = NativeMethods.MonitorFromPoint(cursor, 2); // MONITOR_DEFAULTTONEAREST
            if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitor))
                monitor.Work = monitor.Monitor = new NativeMethods.Rect { Right = 1920, Bottom = 1080 };

            var width = 96;
            var height = 96;
            if (NativeMethods.GetWindowRect(_handle, out var bounds))
            {
                width = Math.Max(1, bounds.Right - bounds.Left);
                height = Math.Max(1, bounds.Bottom - bounds.Top);
            }

            var x = Math.Clamp(cursor.X - width / 2, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
            var y = Math.Clamp(cursor.Y - height / 2, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
            _snapped = true;
            if (!IsVisible) Show();
            NativeMethods.SetWindowPos(_handle, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            return true;
        }
        catch (InvalidOperationException)
        {
            _snapped = false;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // The dispatcher may already have closed the owned window.
        }
    }
}
