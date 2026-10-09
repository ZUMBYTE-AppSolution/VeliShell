using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal sealed record AppStoreIconAttribution(string Text, string? SourceUrl);

internal sealed record AppStoreIconSearchHit(
    long TrackId,
    string AppName,
    string DeveloperName,
    string? BundleId,
    string? Category,
    Uri PreviewUrl,
    Uri? ArtworkUrl,
    Uri StoreUrl,
    string Provider = "appstore",
    Uri? CreditUrl = null);

internal sealed record AppStoreIconDownloadResult(
    IconReference? Icon,
    AppStoreIconAttribution? Attribution,
    string? Error);

internal enum AppStoreIconFailure
{
    InvalidInput,
    RateLimited,
    Network,
    InvalidResponse
}

internal sealed class AppStoreIconServiceException : Exception
{
    internal AppStoreIconServiceException(AppStoreIconFailure failure, string message) : base(message) =>
        Failure = failure;

    internal AppStoreIconFailure Failure { get; }
}

/// <summary>
/// Explicit, user-initiated client for Apple's documented iTunes Search API.
/// Search results are never selected automatically. Preview artwork stays in
/// memory; only the user's chosen result enters the bounded local icon cache.
/// </summary>
internal static class AppStoreIconService
{
    internal const string Provider = ItunesSearchApi.ProviderId;
    internal const string CatalogVersion = ItunesSearchApi.CatalogVersion;
    internal const int MaximumSearchBytes = 1024 * 1024;
    internal const int MaximumImageBytes = 5 * 1024 * 1024;
    internal const int MaximumCacheEntries = 64;
    internal const long MaximumCacheBytes = 64L * 1024L * 1024L;

