using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VeliShell.Desktop.Controls;

internal sealed record AppIconAppearance(Rect NormalizedArtworkBounds, Brush AccentBrush)
{
    private const int SampleSize = 256;
    private const byte ArtworkAlphaThreshold = 128;
    private const int HueBucketCount = 18;
    private static readonly ConditionalWeakTable<ImageSource, AppIconAppearance> Cache = new();

    internal static AppIconAppearance For(ImageSource source) => Cache.GetValue(source, Create);

    internal static AppIconAppearance Empty(Brush background) =>
        new(new Rect(0, 0, 1, 1), background);

    private static AppIconAppearance Create(ImageSource source)
    {
        var neutral = Color.FromRgb(112, 122, 142);
        try
        {
            var pixels = RenderSquareSample(source);
            var bounds = FindArtworkBounds(pixels);
            var dominant = TryGetDominantColor(pixels, out var sampled) ? sampled : neutral;
            return new AppIconAppearance(bounds, CreateAccentBrush(dominant, neutral));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new AppIconAppearance(new Rect(0, 0, 1, 1), CreateAccentBrush(neutral, neutral));
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

    private static Brush CreateAccentBrush(Color dominant, Color neutral)
    {
        // Pull highly saturated artwork slightly toward slate so the mark stays
        // legible in both light and dark docks.
        var basis = Blend(dominant, neutral, 0.18);
        var top = Blend(basis, Colors.White, 0.36);
        var middle = Blend(basis, Colors.White, 0.08);
        var bottom = Blend(basis, Colors.Black, 0.24);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.12, 0),
            EndPoint = new Point(0.88, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(218, top.R, top.G, top.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(205, middle.R, middle.G, middle.B), 0.52));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(222, bottom.R, bottom.G, bottom.B), 1));
        brush.Freeze();
        return brush;
    }

    private static bool TryGetDominantColor(byte[] pixels, out Color color)
    {
        color = default;
        var weights = new double[HueBucketCount];
        var red = new double[HueBucketCount];
        var green = new double[HueBucketCount];
        var blue = new double[HueBucketCount];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha < 48) continue;

            var unpremultiply = 255d / alpha;
            var b = Math.Min(255, pixels[offset] * unpremultiply);
            var g = Math.Min(255, pixels[offset + 1] * unpremultiply);
            var r = Math.Min(255, pixels[offset + 2] * unpremultiply);
            var maximum = Math.Max(r, Math.Max(g, b));
            var minimum = Math.Min(r, Math.Min(g, b));
            var range = maximum - minimum;
            if (maximum <= 0 || range < 18) continue;

            var saturation = range / maximum;
            if (saturation < 0.18 || (minimum > 232 && saturation < 0.35)) continue;

            double hue;
            if (maximum == r)
                hue = ((g - b) / range) % 6;
            else if (maximum == g)
                hue = ((b - r) / range) + 2;
            else
                hue = ((r - g) / range) + 4;
            if (hue < 0) hue += 6;

            var bucket = Math.Min(HueBucketCount - 1, (int)(hue / 6 * HueBucketCount));
            var value = maximum / 255d;
            var weight = alpha / 255d * saturation * (0.45 + value * 0.55);
            weights[bucket] += weight;
            red[bucket] += r * weight;
            green[bucket] += g * weight;
            blue[bucket] += b * weight;
        }

        var selected = 0;
        for (var index = 1; index < weights.Length; index++)
            if (weights[index] > weights[selected]) selected = index;
        if (weights[selected] < 0.2) return false;

        color = Color.FromRgb(
            (byte)Math.Clamp(Math.Round(red[selected] / weights[selected]), 0, 255),
            (byte)Math.Clamp(Math.Round(green[selected] / weights[selected]), 0, 255),
            (byte)Math.Clamp(Math.Round(blue[selected] / weights[selected]), 0, 255));
        return true;
    }

    private static Color Blend(Color first, Color second, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(first.R + (second.R - first.R) * amount),
            (byte)Math.Round(first.G + (second.G - first.G) * amount),
            (byte)Math.Round(first.B + (second.B - first.B) * amount));
    }
}
