namespace VeliShell.Core;

public static class DockMath
{
    // Keep compact icons close together without allowing large icons to
    // spread farther apart than the original dock spacing.
    public static double TileGap(double iconSize) =>
        double.IsFinite(iconSize) && iconSize > 0
            ? Math.Clamp(iconSize * 0.4, 8, 22)
            : 22;

    public static double TileWidth(double iconSize) => iconSize + TileGap(iconSize);

    public static double ScaleAt(double distance, double iconSize, bool enabled)
    {
        if (!enabled || !double.IsFinite(distance) || !double.IsFinite(iconSize) || iconSize <= 0)
            return 1;
        var radius = iconSize * 1.9;
        var influence = Math.Max(0, 1 - Math.Abs(distance) / radius);
        return 1 + 0.42 * influence * influence;
    }

    public static int VisibleCapacity(double workAreaWidth, double iconSize)
    {
        if (!double.IsFinite(workAreaWidth) || workAreaWidth <= 0) return 3;
        if (!double.IsFinite(iconSize) || iconSize <= 0) iconSize = 52;
        return (int)Math.Clamp(Math.Floor((workAreaWidth - 100) / TileWidth(iconSize)), 3, 64);
    }
}
