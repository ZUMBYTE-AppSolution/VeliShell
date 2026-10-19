using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace VeliShell.Desktop.Controls;

/// <summary>Local, alpha-aware colour analysis; cached per source, never per frame.</summary>
internal sealed record AppIconBackground(LinearGradientBrush Brush, bool IsDark, bool HasArtwork)
{
    private const int SampleSize = 64;
    private static readonly ConditionalWeakTable<ImageSource, AppIconBackground> Cache = new();
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(value =>
        value <= 10 ? value / 3294.6 : Math.Pow((value / 255d + .055) / 1.055, 2.4)).ToArray();

    internal static AppIconBackground For(ImageSource source) => Cache.GetValue(source, Create);

    private static AppIconBackground Create(ImageSource source)
    {
        try { return Analyze(AppIconAppearance.RenderSquareSample(source, SampleSize)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        { return Make(Colors.LightGray, Colors.Gainsboro, false); }
    }

    private struct Bin { internal double Weight, R, G, B; }
    private readonly record struct Sample(double Luminance, double Alpha, double Weight);

    internal static AppIconBackground Analyze(byte[] pixels)
    {
        var all = new Bin[512];
        var coloured = new Bin[512];
        var samples = new List<Sample>(pixels.Length / 4);
        double total = 0, colourful = 0;
        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3] / 255d;
            if (alpha < .19) continue; // Ignore invisible RGB and soft outer shadows.
            byte Channel(int offset) => (byte)Math.Clamp(Math.Round(pixels[index + offset] / alpha), 0, 255);
            var r = Channel(2); var g = Channel(1); var b = Channel(0);
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var chroma = (max - min) / 255d;
            var weight = alpha * alpha;
            var bucket = (r >> 5) * 64 + (g >> 5) * 8 + (b >> 5);
            Add(ref all[bucket], weight, r, g, b);
            if (chroma >= .16) { Add(ref coloured[bucket], weight, r, g, b); colourful += weight; }
            samples.Add(new Sample(.2126 * Linear[r] + .7152 * Linear[g] + .0722 * Linear[b], alpha, weight));
            total += weight;
        }
        if (total == 0) return Make(Colors.LightGray, Colors.Gainsboro, false);

        // A large white/black part must not drown out the actual brand accent.
        var palette = colourful / total >= .08 ? coloured : all;
        var dominant = palette.MaxBy(bin => bin.Weight);
        var colour = Color.FromRgb((byte)Math.Round(dominant.R / dominant.Weight),
            (byte)Math.Round(dominant.G / dominant.Weight), (byte)Math.Round(dominant.B / dominant.Weight));
        var (hue, saturation) = HueSaturation(colour);
        var mutedSaturation = saturation < .08 ? 0 : .18 + saturation * .32;
        double bestPenalty = double.PositiveInfinity;
        Color bestTop = default, bestBottom = default;
        foreach (var lightness in new[] { .10, .16, .22, .30, .70, .80, .90 })
        {
            var top = Hsl(hue, mutedSaturation, lightness + .028);
            var bottom = Hsl(hue, mutedSaturation, lightness - .028);
            var topL = Luminance(top); var bottomL = Luminance(bottom);
            double penalty = 0;
            foreach (var pixel in samples)
            {
                // Evaluate both gradient ends, including foreground translucency.
                // Prefer the tone with the least low-contrast foreground, not a
                // same-colour average which would conceal the source artwork.
                var contrast = Math.Min(Contrast(pixel.Luminance * pixel.Alpha + topL * (1 - pixel.Alpha), topL),
                    Contrast(pixel.Luminance * pixel.Alpha + bottomL * (1 - pixel.Alpha), bottomL));
                penalty += pixel.Weight * Math.Pow(Math.Max(0, 3 - contrast), 2);
            }
            // Deterministic tie-breaker: stay closer to the icon's own brightness.
            penalty += .0001 * total * Math.Abs(Luminance(colour) - (topL + bottomL) / 2);
            if (penalty >= bestPenalty) continue;
            bestPenalty = penalty; bestTop = top; bestBottom = bottom;
        }
        return Make(bestTop, bestBottom, true);
    }

    private static void Add(ref Bin bin, double weight, byte r, byte g, byte b)
    { bin.Weight += weight; bin.R += weight * r; bin.G += weight * g; bin.B += weight * b; }

    private static AppIconBackground Make(Color top, Color bottom, bool artwork)
    {
        var brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return new(brush, (Luminance(top) + Luminance(bottom)) / 2 < .35, artwork);
    }

    internal static double Luminance(Color colour) => .2126 * Linear[colour.R] + .7152 * Linear[colour.G] + .0722 * Linear[colour.B];
    internal static double Contrast(double first, double second) => (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);

    private static (double Hue, double Saturation) HueSaturation(Color colour)
    {
        var r = colour.R / 255d; var g = colour.G / 255d; var b = colour.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta == 0) return (0, 0);
        var lightness = (max + min) / 2;
        var hue = max == r ? ((g - b) / delta + 6) % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (hue / 6, delta / (1 - Math.Abs(2 * lightness - 1)));
    }

    private static Color Hsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue * 6 % 2 - 1));
        var m = lightness - chroma / 2;
        var (r, g, b) = (int)(hue * 6) switch
        {
            0 => (chroma, x, 0d), 1 => (x, chroma, 0d), 2 => (0d, chroma, x),
            3 => (0d, x, chroma), 4 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
