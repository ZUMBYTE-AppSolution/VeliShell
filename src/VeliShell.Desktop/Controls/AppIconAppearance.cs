using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VeliShell.Desktop.Controls;

internal sealed record AppIconAppearance(Rect NormalizedArtworkBounds, bool HasTileShape = false)
{
    private const int SampleSize = 256;
    private const byte ArtworkAlphaThreshold = 128;
    private static readonly ConditionalWeakTable<ImageSource, AppIconAppearance> Cache = new();

    internal static AppIconAppearance For(ImageSource source) => Cache.GetValue(source, Create);

    internal static AppIconAppearance Empty() => new(new Rect(0, 0, 1, 1));

    private static AppIconAppearance Create(ImageSource source)
    {
        try
        {
            var pixels = RenderSquareSample(source, SampleSize);
            var bounds = FindArtworkBounds(pixels, out var hasTileShape);
            return new AppIconAppearance(bounds, hasTileShape);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Empty();
        }
    }

    internal static byte[] RenderSquareSample(ImageSource source, int sampleSize)
    {
        var width = source.Width;
        var height = source.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            width = height = sampleSize;

        var scale = Math.Min(sampleSize / width, sampleSize / height);
        var renderWidth = width * scale;
        var renderHeight = height * scale;
        var target = new Rect(
            (sampleSize - renderWidth) / 2,
            (sampleSize - renderHeight) / 2,
            renderWidth,
            renderHeight);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(source, target);

        var sample = new RenderTargetBitmap(sampleSize, sampleSize, 96, 96, PixelFormats.Pbgra32);
        sample.Render(visual);
        var pixels = new byte[sampleSize * sampleSize * 4];
        sample.CopyPixels(pixels, sampleSize * 4, 0);
        return pixels;
    }

    private static Rect FindArtworkBounds(byte[] pixels, out bool hasTileShape)
    {
        hasTileShape = false;
        var left = SampleSize;
        var top = SampleSize;
        var right = -1;
        var bottom = -1;
        var opaquePixels = 0;
        for (var y = 0; y < SampleSize; y++)
        {
            for (var x = 0; x < SampleSize; x++)
            {
                if (pixels[(y * SampleSize + x) * 4 + 3] < ArtworkAlphaThreshold) continue;
                opaquePixels++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top) return new Rect(0, 0, 1, 1);

        // Treat the meaningful alpha area as one square source canvas. It is a
        // crop/normalization hint only: AppIconSurface always renders a fixed
        // plate whose size comes from the dock preference, never from here.
        // The larger extent keeps artwork intact while soft outer shadows and
        // transparent safe zones no longer make one dock icon look smaller.
        var width = right - left + 1;
        var height = bottom - top + 1;
        hasTileShape = IsTileShape(pixels, left, top, width, height, opaquePixels);
        var side = Math.Clamp(Math.Max(width, height) / (double)SampleSize, 1d / SampleSize, 1);
        var sourceCenterX = (left + right + 1) / (2d * SampleSize);
        var sourceCenterY = (top + bottom + 1) / (2d * SampleSize);
        return new Rect(sourceCenterX - side / 2, sourceCenterY - side / 2, side, side);
    }

    private static bool IsTileShape(byte[] pixels, int left, int top, int width, int height, int opaquePixels)
    {
        // Classify the visible silhouette, not the source canvas: ICO/PNG files
        // commonly contain asymmetric transparent padding or a soft shadow.
        // Filled square/rounded-square tiles have near-square extents, a dense
        // body and four long, almost flat sides. Circles, diamonds, folders,
        // sparse glyphs and outlines must not qualify just by their square file.
        if (Math.Min(width, height) < 12 || Math.Min(width, height) / (double)Math.Max(width, height) < .92 ||
            opaquePixels / (double)(width * height) < .84) return false;

        bool HasSide(bool vertical, bool opposite)
        {
            var length = vertical ? height : width;
            var depth = vertical ? width : height;
            var start = (int)Math.Ceiling((length - 1) * .24);
            var end = (int)Math.Floor((length - 1) * .76);
            var tolerance = Math.Max(1, (int)Math.Ceiling(depth * .045));
            var covered = 0;
            for (var position = start; position <= end; position++)
            {
                for (var inset = 0; inset < tolerance; inset++)
                {
                    var edge = opposite ? depth - 1 - inset : inset;
                    var x = left + (vertical ? edge : position);
                    var y = top + (vertical ? position : edge);
                    if (pixels[(y * SampleSize + x) * 4 + 3] < ArtworkAlphaThreshold) continue;
                    covered++;
                    break;
                }
            }
            return covered / (double)(end - start + 1) >= .94;
        }
        return HasSide(true, false) && HasSide(true, true) && HasSide(false, false) && HasSide(false, true);
    }

}
