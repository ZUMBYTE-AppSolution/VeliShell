using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal static class BuildIdentity
{
#if VELISHELL_TEST
    internal static bool IsTest => true;
#else
    internal static bool IsTest => false;
#endif
    internal static string ProfileName => IsTest ? "VeliShell-Test" : "VeliShell";
    internal static string ProductFolder(UpdateChannel channel) => channel == UpdateChannel.Test ? "VeliShell Test" : "VeliShell";
}
