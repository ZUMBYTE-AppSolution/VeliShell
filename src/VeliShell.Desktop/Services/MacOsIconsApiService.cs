using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal static class MacOsIconsApiService
{
    internal const string Provider = "macosicons";
    internal const string CatalogVersion = "api-v2";
    private const int MaximumImageBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private static readonly Uri SourceUrl = new("https://macosicons.com/");
    private static readonly object SearchCacheGate = new();
    private static readonly Dictionary<string,
        (DateTimeOffset ExpiresAt, IReadOnlyList<AppStoreIconSearchHit> Hits)> SearchCache =
        new(StringComparer.Ordinal);
    private static string CacheDirectory => Path.Combine(App.DataDirectory, "icons", "macosicons-api-v2");
    static MacOsIconsApiService() => PurgeCache();

    internal static async Task<IReadOnlyList<AppStoreIconSearchHit>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        var key = UserApiCredentials.Read(UserApiCredentials.MacOsIcons);
        if (string.IsNullOrWhiteSpace(key) || query.Length is < 2 or > 100) return [];
        var cacheKey = MacOsIconSearchCatalog.Normalize(query);
        lock (SearchCacheGate)
            if (SearchCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Hits;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.macosicons.com/api/v1/search");
        request.Headers.Add("x-api-key", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8,
            "application/json");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024)
            throw new HttpRequestException("macOSicons search is unavailable.");
        var body = await BoundedHttpContent.ReadAsync(response.Content, 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        if (!document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            return [];
        var results = new List<AppStoreIconSearchHit>();
        foreach (var value in hits.EnumerateArray().Take(20))
        {
            var name = GetText(value, "appName", 180);
            var preview = GetUri(value, "lowResPngUrl");
            if (name is null || preview is null) continue;
            var fullSize = GetUri(value, "iOSUrl");
            var credit = GetText(value, "credit", 180) ??
                         GetText(value, "uploadedBy", 180) ?? "macOSicons creator";
            var category = GetText(value, "category", 80);
            results.Add(new AppStoreIconSearchHit(0, name, credit, null, category,
                preview, fullSize, SourceUrl, Provider, GetUri(value, "creditUrl")));
        }
        lock (SearchCacheGate)
        {
            foreach (var expired in SearchCache.Where(entry => entry.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                         .Select(entry => entry.Key).ToArray()) SearchCache.Remove(expired);
            if (SearchCache.Count >= 32) SearchCache.Remove(SearchCache.Keys.First());
            SearchCache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(10), results);
        }
        return results;
    }

    internal static async Task<BitmapSource?> LoadPreviewAsync(
        AppStoreIconSearchHit hit, CancellationToken cancellationToken)
    {
        try { return await DownloadBitmapAsync(hit.PreviewUrl, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                          InvalidDataException or NotSupportedException or
                                          System.Runtime.InteropServices.COMException) { return null; }
    }

    internal static async Task<AppStoreIconDownloadResult> DownloadAsync(
        AppStoreIconSearchHit hit, CancellationToken cancellationToken = default)
    {
        if (hit.Provider != Provider) return new(null, null, "Wrong icon provider.");
        foreach (var uri in new[] { hit.ArtworkUrl, hit.PreviewUrl }.Where(uri => uri is not null))
        {
            try
            {
                var bitmap = await DownloadBitmapAsync(uri!, cancellationToken).ConfigureAwait(false);
                if (bitmap is null) continue;
                using var output = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
                var png = output.ToArray();
                if (png.Length is 0 or > MaximumImageBytes) continue;
                var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
                Directory.CreateDirectory(CacheDirectory);
                var metadata = new CacheMetadata(hash, hit.AppName, hit.DeveloperName,
                    hit.CreditUrl?.AbsoluteUri, DateTimeOffset.UtcNow);
                await File.WriteAllBytesAsync(ImagePath(hash), png, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(MetadataPath(hash), JsonSerializer.Serialize(metadata),
                    cancellationToken).ConfigureAwait(false);
                PurgeCache();
                var reference = new IconReference(Provider, hash, CatalogVersion, hash);
                return new(reference, Attribution(metadata), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                                              InvalidDataException or NotSupportedException or
                                              System.Runtime.InteropServices.COMException)
            {
                // An unavailable high-resolution rendition may fall back to the PNG preview.
            }
        }
        return new(null, null, LocalizationService.Current.Get("ItunesSearch.UnsafeImage"));
    }

    internal static BitmapSource? TryLoad(IconReference? reference) =>
        TryRead(reference, out var bitmap, out _) ? bitmap : null;

    internal static AppStoreIconAttribution? TryGetAttribution(IconReference? reference) =>
        TryRead(reference, out _, out var metadata) ? Attribution(metadata) : null;

    private static bool TryRead(IconReference? reference, out BitmapSource bitmap, out CacheMetadata metadata)
    {
        bitmap = null!;
        metadata = null!;
        if (reference is null || reference.Provider != Provider ||
            reference.CatalogVersion != CatalogVersion || !ValidHash(reference.IconId) ||
            !string.Equals(reference.IconId, reference.ContentSha256, StringComparison.Ordinal)) return false;
        try
        {
            var info = new FileInfo(ImagePath(reference.IconId));
            if (!info.Exists || info.Length is <= 0 or > MaximumImageBytes) return false;
            metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(MetadataPath(reference.IconId)))!;
            if (metadata is null || metadata.Hash != reference.IconId ||
                DateTimeOffset.UtcNow - metadata.FetchedAtUtc > CacheLifetime ||
                metadata.FetchedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5)) return false;
            var bytes = File.ReadAllBytes(info.FullName);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),
                    Convert.FromHexString(reference.ContentSha256))) return false;
            bitmap = DecodeBitmap(bytes);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or InvalidDataException or FormatException or
                                          NotSupportedException or System.Runtime.InteropServices.COMException)
        { return false; }
    }

    private static async Task<BitmapSource> DownloadBitmapAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsSafeRemoteUri(uri))
            throw new InvalidDataException("An icon URL is not a standard HTTPS URL.");
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumImageBytes)
            throw new InvalidDataException("The image server did not return a bounded image.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumImageBytes) throw new InvalidDataException("Image too large.");
            output.Write(buffer, 0, count);
        }
        return DecodeBitmap(output.ToArray());
    }

    private static BitmapSource DecodeBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("Missing image frame.");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is < 32 or > 2048 || frame.PixelHeight is < 32 or > 2048 ||
            (long)frame.PixelWidth * frame.PixelHeight > 4L * 1024 * 1024)
            throw new InvalidDataException("Unsafe icon dimensions.");
        frame.Freeze();
        return frame;
    }

    private static void PurgeCache()
    {
        try
        {
            var files = new DirectoryInfo(CacheDirectory).EnumerateFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            long retainedBytes = 0;
            var retainedCount = 0;
            foreach (var file in files)
            {
                var hash = Path.GetFileNameWithoutExtension(file.Name);
                if (!ValidHash(hash)) continue;
                var image = new FileInfo(ImagePath(hash));
                if (!image.Exists) { file.Delete(); continue; }
                var expired = DateTime.UtcNow - file.LastWriteTimeUtc > CacheLifetime;
                if (!expired && retainedCount < 64 && retainedBytes + image.Length <= 64L * 1024 * 1024)
                {
                    retainedCount++;
                    retainedBytes += image.Length;
                    continue;
                }
                file.Delete();
                if (image.Exists) image.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static string ImagePath(string hash) => Path.Combine(CacheDirectory, hash + ".png");
    private static string MetadataPath(string hash) => Path.Combine(CacheDirectory, hash + ".json");
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static AppStoreIconAttribution Attribution(CacheMetadata metadata) =>
        new("macOSicons.com · " + metadata.Creator,
            metadata.CreditUrl ?? SourceUrl.AbsoluteUri);
    private static string? GetText(JsonElement value, string key, int limit) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String &&
        property.GetString() is { } text && text.Length is > 0 and <= 180 && text.Length <= limit
            ? text.Trim() : null;
    private static Uri? GetUri(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String &&
        Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri) &&
        IsSafeRemoteUri(uri)
            ? uri : null;

    private static bool IsSafeRemoteUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.AbsoluteUri.Length <= 2048 &&
        uri.UserInfo.Length == 0 && uri.Port == 443 &&
        !uri.IsLoopback && !IPAddress.TryParse(uri.Host, out _);

    private sealed record CacheMetadata(string Hash, string AppName, string Creator,
        string? CreditUrl, DateTimeOffset FetchedAtUtc);
}
