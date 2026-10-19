using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VeliShell.Desktop.Native;

internal static partial class Program
{
    // Read-only inspection. App input is exercised separately against explicitly
    // opened real windows, never inferred from a fixture's appearance.
    private static int InspectRealApp(string value)
    {
        if(!long.TryParse(value,out var handle)) return 2;
        nint window=(nint)handle;
        NativeMethods.GetWindowThreadProcessId(window,out var pid);
        using var process=Process.GetProcessById((int)pid);
        var cls=new StringBuilder(256); AppWindowClass(window,cls,256);
        NativeMethods.GetWindowRect(window,out var bounds);
        NativePrototypeMethods.GetClientRect(window,out var client);
        var origin=new NativeMethods.Point(); NativePrototypeMethods.ClientToScreen(window,ref origin);
        Console.WriteLine($"HWND={handle}; PID={pid}; Class={cls}; Path={process.MainModule?.FileName}");
        Console.WriteLine($"Version={process.MainModule?.FileVersionInfo.FileVersion}; Kind={NativePrototypeMethods.VeliModernKind(window)}; ModernReason={NativePrototypeMethods.VeliModernReason(window)}; ClassicReason={NativePrototypeMethods.VeliClassicReason(window,1)}");
        Console.WriteLine($"Window={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}; Client={client.Right},{client.Bottom}; Origin={origin.X-bounds.Left},{origin.Y-bounds.Top}");
        Console.WriteLine($"FrameActive={NativePrototypeMethods.GetProp(window,NativePrototypeMethods.ClassicActive)!=0}; Glass={NativePrototypeMethods.GetProp(window,"VeliShell.ClassicFrame.Glass")}; Caption={NativePrototypeMethods.GetProp(window,"VeliShell.ClassicFrame.Caption")}; Adapter={NativePrototypeMethods.GetProp(window,"VeliShell.ClassicFrame.Adapter")}");
        return 0;
    }
    [DllImport("user32.dll",EntryPoint="GetClassNameW",CharSet=CharSet.Unicode)]
    private static extern int AppWindowClass(nint window,StringBuilder name,int count);
}
