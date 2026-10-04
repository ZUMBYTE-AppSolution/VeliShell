using System.Windows;
using System.Windows.Media;

namespace VeliShell.Desktop.Views;

internal static class MenuPanelPlacement
{
    internal static void PlaceBelow(FrameworkElement anchor, Window panel)
    {
        var screenPoint = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(anchor);
        var width = double.IsNaN(panel.Width) ? panel.ActualWidth : panel.Width;
        var height = double.IsNaN(panel.Height) ? panel.ActualHeight : panel.Height;
        var bounds = CalculateBounds(
            new Point(screenPoint.X / dpi.DpiScaleX, screenPoint.Y / dpi.DpiScaleY),
            new Size(width, height),
            SystemParameters.WorkArea);

        // Keep the authored height on ordinary displays, but let WPF constrain
        // the window when per-monitor scaling leaves fewer logical DIPs. The
        // panel content can then scroll instead of extending past the work area.
        panel.MaxHeight = bounds.Height;
        panel.Left = bounds.Left;
        panel.Top = bounds.Top;
    }

    private static Rect CalculateBounds(Point anchorBottomRight, Size desiredSize, Rect workArea)
    {
        const double horizontalMargin = 8;
        const double preferredTopMargin = 2;
        const double preferredBottomMargin = 8;
        const double anchorGap = 7;

        if (workArea.IsEmpty || !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) ||
            workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));
        if (!double.IsFinite(desiredSize.Width) || !double.IsFinite(desiredSize.Height) ||
            desiredSize.Width <= 0 || desiredSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(desiredSize));

        var minimumLeft = workArea.Left + horizontalMargin;
        var maximumLeft = Math.Max(minimumLeft, workArea.Right - desiredSize.Width - horizontalMargin);

        // Prefer the existing visual breathing room, but treat the real work-area
        // edges as the hard fallback for unusually small logical work areas.
        var minimumTop = workArea.Top + preferredTopMargin;
        var maximumBottom = workArea.Bottom - preferredBottomMargin;
        if (maximumBottom <= minimumTop)
        {
            minimumTop = workArea.Top;
            maximumBottom = workArea.Bottom;
        }

        var availableHeight = maximumBottom - minimumTop;
        var height = Math.Min(desiredSize.Height, availableHeight);
        var maximumTop = maximumBottom - height;
        var requestedTop = anchorBottomRight.Y + anchorGap;
        var top = Math.Clamp(requestedTop, minimumTop, maximumTop);

        return new Rect(
            Math.Clamp(anchorBottomRight.X - desiredSize.Width, minimumLeft, maximumLeft),
            top,
            desiredSize.Width,
            height);
    }
}
