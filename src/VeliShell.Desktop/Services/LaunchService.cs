using System.Diagnostics;
using System.IO;
using System.Windows;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal static class LaunchService
{
    internal static void Open(string target)
    {
        try
        {
            // ShellExecute is intentional. No command line, shell script or elevation is injected.
            Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log("Launch failed", ex);
            MessageBox.Show(string.Format(LocalizationService.Current.ActiveCulture,
                    LocalizationService.Current.Get("Launch.Failed"), target, ex.Message),
                LocalizationService.Current.Get("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal static Pin? PinFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Environment.ExpandEnvironmentVariables(path);
        if (Directory.Exists(path))
            return new Pin(Guid.NewGuid().ToString("N"), new DirectoryInfo(path).Name, path);
        var extension = Path.GetExtension(path);
        if (!File.Exists(path)) return null;

        string? process = null;
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            process = Path.GetFileNameWithoutExtension(path);
        else if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ShellLinkService.ResolveTarget(path);
            if (resolved?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                process = Path.GetFileNameWithoutExtension(resolved);
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(path);
        return new Pin(Guid.NewGuid().ToString("N"), name, path, process);
    }
}
