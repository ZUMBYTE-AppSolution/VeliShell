using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

// Generic credentials are stored by Windows Credential Manager for the current
// Windows user; API keys never enter Settings JSON, logs, or the repository.
internal static class UserApiCredentials
{
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    internal const string MacOsIcons = "VeliShell:macosicons:user-api-key";
    internal const string BraveSearch = "VeliShell:brave-search:user-api-key";

    internal static void Save(string target, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 1024 || secret.Any(char.IsControl))
            throw new ArgumentException("The API key must contain 1–1024 characters.", nameof(secret));
        var bytes = Encoding.UTF8.GetBytes(secret.Trim());
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeMethods.Credential
            {
                Type = Generic,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachine,
                UserName = Environment.UserName
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(blob, index, 0);
            Marshal.FreeHGlobal(blob);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string? Read(string target)
    {
        if (!NativeMethods.CredRead(target, Generic, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
            if (credential.CredentialBlobSize is 0 or > 4096 || credential.CredentialBlob == 0)
                return null;
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { NativeMethods.CredFree(pointer); }
    }

    internal static void Delete(string target)
    {
        if (!NativeMethods.CredDelete(target, Generic, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
