using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Resolves current App Store selections and read-only legacy cache entries.
/// New network access is implemented exclusively by AppStoreIconService.
/// </summary>
internal static class OnlineIconService
{
    internal static BitmapSource? TryLoad(IconReference? reference) =>
        AppStoreIconService.TryLoad(reference) ?? LegacyMacOsIconCacheService.TryLoad(reference);

    internal static AppStoreIconAttribution? TryGetAttribution(IconReference? reference) =>
        AppStoreIconService.TryGetAttribution(reference) ??
        LegacyMacOsIconCacheService.TryGetAttribution(reference);
}
