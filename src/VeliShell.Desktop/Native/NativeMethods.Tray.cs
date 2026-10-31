using System.Runtime.InteropServices;
using System.Text;

namespace VeliShell.Desktop.Native;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Callback;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    internal static extern nint LoadSharedIcon(nint instance, nint name);
    [StructLayout(LayoutKind.Sequential)]
    internal struct TrayCallback
    {
        internal ulong Window, Started;
        internal uint Pid, Id, Message, Version, Active;
        internal Guid Guid;
    }

    [DllImport("VeliShell.TrayBridge.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliShellReadTrayCallbacks(in Guid session, [Out] StringBuilder output, int capacity);

    [DllImport("VeliShell.TrayBridge.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliShellShowTrayCallback(in Guid session, in TrayCallback target, int x, int y);

    [DllImport("VeliShell.TrayBridge.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliShellRelayRequestStop(nint session, [MarshalAs(UnmanagedType.Bool)] bool emergency);

    [DllImport("VeliShell.TrayBridge.dll", EntryPoint = "VeliShellRelayRequestStop", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliShellRelayStopSession(in Guid session, [MarshalAs(UnmanagedType.Bool)] bool emergency);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    internal static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int RelayStart(uint owner, ulong started, in Guid session);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int RelayOperation();
    [DllImport("VeliShell.TrayBridge.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliShellTrayProtocol();

    [DllImport("VeliShell.TrayBridge.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliShellReadTrayElements([Out] StringBuilder output, int capacity);

    [DllImport("VeliShell.TrayBridge.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int VeliShellShowTrayContextMenu(uint explorerPid, ulong explorerStarted,
        [In] int[] runtimeId, int count, string expectedName);
}
