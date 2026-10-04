using System.Reflection;
using System.IO;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace VeliShell.Desktop.Services;

public static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VeliShell";
    private const string PackagedTaskId = "VeliShellStartup";

    public static async Task<bool> IsUserLoginEnabledAsync()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (PackageIdentityService.HasIdentity)
        {
            var task = await StartupTask.GetAsync(PackagedTaskId);
            return task.State.ToString().StartsWith("Enabled", StringComparison.Ordinal);
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = key?.GetValue(RunValueName) as string;
        if (string.IsNullOrWhiteSpace(command)) return false;
        return command.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    public static async Task SetUserLoginEnabledAsync(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(LocalizationService.Current.Get("Startup.WindowsOnly"));
        if (PackageIdentityService.HasIdentity)
        {
            var task = await StartupTask.GetAsync(PackagedTaskId);
            if (!enabled)
            {
                task.Disable();
                return;
            }

            var state = await task.RequestEnableAsync();
            if (!state.ToString().StartsWith("Enabled", StringComparison.Ordinal))
                throw new InvalidOperationException(LocalizationService.Current.Get("Startup.PackagedEnableRejected"));
            return;
        }

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
