using System.Runtime.InteropServices;
using System.Text;

namespace VeliShell.Desktop.Native;

internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x80;
    internal const long WsExTransparent = 0x20;
    internal const long WsExNoActivate = 0x08000000;
    internal const int SwHide = 0, SwShowNoActivate = 4;
    internal const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    internal delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo
    { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct MinMaxInfo
    { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct QueryRecycleBinInfo
    {
        public int Size;
        public long TotalBytes;
        public long ItemCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DataBlob
    {
        public int Size;
        public nint Data;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DwmThumbnailProperties
    {
        public uint Flags;
        public Rect Destination;
        public Rect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [Flags]
    internal enum ShellItemImageFactoryFlags : uint
    {
        BiggerSizeOk = 0x00000001,
        IconOnly = 0x00000004
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            NativeSize size,
            ShellItemImageFactoryFlags flags,
            out nint bitmap);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint memory);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        nint optionalEntropy,
        nint reserved,
        nint prompt,
        uint flags,
        out DataBlob dataOut);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out nint description,
        nint optionalEntropy,
        nint reserved,
        nint prompt,
        uint flags,
        out DataBlob dataOut);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] internal static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] internal static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out NativeSize size);
    [DllImport("dwmapi.dll")] internal static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    internal static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? imageFactory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    internal static extern int SHQueryRecycleBin(string? rootPath, ref QueryRecycleBinInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    internal static extern int SHEmptyRecycleBin(nint owner, string? rootPath, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);

    internal static MonitorInfo PrimaryMonitor()
    {
        var result = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(new Point(), 1), ref result))
            result.Work = result.Monitor = new Rect { Right = 1920, Bottom = 1080 };
        return result;
    }
}
