using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace VeliShell.Desktop.Services;

internal sealed record SteamClient(int ProcessId, string Executable);

internal enum SteamClientCommand { Open, Settings, Quit }

internal static class SteamClientService
{
    // Steam normally remains in the tray without a top-level window. Check the
    // client process, not the dock's window catalog or its CEF helper processes.
    internal static SteamClient? ReadRunning()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("steam"); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return null; }

        using var current = Process.GetCurrentProcess();
        var sessionId = current.SessionId;
        SteamClient? found = null;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (found is not null || process.HasExited || process.SessionId != sessionId) continue;
                    var executable = WindowCatalog.GetProcessPath((uint)process.Id);
                    if (IsSteamExecutable(executable)) found = new SteamClient(process.Id, executable);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
            }
        }
        return found;
    }

    internal static bool IsSteamExecutable(string path) =>
        Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetFileName(path), "steam.exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    internal static ProcessStartInfo CreateStartInfo(SteamClient client, SteamClientCommand command)
    {
        if (!IsSteamExecutable(client.Executable)) throw new FileNotFoundException("Steam is unavailable.", client.Executable);
        var uri = command switch
        {
            SteamClientCommand.Open => "steam://open/main",
            SteamClientCommand.Settings => "steam://open/settings",
            SteamClientCommand.Quit => "steam://exit",
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        // Steam's registered protocol uses steam.exe -- "steam://...". Invoke
        // the executable observed in this user session directly, never a
        // generic shell association or a forced process termination.
        var start = new ProcessStartInfo(client.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(client.Executable)!
        };
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(uri);
        return start;
    }

    internal static void Send(SteamClient client, SteamClientCommand command)
    {
        var running = ReadRunning();
        if (running is null || running.ProcessId != client.ProcessId ||
            !string.Equals(running.Executable, client.Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Steam is no longer running in this session.");
        using var request = Process.Start(CreateStartInfo(running, command));
        if (request is null) throw new InvalidOperationException("Steam did not accept the command.");
    }
}
