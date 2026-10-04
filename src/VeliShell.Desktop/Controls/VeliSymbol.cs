using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace VeliShell.Desktop.Controls;

/// <summary>
/// Small, dependency-free vector symbols drawn on a shared 24x24 optical grid.
/// The artwork is original to VeliShell and intentionally does not bundle a
/// platform-vendor symbol font or exported third-party glyphs.
/// </summary>
public sealed class VeliSymbol : Control
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(VeliSymbolKind), typeof(VeliSymbol),
        new FrameworkPropertyMetadata(VeliSymbolKind.Info,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(VeliSymbol),
        new FrameworkPropertyMetadata(1.75d,
            FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double thickness && double.IsFinite(thickness) && thickness >= 0d);

    static VeliSymbol()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(VeliSymbol),
            new FrameworkPropertyMetadata(typeof(VeliSymbol)));
        FocusableProperty.OverrideMetadata(typeof(VeliSymbol),
            new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(VeliSymbol),
            new FrameworkPropertyMetadata(false));
    }

    public VeliSymbolKind Symbol
    {
        get => (VeliSymbolKind)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        const double preferredSize = 18d;
        var width = double.IsInfinity(constraint.Width)
            ? preferredSize
            : Math.Min(preferredSize, constraint.Width);
        var height = double.IsInfinity(constraint.Height)
            ? preferredSize
            : Math.Min(preferredSize, constraint.Height);
        return new Size(width, height);
    }

    // The symbols are decorative children of already named buttons and navigation
    // choices. Keeping them out of the automation tree avoids duplicate, unnamed
    // controls while the actionable parent remains fully accessible.
    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0 || Foreground is null) return;

        const double designSize = 24d;
        var scale = Math.Min(ActualWidth, ActualHeight) / designSize;
        var offsetX = (ActualWidth - (designSize * scale)) / 2d;
        var offsetY = (ActualHeight - (designSize * scale)) / 2d;
        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        DrawSymbol(drawingContext, pen);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void DrawSymbol(DrawingContext dc, Pen pen)
    {
        switch (Symbol)
        {
            case VeliSymbolKind.Appearance:
                Circle(dc, pen, 12, 12, 4);
                foreach (var (x1, y1, x2, y2) in new[]
                {
                    (12d, 2d, 12d, 4d), (12d, 20d, 12d, 22d),
                    (2d, 12d, 4d, 12d), (20d, 12d, 22d, 12d),
                    (4.9d, 4.9d, 6.3d, 6.3d), (17.7d, 17.7d, 19.1d, 19.1d),
                    (17.7d, 6.3d, 19.1d, 4.9d), (4.9d, 19.1d, 6.3d, 17.7d)
                }) Line(dc, pen, x1, y1, x2, y2);
                break;
            case VeliSymbolKind.Dock:
                RoundedRect(dc, pen, 2.5, 6.5, 19, 12.5, 3.2);
                Line(dc, pen, 5, 16, 19, 16);
                Circle(dc, pen, 7, 11.2, 1.45);
                Circle(dc, pen, 12, 11.2, 1.45);
                Circle(dc, pen, 17, 11.2, 1.45);
                break;
            case VeliSymbolKind.Apps:
                RoundedRect(dc, pen, 3, 3, 7, 7, 1.8);
                RoundedRect(dc, pen, 14, 3, 7, 7, 1.8);
                RoundedRect(dc, pen, 3, 14, 7, 7, 1.8);
                RoundedRect(dc, pen, 14, 14, 7, 7, 1.8);
                break;
            case VeliSymbolKind.Windows:
                RoundedRect(dc, pen, 3, 6, 14, 13, 2.4);
                RoundedRect(dc, pen, 7, 3, 14, 13, 2.4);
                break;
            case VeliSymbolKind.Info:
                Circle(dc, pen, 12, 12, 9);
                Line(dc, pen, 12, 10.6, 12, 17);
                Circle(dc, pen, 12, 7.2, .35);
                break;
            case VeliSymbolKind.Network:
                Arc(dc, pen, "M 3.2 9.4 C 8.2 4.8, 15.8 4.8, 20.8 9.4");
                Arc(dc, pen, "M 6.4 12.8 C 9.6 9.8, 14.4 9.8, 17.6 12.8");
                Arc(dc, pen, "M 9.6 16.1 C 10.9 14.9, 13.1 14.9, 14.4 16.1");
                Circle(dc, pen, 12, 19.2, .45);
                break;
            case VeliSymbolKind.Sound:
                var speaker = Geometry.Parse("M 3 10 L 7 10 L 12 6 L 12 18 L 7 14 L 3 14 Z");
                dc.DrawGeometry(null, pen, speaker);
                Arc(dc, pen, "M 15 9 C 17 10.7, 17 13.3, 15 15");
                Arc(dc, pen, "M 17.8 6.5 C 22 10, 22 14, 17.8 17.5");
                break;
            case VeliSymbolKind.Battery:
                RoundedRect(dc, pen, 2.5, 7, 18, 10, 2.2);
                Line(dc, pen, 22, 10, 22, 14);
                RoundedRect(dc, pen, 5, 9.5, 10.5, 5, 1.2);
                break;
            case VeliSymbolKind.Bluetooth:
                // A neutral short-range-radio mark rather than the protected
                // Bluetooth trademark artwork.
                Circle(dc, pen, 12, 12, 2.2);
                Arc(dc, pen, "M 8.6 8.6 C 6.7 10.5, 6.7 13.5, 8.6 15.4");
                Arc(dc, pen, "M 15.4 8.6 C 17.3 10.5, 17.3 13.5, 15.4 15.4");
                Arc(dc, pen, "M 5.8 5.8 C 2.3 9.3, 2.3 14.7, 5.8 18.2");
                Arc(dc, pen, "M 18.2 5.8 C 21.7 9.3, 21.7 14.7, 18.2 18.2");
                break;
            case VeliSymbolKind.Focus:
                Arc(dc, pen, "M 18.4 15.9 C 15.7 20, 9.8 20.7, 6.2 17.2 C 2.7 13.8, 3.3 7.9, 7.4 5.2 C 8.7 4.3, 10.2 3.8, 11.7 3.7 C 9.7 6.4, 9.9 10.3, 12.3 12.7 C 14 14.4, 16.2 15.5, 18.4 15.9 Z");
                break;
            case VeliSymbolKind.Display:
                RoundedRect(dc, pen, 2.8, 4.2, 18.4, 13.2, 2.2);
                Line(dc, pen, 9, 20.3, 15, 20.3);
                Line(dc, pen, 12, 17.5, 12, 20.1);
                Circle(dc, pen, 12, 10.7, 2.15);
                break;
            case VeliSymbolKind.Notification:
                Arc(dc, pen, "M 5 17 C 7 15.2, 7.2 13.1, 7.2 10.4 C 7.2 7.5, 9.2 5.4, 12 5.4 C 14.8 5.4, 16.8 7.5, 16.8 10.4 C 16.8 13.1, 17 15.2, 19 17 Z");
                Line(dc, pen, 4.6, 17, 19.4, 17);
                Arc(dc, pen, "M 9.8 19.3 C 10.8 20.4, 13.2 20.4, 14.2 19.3");
                Line(dc, pen, 12, 3.4, 12, 4.5);
                break;
            case VeliSymbolKind.ControlCenter:
                Line(dc, pen, 3, 7, 21, 7);
                Circle(dc, pen, 8, 7, 2.35);
                Line(dc, pen, 3, 17, 21, 17);
                Circle(dc, pen, 16, 17, 2.35);
                break;
            case VeliSymbolKind.Update:
                Arc(dc, pen, "M 19.2 8.3 C 17.8 4.9, 14.3 3, 10.7 3.6 C 7.1 4.2, 4.3 7.1, 4 10.8 C 3.6 15.3, 7 19.4, 11.5 19.8 C 15.7 20.2, 19.4 17.4, 20.2 13.4");
                Arc(dc, pen, "M 15.4 7.9 L 19.6 8.7 L 20.4 4.5");
                break;
            case VeliSymbolKind.Search:
                Circle(dc, pen, 10.2, 10.2, 6.2);
                Line(dc, pen, 14.8, 14.8, 20.5, 20.5);
                break;
            case VeliSymbolKind.Power:
                Arc(dc, pen, "M 8.1 5.2 C 4.9 6.8, 3 10, 3.7 13.8 C 4.5 18.1, 8.6 20.8, 12.9 20.1 C 17.2 19.4, 20.1 15.5, 19.6 11.1 C 19.3 8.4, 17.8 6.3, 15.8 5.2");
                Line(dc, pen, 12, 2.5, 12, 11.2);
                break;
            case VeliSymbolKind.Close:
                Line(dc, pen, 5.5, 5.5, 18.5, 18.5);
                Line(dc, pen, 18.5, 5.5, 5.5, 18.5);
                break;
            case VeliSymbolKind.ChevronRight:
                Arc(dc, pen, "M 8.5 4.5 L 16 12 L 8.5 19.5");
                break;
            case VeliSymbolKind.ArrowLeft:
                Arc(dc, pen, "M 10 5 L 3 12 L 10 19");
                Line(dc, pen, 3.5, 12, 21, 12);
                break;
            case VeliSymbolKind.Folder:
                Arc(dc, pen, "M 2.5 7.5 L 2.5 18.5 C 2.5 20 3.5 21 5 21 L 19 21 C 20.5 21 21.5 20 21.5 18.5 L 21.5 8.5 C 21.5 7 20.5 6 19 6 L 12 6 L 9.5 3.5 L 5 3.5 C 3.5 3.5 2.5 4.5 2.5 6 Z");
                break;
            case VeliSymbolKind.Document:
                Arc(dc, pen, "M 5 2.5 L 14 2.5 L 19 7.5 L 19 21.5 L 5 21.5 Z");
                Arc(dc, pen, "M 14 2.8 L 14 8 L 18.7 8");
                Line(dc, pen, 8, 12, 16, 12);
                Line(dc, pen, 8, 16, 16, 16);
                break;
            case VeliSymbolKind.External:
                RoundedRect(dc, pen, 3, 7, 14, 14, 2.2);
                Arc(dc, pen, "M 11 4 L 20 4 L 20 13");
                Line(dc, pen, 19.5, 4.5, 10, 14);
                break;
        }
    }

    private static void Line(DrawingContext dc, Pen pen, double x1, double y1, double x2, double y2) =>
        dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));

    private static void Circle(DrawingContext dc, Pen pen, double x, double y, double radius) =>
        dc.DrawEllipse(null, pen, new Point(x, y), radius, radius);

    private static void RoundedRect(DrawingContext dc, Pen pen, double x, double y,
        double width, double height, double radius) =>
        dc.DrawRoundedRectangle(null, pen, new Rect(x, y, width, height), radius, radius);

    private static void Arc(DrawingContext dc, Pen pen, string path) =>
        dc.DrawGeometry(null, pen, Geometry.Parse(path));
}

public enum VeliSymbolKind
{
    Appearance,
    Dock,
    Apps,
    Windows,
    Info,
    Network,
    Sound,
    Battery,
    Bluetooth,
    Focus,
    Display,
    Notification,
    ControlCenter,
    Update,
    Search,
    Power,
    Close,
    ChevronRight,
    ArrowLeft,
    Folder,
    Document,
    External
}
