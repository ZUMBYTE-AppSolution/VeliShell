using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Native;

internal static class CompositionInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DispatcherQueueOptions
    {
        internal int Size;
        internal int ThreadType;
        internal int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    internal static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options, out nint controller);

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(nint window, [MarshalAs(UnmanagedType.Bool)] bool topmost,
            out nint target);
        void EnsureOnThread(int threadId);
    }
}
