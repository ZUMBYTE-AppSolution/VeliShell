using Microsoft.Win32;
using System.IO;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Registers a classic static Shell verb for the current user. No code is
/// loaded into Explorer; selecting the verb starts the normal VeliShell EXE.
/// </summary>
internal static class ShellVerbRegistrationService
{
    internal const string VerbName = "VeliShell.PinToDock";
    internal const string DisplayName = "Im Dock anheften";
    internal static readonly string[] VerbKeys =
    [
        $@"Software\Classes\*\shell\{VerbName}",
        $@"Software\Classes\Directory\shell\{VerbName}"
    ];

    internal static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.IndexOf('"') >= 0)
            throw new ArgumentException("The executable path cannot contain a quotation mark.", nameof(executablePath));
        return $"\"{executablePath}\" {VeliShell.Core.ShellPinCommand.Option} \"%1\"";
    }

    internal static void EnsureRegisteredForCurrentUser(string executablePath)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            return;

        var changed = false;
        var command = BuildCommand(executablePath);
        foreach (var verbPath in VerbKeys)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(verbPath, writable: true)
                ?? throw new UnauthorizedAccessException($"Cannot create HKCU\\{verbPath}.");
            changed |= SetString(verb, "", DisplayName);
            changed |= SetString(verb, "MUIVerb", DisplayName);
            changed |= SetString(verb, "Icon", executablePath + ",0");
            changed |= SetString(verb, "MultiSelectModel", "Single");
            changed |= SetString(verb, "VeliShellExecutable", executablePath);
            using var commandKey = verb.CreateSubKey("command", writable: true)
                ?? throw new UnauthorizedAccessException($"Cannot create HKCU\\{verbPath}\\command.");
            changed |= SetString(commandKey, "", command);
        }

        if (changed)
            NativeMethods.SHChangeNotify(
                NativeMethods.ShcneAssocChanged,
                NativeMethods.ShcnfIdList | NativeMethods.ShcnfFlushNoWait,
                0,
                0);
    }

    internal static void UnregisterForCurrentUser(string executablePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        var changed = false;
        foreach (var verbPath in VerbKeys)
        {
            using var verb = Registry.CurrentUser.OpenSubKey(verbPath, writable: false);
            if (!string.Equals(verb?.GetValue("VeliShellExecutable", null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                executablePath, StringComparison.OrdinalIgnoreCase))
                continue;

            Registry.CurrentUser.DeleteSubKeyTree(verbPath, throwOnMissingSubKey: false);
            changed = true;
        }

        if (changed)
            NativeMethods.SHChangeNotify(
                NativeMethods.ShcneAssocChanged,
                NativeMethods.ShcnfIdList | NativeMethods.ShcnfFlushNoWait,
                0,
                0);
    }

    private static bool SetString(RegistryKey key, string name, string value)
    {
        if (string.Equals(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                value, StringComparison.Ordinal))
            return false;
        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }
}
