using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Native;

internal static class NativePrototypeMethods
{
    internal const int RestoreMessage = 0x8000 + 0x531;
    internal const string OptIn = "VeliShell.NativePrototype.OptIn";
    internal const string Active = "VeliShell.NativePrototype.Active";
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliFrameApiVersion();
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliFrameAttach(nint window, int dark, int highContrast);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliFrameRestore(nint window);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliFrameGraphicsDevice(nint compositor, out nint device);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliFramePaintCaption(nint surface, int width, int height, float scale, int dark, uint plate, int hover, string title, string restore);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, uint message, nuint wp, nint lp);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out NativeMethods.Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref NativeMethods.Point point);
}
