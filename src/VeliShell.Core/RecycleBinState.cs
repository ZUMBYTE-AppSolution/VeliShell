namespace VeliShell.Core;

public enum RecycleBinFillState
{
    Unavailable,
    Empty,
    Full
}

public static class RecycleBinState
{
    public static RecycleBinFillState From(bool available, long itemCount)
    {
        if (!available) return RecycleBinFillState.Unavailable;
        return itemCount > 0 ? RecycleBinFillState.Full : RecycleBinFillState.Empty;
    }
}
