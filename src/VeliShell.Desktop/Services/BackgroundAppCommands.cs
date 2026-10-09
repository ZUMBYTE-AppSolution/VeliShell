using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal enum BackgroundAppCommandResult
{
    Requested,
    NoCloseWindow,
    NotRunning,
    Failed
}

/// <summary>
/// Acts only on the exact process previously observed by the menu bar. A PID
/// by itself is never enough: Windows can reuse it after the original exits.
/// </summary>
internal static class BackgroundAppCommands
{
    internal static BackgroundAppCommandResult RequestClose(BackgroundApp app) => Execute(app, process =>
        process.CloseMainWindow() ? BackgroundAppCommandResult.Requested : BackgroundAppCommandResult.NoCloseWindow);

    internal static BackgroundAppCommandResult ForceQuit(BackgroundApp app)
    {
        if (app.ProcessId <= 0) return BackgroundAppCommandResult.NotRunning;
        // Keep one kernel handle from identity check through termination. A
        // Process.Kill call by PID could otherwise race with PID reuse.
        const uint queryAndTerminate = 0x1000 | 0x0001;
        var handle = NativeMethods.OpenProcess(queryAndTerminate, false, (uint)app.ProcessId);
        if (handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 87) return BackgroundAppCommandResult.NotRunning; // PID no longer exists
            App.Log("Could not open selected background app process", new Win32Exception(error));
            return BackgroundAppCommandResult.Failed;
        }
        try
        {
            uint capacity = 32768;
            var path = new StringBuilder((int)capacity);
            using var current = Process.GetCurrentProcess();
            if (NativeMethods.GetProcessId(handle) != (uint)app.ProcessId)
                return BackgroundAppCommandResult.NotRunning;
            if (!NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _))
                return NativeFailure("Could not read background app start time");
            if (DateTime.FromFileTimeUtc(created).Ticks != app.StartTimeUtcTicks)
                return BackgroundAppCommandResult.NotRunning;
            if (!NativeMethods.ProcessIdToSessionId((uint)app.ProcessId, out var sessionId))
                return NativeFailure("Could not read background app session");
            if (sessionId != current.SessionId) return BackgroundAppCommandResult.NotRunning;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, path, ref capacity))
                return NativeFailure("Could not read background app path");
            if (!string.Equals(path.ToString(), app.Executable, StringComparison.OrdinalIgnoreCase))
                return BackgroundAppCommandResult.NotRunning;
            if (!NativeMethods.GetExitCodeProcess(handle, out var exitCode))
                return NativeFailure("Could not read background app exit status");
            if (exitCode != 259) return BackgroundAppCommandResult.NotRunning;

            // Only this handle's process is terminated; child processes are
            // deliberately not targeted.
            return NativeMethods.TerminateProcess(handle, 1)
                ? BackgroundAppCommandResult.Requested
                : NativeFailure("Could not terminate selected background app process");
        }
        catch (Exception exception) when (IsProcessAccessFailure(exception))
        {
            App.Log("Background app force quit failed", exception);
            return BackgroundAppCommandResult.Failed;
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    internal static bool CanRequestClose(BackgroundApp app)
    {
        using var process = OpenMatchingProcess(app);
        if (process is null) return false;
        try { return process.MainWindowHandle != 0; }
        catch (Exception exception) when (IsProcessAccessFailure(exception)) { return false; }
    }

    private static BackgroundAppCommandResult Execute(
        BackgroundApp app, Func<Process, BackgroundAppCommandResult> action)
    {
        using var process = OpenMatchingProcess(app);
        if (process is null) return BackgroundAppCommandResult.NotRunning;
        try { return action(process); }
        catch (Exception exception) when (IsProcessAccessFailure(exception))
        {
            App.Log("Background app command failed", exception);
            return BackgroundAppCommandResult.Failed;
        }
    }

    private static Process? OpenMatchingProcess(BackgroundApp app)
    {
        if (app.ProcessId <= 0) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(app.ProcessId);
            using var current = Process.GetCurrentProcess();
            if (process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks != app.StartTimeUtcTicks ||
                process.SessionId != current.SessionId ||
                !string.Equals(WindowCatalog.GetProcessPath((uint)app.ProcessId),
                    app.Executable, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (Exception exception) when (IsProcessAccessFailure(exception))
        {
            process?.Dispose();
            return null;
        }
    }

    private static bool IsProcessAccessFailure(Exception exception) =>
        exception is ArgumentException or Win32Exception or InvalidOperationException or
            NotSupportedException or IOException or UnauthorizedAccessException or SecurityException;

    private static BackgroundAppCommandResult NativeFailure(string message)
    {
        App.Log(message, new Win32Exception(Marshal.GetLastWin32Error()));
        return BackgroundAppCommandResult.Failed;
    }
}
