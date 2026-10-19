using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Resolves current App Store, Wikipedia/Commons and macOSicons selections, plus read-only legacy
/// macOSicons cache entries. Network access remains in the provider services.
/// </summary>
internal static class OnlineIconService
{
    internal static BitmapSource? TryLoad(IconReference? reference) =>
        reference is null ? null : AppStoreIconService.TryLoad(reference) ?? MacOsIconsApiService.TryLoad(reference) ??
        WikipediaIconService.TryLoad(reference) ??
        LegacyMacOsIconCacheService.TryLoad(reference);

    internal static AppStoreIconAttribution? TryGetAttribution(IconReference? reference) =>
        reference is null ? null : AppStoreIconService.TryGetAttribution(reference) ?? MacOsIconsApiService.TryGetAttribution(reference) ??
        WikipediaIconService.TryGetAttribution(reference) ??
        LegacyMacOsIconCacheService.TryGetAttribution(reference);

    internal static Task<BitmapSource?> LoadPreviewAsync(AppStoreIconSearchHit hit, CancellationToken cancellationToken) =>
        hit.Provider switch
        {
            MacOsIconsApiService.Provider => MacOsIconsApiService.LoadPreviewAsync(hit, cancellationToken),
            WikipediaIconService.Provider => WikipediaIconService.LoadPreviewAsync(hit, cancellationToken),
            _ => AppStoreIconService.LoadPreviewAsync(hit, cancellationToken)
        };

    internal static Task<AppStoreIconDownloadResult> DownloadAsync(AppStoreIconSearchHit hit) => hit.Provider switch
    {
        MacOsIconsApiService.Provider => MacOsIconsApiService.DownloadAsync(hit),
        WikipediaIconService.Provider => WikipediaIconService.DownloadAsync(hit),
        _ => AppStoreIconService.DownloadAsync(hit)
    };
}
