using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Native;

internal static class WinTrustNative
{
    internal static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal const uint UiNone = 2;
    internal const uint RevokeNone = 0;
    internal const uint UnionChoiceFile = 1;
    internal const uint StateActionIgnore = 0;
    internal const uint ProviderFlagRevocationCheckNone = 0x00000010;
    internal const uint ProviderFlagCacheOnlyUrlRetrieval = 0x00001000;

    internal const int TrustENoSignature = unchecked((int)0x800B0100);
    internal const int CryptENoMatch = unchecked((int)0x80092009);
    internal const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WinTrustFileInfo
    {
        internal uint Size;
        internal nint FilePath;
        internal nint FileHandle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WinTrustData
    {
        internal uint Size;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint FileInfo;
        internal uint StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int WinVerifyTrust(
        nint window,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);
}
