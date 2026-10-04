using System.Runtime.InteropServices;
using VeliShell.Core;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal sealed record RecycleBinStatus(long ItemCount, long TotalBytes, bool Available)
{
    internal static RecycleBinStatus Unavailable { get; } = new(0, 0, false);
    internal RecycleBinFillState FillState => RecycleBinState.From(Available, ItemCount);
}

internal static class RecycleBinService
{
    internal static RecycleBinStatus Query()
    {
        var info = new NativeMethods.QueryRecycleBinInfo
        {
            Size = Marshal.SizeOf<NativeMethods.QueryRecycleBinInfo>()
        };
        var result = NativeMethods.SHQueryRecycleBin(null, ref info);
        return result == 0
            ? new RecycleBinStatus(Math.Max(0, info.ItemCount), Math.Max(0, info.TotalBytes), true)
            : RecycleBinStatus.Unavailable;
    }

    internal static void EmptyWithWindowsConfirmation(nint owner)
    {
        // Flags stay at zero so Windows provides its own confirmation, progress and sound.
        var result = NativeMethods.SHEmptyRecycleBin(owner, null, 0);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }
}
