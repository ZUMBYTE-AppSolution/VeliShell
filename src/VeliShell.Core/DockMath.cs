namespace VeliShell.Core;

public static class DockMath
{
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
        return (int)Math.Clamp(Math.Floor((workAreaWidth - 100) / (iconSize + 22)), 3, 64);
    }
}
