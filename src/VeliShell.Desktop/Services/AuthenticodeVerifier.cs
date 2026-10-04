using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal static class AuthenticodeVerifier
{
    internal static UpdatePackageVerification Verify(string filePath, string sha256, UpdateVerificationPolicy policy)
    {
        var signatureStatus = VerifyWindowsTrust(filePath);
        string? subject = null;
        string? thumbprint = null;

        if (signatureStatus == AuthenticodeStatus.Valid)
        {
            try
            {
#pragma warning disable SYSLIB0057 // The signer is embedded in a signed PE/MSI, not a standalone certificate file.
                using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
                subject = signer.Subject;
                thumbprint = NormalizeThumbprint(signer.Thumbprint);
            }
            catch (CryptographicException)
            {
                signatureStatus = AuthenticodeStatus.InvalidOrUntrusted;
            }
        }

        var allowed = policy.AllowedPublisherThumbprints
            .Select(NormalizeThumbprint)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (signatureStatus == AuthenticodeStatus.Valid &&
            allowed.Count > 0 &&
            (thumbprint is null || !allowed.Contains(thumbprint)))
            signatureStatus = AuthenticodeStatus.PublisherNotAllowed;

        return new UpdatePackageVerification(sha256, signatureStatus, subject, thumbprint);
    }

    private static AuthenticodeStatus VerifyWindowsTrust(string filePath)
    {
        var pathPointer = nint.Zero;
        var fileInfoPointer = nint.Zero;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(filePath);
            var fileInfo = new WinTrustNative.WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustNative.WinTrustFileInfo>(),
                FilePath = pathPointer
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustNative.WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var trustData = new WinTrustNative.WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustNative.WinTrustData>(),
                UiChoice = WinTrustNative.UiNone,
                RevocationChecks = WinTrustNative.RevokeNone,
                UnionChoice = WinTrustNative.UnionChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustNative.StateActionIgnore,
                // Keep WinVerifyTrust deterministic and offline. Enabling whole-chain
                // revocation here would either require uncancellable network retrieval or
                // reject valid first-run signatures when an offline cache has no CRL/OCSP data.
                ProviderFlags = WinTrustNative.ProviderFlagRevocationCheckNone |
                                WinTrustNative.ProviderFlagCacheOnlyUrlRetrieval
            };
            var result = WinTrustNative.WinVerifyTrust(nint.Zero, WinTrustNative.GenericVerifyV2, ref trustData);
            if (result == 0) return AuthenticodeStatus.Valid;
            if (result is WinTrustNative.TrustENoSignature or WinTrustNative.CryptENoMatch or WinTrustNative.TrustEProviderUnknown)
                return AuthenticodeStatus.NotSigned;
            return AuthenticodeStatus.InvalidOrUntrusted;
        }
        finally
        {
            if (fileInfoPointer != nint.Zero) Marshal.FreeHGlobal(fileInfoPointer);
            if (pathPointer != nint.Zero) Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static string NormalizeThumbprint(string? thumbprint) =>
        new((thumbprint ?? string.Empty).Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}
