using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VeliShell.Desktop.Controls;

/// <summary>
/// Keeps app artwork centered, exactly sized, and continuously rounded. Source
/// alpha bounds normalize transparent safe zones; VeliShell deliberately draws
/// no generated color, gradient, tile, or backdrop behind the icon. Explicit
/// freeform utility art (such as the Recycle Bin and folder shortcuts) preserves its source canvas.
/// </summary>
internal sealed class AppIconSurface : Grid
{
    private readonly Canvas _plate;
    private AppIconAppearance _appearance;
    private bool _freeform;

    internal Image IconImage { get; }
    internal FrameworkElement PlateElement => _plate;
    internal Rect NormalizedArtworkBounds => _appearance.NormalizedArtworkBounds;
    // Kept temporarily for binary/test compatibility while callers move to the
    // more accurate name. These are source crop bounds, not output plate bounds.
    internal Rect NormalizedPlateBounds => NormalizedArtworkBounds;

    internal AppIconSurface(ImageSource? source, double side) : this(source, side, freeform: false)
    {
    }

    internal AppIconSurface(ImageSource? source, double side, bool freeform)
    {
        side = Math.Clamp(side, 1, 512);
        Width = side;
        Height = side;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        ClipToBounds = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _freeform = freeform;
        _appearance = source is null ? AppIconAppearance.Empty() : AppIconAppearance.For(source);
        _plate = new Canvas
        {
            Width = side,
            Height = side,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            ClipToBounds = true,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        IconImage = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            // Alpha-bound normalization needs sub-DIP precision. Rounding this
            // oversized source canvas would make compact source icons map to a
            // slightly different visible size at 32/58/96 DIP.
            UseLayoutRounding = false,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(IconImage, BitmapScalingMode.HighQuality);
        _plate.Children.Add(IconImage);
        _plate.SizeChanged += (_, args) =>
        {
            var actualPlateSide = Math.Min(args.NewSize.Width, args.NewSize.Height);
            if (double.IsFinite(actualPlateSide) && actualPlateSide > 0)
                _plate.Clip = _freeform ? null : AppIconMask.Create(actualPlateSide);
        };
        Children.Add(_plate);
        ApplyLayout(side);
    }

    internal void UpdateSource(ImageSource source, bool freeform = false)
    {
        _freeform = freeform;
        _appearance = AppIconAppearance.For(source);
        IconImage.Source = source;
        IconImage.Visibility = Visibility.Visible;
        var actual = Math.Min(RenderSize.Width, RenderSize.Height);
        ApplyLayout(double.IsFinite(actual) && actual > 0 ? actual : Width);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var side = Math.Min(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
        if (double.IsFinite(side) && side > 0) ApplyLayout(side);
    }

    private void ApplyLayout(double side)
    {
        // The configured dock size is the one and only visible plate size.
        // Alpha bounds below affect just the source-image crop/zoom.
        _plate.Width = side;
        _plate.Height = side;
        _plate.Margin = new Thickness(0);
        _plate.Clip = _freeform ? null : AppIconMask.Create(side);

        // macOS-style utility artwork such as the Recycle Bin is intentionally
        // a transparent freeform object rather than a rounded-square app tile.
        // Preserve the shared square source canvas so empty/full states keep
        // exactly the same can size and only the contents change.
        if (_freeform)
        {
            IconImage.Width = side;
            IconImage.Height = side;
            Canvas.SetLeft(IconImage, 0);
            Canvas.SetTop(IconImage, 0);
            return;
        }

        var normalized = _appearance.NormalizedArtworkBounds;
        var normalizedSide = Math.Clamp(Math.Max(normalized.Width, normalized.Height), 1d / 256, 1);
        var sourceCanvasSide = side / normalizedSide;
        var mappedWidth = normalized.Width * sourceCanvasSide;
        var mappedHeight = normalized.Height * sourceCanvasSide;

        IconImage.Width = sourceCanvasSide;
        IconImage.Height = sourceCanvasSide;
        // Render the same square source canvas used by AppIconAppearance, then
        // map its meaningful alpha bounds onto the common side x side plate.
        Canvas.SetLeft(IconImage, (side - mappedWidth) / 2 - normalized.X * sourceCanvasSide);
        Canvas.SetTop(IconImage, (side - mappedHeight) / 2 - normalized.Y * sourceCanvasSide);
    }
}