    private const int CacheSchema = 1;
    private const int MaximumMetadataBytes = 48 * 1024;
    private const int MinimumPixels = 64;
    private const int MaximumPixels = 1024;
    private const long MaximumDecodedPixels = 1024L * 1024L;
    private const long MaximumDecodedBytes = 4L * 1024L * 1024L;
    private const int MaximumRedirects = 2;
    private const int MaximumSearchCacheEntries = 32;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumSearchInterval = TimeSpan.FromSeconds(3.1);
    private static readonly Uri DocumentationUrl = new(
        "https://performance-partners.apple.com/resources/documentation/itunes-store-web-service-search-api/");
    private static readonly string UserAgent =
        $"VeliShell/{typeof(AppStoreIconService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    private static readonly SemaphoreSlim NetworkGate = new(1, 1);
    private static readonly SemaphoreSlim SearchRateGate = new(1, 1);
    private static readonly object CacheMaintenanceGate = new();
    private static readonly HttpClient SearchHttp = CreateHttpClient(IsAllowedSearchHost);
    private static readonly HttpClient ImageHttp = CreateHttpClient(IsAllowedImageHost);
    private static readonly ConcurrentDictionary<string, MemoryImage> MemoryCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SearchCacheEntry> SearchCache =
        new(StringComparer.Ordinal);
    private static DateTimeOffset _nextSearchUtc = DateTimeOffset.MinValue;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 10
    };
    private static readonly HashSet<string> SafeAppProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft-edge", "ms-settings"
    };

    static AppStoreIconService() => PurgeExpiredCache();

    internal static bool IsEligibleAppPin(Pin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.Kind == PinKind.VirtualFolder) return false;
        try
        {
            var target = Environment.ExpandEnvironmentVariables(pin.Target ?? "").Trim().Trim('"');
            if (target.Length == 0 || Directory.Exists(target)) return false;

            var extension = Path.GetExtension(target);
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (extension.Equals(".msc", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(target);
                return name.Equals("eventvwr", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("diskmgmt", StringComparison.OrdinalIgnoreCase);
            }

            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            {
                var resolved = ShellLinkService.ResolveTarget(target);
                if (resolved is not null &&
                    Path.GetExtension(resolved).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return true;
                return MacOsIconSearchCatalog.CreatePlan(pin) is not null;
            }

            var separator = target.IndexOf(':');
            if (separator > 0 && SafeAppProtocols.Contains(target[..separator])) return true;
            return MacOsIconSearchCatalog.CreatePlan(pin) is not null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal static async Task<IReadOnlyList<AppStoreIconSearchHit>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        string validatedQuery;
        try
        {
            validatedQuery = ItunesSearchApi.ValidateQuery(query);
        }
        catch (ArgumentException)
        {
            throw new AppStoreIconServiceException(
                AppStoreIconFailure.InvalidInput,
                LocalizationService.Current.Get("ItunesSearch.InvalidQuery"));
        }

        var country = ResolveCountryCode();
        var cacheKey = country + ":" + MacOsIconSearchCatalog.Normalize(validatedQuery);
        if (SearchCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return cached.Hits;

        var endpoint = ItunesSearchApi.CreateSoftwareSearchUri(validatedQuery, country);
        try
        {
            await WaitForSearchSlotAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/javascript"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));

            await NetworkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var response = await SearchHttp.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    throw new AppStoreIconServiceException(
                        AppStoreIconFailure.RateLimited,
                        LocalizationService.Current.Get("ItunesSearch.RateLimited"));
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new AppStoreIconServiceException(
                        AppStoreIconFailure.Network,
                        LocalizationService.Current.Get("ItunesSearch.Unavailable"));
                if (!IsAcceptedSearchMediaType(response.Content.Headers.ContentType?.MediaType) ||
                    response.Content.Headers.ContentLength is long length && length > MaximumSearchBytes)
                    throw InvalidResponse();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var bytes = await ReadWithLimitAsync(stream, MaximumSearchBytes, cancellationToken)
                    .ConfigureAwait(false);
                var hits = ItunesSearchApi.ParseSearchResponse(bytes)
                    .Select(ToSearchHit)
                    .Where(hit => hit is not null)
                    .Cast<AppStoreIconSearchHit>()
                    .ToArray();
                StoreSearchCache(cacheKey, hits);
                return hits;
            }
            finally
            {
                NetworkGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppStoreIconServiceException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new AppStoreIconServiceException(
                AppStoreIconFailure.Network,
                LocalizationService.Current.Get("ItunesSearch.Timeout"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new AppStoreIconServiceException(
                AppStoreIconFailure.Network,
                LocalizationService.Current.Get("ItunesSearch.Unavailable"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException)
        {
            throw InvalidResponse();
        }
    }

    internal static async Task<BitmapSource?> LoadPreviewAsync(
        AppStoreIconSearchHit hit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hit);
        foreach (var uri in new[] { hit.PreviewUrl, hit.ArtworkUrl }
                     .Where(IsAllowedImageUri).Cast<Uri>()
                     .DistinctBy(candidate => candidate.AbsoluteUri, StringComparer.Ordinal))
        {
            try
            {
                return DecodeAndNormalizeImage(
                    await DownloadImageAsync(uri, cancellationToken).ConfigureAwait(false)).Bitmap;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedDownloadFailure(exception))
            {
                // A result can contain separate preview and full-size artwork.
            }
        }
        return null;
    }

    internal static async Task<AppStoreIconDownloadResult> DownloadAsync(
        AppStoreIconSearchHit hit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (hit.TrackId <= 0 || !IsSafeText(hit.AppName, 180) || !IsSafeText(hit.DeveloperName, 180) ||
            !IsAllowedStoreUri(hit.StoreUrl)) return DownloadError();

        foreach (var uri in new[] { hit.ArtworkUrl, hit.PreviewUrl }
                     .Where(IsAllowedImageUri).Cast<Uri>()
                     .DistinctBy(candidate => candidate.AbsoluteUri, StringComparer.Ordinal))
        {
            var iconId = Sha256(Encoding.UTF8.GetBytes(hit.TrackId.ToString(CultureInfo.InvariantCulture) + "\n" +
                                                       uri.AbsoluteUri));
            if (TryReadCachedById(iconId, expectedHash: null, out var cached, out var cachedBitmap))
            {
                var cachedReference = new IconReference(Provider, iconId, CatalogVersion, cached.ContentSha256);
                AddToMemoryCache(cachedReference, cached, cachedBitmap);
                return new AppStoreIconDownloadResult(cachedReference, ToAttribution(cached), null);
            }

            try
            {
                var decoded = DecodeAndNormalizeImage(
                    await DownloadImageAsync(uri, cancellationToken).ConfigureAwait(false));
                var contentHash = Sha256(decoded.PngBytes);
                var metadata = new CacheMetadata(
                    iconId,
                    contentHash,
                    hit.TrackId.ToString(CultureInfo.InvariantCulture),
                    hit.AppName.Trim(),
                    hit.DeveloperName.Trim(),
                    hit.StoreUrl,
                    uri,
                    DateTimeOffset.UtcNow,
                    decoded.Bitmap.PixelWidth,
                    decoded.Bitmap.PixelHeight);
                await WriteAtomicallyAsync(ImagePath(iconId), decoded.PngBytes, cancellationToken)
                    .ConfigureAwait(false);
                await WriteAtomicallyAsync(MetadataPath(iconId), SerializeMetadata(metadata), cancellationToken)
                    .ConfigureAwait(false);
                PurgeExpiredCache();
                var reference = new IconReference(Provider, iconId, CatalogVersion, contentHash);
                AddToMemoryCache(reference, metadata, decoded.Bitmap);
                return new AppStoreIconDownloadResult(reference, ToAttribution(metadata), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedDownloadFailure(exception))
            {
                // Fall back from full-size to the provider's 100-pixel image.
            }
        }
        return DownloadError();
    }

    internal static BitmapSource? TryLoad(IconReference? reference)
    {
        if (!IsValidReference(reference)) return null;
        var key = MemoryKey(reference!);
        if (MemoryCache.TryGetValue(key, out var memory))
        {
            if (memory.ExpiresAtUtc > DateTimeOffset.UtcNow) return memory.Bitmap;
            MemoryCache.TryRemove(key, out _);
        }

        if (!TryReadCached(reference!, out var metadata, out var bitmap)) return null;
        AddToMemoryCache(reference!, metadata, bitmap);
        return bitmap;
    }

    internal static AppStoreIconAttribution? TryGetAttribution(IconReference? reference)
    {
        if (!IsValidReference(reference)) return null;
        var key = MemoryKey(reference!);
        if (MemoryCache.TryGetValue(key, out var memory) && memory.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return memory.Attribution;
        if (!TryReadCached(reference!, out var metadata, out var bitmap)) return null;
        AddToMemoryCache(reference!, metadata, bitmap);
        return ToAttribution(metadata);
    }

    private static async Task WaitForSearchSlotAsync(CancellationToken cancellationToken)
    {
        await SearchRateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var delay = _nextSearchUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _nextSearchUtc = DateTimeOffset.UtcNow + MinimumSearchInterval;
        }
        finally
        {
            SearchRateGate.Release();
        }
    }

    private static string ResolveCountryCode()
    {
        try
        {
            return ItunesSearchApi.NormalizeCountryCode(RegionInfo.CurrentRegion.TwoLetterISORegionName);
        }
        catch (ArgumentException)
        {
            return "US";
        }
    }

    private static AppStoreIconServiceException InvalidResponse() =>
        new(AppStoreIconFailure.InvalidResponse,
            LocalizationService.Current.Get("ItunesSearch.UnknownFormat"));

    private static AppStoreIconSearchHit? ToSearchHit(ItunesSoftwareSearchHit hit)
    {
        var preview = ParseHttpsUri(hit.ArtworkUrl100, IsAllowedImageUri);
        var store = ParseHttpsUri(hit.TrackViewUrl, IsAllowedStoreUri);
        if (preview is null || store is null) return null;
        return new AppStoreIconSearchHit(
            hit.TrackId,
            hit.TrackName,
            hit.DeveloperName,
            hit.BundleId,
            hit.PrimaryGenreName,
            preview,
            ParseHttpsUri(hit.ArtworkUrl512, IsAllowedImageUri),
            store);
    }

    private static Uri? ParseHttpsUri(string? value, Func<Uri?, bool> validator)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !validator(uri))
            return null;
        return uri;
    }

    private static void StoreSearchCache(string key, IReadOnlyList<AppStoreIconSearchHit> hits)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in SearchCache.Where(item => item.Value.ExpiresAtUtc <= now))
            SearchCache.TryRemove(item.Key, out _);
        if (SearchCache.Count >= MaximumSearchCacheEntries)
        {
            var oldest = SearchCache.OrderBy(item => item.Value.ExpiresAtUtc).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key)) SearchCache.TryRemove(oldest.Key, out _);
        }
        SearchCache[key] = new SearchCacheEntry(hits, now + SearchCacheLifetime);
    }

    private static bool IsAcceptedSearchMediaType(string? mediaType) =>
        string.Equals(mediaType, "text/javascript", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task<DownloadedImage> DownloadImageAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        await NetworkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = initialUri;
            for (var redirects = 0; redirects <= MaximumRedirects; redirects++)
            {
                if (!IsAllowedImageUri(current)) throw new InvalidDataException("Unsafe artwork URL.");
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
                using var response = await ImageHttp.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects == MaximumRedirects || response.Headers.Location is null)
                        throw new InvalidDataException("Invalid artwork redirect.");
                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                    throw new HttpRequestException("Artwork request failed.", null, response.StatusCode);
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!IsAcceptedImageMediaType(mediaType))
                    throw new InvalidDataException("Unexpected artwork content type.");
                if (response.Content.Headers.ContentLength is long length && length > MaximumImageBytes)
                    throw new InvalidDataException("Artwork response is too large.");

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var bytes = await ReadWithLimitAsync(stream, MaximumImageBytes, cancellationToken)
                    .ConfigureAwait(false);
                return new DownloadedImage(bytes, mediaType!.ToLowerInvariant());
            }
            throw new InvalidDataException("Too many artwork redirects.");
        }
        finally
        {
            NetworkGate.Release();
        }
    }

    private static bool IsAcceptedImageMediaType(string? mediaType) =>
        string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient(Func<string, bool> allowedHost)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.Brotli |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.GZip,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 1,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = (context, token) => ConnectPublicHostAsync(context, token, allowedHost)
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(12) };
    }

    private static async ValueTask<Stream> ConnectPublicHostAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken,
        Func<string, bool> allowedHost)
    {
        if (context.DnsEndPoint.Port != 443 || !allowedHost(context.DnsEndPoint.Host))
            throw new HttpRequestException("Remote host is not allowed.");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new HttpRequestException("Remote host could not be resolved.", exception);
        }

        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Remote host did not resolve to public addresses.");

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastError = exception;
            }
        }
        throw new HttpRequestException("No public endpoint was reachable.", lastError);
    }

    private static DecodedImage DecodeAndNormalizeImage(DownloadedImage download)
    {
        if (download.MediaType == "image/png") ValidatePngHeader(download.Bytes, MinimumPixels, MaximumPixels);
        else if (download.MediaType == "image/jpeg") ValidateJpegHeader(download.Bytes, MinimumPixels, MaximumPixels);
        else throw new InvalidDataException("Artwork format is not supported.");

        using var stream = new MemoryStream(download.Bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Artwork must have one frame.");
        var bitmap = new WriteableBitmap(decoder.Frames[0]);
        bitmap.Freeze();
        if (bitmap.PixelWidth < MinimumPixels || bitmap.PixelHeight < MinimumPixels ||
            bitmap.PixelWidth > MaximumPixels || bitmap.PixelHeight > MaximumPixels)
            throw new InvalidDataException("Decoded artwork dimensions are invalid.");
        ValidateDecodedBitmap(bitmap);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        if (output.Length > MaximumImageBytes) throw new InvalidDataException("Normalized artwork is too large.");
        return new DecodedImage(output.ToArray(), bitmap);
    }

    private static BitmapSource DecodeCachedPng(byte[] bytes)
    {
        ValidatePngHeader(bytes, MinimumPixels, MaximumPixels);
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Image must have one frame.");
        var bitmap = new WriteableBitmap(decoder.Frames[0]);
        bitmap.Freeze();
        ValidateDecodedBitmap(bitmap);
        return bitmap;
    }

    private static void ValidatePngHeader(byte[] bytes, int minimum, int maximum)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Downloaded data is not a PNG image.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        ValidateDimensions(width, height, minimum, maximum);
        var bitDepth = bytes[24];
        var colorType = bytes[25];
        if (bitDepth != 8 || colorType is not (0 or 2 or 3 or 4 or 6) ||
            bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1)
            throw new InvalidDataException("PNG format is not supported safely.");
    }

    private static void ValidateJpegHeader(byte[] bytes, int minimum, int maximum)
    {
        if (bytes.Length < 12 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            throw new InvalidDataException("Downloaded data is not a JPEG image.");
        var offset = 2;
        while (offset + 3 < bytes.Length)
        {
            if (bytes[offset++] != 0xFF) continue;
            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            if (offset + 2 > bytes.Length) break;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length)
                throw new InvalidDataException("JPEG segment is invalid.");
            if (IsJpegStartOfFrame(marker))
            {
                if (segmentLength < 8 || bytes[offset + 2] != 8)
                    throw new InvalidDataException("JPEG frame is not supported safely.");
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                ValidateDimensions(width, height, minimum, maximum);
                return;
            }
            if (marker == 0xDA) break;
            offset += segmentLength;
        }
        throw new InvalidDataException("JPEG dimensions were not found.");
    }

    private static bool IsJpegStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static void ValidateDimensions(long width, long height, int minimum, int maximum)
    {
        if (width < minimum || height < minimum || width > maximum || height > maximum)
            throw new InvalidDataException("Artwork dimensions are invalid.");
        if (checked(width * height) > MaximumDecodedPixels || checked(width * height * 4L) > MaximumDecodedBytes)
            throw new InvalidDataException("Artwork pixel data is too large.");
    }

    private static void ValidateDecodedBitmap(BitmapSource bitmap)
    {
        var bitsPerPixel = bitmap.Format.BitsPerPixel;
        if (bitsPerPixel is <= 0 or > 32)
            throw new InvalidDataException("Decoded pixel format is too large.");
        var stride = ((long)bitmap.PixelWidth * bitsPerPixel + 7L) / 8L;
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaximumDecodedPixels ||
            stride * bitmap.PixelHeight > MaximumDecodedBytes)
            throw new InvalidDataException("Decoded artwork is too large.");
    }

    private static bool TryReadCached(
        IconReference reference,
        out CacheMetadata metadata,
        out BitmapSource bitmap) =>
        TryReadCachedById(reference.IconId, reference.ContentSha256, out metadata, out bitmap);

    private static bool TryReadCachedById(
        string iconId,
        string? expectedHash,
        out CacheMetadata metadata,
        out BitmapSource bitmap)
    {
        metadata = null!;
        bitmap = null!;
        try
        {
            if (!TryReadMetadata(iconId, expectedHash, out metadata)) return false;
            var bytes = ReadFileWithLimit(ImagePath(iconId), MaximumImageBytes);
            if (!FixedHashEquals(Sha256(bytes), metadata.ContentSha256)) return false;
            bitmap = DecodeCachedPng(bytes);
            return bitmap.PixelWidth == metadata.PixelWidth && bitmap.PixelHeight == metadata.PixelHeight;
        }
        catch (Exception exception) when (IsExpectedCacheFailure(exception))
        {
            return false;
        }
    }

    private static bool TryReadMetadata(string iconId, string? expectedHash, out CacheMetadata metadata)
    {
        metadata = null!;
        try
        {
            if (!IsSha256(iconId)) return false;
            var bytes = ReadFileWithLimit(MetadataPath(iconId), MaximumMetadataBytes);
            var payload = JsonSerializer.Deserialize<CachePayload>(bytes, JsonOptions);
            if (payload is null || payload.Schema != CacheSchema || payload.Provider != Provider ||
                payload.CatalogVersion != CatalogVersion || payload.IconId != iconId ||
                !IsSha256(payload.ContentSha256) ||
                (expectedHash is not null && !FixedHashEquals(payload.ContentSha256, expectedHash)) ||
                payload.Source != "Apple iTunes Search API" ||
                payload.SourceUrl != DocumentationUrl.AbsoluteUri ||
                !IsSafeCatalogId(payload.CatalogId) || !IsSafeText(payload.AppName, 180) ||
                !IsSafeText(payload.Developer, 180) ||
                !Uri.TryCreate(payload.DetailUrl, UriKind.Absolute, out var detailUrl) ||
                !IsAllowedStoreUri(detailUrl) ||
                !Uri.TryCreate(payload.ImageUrl, UriKind.Absolute, out var imageUrl) ||
                !IsAllowedImageUri(imageUrl) ||
                payload.PixelWidth is < MinimumPixels or > MaximumPixels ||
                payload.PixelHeight is < MinimumPixels or > MaximumPixels)
                return false;

            var now = DateTimeOffset.UtcNow;
            if (payload.FetchedAtUtc > now.AddMinutes(5)) return false;
            if (now - payload.FetchedAtUtc > CacheLifetime)
            {
                DeleteCachePair(iconId);
                return false;
            }

            metadata = new CacheMetadata(
                iconId,
                payload.ContentSha256.ToLowerInvariant(),
                payload.CatalogId,
                payload.AppName,
                payload.Developer,
                detailUrl,
                imageUrl,
                payload.FetchedAtUtc,
                payload.PixelWidth,
                payload.PixelHeight);
            return true;
        }
        catch (Exception exception) when (IsExpectedCacheFailure(exception))
        {
            return false;
        }
    }

    private static byte[] SerializeMetadata(CacheMetadata metadata)
    {
        var payload = new CachePayload
        {
            Schema = CacheSchema,
            Provider = Provider,
            CatalogVersion = CatalogVersion,
            IconId = metadata.IconId,
            ContentSha256 = metadata.ContentSha256,
            CatalogId = metadata.CatalogId,
            AppName = metadata.AppName,
            Developer = metadata.Developer,
            Source = "Apple iTunes Search API",
            SourceUrl = DocumentationUrl.AbsoluteUri,
            DetailUrl = metadata.DetailUrl.AbsoluteUri,
            ImageUrl = metadata.ImageUrl.AbsoluteUri,
            FetchedAtUtc = metadata.FetchedAtUtc,
            PixelWidth = metadata.PixelWidth,
            PixelHeight = metadata.PixelHeight
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("Metadata is too large.");
        return bytes;
    }

    private static void AddToMemoryCache(IconReference reference, CacheMetadata metadata, BitmapSource bitmap) =>
        MemoryCache[MemoryKey(reference)] = new MemoryImage(
            bitmap,
            metadata.FetchedAtUtc + CacheLifetime,
            ToAttribution(metadata));

    private static AppStoreIconAttribution ToAttribution(CacheMetadata metadata) =>
        new("App Store · " + metadata.Developer, metadata.DetailUrl.AbsoluteUri);

    private static AppStoreIconDownloadResult DownloadError(string? message = null) =>
        new(null, null, message ?? LocalizationService.Current.Get("ItunesSearch.UnsafeImage"));

    private static async Task<byte[]> ReadWithLimitAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Response is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static byte[] ReadFileWithLimit(string path, int maximumBytes)
    {
        path = ValidateCacheReadPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Cache is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task WriteAtomicallyAsync(string destination, byte[] bytes, CancellationToken token)
    {
        var root = EnsureSafeCacheDirectory();
        destination = Path.GetFullPath(destination);
        if (!IsDirectCacheChild(destination, root) || !IsCacheArtifactName(Path.GetFileName(destination)))
            throw new InvalidDataException("Invalid cache destination.");
        EnsureReplaceableCacheTarget(destination);
        var temporary = Path.Combine(root, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            var verifiedRoot = EnsureSafeCacheDirectory();
            if (!string.Equals(root, verifiedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cache directory changed while writing.");
            EnsureReplaceableCacheTarget(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            SafeDeleteCacheFile(temporary, temporaryFile: true);
        }
    }

    private static bool IsValidReference(IconReference? reference) =>
        reference is not null && reference.Provider == Provider && reference.CatalogVersion == CatalogVersion &&
        IsSha256(reference.IconId) && IsSha256(reference.ContentSha256);

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool FixedHashEquals(string left, string right) => IsSha256(left) && IsSha256(right) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSafeCatalogId(string? value) => value is { Length: >= 1 and <= 24 } &&
        value.All(char.IsAsciiDigit);

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static bool IsSafeHttpsUri(Uri? uri) => uri is
    {
        IsAbsoluteUri: true,
        Scheme: "https",
        UserInfo.Length: 0,
        IsDefaultPort: true
    } && !uri.IsLoopback && !string.IsNullOrWhiteSpace(uri.Host);

    private static bool IsAllowedImageUri(Uri? uri) =>
        IsSafeHttpsUri(uri) && IsAllowedImageHost(uri!.DnsSafeHost);

    private static bool IsAllowedStoreUri(Uri? uri) =>
        IsSafeHttpsUri(uri) && IsAllowedStoreHost(uri!.DnsSafeHost);

    private static bool IsAllowedSearchHost(string host) =>
        string.Equals(host, "itunes.apple.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedStoreHost(string host) =>
        string.Equals(host, "apps.apple.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "itunes.apple.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedImageHost(string host) =>
        string.Equals(host, "mzstatic.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];
            var third = bytes[2];
            if (first is 0 or 10 or 127 || first >= 224) return false;
            if (first == 100 && second is >= 64 and <= 127) return false;
            if (first == 169 && second == 254) return false;
            if (first == 172 && second is >= 16 and <= 31) return false;
            if (first == 192 && second == 168) return false;
            if (first == 192 && second == 0 && third is 0 or 2) return false;
            if (first == 192 && second == 88 && third == 99) return false;
            if (first == 198 && second is 18 or 19) return false;
            if (first == 198 && second == 51 && third == 100) return false;
            if (first == 203 && second == 0 && third == 113) return false;
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        if ((bytes[0] & 0xFE) == 0xFC) return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            return false;
        return true;
    }

    private static string MemoryKey(IconReference reference) =>
        reference.Provider + ":" + reference.IconId + ":" + reference.ContentSha256;

    private static string CacheDirectory => Path.Combine(App.DataDirectory, "icons", Provider);
    private static string ImagePath(string iconId) => Path.Combine(CacheDirectory, iconId + ".png");
    private static string MetadataPath(string iconId) => Path.Combine(CacheDirectory, iconId + ".json");

    private static string EnsureSafeCacheDirectory()
    {
        var dataDirectory = NormalizeDirectoryPath(App.DataDirectory);
        var iconsDirectory = NormalizeDirectoryPath(Path.Combine(dataDirectory, "icons"));
        var cacheDirectory = NormalizeDirectoryPath(Path.Combine(iconsDirectory, Provider));
        EnsurePlainDirectory(dataDirectory);
        EnsurePlainDirectory(iconsDirectory);
        EnsurePlainDirectory(cacheDirectory);
        if (!IsPlainDirectory(dataDirectory) || !IsPlainDirectory(iconsDirectory) ||
            !IsPlainDirectory(cacheDirectory))
            throw new InvalidDataException("Cache directory is not safe.");
        return cacheDirectory;
    }

    private static bool TryGetSafeExistingCacheDirectory(out string cacheDirectory)
    {
        cacheDirectory = "";
        try
        {
            var dataDirectory = NormalizeDirectoryPath(App.DataDirectory);
            var iconsDirectory = NormalizeDirectoryPath(Path.Combine(dataDirectory, "icons"));
            cacheDirectory = NormalizeDirectoryPath(Path.Combine(iconsDirectory, Provider));
            return IsPlainDirectory(dataDirectory) && IsPlainDirectory(iconsDirectory) &&
                   IsPlainDirectory(cacheDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void EnsurePlainDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!IsPlainDirectory(path)) throw new InvalidDataException("Cache directory is not safe.");
    }

    private static bool IsPlainDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.Directory) != 0 &&
               (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsDirectCacheChild(string path, string cacheDirectory) =>
        string.Equals(
            NormalizeDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""),
            NormalizeDirectoryPath(cacheDirectory),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsCacheArtifactName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) &&
               IsSha256(Path.GetFileNameWithoutExtension(fileName));
    }

    private static bool IsOwnedTemporaryName(string fileName)
    {
        if (!fileName.StartsWith(".", StringComparison.Ordinal) ||
            !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = fileName[1..^4].Split('.');
        return parts.Length == 3 && IsSha256(parts[0]) &&
               (parts[1].Equals("png", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Equals("json", StringComparison.OrdinalIgnoreCase)) &&
               Guid.TryParseExact(parts[2], "N", out _);
    }

    private static string ValidateCacheReadPath(string path)
    {
        if (!TryGetSafeExistingCacheDirectory(out var root))
            throw new InvalidDataException("Cache directory is not safe.");
        path = Path.GetFullPath(path);
        if (!IsDirectCacheChild(path, root) || !IsCacheArtifactName(Path.GetFileName(path)))
            throw new InvalidDataException("Invalid cache path.");
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Cache file is not safe.");
        return path;
    }

    private static void EnsureReplaceableCacheTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Cache target is not safe.");
        }
        catch (FileNotFoundException)
        {
            // A missing target is expected for a new cache entry.
        }
    }

    private static void PurgeExpiredCache()
    {
        try
        {
            lock (CacheMaintenanceGate)
            {
                if (!TryGetSafeExistingCacheDirectory(out var root)) return;
                var entries = new List<(string IconId, DateTimeOffset FetchedAtUtc, long Bytes)>();
                foreach (var metadataPath in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var iconId = Path.GetFileNameWithoutExtension(metadataPath);
                    var imagePath = ImagePath(iconId);
                    if (!IsSha256(iconId) || !TryReadMetadata(iconId, expectedHash: null, out var metadata) ||
                        !IsSafeRegularCacheFile(imagePath) || !IsSafeRegularCacheFile(metadataPath))
                    {
                        if (IsSha256(iconId)) DeleteCachePair(iconId);
                        continue;
                    }
                    entries.Add((iconId, metadata.FetchedAtUtc,
                        checked(new FileInfo(imagePath).Length + new FileInfo(metadataPath).Length)));
                }
                foreach (var imagePath in Directory.EnumerateFiles(root, "*.png", SearchOption.TopDirectoryOnly))
                {
                    var iconId = Path.GetFileNameWithoutExtension(imagePath);
                    if (IsSha256(iconId) && IsSafeRegularCacheFile(imagePath) &&
                        !IsSafeRegularCacheFile(MetadataPath(iconId)) &&
                        File.GetLastWriteTimeUtc(imagePath) < DateTime.UtcNow.AddHours(-1))
                        DeleteCachePair(iconId);
                }
                foreach (var temporaryPath in Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly))
                {
                    if (IsOwnedTemporaryName(Path.GetFileName(temporaryPath)) &&
                        IsSafeOwnedTemporaryFile(temporaryPath) &&
                        File.GetLastWriteTimeUtc(temporaryPath) < DateTime.UtcNow.AddHours(-1))
                        SafeDeleteCacheFile(temporaryPath, temporaryFile: true);
                }

                var totalBytes = entries.Sum(entry => entry.Bytes);
                var removeCount = Math.Max(0, entries.Count - MaximumCacheEntries);
                foreach (var entry in entries.OrderBy(entry => entry.FetchedAtUtc))
                {
                    if (removeCount <= 0 && totalBytes <= MaximumCacheBytes) break;
                    DeleteCachePair(entry.IconId);
                    foreach (var key in MemoryCache.Keys.Where(key =>
                                 key.Contains(":" + entry.IconId + ":", StringComparison.Ordinal)))
                        MemoryCache.TryRemove(key, out _);
                    totalBytes -= entry.Bytes;
                    if (removeCount > 0) removeCount--;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or OverflowException)
        {
            // Cache cleanup must never block VeliShell startup.
        }
    }

    private static void DeleteCachePair(string iconId)
    {
        if (!IsSha256(iconId)) return;
        foreach (var path in new[] { ImagePath(iconId), MetadataPath(iconId) })
            SafeDeleteCacheFile(path, temporaryFile: false);
    }

    private static bool IsSafeRegularCacheFile(string path)
    {
        try
        {
            ValidateCacheReadPath(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSafeOwnedTemporaryFile(string path)
    {
        try
        {
            if (!TryGetSafeExistingCacheDirectory(out var root)) return false;
            path = Path.GetFullPath(path);
            if (!IsDirectCacheChild(path, root) || !IsOwnedTemporaryName(Path.GetFileName(path))) return false;
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void SafeDeleteCacheFile(string path, bool temporaryFile)
    {
        try
        {
            if (!TryGetSafeExistingCacheDirectory(out var root)) return;
            path = Path.GetFullPath(path);
            var fileName = Path.GetFileName(path);
            if (!IsDirectCacheChild(path, root) ||
                !(temporaryFile ? IsOwnedTemporaryName(fileName) : IsCacheArtifactName(fileName))) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return;
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Cleanup is best effort and must never block VeliShell.
        }
    }

    private static bool IsExpectedDownloadFailure(Exception exception) =>
        exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or
        JsonException or NotSupportedException or InvalidOperationException or ArgumentException or
        System.Runtime.InteropServices.COMException or OperationCanceledException or OverflowException;

    private static bool IsExpectedCacheFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
        NotSupportedException or InvalidOperationException or FormatException or ArgumentException or
        System.Runtime.InteropServices.COMException or OverflowException;

    private sealed record DownloadedImage(byte[] Bytes, string MediaType);
    private sealed record DecodedImage(byte[] PngBytes, BitmapSource Bitmap);
    private sealed record MemoryImage(
        BitmapSource Bitmap,
        DateTimeOffset ExpiresAtUtc,
        AppStoreIconAttribution Attribution);
    private sealed record SearchCacheEntry(
        IReadOnlyList<AppStoreIconSearchHit> Hits,
        DateTimeOffset ExpiresAtUtc);
    private sealed record CacheMetadata(
        string IconId,
        string ContentSha256,
        string CatalogId,
        string AppName,
        string Developer,
        Uri DetailUrl,
        Uri ImageUrl,
        DateTimeOffset FetchedAtUtc,
        int PixelWidth,
        int PixelHeight);

    private sealed class CachePayload
    {
        public int Schema { get; init; }
        public string Provider { get; init; } = "";
        public string CatalogVersion { get; init; } = "";
        public string IconId { get; init; } = "";
        public string ContentSha256 { get; init; } = "";
        public string CatalogId { get; init; } = "";
        public string AppName { get; init; } = "";
        public string Developer { get; init; } = "";
        public string Source { get; init; } = "";
        public string SourceUrl { get; init; } = "";
        public string DetailUrl { get; init; } = "";
        public string ImageUrl { get; init; } = "";
        public DateTimeOffset FetchedAtUtc { get; init; }
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }
    }
}
