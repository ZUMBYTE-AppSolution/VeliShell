using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Resolves current App Store and macOSicons selections, plus read-only legacy
/// macOSicons cache entries. Network access remains in the provider services.
/// </summary>
internal static class OnlineIconService
{
    internal static BitmapSource? TryLoad(IconReference? reference) =>
        AppStoreIconService.TryLoad(reference) ?? MacOsIconsApiService.TryLoad(reference) ??
        LegacyMacOsIconCacheService.TryLoad(reference);

    internal static AppStoreIconAttribution? TryGetAttribution(IconReference? reference) =>
        AppStoreIconService.TryGetAttribution(reference) ?? MacOsIconsApiService.TryGetAttribution(reference) ??
        LegacyMacOsIconCacheService.TryGetAttribution(reference);
}
