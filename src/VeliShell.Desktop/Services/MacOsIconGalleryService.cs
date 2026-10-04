using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal sealed record MacOsIconAttribution(string Text, string? SourceUrl);

internal sealed record MacOsIconMatchResult(
    string PinId,
    string? MatchedAppName,
    IconReference? Icon,
    MacOsIconAttribution? Attribution,
    string? Error);

internal sealed record MacOsIconDownloadResult(
    IconReference? Icon,
    MacOsIconAttribution? Attribution,
    string? Error);

internal enum MacOsIconGalleryFailure
{
    InvalidInput,
    Network,
    InvalidResponse
}

internal sealed class MacOsIconGalleryServiceException : Exception
{
    internal MacOsIconGalleryServiceException(MacOsIconGalleryFailure failure, string message) : base(message) =>
        Failure = failure;

    internal MacOsIconGalleryFailure Failure { get; }
}

/// <summary>
/// Opt-in client for macosicongallery.com. VeliShell downloads the fixed public
/// catalog once and performs every name comparison locally. Only detail pages
/// selected from that catalog and their declared PNG assets are requested.
/// </summary>
internal static class MacOsIconGalleryService
{
    internal const string Provider = "macosicongallery";
    internal const string CatalogVersion = "search-data-v1";

    private const int CacheSchema = 2;
    private const int MaximumPins = 32;
    private const int MaximumCatalogBytes = 768 * 1024;
    private const int MaximumDetailBytes = 512 * 1024;
    private const int MaximumImageBytes = 5 * 1024 * 1024;
    private const int MaximumMetadataBytes = 48 * 1024;
    private const int MinimumPixels = 128;
    private const int MaximumPixels = 1024;
    private const long MaximumDecodedPixels = 1024L * 1024L;
    private const long MaximumDecodedBytes = 4L * 1024L * 1024L;
    private const int MaximumCacheEntries = 64;
    private const long MaximumCacheBytes = 64L * 1024L * 1024L;
    private const int MaximumRedirects = 2;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan CatalogRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly Uri CatalogEndpoint = new("https://www.macosicongallery.com/search/data.json");
    private static readonly Uri SourceUrl = new("https://www.macosicongallery.com/");
    private static readonly SemaphoreSlim NetworkGate = new(2, 2);
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static readonly object CacheMaintenanceGate = new();
    private static readonly HttpClient WebsiteHttp = CreateHttpClient(IsAllowedWebsiteHost);
    private static readonly HttpClient ImageHttp = CreateHttpClient(IsAllowedImageHost);
    private static readonly ConcurrentDictionary<string, MemoryImage> MemoryCache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 10
    };
    private static readonly Regex TagRegex = new(
        "<(?:meta|img)\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex AttributeRegex = new(
        "(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)')",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly HashSet<string> SafeAppProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft-edge", "ms-settings"
    };

    private static IReadOnlyList<MacOsIconGalleryEntry>? _catalog;
    private static DateTimeOffset _lastCatalogAttemptUtc = DateTimeOffset.MinValue;

    static MacOsIconGalleryService() => PurgeExpiredCache();

    internal static bool IsEligibleAppPin(Pin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
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
                // Packaged Windows apps often expose a shell link without a
                // file-system executable. Only known Apple-analogy mappings
                // may use that path; arbitrary document shortcuts remain out.
                return MacOsIconGalleryCatalog.CreatePlan(pin) is { RequireAppleDeveloper: true };
            }

            var separator = target.IndexOf(':');
            if (separator > 0 && SafeAppProtocols.Contains(target[..separator])) return true;

            // AUMIDs and packaged-app launch strings do not always resemble a
            // path. They are eligible only for the curated system-app map.
            return MacOsIconGalleryCatalog.CreatePlan(pin) is { RequireAppleDeveloper: true };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal static async Task<IReadOnlyList<MacOsIconMatchResult>> FindAndDownloadExactMatchesAsync(
        IEnumerable<Pin> pins,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var pinList = pins.Where(pin => pin is not null && IsEligibleAppPin(pin))
            .DistinctBy(pin => pin.Id, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumPins)
            .ToList();
        if (pinList.Count == 0) return Array.Empty<MacOsIconMatchResult>();

        var results = new MacOsIconMatchResult?[pinList.Count];
        var plans = new IReadOnlyList<MacOsIconGalleryPlan>?[pinList.Count];
        var needsCatalog = false;
        for (var index = 0; index < pinList.Count; index++)
        {
            var pin = pinList[index];
            if (pin.Icon is not null && TryReadCached(pin.Icon, out var existing, out var bitmap))
            {
                AddToMemoryCache(pin.Icon, existing, bitmap);
                results[index] = new MacOsIconMatchResult(
                    pin.Id, existing.AppName, pin.Icon, ToAttribution(existing), null);
                continue;
            }

            plans[index] = MacOsIconGalleryCatalog.CreatePlans(pin);
            needsCatalog |= plans[index]!.Count > 0;
        }

        IReadOnlyList<MacOsIconGalleryEntry> catalog = Array.Empty<MacOsIconGalleryEntry>();
        if (needsCatalog)
            catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);

        var selected = new MacOsIconGalleryEntry?[pinList.Count];
        for (var index = 0; index < pinList.Count; index++)
        {
            if (results[index] is not null) continue;
            var pinPlans = plans[index];
            if (pinPlans is null || pinPlans.Count == 0)
            {
                results[index] = new MacOsIconMatchResult(pinList[index].Id, null, null, null, null);
                continue;
            }

            selected[index] = pinPlans
                .Select(plan => MacOsIconGalleryCatalog.FindLatestExact(catalog, plan))
                .FirstOrDefault(match => match is not null);
            if (selected[index] is null)
                results[index] = new MacOsIconMatchResult(pinList[index].Id, null, null, null, null);
        }

        var downloads = new Dictionary<string, Task<MacOsIconDownloadResult>>(StringComparer.Ordinal);
        foreach (var entry in selected.OfType<MacOsIconGalleryEntry>())
        {
            if (!downloads.ContainsKey(entry.Id))
                downloads.Add(entry.Id, ResolveAndDownloadAsync(entry, cancellationToken));
        }
        await Task.WhenAll(downloads.Values).ConfigureAwait(false);

        for (var index = 0; index < pinList.Count; index++)
        {
            if (results[index] is not null) continue;
            var entry = selected[index]!;
            var downloaded = await downloads[entry.Id].ConfigureAwait(false);
            results[index] = new MacOsIconMatchResult(
                pinList[index].Id,
                entry.Name,
                downloaded.Icon,
                downloaded.Attribution,
                downloaded.Error);
        }

        return results.Select(result => result!).ToArray();
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

    internal static MacOsIconAttribution? TryGetAttribution(IconReference? reference)
    {
        if (!IsValidReference(reference)) return null;
        var key = MemoryKey(reference!);
        if (MemoryCache.TryGetValue(key, out var memory) && memory.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return memory.Attribution;
        // Attribution is shown only when the corresponding image still passes
        // the same expiry, hash and PNG validation used by TryLoad. A stale or
        // replaced cache file must not label a local fallback as Gallery art.
        if (!TryReadCached(reference!, out var metadata, out var bitmap)) return null;
        AddToMemoryCache(reference!, metadata, bitmap);
        return ToAttribution(metadata);
    }

    private static async Task<IReadOnlyList<MacOsIconGalleryEntry>> GetCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (_catalog is not null) return _catalog;

        await CatalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalog is not null) return _catalog;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastCatalogAttemptUtc < CatalogRetryDelay)
                throw new MacOsIconGalleryServiceException(
                    MacOsIconGalleryFailure.Network,
                    LocalizationService.Current.Get("Gallery.CatalogUnavailable"));
            _lastCatalogAttemptUtc = now;

            try
            {
                var bytes = await DownloadWebsiteBytesAsync(
                    CatalogEndpoint,
                    "application/json",
                    MaximumCatalogBytes,
                    cancellationToken).ConfigureAwait(false);
                _catalog = MacOsIconGalleryCatalog.Parse(bytes);
                return _catalog;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (MacOsIconGalleryServiceException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new MacOsIconGalleryServiceException(
                    MacOsIconGalleryFailure.Network,
                    LocalizationService.Current.Get("Gallery.Timeout"));
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new MacOsIconGalleryServiceException(
                    MacOsIconGalleryFailure.Network,
                    LocalizationService.Current.Get("Gallery.Unavailable"));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException)
            {
                throw new MacOsIconGalleryServiceException(
                    MacOsIconGalleryFailure.InvalidResponse,
                    LocalizationService.Current.Get("Gallery.UnknownFormat"));
            }
        }
        finally
        {
            CatalogGate.Release();
        }
    }

    private static async Task<MacOsIconDownloadResult> ResolveAndDownloadAsync(
        MacOsIconGalleryEntry entry,
        CancellationToken cancellationToken)
    {
        if (!IsSafeCatalogId(entry.Id)) return DownloadError();
        var detailUrl = new Uri(SourceUrl, "icons/" + entry.Id + "/");
        if (!IsExpectedDetailUri(detailUrl, entry.Id)) return DownloadError();

        try
        {
            var detailBytes = await DownloadWebsiteBytesAsync(
                detailUrl,
                "text/html",
                MaximumDetailBytes,
                cancellationToken).ConfigureAwait(false);
            var imageUrl = ExtractOfficialImageUri(detailBytes, detailUrl);
            if (imageUrl is null) return DownloadError(LocalizationService.Current.Get("Gallery.MissingPng"));
            return await DownloadAndCacheAsync(entry, detailUrl, imageUrl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedDownloadFailure(exception))
        {
            return DownloadError();
        }
    }

    private static async Task<MacOsIconDownloadResult> DownloadAndCacheAsync(
        MacOsIconGalleryEntry entry,
        Uri detailUrl,
        Uri imageUrl,
        CancellationToken cancellationToken)
    {
        if (!IsExpectedDetailUri(detailUrl, entry.Id) || !IsAllowedImageUri(imageUrl)) return DownloadError();
        var iconId = Sha256(Encoding.UTF8.GetBytes(entry.Id + "\n" + imageUrl.AbsoluteUri));
        if (TryReadCachedById(iconId, expectedHash: null, out var cached, out var cachedBitmap))
        {
            var reference = new IconReference(Provider, iconId, CatalogVersion, cached.ContentSha256);
            AddToMemoryCache(reference, cached, cachedBitmap);
            return new MacOsIconDownloadResult(reference, ToAttribution(cached), null);
        }

        var bytes = await DownloadImageBytesAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        var decoded = DecodeAndNormalizePng(bytes);
        var contentHash = Sha256(decoded.PngBytes);
        var metadata = new CacheMetadata(
            iconId,
            contentHash,
            entry.Id,
            entry.Name,
            CleanOptional(entry.Developer, 180),
            CleanOptional(entry.Designer, 180),
            detailUrl,
            imageUrl,
            DateTimeOffset.UtcNow,
            decoded.Bitmap.PixelWidth,
            decoded.Bitmap.PixelHeight);

        var metadataBytes = SerializeMetadata(metadata);
        await WriteAtomicallyAsync(ImagePath(iconId), decoded.PngBytes, cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(MetadataPath(iconId), metadataBytes, cancellationToken).ConfigureAwait(false);
        PurgeExpiredCache();

        var iconReference = new IconReference(Provider, iconId, CatalogVersion, contentHash);
        AddToMemoryCache(iconReference, metadata, decoded.Bitmap);
        return new MacOsIconDownloadResult(iconReference, ToAttribution(metadata), null);
    }

    private static Uri? ExtractOfficialImageUri(byte[] htmlBytes, Uri detailUrl)
    {
        var html = Encoding.UTF8.GetString(htmlBytes);
        string? fallback = null;
        foreach (Match tag in TagRegex.Matches(html))
        {
            var attributes = ParseAttributes(tag.Value);
            if (tag.Value.StartsWith("<meta", StringComparison.OrdinalIgnoreCase) &&
                attributes.TryGetValue("property", out var property) &&
                property.Equals("og:image", StringComparison.OrdinalIgnoreCase) &&
                attributes.TryGetValue("content", out var content))
            {
                var candidate = ParseDeclaredImageUri(content, detailUrl);
                if (candidate is not null) return candidate;
            }

            if (fallback is null && tag.Value.StartsWith("<img", StringComparison.OrdinalIgnoreCase) &&
                attributes.TryGetValue("alt", out var alt) &&
                alt.EndsWith("app icon", StringComparison.OrdinalIgnoreCase) &&
                attributes.TryGetValue("src", out var src))
                fallback = src;
        }
        return fallback is null ? null : ParseDeclaredImageUri(fallback, detailUrl);
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributeRegex.Matches(tag))
        {
            var name = attribute.Groups["name"].Value;
            var value = attribute.Groups["dq"].Success
                ? attribute.Groups["dq"].Value
                : attribute.Groups["sq"].Value;
            values.TryAdd(name, WebUtility.HtmlDecode(value));
        }
        return values;
    }

    private static Uri? ParseDeclaredImageUri(string value, Uri detailUrl)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.RelativeOrAbsolute, out var parsed)) return null;
        var absolute = parsed.IsAbsoluteUri ? parsed : new Uri(detailUrl, parsed);
        return IsAllowedImageUri(absolute) ? absolute : null;
    }

    private static async Task<byte[]> DownloadWebsiteBytesAsync(
        Uri initialUri,
        string expectedMediaType,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        await DownloadBytesAsync(
            WebsiteHttp,
            initialUri,
            IsAllowedWebsiteUri,
            expectedMediaType,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);

    private static async Task<byte[]> DownloadImageBytesAsync(Uri initialUri, CancellationToken cancellationToken) =>
        await DownloadBytesAsync(
            ImageHttp,
            initialUri,
            IsAllowedImageUri,
            "image/png",
            MaximumImageBytes,
            cancellationToken).ConfigureAwait(false);

    private static async Task<byte[]> DownloadBytesAsync(
        HttpClient client,
        Uri initialUri,
        Func<Uri?, bool> uriAllowed,
        string expectedMediaType,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await NetworkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = initialUri;
            for (var redirects = 0; redirects <= MaximumRedirects; redirects++)
            {
                if (!uriAllowed(current)) throw new InvalidDataException("Unsafe remote URL.");
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", "VeliShell/0.3.1");
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects == MaximumRedirects || response.Headers.Location is null)
                        throw new InvalidDataException("Invalid redirect.");
                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                    throw new HttpRequestException("Remote request failed.", null, response.StatusCode);
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(mediaType, expectedMediaType, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unexpected content type.");
                if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
                    throw new InvalidDataException("Remote response is too large.");

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                return await ReadWithLimitAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidDataException("Too many redirects.");
        }
        finally
        {
            NetworkGate.Release();
        }
    }

    private static HttpClient CreateHttpClient(Func<string, bool> allowedHost)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.Brotli |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.GZip,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 2,
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

    private static DecodedImage DecodeAndNormalizePng(byte[] bytes)
    {
        ValidatePngHeader(bytes, MinimumPixels, MaximumPixels);
        var bitmap = DecodePng(bytes);
        if (bitmap.PixelWidth < MinimumPixels || bitmap.PixelHeight < MinimumPixels ||
            bitmap.PixelWidth > MaximumPixels || bitmap.PixelHeight > MaximumPixels)
            throw new InvalidDataException("Decoded image dimensions are invalid.");
        ValidateDecodedBitmap(bitmap);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        if (output.Length > MaximumImageBytes) throw new InvalidDataException("Normalized image is too large.");
        return new DecodedImage(output.ToArray(), bitmap);
    }

    private static BitmapSource DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Image must have one frame.");
        var bitmap = new WriteableBitmap(decoder.Frames[0]);
        bitmap.Freeze();
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
        if (width < minimum || height < minimum || width > maximum || height > maximum)
            throw new InvalidDataException("PNG dimensions are invalid.");
        var pixelCount = (long)width * height;
        if (pixelCount > MaximumDecodedPixels || pixelCount * 4L > MaximumDecodedBytes)
            throw new InvalidDataException("PNG pixel data is too large.");

        var bitDepth = bytes[24];
        var colorType = bytes[25];
        if (bitDepth != 8 || colorType is not (0 or 2 or 3 or 4 or 6) ||
            bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1)
            throw new InvalidDataException("PNG format is not supported safely.");
    }

    private static void ValidateDecodedBitmap(BitmapSource bitmap)
    {
        var bitsPerPixel = bitmap.Format.BitsPerPixel;
        if (bitsPerPixel is <= 0 or > 32)
            throw new InvalidDataException("Decoded pixel format is too large.");
        var stride = ((long)bitmap.PixelWidth * bitsPerPixel + 7L) / 8L;
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaximumDecodedPixels ||
            stride * bitmap.PixelHeight > MaximumDecodedBytes)
            throw new InvalidDataException("Decoded image is too large.");
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
            ValidatePngHeader(bytes, MinimumPixels, MaximumPixels);
            bitmap = DecodePng(bytes);
            ValidateDecodedBitmap(bitmap);
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
                payload.Source != "macOS Icon Gallery" || payload.SourceUrl != SourceUrl.AbsoluteUri ||
                !IsSafeCatalogId(payload.CatalogId) || !IsSafeText(payload.AppName, 180) ||
                !IsOptionalSafeText(payload.Developer, 180) || !IsOptionalSafeText(payload.Designer, 180) ||
                !Uri.TryCreate(payload.DetailUrl, UriKind.Absolute, out var detailUrl) ||
                !IsExpectedDetailUri(detailUrl, payload.CatalogId) ||
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
                CleanOptional(payload.Developer, 180),
                CleanOptional(payload.Designer, 180),
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
            Designer = metadata.Designer,
            Source = "macOS Icon Gallery",
            SourceUrl = SourceUrl.AbsoluteUri,
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

    private static MacOsIconAttribution ToAttribution(CacheMetadata metadata)
    {
        var creator = metadata.Developer ?? metadata.Designer ?? metadata.AppName;
        return new MacOsIconAttribution("macOS Icon Gallery · " + creator, metadata.DetailUrl.AbsoluteUri);
    }

    private static MacOsIconDownloadResult DownloadError(string? message = null) =>
        new(null, null, message ?? LocalizationService.Current.Get("Gallery.UnsafePng"));

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

    private static bool IsSafeCatalogId(string? value) => value is { Length: >= 3 and <= 180 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static bool IsOptionalSafeText(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || IsSafeText(value, maximumLength);

    private static string? CleanOptional(string? value, int maximumLength) =>
        IsSafeText(value, maximumLength) ? value!.Trim() : null;

    private static bool IsSafeHttpsUri(Uri? uri) => uri is
    {
        IsAbsoluteUri: true,
        Scheme: "https",
        UserInfo.Length: 0,
        IsDefaultPort: true
    } && !uri.IsLoopback && !string.IsNullOrWhiteSpace(uri.Host);

    private static bool IsAllowedWebsiteUri(Uri? uri) =>
        IsSafeHttpsUri(uri) && IsAllowedWebsiteHost(uri!.DnsSafeHost);

    private static bool IsAllowedImageUri(Uri? uri) =>
        IsSafeHttpsUri(uri) && IsAllowedImageHost(uri!.DnsSafeHost);

    private static bool IsExpectedDetailUri(Uri? uri, string catalogId) =>
        IsSafeCatalogId(catalogId) && IsAllowedWebsiteUri(uri) &&
        string.IsNullOrEmpty(uri!.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath.Equals("/icons/" + catalogId + "/", StringComparison.Ordinal);

    private static bool IsAllowedWebsiteHost(string host) =>
        string.Equals(host, "www.macosicongallery.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "macosicongallery.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedImageHost(string host) =>
        string.Equals(host, "cdn.jim-nielsen.com", StringComparison.OrdinalIgnoreCase);

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

    private static string MemoryKey(IconReference reference) => reference.IconId + ":" + reference.ContentSha256;
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
                    if (!IsSha256(iconId) ||
                        !TryReadMetadata(iconId, expectedHash: null, out var metadata) ||
                        !IsSafeRegularCacheFile(imagePath) ||
                        !IsSafeRegularCacheFile(metadataPath))
                    {
                        if (IsSha256(iconId)) DeleteCachePair(iconId);
                        continue;
                    }

                    entries.Add((
                        iconId,
                        metadata.FetchedAtUtc,
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
                    // A second parallel download may currently be between
                    // flushing and atomically moving this owned temp file.
                    // Only abandoned files from an older run are safe here.
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
                                 key.StartsWith(entry.IconId + ":", StringComparison.Ordinal)))
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
        System.Runtime.InteropServices.COMException or OperationCanceledException;

    private static bool IsExpectedCacheFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
        NotSupportedException or InvalidOperationException or FormatException or ArgumentException or
        System.Runtime.InteropServices.COMException;

    private sealed record DecodedImage(byte[] PngBytes, BitmapSource Bitmap);
    private sealed record MemoryImage(BitmapSource Bitmap, DateTimeOffset ExpiresAtUtc, MacOsIconAttribution Attribution);
    private sealed record CacheMetadata(
        string IconId,
        string ContentSha256,
        string CatalogId,
        string AppName,
        string? Developer,
        string? Designer,
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
        public string? Developer { get; init; }
        public string? Designer { get; init; }
        public string Source { get; init; } = "";
        public string SourceUrl { get; init; } = "";
        public string DetailUrl { get; init; } = "";
        public string ImageUrl { get; init; } = "";
        public DateTimeOffset FetchedAtUtc { get; init; }
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }
    }
}
