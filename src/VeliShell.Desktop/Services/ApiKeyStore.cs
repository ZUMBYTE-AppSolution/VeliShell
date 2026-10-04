using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Stores the user's own macOSicons.com key in Windows Credential Manager.
/// Nothing is written to settings.json, logs, diagnostics, or the repository.
/// </summary>
internal static class ApiKeyStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumApiKeyBytes = 512;
    private const string CredentialTarget = "Zumbyte.VeliShell/macosicons-api";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string LegacyKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HarborDesktop",
        "secrets",
        "macosicons.key");

    internal static bool HasMacOsIconsKey
    {
        get
        {
            try
            {
                if (!TryGetMacOsIconsKey(out var value)) return false;
                // Strings cannot be reliably zeroed by managed code. Keep the
                // lifetime deliberately short and never retain this instance.
                return value.Length > 0;
            }
            catch (Win32Exception)
            {
                // Settings must remain usable even when the Windows vault is
                // temporarily unavailable or access is denied by policy.
                return false;
            }
        }
    }

    internal static void SaveMacOsIconsKey(string apiKey)
    {
        apiKey = Validate(apiKey);
        byte[]? bytes = null;
        nint blob = 0;
        try
        {
            bytes = StrictUtf8.GetBytes(apiKey);
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeMethods.Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = CredentialTarget,
                Comment = "VeliShell macOSicons.com API key",
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Der API-Schlüssel konnte nicht im Windows-Anmeldeinformationsmanager gespeichert werden.");

            DeleteLegacyFile();
        }
        finally
        {
            if (blob != 0)
            {
                ZeroUnmanaged(blob, bytes?.Length ?? 0);
                Marshal.FreeHGlobal(blob);
            }
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static bool TryGetMacOsIconsKey(out string apiKey)
    {
        apiKey = string.Empty;
        if (!NativeMethods.CredRead(CredentialTarget, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return false;
            throw new Win32Exception(error,
                "Der API-Schlüssel konnte nicht aus dem Windows-Anmeldeinformationsmanager gelesen werden.");
        }

        byte[]? bytes = null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize is 0 or > MaximumApiKeyBytes)
                return false;
            bytes = GC.AllocateUninitializedArray<byte>(checked((int)credential.CredentialBlobSize));
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var candidate = StrictUtf8.GetString(bytes);
            apiKey = Validate(candidate);
            return true;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
        {
            apiKey = string.Empty;
            return false;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            NativeMethods.CredFree(pointer);
        }
    }

    internal static void DeleteMacOsIconsKey()
    {
        if (!NativeMethods.CredDelete(CredentialTarget, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new Win32Exception(error,
                    "Der API-Schlüssel konnte nicht aus dem Windows-Anmeldeinformationsmanager entfernt werden.");
        }
        DeleteLegacyFile();
    }

    private static string Validate(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length is < 16 or > MaximumApiKeyBytes ||
            candidate.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Der API-Schlüssel hat ein ungültiges Format.", nameof(value));
        return candidate;
    }

    private static void DeleteLegacyFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(LegacyKeyPath);
            if (directory is null || !Directory.Exists(directory) || !File.Exists(LegacyKeyPath)) return;
            var directoryAttributes = File.GetAttributes(directory);
            var fileAttributes = File.GetAttributes(LegacyKeyPath);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0 ||
                (fileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return;
            File.Delete(LegacyKeyPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            // A retired DPAPI file is never opened. Best-effort deletion must
            // not make a valid Credential Manager write fail.
        }
    }

    private static void ZeroUnmanaged(nint pointer, int length)
    {
        for (var offset = 0; offset < length; offset++) Marshal.WriteByte(pointer, offset, 0);
    }
}
