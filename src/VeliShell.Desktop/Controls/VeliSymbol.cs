using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace VeliShell.Desktop.Controls;

/// <summary>
/// Material Symbols Rounded vectors on a shared 24x24 optical grid.
/// The bundled Apache-2.0 paths render without a network connection.
/// </summary>
public sealed class VeliSymbol : Control
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(VeliSymbolKind), typeof(VeliSymbol),
        new FrameworkPropertyMetadata(VeliSymbolKind.Info,
            FrameworkPropertyMetadataOptions.AffectsRender));

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
        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(Foreground, null, MaterialSymbolPaths.For(Symbol));
        drawingContext.Pop();
        drawingContext.Pop();
    }
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
    Microphone,
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
