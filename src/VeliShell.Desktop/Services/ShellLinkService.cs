using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal static class ShellLinkService
{
    internal static string? ResolveTarget(string shortcutPath)
    {
        if (!File.Exists(shortcutPath) || !shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        object? instance = null;
        try
        {
            instance = new ShellLinkComObject();
            ((IPersistFile)instance).Load(shortcutPath, 0);
            var path = new StringBuilder(32768);
            ((IShellLinkW)instance).GetPath(path, path.Capacity, 0, 0);
            return path.Length == 0 ? null : Environment.ExpandEnvironmentVariables(path.ToString());
        }
        catch (Exception ex)
        {
            App.Log("Could not resolve a shell link", ex);
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }
}
