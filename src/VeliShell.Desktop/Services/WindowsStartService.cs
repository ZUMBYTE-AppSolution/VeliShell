using System.Runtime.InteropServices;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal static class WindowsStartService
{
    internal static bool Open()
    {
        // The Start menu has no supported ShellExecute URI. Send the same
        // Windows-key press a user would make; do not patch or replace Explorer.
        const ushort leftWindowsKey = 0x5B;
        var inputs = new[]
        {
            new NativeMethods.Input { Type = 1, Data = new NativeMethods.InputUnion
                { Keyboard = new NativeMethods.KeyboardInput { VirtualKey = leftWindowsKey } } },
            new NativeMethods.Input { Type = 1, Data = new NativeMethods.InputUnion
                { Keyboard = new NativeMethods.KeyboardInput
                    { VirtualKey = leftWindowsKey, Flags = NativeMethods.KeyEventKeyUp } } }
        };
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        if (sent == inputs.Length) return true;
        App.Log($"Windows did not accept the Start-menu keystroke ({Marshal.GetLastWin32Error()}).");
        return false;
    }
}
