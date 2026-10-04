using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Read-only compatibility for icons a previous VeliShell version already
/// downloaded from macOSicons.com. It performs no provider or image requests.
/// </summary>
internal static class LegacyMacOsIconCacheService
{
    internal const string Provider = "macosicons";
    internal const string CatalogVersion = "api-v1";

    private const int CacheSchema = 3;
    private const int MaximumImageBytes = 5 * 1024 * 1024;
    private const int MaximumMetadataBytes = 48 * 1024;
    private const int MinimumPixels = 128;
    private const int MaximumPixels = 1024;
    private const long MaximumDecodedPixels = 1024L * 1024L;
    private const long MaximumDecodedBytes = 4L * 1024L * 1024L;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly ConcurrentDictionary<string, MemoryImage> MemoryCache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 10
    };

    internal static BitmapSource? TryLoad(IconReference? reference)
    {
        if (!IsValidReference(reference)) return null;
        var key = reference!.IconId + ":" + reference.ContentSha256;
        if (MemoryCache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return cached.Bitmap;
        if (!TryRead(reference, out var metadata, out var bitmap)) return null;
        MemoryCache[key] = new MemoryImage(bitmap, metadata.FetchedAtUtc + CacheLifetime, ToAttribution(metadata));
        return bitmap;
    }

    internal static AppStoreIconAttribution? TryGetAttribution(IconReference? reference)
    {
        if (!IsValidReference(reference)) return null;
        var key = reference!.IconId + ":" + reference.ContentSha256;
        if (MemoryCache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return cached.Attribution;
        if (!TryRead(reference, out var metadata, out var bitmap)) return null;
        var attribution = ToAttribution(metadata);
        MemoryCache[key] = new MemoryImage(bitmap, metadata.FetchedAtUtc + CacheLifetime, attribution);
        return attribution;
    }

    private static bool TryRead(
        IconReference reference,
        out LegacyMetadata metadata,
        out BitmapSource bitmap)
    {
        metadata = null!;
        bitmap = null!;
        try
        {
            var metadataBytes = ReadFileWithLimit(MetadataPath(reference.IconId), MaximumMetadataBytes);
            var payload = JsonSerializer.Deserialize<LegacyPayload>(metadataBytes, JsonOptions);
            if (payload is null || payload.Schema != CacheSchema || payload.Provider != Provider ||
                payload.CatalogVersion != CatalogVersion || payload.IconId != reference.IconId ||
                !IsSha256(payload.ContentSha256) ||
                !FixedHashEquals(payload.ContentSha256, reference.ContentSha256) ||
                payload.Source != "macOSicons.com" || payload.SourceUrl != "https://macosicons.com/" ||
                !IsSafeText(payload.AppName, 180) || !IsOptionalSafeText(payload.Developer, 180) ||
                !IsOptionalSafeText(payload.Designer, 180) ||
                !Uri.TryCreate(payload.DetailUrl, UriKind.Absolute, out var detailUrl) ||
                !IsSafeHttpsUri(detailUrl) ||
                payload.PixelWidth is < MinimumPixels or > MaximumPixels ||
                payload.PixelHeight is < MinimumPixels or > MaximumPixels)
                return false;

            var now = DateTimeOffset.UtcNow;
            if (payload.FetchedAtUtc > now.AddMinutes(5) || now - payload.FetchedAtUtc > CacheLifetime)
                return false;

            var imageBytes = ReadFileWithLimit(ImagePath(reference.IconId), MaximumImageBytes);
            if (!FixedHashEquals(Sha256(imageBytes), payload.ContentSha256)) return false;
            bitmap = DecodePng(imageBytes);
            if (bitmap.PixelWidth != payload.PixelWidth || bitmap.PixelHeight != payload.PixelHeight) return false;
            metadata = new LegacyMetadata(
                payload.AppName,
                Clean(payload.Developer, 180),
                Clean(payload.Designer, 180),
                detailUrl,
                payload.FetchedAtUtc);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or JsonException or NotSupportedException or
                                          InvalidOperationException or FormatException or ArgumentException or
                                          System.Runtime.InteropServices.COMException or OverflowException)
        {
            return false;
        }
    }

    private static BitmapSource DecodePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Legacy icon is not a PNG image.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width < MinimumPixels || height < MinimumPixels || width > MaximumPixels || height > MaximumPixels ||
            checked((long)width * height) > MaximumDecodedPixels ||
            checked((long)width * height * 4L) > MaximumDecodedBytes)
            throw new InvalidDataException("Legacy icon dimensions are invalid.");

        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Legacy icon must have one frame.");
        var bitmap = new WriteableBitmap(decoder.Frames[0]);
        var bitsPerPixel = bitmap.Format.BitsPerPixel;
        var stride = ((long)bitmap.PixelWidth * bitsPerPixel + 7L) / 8L;
        if (bitsPerPixel is <= 0 or > 32 || stride * bitmap.PixelHeight > MaximumDecodedBytes)
            throw new InvalidDataException("Legacy icon pixel format is invalid.");
        bitmap.Freeze();
        return bitmap;
    }

    private static AppStoreIconAttribution ToAttribution(LegacyMetadata metadata)
    {
        var creators = new[] { metadata.Developer, metadata.Designer }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (creators.Count == 0) creators.Add(metadata.AppName);
        return new AppStoreIconAttribution(
            "macOSicons.com · " + string.Join(" · ", creators),
            metadata.DetailUrl.AbsoluteUri);
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
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Legacy cache is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string ValidateCacheReadPath(string path)
    {
        var root = Path.GetFullPath(CacheDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dataRoot = Path.GetFullPath(App.DataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) || !Directory.Exists(dataRoot) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(dataRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Legacy cache directory is not safe.");

        path = Path.GetFullPath(path);
        var parent = Path.GetFullPath(Path.GetDirectoryName(path) ?? "")
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(Path.GetFileNameWithoutExtension(path)) ||
            Path.GetExtension(path) is not (".png" or ".json"))
            throw new InvalidDataException("Legacy cache path is invalid.");
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Legacy cache file is not safe.");
        return path;
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

    private static bool IsSafeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static bool IsOptionalSafeText(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || IsSafeText(value, maximumLength);

    private static string? Clean(string? value, int maximumLength) =>
        IsSafeText(value, maximumLength) ? value!.Trim() : null;

    private static bool IsSafeHttpsUri(Uri uri) => uri is
    {
        IsAbsoluteUri: true,
        Scheme: "https",
        UserInfo.Length: 0,
        IsDefaultPort: true
    } && !uri.IsLoopback && !string.IsNullOrWhiteSpace(uri.Host);

    private static string CacheDirectory => Path.Combine(App.DataDirectory, "icons", Provider);
    private static string ImagePath(string iconId) => Path.Combine(CacheDirectory, iconId + ".png");
    private static string MetadataPath(string iconId) => Path.Combine(CacheDirectory, iconId + ".json");

    private sealed record LegacyMetadata(
        string AppName,
        string? Developer,
        string? Designer,
        Uri DetailUrl,
        DateTimeOffset FetchedAtUtc);

    private sealed record MemoryImage(
        BitmapSource Bitmap,
        DateTimeOffset ExpiresAtUtc,
        AppStoreIconAttribution Attribution);

    private sealed class LegacyPayload
    {
        public int Schema { get; init; }
        public string Provider { get; init; } = "";
        public string CatalogVersion { get; init; } = "";
        public string IconId { get; init; } = "";
        public string ContentSha256 { get; init; } = "";
        public string AppName { get; init; } = "";
        public string? Developer { get; init; }
        public string? Designer { get; init; }
        public string Source { get; init; } = "";
        public string SourceUrl { get; init; } = "";
        public string DetailUrl { get; init; } = "";
        public DateTimeOffset FetchedAtUtc { get; init; }
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }
    }
}
