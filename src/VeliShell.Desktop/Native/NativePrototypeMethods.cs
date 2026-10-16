using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Native;

internal static class NativePrototypeMethods
{
    internal const int RestoreMessage = 0x8000 + 0x531;
    internal const string OptIn = "VeliShell.NativePrototype.OptIn";
    internal const string Active = "VeliShell.NativePrototype.Active";
    internal const string ClassicActive = "VeliShell.ClassicFrame.Active";
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliClassicEligible(nint window);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliClassicReason(nint window, int crossBitness);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliMaterialEligible(nint window);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliModernKind(nint window);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliModernReason(nint window);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliClassicBeginError();
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern nint VeliModernBegin(nint window, string session, uint expectedPid, ulong expectedStart, int dark, int gloss, int transparency, uint tint);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern nint VeliClassicBegin(nint window, string session, uint expectedPid, ulong expectedStart, int dark, int gloss, int transparency, uint tint);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliClassicPoll(nint session, int stop);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern void VeliClassicConfigure(nint session, int dark, int gloss, int transparency, uint tint);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliClassicError(nint session);
    [DllImport("VeliShell.NativeFrame.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliClassicEnd(nint session);
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
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
}
