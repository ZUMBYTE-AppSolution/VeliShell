using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

internal sealed class WindowThumbnailPreview : Window, IDisposable
{
    private const uint DestinationRectangle = 0x00000001;
    private const uint OpacityFlag = 0x00000004;
    private const uint VisibleFlag = 0x00000008;
    private const uint SourceClientAreaOnlyFlag = 0x00000010;

    private readonly StackPanel _fallback;
    private readonly Image _fallbackIcon;
    private readonly TextBlock _fallbackTitle;
    private HwndSource? _source;
    private nint _handle;
    private nint _thumbnail;
    private bool _disposed;

    internal WindowThumbnailPreview(Window owner)
    {
        Owner = owner;
        Title = LocalizationService.Current.Get("Preview.WindowTitle");
        Width = 336;
        Height = 216;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        SetResourceReference(BackgroundProperty, "MenuSurface");

        _fallbackIcon = new Image
        {
            Width = 58,
            Height = 58,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(_fallbackIcon, BitmapScalingMode.HighQuality);
        _fallbackTitle = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 270,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _fallbackTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var unavailable = new TextBlock
        {
            Text = LocalizationService.Current.Get("Preview.Unavailable"),
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0)
        };
        unavailable.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        _fallback = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _fallbackIcon,
                _fallbackTitle,
                unavailable
            }
        };

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                ShadowDepth = 7,
                Opacity = 0.36
            },
            Child = _fallback
        };
        frame.SetResourceReference(Border.BackgroundProperty, "MenuSurface");
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
        Content = frame;

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            ReleaseThumbnail();
            _source?.RemoveHook(WindowHook);
            _source = null;
        };
    }

    internal void ShowFor(nint sourceWindow, FrameworkElement anchor, string title, ImageSource fallbackIcon)
    {
        if (_disposed) return;
        _fallbackIcon.Source = fallbackIcon;
        _fallbackTitle.Text = string.IsNullOrWhiteSpace(title) ? LocalizationService.Current.Get("Preview.Window") : title;
        _fallback.Visibility = Visibility.Visible;

        Prepare();
        UpdateLayout();
        PositionBeside(anchor);
        if (TryAttachThumbnail(sourceWindow))
            _fallback.Visibility = Visibility.Collapsed;
        Opacity = 1;
    }

    internal void Prepare()
    {
        if (_disposed || IsVisible) return;
        Opacity = 0;
        Show();
    }

    internal void HidePreview()
    {
        if (_disposed) return;
        Opacity = 0;
        ReleaseThumbnail();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            (nint)(style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent));
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowHook);

        var rounded = 2; // DWMWCP_ROUND
        NativeMethods.DwmSetWindowAttribute(_handle, 33, ref rounded, sizeof(int));
    }

    private static nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmNcHitTest = 0x0084;
        const int HitTransparent = -1;
        if (message != WmNcHitTest) return nint.Zero;
        handled = true;
        return (nint)HitTransparent;
    }

    private bool TryAttachThumbnail(nint sourceWindow)
    {
        ReleaseThumbnail();
        if (_handle == 0 || sourceWindow == 0 || !NativeMethods.IsWindow(sourceWindow)) return false;

        try
        {
            var result = NativeMethods.DwmRegisterThumbnail(_handle, sourceWindow, out _thumbnail);
            if (result < 0 || _thumbnail == 0)
            {
                ReleaseThumbnail();
                return false;
            }

            result = NativeMethods.DwmQueryThumbnailSourceSize(_thumbnail, out var sourceSize);
            if (result < 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0)
            {
                ReleaseThumbnail();
                return false;
            }

            if (!NativeMethods.GetClientRect(_handle, out var client) || client.Right <= 0 || client.Bottom <= 0)
            {
                ReleaseThumbnail();
                return false;
            }

            const int inset = 8;
            var availableWidth = Math.Max(1, client.Right - inset * 2);
            var availableHeight = Math.Max(1, client.Bottom - inset * 2);
            var scale = Math.Min(
                availableWidth / (double)sourceSize.Width,
                availableHeight / (double)sourceSize.Height);
            var width = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
            var height = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
            var left = (client.Right - width) / 2;
            var top = (client.Bottom - height) / 2;
            var properties = new NativeMethods.DwmThumbnailProperties
            {
                Flags = DestinationRectangle | OpacityFlag | VisibleFlag | SourceClientAreaOnlyFlag,
                Destination = new NativeMethods.Rect
                {
                    Left = left,
                    Top = top,
                    Right = left + width,
                    Bottom = top + height
                },
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = false
            };
            result = NativeMethods.DwmUpdateThumbnailProperties(_thumbnail, ref properties);
            if (result >= 0) return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            // Older/disabled DWM environments use the local icon-and-title fallback.
        }

        ReleaseThumbnail();
        return false;
    }

    private void PositionBeside(FrameworkElement anchor)
    {
        if (_handle == 0) return;

        NativeMethods.Point topLeft;
        NativeMethods.Point bottomRight;
        try
        {
            var first = anchor.PointToScreen(new Point(0, 0));
            var second = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            topLeft = new NativeMethods.Point { X = (int)Math.Round(first.X), Y = (int)Math.Round(first.Y) };
            bottomRight = new NativeMethods.Point { X = (int)Math.Round(second.X), Y = (int)Math.Round(second.Y) };
        }
        catch (InvalidOperationException)
        {
            if (!NativeMethods.GetCursorPos(out topLeft)) return;
            bottomRight = topLeft;
        }

        var monitor = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromPoint(bottomRight, 2), ref monitor))
            monitor.Work = monitor.Monitor = new NativeMethods.Rect { Right = 1920, Bottom = 1080 };

        var width = 336;
        var height = 216;
        if (NativeMethods.GetWindowRect(_handle, out var bounds))
        {
            width = Math.Max(1, bounds.Right - bounds.Left);
            height = Math.Max(1, bounds.Bottom - bounds.Top);
        }

        var x = bottomRight.X + 10;
        if (x + width > monitor.Work.Right) x = topLeft.X - width - 10;
        x = Math.Clamp(x, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        var y = Math.Clamp(topLeft.Y - 18, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        NativeMethods.SetWindowPos(_handle, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    private void ReleaseThumbnail()
    {
        if (_thumbnail == 0) return;
        NativeMethods.DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseThumbnail();
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // The owner dispatcher is already shutting down.
        }
    }
}
