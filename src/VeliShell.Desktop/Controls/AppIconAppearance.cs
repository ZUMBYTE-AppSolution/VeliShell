using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VeliShell.Desktop.Controls;

internal sealed record AppIconAppearance(Rect NormalizedArtworkBounds)
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
            var pixels = RenderSquareSample(source);
            return new AppIconAppearance(FindArtworkBounds(pixels));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Empty();
        }
    }

    private static byte[] RenderSquareSample(ImageSource source)
    {
        var width = source.Width;
        var height = source.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            width = height = SampleSize;

        var scale = Math.Min(SampleSize / width, SampleSize / height);
        var renderWidth = width * scale;
        var renderHeight = height * scale;
        var target = new Rect(
            (SampleSize - renderWidth) / 2,
            (SampleSize - renderHeight) / 2,
            renderWidth,
            renderHeight);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(source, target);

        var sample = new RenderTargetBitmap(SampleSize, SampleSize, 96, 96, PixelFormats.Pbgra32);
        sample.Render(visual);
        var pixels = new byte[SampleSize * SampleSize * 4];
        sample.CopyPixels(pixels, SampleSize * 4, 0);
        return pixels;
    }

    private static Rect FindArtworkBounds(byte[] pixels)
    {
        var left = SampleSize;
        var top = SampleSize;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < SampleSize; y++)
        {
            for (var x = 0; x < SampleSize; x++)
            {
                if (pixels[(y * SampleSize + x) * 4 + 3] < ArtworkAlphaThreshold) continue;
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
        var side = Math.Clamp(Math.Max(width, height) / (double)SampleSize, 1d / SampleSize, 1);
        var sourceCenterX = (left + right + 1) / (2d * SampleSize);
        var sourceCenterY = (top + bottom + 1) / (2d * SampleSize);
        return new Rect(sourceCenterX - side / 2, sourceCenterY - side / 2, side, side);
    }

}
