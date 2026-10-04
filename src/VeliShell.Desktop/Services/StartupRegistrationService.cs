using System.Reflection;
using System.IO;
using Microsoft.Win32;

namespace VeliShell.Desktop.Services;

public sealed record StartupRegistrationStatus(bool UserLoginEnabled, bool BackgroundServiceInstalled);

public static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VeliShell";
    public const string BackgroundServiceName = "VeliShell.UpdateService";

    public static StartupRegistrationStatus GetStatus() =>
        new(IsUserLoginEnabled(), IsBackgroundServiceInstalled());

    public static bool IsUserLoginEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = key?.GetValue(RunValueName) as string;
        if (string.IsNullOrWhiteSpace(command)) return false;
        return command.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetUserLoginEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(LocalizationService.Current.Get("Startup.WindowsOnly"));
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException(LocalizationService.Current.Get("Startup.RegistryUnavailable"));
        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        var executable = GetExecutablePath();
        key.SetValue(RunValueName, $"\"{executable}\" --autostart", RegistryValueKind.String);
    }

    public static bool IsBackgroundServiceInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{BackgroundServiceName}", writable: false);
        return key is not null;
    }

    private static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetExtension(processPath), ".exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(processPath);

        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(assemblyPath))
            return Path.ChangeExtension(Path.GetFullPath(assemblyPath), ".exe");
        throw new InvalidOperationException(LocalizationService.Current.Get("Startup.ExecutableUnknown"));
    }
}
