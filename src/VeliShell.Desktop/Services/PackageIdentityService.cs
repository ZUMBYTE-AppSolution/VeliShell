using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Services;

internal static class PackageIdentityService
{
    private const int ErrorInsufficientBuffer = 122;

    internal static bool HasIdentity
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result is 0 or ErrorInsufficientBuffer;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        char[]? packageFullName);
}
