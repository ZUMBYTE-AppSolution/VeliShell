using System.IO;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Removal-only compatibility for a credential written by the retired
/// macOSicons provider. VeliShell deliberately never opens or decrypts the file.
/// </summary>
internal static class ApiKeyStore
{
    private static readonly string SecretDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HarborDesktop",
        "secrets");
    private static readonly string KeyPath = Path.Combine(SecretDirectory, "macosicons.key");

    internal static bool HasMacOsIconsKey => IsSafeLegacyKeyFile();

    internal static void DeleteMacOsIconsKey()
    {
        if (IsSafeLegacyKeyFile()) File.Delete(KeyPath);
    }

    private static bool IsSafeLegacyKeyFile()
    {
        try
        {
            if (!Directory.Exists(SecretDirectory) || !File.Exists(KeyPath)) return false;
            var directoryAttributes = File.GetAttributes(SecretDirectory);
            var fileAttributes = File.GetAttributes(KeyPath);
            return (directoryAttributes & FileAttributes.Directory) != 0 &&
                   (directoryAttributes & FileAttributes.ReparsePoint) == 0 &&
                   (fileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                   string.Equals(
                       Path.GetDirectoryName(Path.GetFullPath(KeyPath)),
                       Path.GetFullPath(SecretDirectory).TrimEnd(
                           Path.DirectorySeparatorChar,
                           Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
