using System.Buffers.Binary;
using System.Collections.Concurrent;
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

/// <summary>On-demand Wikipedia logo search, with Commons license metadata and a bounded selected-image cache.</summary>
internal static class WikipediaIconService
{
    internal const string Provider = WikipediaIconApi.ProviderId;
    private const int MaximumImageBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly SemaphoreSlim SearchGate = new(1, 1);
    private static readonly SemaphoreSlim RequestGate = new(2, 2);
    private static readonly ConcurrentDictionary<string, SearchEntry> Searches = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CachedImage> Images = new(StringComparer.Ordinal);
    private static DateTimeOffset _nextRequest;
    private static string CacheRoot => Path.Combine(App.DataDirectory, "icons", Provider);

    internal static async Task<IReadOnlyList<AppStoreIconSearchHit>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        query = WikipediaIconApi.ValidateQuery(query);
        var german = LocalizationService.Current.ActiveCulture.TwoLetterISOLanguageName == "de";
        var key = (german ? "de:" : "en:") + query.ToUpperInvariant();
        await SearchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Searches.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Hits;
            var candidates = new List<WikipediaLogoCandidate>();
            WikipediaArticle? article = null;
            foreach (var language in german ? new[] { "de", "en" } : new[] { "en" })
            {
                var delay = _nextRequest - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                _nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(350);
                var bytes = await GetBytesAsync(WikipediaIconApi.CreateArticleSearchUri(query, language),
                    image: false, cancellationToken).ConfigureAwait(false);
                candidates.AddRange(WikipediaIconApi.ParseArticleImages(bytes, query, language));
                article ??= WikipediaIconApi.ParseArticle(bytes, query, language);
            }
            var files = candidates.DistinctBy(item => item.FileTitle, StringComparer.OrdinalIgnoreCase)
                .Take(WikipediaIconApi.MaximumFiles).ToArray();
            var metadata = files.Length == 0 ? Array.Empty<WikimediaLogo>() : WikipediaIconApi.ParseFileInfo(await GetBytesAsync(
                WikipediaIconApi.CreateFileInfoUri(files.Select(file => file.FileTitle)),
                image: false, cancellationToken).ConfigureAwait(false), query);
            if (article is not null && metadata.Count < 4)
            {
                var extra = WikipediaIconApi.ParseFileInfo(await GetBytesAsync(
                    WikipediaIconApi.CreateLogoSearchUri(query), image: false, cancellationToken).ConfigureAwait(false), query);
                metadata = metadata.Concat(extra).DistinctBy(item => item.SourceUrl).Take(WikipediaIconApi.MaximumFiles).ToArray();
            }
            var hits = metadata.Select(image => (Image: image, File: files.FirstOrDefault(file =>
                    image.FileTitle.Equals(file.FileTitle, StringComparison.OrdinalIgnoreCase))))
                .Where(item => item.File is not null || article is not null)
                .Select(item => new AppStoreIconSearchHit(0, item.File?.AppName ?? article!.AppName,
                    item.Image.Creator.Length == 0 ? LocalizationService.Current.Get("WikipediaSearch.CreatorSource") : item.Image.Creator,
                    null, Path.GetFileNameWithoutExtension(item.Image.FileTitle[5..]),
                    WithoutTracking(item.Image.ImageUrl), WithoutTracking(item.Image.ImageUrl),
                    item.File?.ArticleUrl ?? article!.Url, Provider, item.Image.SourceUrl, item.Image.LicenseName, item.Image.LicenseUrl))
                .DistinctBy(hit => hit.CreditUrl)
                .ToArray();
            return StoreSearch(key, hits);
        }
        finally { SearchGate.Release(); }
    }

    private static IReadOnlyList<AppStoreIconSearchHit> StoreSearch(string key, IReadOnlyList<AppStoreIconSearchHit> hits)
    {
        foreach (var pair in Searches.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow))
            Searches.TryRemove(pair.Key, out _);
        if (Searches.Count >= 32) Searches.TryRemove(Searches.Keys.First(), out _);
        Searches[key] = new(DateTimeOffset.UtcNow.AddMinutes(10), hits);
        return hits;
    }

    internal static async Task<BitmapSource?> LoadPreviewAsync(AppStoreIconSearchHit hit, CancellationToken cancellationToken)
    {
        if (!ValidHit(hit)) return null;
        try { return DecodePng(await GetBytesAsync(hit.PreviewUrl, image: true, cancellationToken).ConfigureAwait(false)); }
        catch (Exception exception) when (ExpectedFailure(exception)) { return null; }
    }

    internal static async Task<AppStoreIconDownloadResult> DownloadAsync(
        AppStoreIconSearchHit hit, CancellationToken cancellationToken = default)
    {
        if (!ValidHit(hit)) return Error();
        try
        {
            var bitmap = DecodePng(await GetBytesAsync(hit.ArtworkUrl ?? hit.PreviewUrl,
                image: true, cancellationToken).ConfigureAwait(false));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            var png = output.ToArray();
            if (png.Length is 0 or > MaximumImageBytes) return Error();
            var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash + "\n" + hit.CreditUrl)))
                .ToLowerInvariant();
            var metadata = new CacheMetadata(hash, hit.AppName, hit.DeveloperName, hit.LicenseName!,
                hit.LicenseUrl, hit.CreditUrl!, hit.StoreUrl, DateTimeOffset.UtcNow);
            EnsureCacheRoot(create: true);
            await WriteAtomicAsync(CachePath(id, ".png"), png, cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(CachePath(id, ".json"), JsonSerializer.SerializeToUtf8Bytes(metadata),
                cancellationToken).ConfigureAwait(false);
            var reference = new IconReference(Provider, id, WikipediaIconApi.CatalogVersion, hash);
            Images[id] = new(bitmap, metadata);
            PurgeCache();
            return new(reference, Attribution(metadata), null);
        }
        catch (Exception exception) when (ExpectedFailure(exception)) { return Error(); }
    }

    internal static BitmapSource? TryLoad(IconReference? reference) => TryRead(reference)?.Bitmap;
    internal static AppStoreIconAttribution? TryGetAttribution(IconReference? reference) =>
        TryRead(reference) is { } image ? Attribution(image.Metadata) : null;

    private static CachedImage? TryRead(IconReference? reference)
    {
        if (reference is null || reference.Provider != Provider || reference.CatalogVersion != WikipediaIconApi.CatalogVersion ||
            !Hash(reference.IconId) || !Hash(reference.ContentSha256)) return null;
        if (Images.TryGetValue(reference.IconId, out var memory) &&
            memory.Metadata.Hash == reference.ContentSha256 && !Expired(memory.Metadata)) return memory;
        try
        {
            EnsureCacheRoot(create: false);
            var metadataBytes = ReadCacheFile(CachePath(reference.IconId, ".json"), 32 * 1024);
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(metadataBytes);
            if (metadata is null || metadata.Hash != reference.ContentSha256 || Expired(metadata) ||
                !WikipediaIconApi.IsSourceUri(metadata.SourceUrl) || !WikipediaIconApi.IsArticleUri(metadata.ArticleUrl) ||
                metadata.AppName is not { Length: > 0 and <= 180 } || metadata.Creator is not { Length: <= 180 } ||
                metadata.LicenseName is not { Length: > 0 and <= 120 } ||
                !WikipediaIconApi.IsSupportedFreeLicense(metadata.LicenseName, metadata.LicenseUrl)) return null;
            var bytes = ReadCacheFile(CachePath(reference.IconId, ".png"), MaximumImageBytes);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),
                    Convert.FromHexString(reference.ContentSha256))) return null;
            var image = new CachedImage(DecodePng(bytes), metadata);
            if (Images.Count >= 64) Images.Clear();
            Images[reference.IconId] = image;
            return image;
        }
        catch (Exception exception) when (ExpectedFailure(exception)) { return null; }
    }

    private static async Task<byte[]> GetBytesAsync(Uri uri, bool image, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var entered = false;
        try
        {
            await RequestGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            for (var redirects = 0; redirects <= 2; redirects++)
            {
                if (image ? !WikipediaIconApi.IsImageUri(uri) : !ApiUri(uri))
                    throw new InvalidDataException("Unexpected Wikimedia endpoint.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd($"VeliShell/{GitHubReleaseUpdateService.InstalledVersion} " +
                    "(https://www.zumbyte.de/; info@zumbyte.de)");
                request.Headers.Accept.ParseAdd(image ? "image/png" : "application/json");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location) break;
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != (image ? "image/png" : "application/json"))
                    throw new InvalidDataException("Unexpected Wikimedia response format.");
                return await BoundedHttpContent.ReadAsync(response.Content,
                    image ? MaximumImageBytes : 1024 * 1024, timeout.Token).ConfigureAwait(false);
            }
            throw new InvalidDataException("Too many Wikimedia redirects.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("The Wikipedia request timed out.", exception); }
        finally { if (entered) RequestGate.Release(); }
    }

    private static BitmapSource DecodePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("Not a PNG icon.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 16 or > 2048 || height is < 16 or > 2048 || (long)width * height > 4L * 1024 * 1024)
            throw new InvalidDataException("Unsafe icon dimensions.");
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Expected one icon frame.");
        BitmapSource bitmap = decoder.Frames[0];
        var longest = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (longest > 512) bitmap = new TransformedBitmap(bitmap, new ScaleTransform(512d / longest, 512d / longest));
        bitmap.Freeze();
        return bitmap;
    }

    private static bool ValidHit(AppStoreIconSearchHit hit) => hit.Provider == Provider &&
        hit.AppName is { Length: > 0 and <= 180 } && hit.DeveloperName is { Length: <= 180 } &&
        hit.LicenseName is { Length: > 0 and <= 120 } &&
        WikipediaIconApi.IsSupportedFreeLicense(hit.LicenseName, hit.LicenseUrl) &&
        WikipediaIconApi.IsImageUri(hit.PreviewUrl) &&
        (hit.ArtworkUrl is null || WikipediaIconApi.IsImageUri(hit.ArtworkUrl)) &&
        WikipediaIconApi.IsSourceUri(hit.CreditUrl) && WikipediaIconApi.IsArticleUri(hit.StoreUrl);

    private static bool ApiUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && uri.Host is "en.wikipedia.org" or "de.wikipedia.org" or "commons.wikimedia.org" &&
        uri.AbsolutePath == "/w/api.php";
    private static Uri WithoutTracking(Uri uri) => new UriBuilder(uri) { Query = "" }.Uri;
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Expired(CacheMetadata metadata) => metadata.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
        DateTimeOffset.UtcNow - metadata.FetchedAt > CacheLifetime;
    private static bool ExpectedFailure(Exception exception) => exception is HttpRequestException or IOException or
        UnauthorizedAccessException or JsonException or NotSupportedException or FormatException or
        System.Runtime.InteropServices.COMException;
    private static AppStoreIconDownloadResult Error() => new(null, null, LocalizationService.Current.Get("WikipediaSearch.UnsafeImage"));
    private static AppStoreIconAttribution Attribution(CacheMetadata metadata) => new(
        $"Wikipedia / Wikimedia Commons · {metadata.Creator} · {metadata.LicenseName}", metadata.SourceUrl.AbsoluteUri);

    private static string CachePath(string id, string extension)
    {
        if (!Hash(id) || extension is not (".png" or ".json")) throw new InvalidDataException("Invalid cache key.");
        return Path.Combine(CacheRoot, id.ToLowerInvariant() + extension);
    }

    private static void EnsureCacheRoot(bool create)
    {
        foreach (var path in new[] { App.DataDirectory, Path.Combine(App.DataDirectory, "icons"), CacheRoot })
        {
            if (!Directory.Exists(path) && create) Directory.CreateDirectory(path);
            if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The icon cache is unavailable.");
        }
    }

    private static byte[] ReadCacheFile(string path, int maximum)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 || info.Length > maximum ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Invalid icon cache file.");
        return File.ReadAllBytes(path);
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void PurgeCache()
    {
        try
        {
            EnsureCacheRoot(create: false);
            var entries = new DirectoryInfo(CacheRoot).EnumerateFiles("*.json")
                .Where(file => Hash(Path.GetFileNameWithoutExtension(file.Name)) &&
                    (file.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            long total = 0;
            var count = 0;
            foreach (var entry in entries)
            {
                var id = Path.GetFileNameWithoutExtension(entry.Name);
                var png = new FileInfo(CachePath(id, ".png"));
                if (png.Exists && (png.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var size = entry.Length + (png.Exists ? png.Length : 0);
                if (png.Exists && DateTime.UtcNow - entry.LastWriteTimeUtc < CacheLifetime && count < 64 && total + size <= 64L * 1024 * 1024)
                { count++; total += size; continue; }
                entry.Delete();
                if (png.Exists) png.Delete();
                Images.TryRemove(id, out _);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed record SearchEntry(DateTimeOffset Expires, IReadOnlyList<AppStoreIconSearchHit> Hits);
    private sealed record CacheMetadata(string Hash, string AppName, string Creator, string LicenseName,
        Uri? LicenseUrl, Uri SourceUrl, Uri ArticleUrl, DateTimeOffset FetchedAt);
    private sealed record CachedImage(BitmapSource Bitmap, CacheMetadata Metadata);
}
