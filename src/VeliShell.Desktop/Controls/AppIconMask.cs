using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace VeliShell.Desktop.Controls;

internal static class AppIconMask
{
    private const int Samples = 64;
    private const double MaximumGeometrySize = 2048;
    // An independent fit to current rendered macOS icon silhouettes. At the
    // supported 32-96 DIP range this is visually indistinguishable from the
    // system contour without copying Apple's licensed template control points.
    internal const double ContinuousCornerExponent = 4.37;
    private static readonly ConcurrentDictionary<double, Geometry> Cache = new();

    /// <summary>
    /// Creates VeliShell's continuous app-icon contour. The exponent is calibrated
    /// against the visible silhouettes of current macOS app icons so generated
    /// plates align with downloaded artwork. Apple does not publish the numeric
    /// production curve, so no Apple template geometry is embedded here.
    /// </summary>
    internal static Geometry Create(double size)
    {
        var boundedSize = Math.Clamp(size, 1, MaximumGeometrySize);
        var cacheKey = Math.Round(boundedSize, 3);
        return Cache.GetOrAdd(cacheKey, CreateCore);
    }

    private static Geometry CreateCore(double size)
    {
        var points = new Point[Samples];
        var radius = size / 2;
        var power = 2 / ContinuousCornerExponent;
        for (var index = 0; index < points.Length; index++)
        {
            var angle = index * Math.Tau / points.Length;
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);
            points[index] = new Point(
                radius + radius * Math.CopySign(Math.Pow(Math.Abs(cosine), power), cosine),
                radius + radius * Math.CopySign(Math.Pow(Math.Abs(sine), power), sine));
        }

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            for (var index = 0; index < points.Length; index++)
            {
                var previous = points[(index - 1 + points.Length) % points.Length];
                var current = points[index];
                var next = points[(index + 1) % points.Length];
                var afterNext = points[(index + 2) % points.Length];
                var firstControl = new Point(
                    current.X + (next.X - previous.X) / 6,
                    current.Y + (next.Y - previous.Y) / 6);
                var secondControl = new Point(
                    next.X - (afterNext.X - current.X) / 6,
                    next.Y - (afterNext.Y - current.Y) / 6);
                context.BezierTo(firstControl, secondControl, next, isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
