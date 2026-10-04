using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VeliShell.Desktop.Controls;

/// <summary>
/// Keeps artwork and its generated accent plate in one centered, continuously
/// rounded surface. Every instance has exactly the requested dock size. Source
/// alpha bounds are used only to normalize transparent safe zones into that
/// fixed plate; source pixels can never make the plate itself larger or smaller.
/// </summary>
internal sealed class AppIconSurface : Grid
{
    private readonly Canvas _plate;
    private AppIconAppearance _appearance;

    internal Border AccentBackground { get; }
    internal Image IconImage { get; }
    internal FrameworkElement PlateElement => _plate;
    internal Rect NormalizedArtworkBounds => _appearance.NormalizedArtworkBounds;
    // Kept temporarily for binary/test compatibility while callers move to the
    // more accurate name. These are source crop bounds, not output plate bounds.
    internal Rect NormalizedPlateBounds => NormalizedArtworkBounds;

    internal AppIconSurface(ImageSource? source, double side, Brush? background = null)
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

        _appearance = source is null
            ? AppIconAppearance.Empty(background ?? Brushes.Transparent)
            : AppIconAppearance.For(source);
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
        AccentBackground = new Border
        {
            Background = background ?? _appearance.AccentBrush,
            IsHitTestVisible = false,
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
        _plate.Children.Add(AccentBackground);
        _plate.Children.Add(IconImage);
        _plate.SizeChanged += (_, args) =>
        {
            var actualPlateSide = Math.Min(args.NewSize.Width, args.NewSize.Height);
            if (double.IsFinite(actualPlateSide) && actualPlateSide > 0)
                _plate.Clip = AppIconMask.Create(actualPlateSide);
        };
        Children.Add(_plate);
        ApplyLayout(side);
    }

    internal void UpdateSource(ImageSource source, Brush? background = null)
    {
        _appearance = AppIconAppearance.For(source);
        IconImage.Source = source;
        IconImage.Visibility = Visibility.Visible;
        AccentBackground.Background = background ?? _appearance.AccentBrush;
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
        _plate.Clip = AppIconMask.Create(side);

        AccentBackground.Width = side;
        AccentBackground.Height = side;
        Canvas.SetLeft(AccentBackground, 0);
        Canvas.SetTop(AccentBackground, 0);

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
