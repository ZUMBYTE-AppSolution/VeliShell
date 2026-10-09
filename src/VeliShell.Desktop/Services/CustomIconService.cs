using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Imports user-selected artwork into VeliShell-owned storage. The original
/// path is deliberately not persisted, so icons survive moves and do not leak
/// personal folder names into settings or diagnostics.
/// </summary>
internal static class CustomIconService
{
    internal const string Provider = "velishell-custom";
    internal const string FormatVersion = "1";
    internal const long MaximumSourceBytes = 32L * 1024 * 1024;
    internal const int MaximumSourceDimension = 4096;
    internal const int MaximumStoredDimension = 1024;
    private const long MaximumSourcePixels = 16L * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".ico" };
    private static readonly ConcurrentDictionary<string, BitmapSource> MemoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IconReference Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new InvalidDataException("No image was selected.");
        sourcePath = Path.GetFullPath(sourcePath);
        if (!SupportedExtensions.Contains(Path.GetExtension(sourcePath)))
            throw new InvalidDataException("Unsupported image format.");

        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists || sourceInfo.Length is <= 0 or > MaximumSourceBytes ||
            (sourceInfo.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The selected image is unavailable or too large.");

        BitmapSource frame;
        try
        {
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > MaximumSourceBytes)
                throw new InvalidDataException("The selected image is unavailable or too large.");
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand);
            var candidate = decoder.Frames
                .Where(candidate => IsSafeSize(candidate.PixelWidth, candidate.PixelHeight))
                .OrderByDescending(candidate => (long)candidate.PixelWidth * candidate.PixelHeight)
                .FirstOrDefault()
                ?? throw new InvalidDataException("The selected image has unsupported dimensions.");
            frame = new WriteableBitmap(candidate);
            frame.Freeze();
        }
        catch (Exception exception) when (IsExpectedCodecFailure(exception))
        {
            // WPF reports malformed image containers as FileFormatException
            // (a FormatException), COMException, or InvalidOperationException,
            // depending on the installed Windows Imaging Component codec.
            throw new InvalidDataException("The selected image could not be decoded safely.", exception);
        }

        byte[] png;
        try
        {
            BitmapSource normalized = frame;
            var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (longest > MaximumStoredDimension)
            {
                var scale = MaximumStoredDimension / (double)longest;
                normalized = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                normalized.Freeze();
            }

            using var output = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(normalized));
            encoder.Save(output);
            if (output.Length is <= 0 or > MaximumSourceBytes)
                throw new InvalidDataException("The normalized image is too large.");
            png = output.ToArray();
        }
        catch (Exception exception) when (IsExpectedCodecFailure(exception))
        {
            throw new InvalidDataException("The selected image could not be normalized safely.", exception);
        }

        var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        var root = EnsureStorageDirectory();
        var destination = Path.Combine(root, hash + ".png");
        if (!StoredFileMatches(destination, hash)) WriteAtomically(root, destination, png);

        var bitmap = DecodeStoredPng(png);
        MemoryCache[hash] = bitmap;
        return new IconReference(Provider, hash, FormatVersion, hash);
    }

    internal static IconReference ImportPng(byte[] png)
    {
        if (png.Length == 0 || png.LongLength > MaximumSourceBytes)
            throw new InvalidDataException("The generated PNG is too large.");
        var bitmap = DecodeStoredPng(png);
        var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        var root = EnsureStorageDirectory();
        var destination = Path.Combine(root, hash + ".png");
        if (!StoredFileMatches(destination, hash)) WriteAtomically(root, destination, png);
        MemoryCache[hash] = bitmap;
        return new IconReference(Provider, hash, FormatVersion, hash);
    }

    internal static ImageSource? TryLoad(IconReference? reference)
    {
        if (!IsValidReference(reference)) return null;
        if (MemoryCache.TryGetValue(reference!.IconId, out var cached)) return cached;

        try
        {
            var root = TryGetStorageDirectory();
            if (root is null) return null;
            var path = Path.GetFullPath(Path.Combine(root, reference.IconId + ".png"));
            if (!IsDirectChild(path, root)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumSourceBytes ||
                (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return null;
            var bytes = File.ReadAllBytes(path);
            var actualHash = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(reference.ContentSha256)))
                return null;
            var bitmap = DecodeStoredPng(bytes);
            MemoryCache[reference.IconId] = bitmap;
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or NotSupportedException or ArgumentException or
                                          FormatException or InvalidOperationException or OverflowException or
                                          System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    internal static bool IsCustom(IconReference? reference) => IsValidReference(reference);

    private static BitmapSource DecodeStoredPng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames.FirstOrDefault()
                     ?? throw new InvalidDataException("The icon contains no bitmap frame.");
        if (!IsSafeSize(bitmap.PixelWidth, bitmap.PixelHeight))
            throw new InvalidDataException("The icon has unsupported dimensions.");
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsSafeSize(int width, int height) =>
        width is >= 16 and <= MaximumSourceDimension &&
        height is >= 16 and <= MaximumSourceDimension &&
        (long)width * height <= MaximumSourcePixels;

    private static bool IsExpectedCodecFailure(Exception exception) =>
        // FileFormatException derives from FormatException.
        exception is FormatException or NotSupportedException or InvalidOperationException or
        ArgumentException or OverflowException or System.Runtime.InteropServices.COMException;

    private static bool IsValidReference(IconReference? reference) =>
        reference is not null &&
        string.Equals(reference.Provider, Provider, StringComparison.Ordinal) &&
        string.Equals(reference.CatalogVersion, FormatVersion, StringComparison.Ordinal) &&
        IsSha256(reference.IconId) &&
        string.Equals(reference.IconId, reference.ContentSha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string EnsureStorageDirectory()
    {
        var data = Path.GetFullPath(App.DataDirectory);
        var icons = Path.Combine(data, "icons");
        var custom = Path.Combine(icons, "custom");
        foreach (var directory in new[] { data, icons, custom })
        {
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The icon storage directory is not safe.");
        }
        return Path.GetFullPath(custom);
    }

    private static string? TryGetStorageDirectory()
    {
        var data = Path.GetFullPath(App.DataDirectory);
        var icons = Path.Combine(data, "icons");
        var custom = Path.Combine(icons, "custom");
        foreach (var directory in new[] { data, icons, custom })
            if (!Directory.Exists(directory) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return null;
        return custom;
    }

    private static bool IsDirectChild(string path, string root) =>
        string.Equals(
            Path.GetFullPath(Path.GetDirectoryName(path) ?? "").TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void WriteAtomically(string root, string destination, byte[] bytes)
    {
        if (!IsDirectChild(destination, root)) throw new InvalidDataException("Invalid icon destination.");
        if (File.Exists(destination) &&
            (File.GetAttributes(destination) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The icon destination is not safe.");
        var temporary = Path.Combine(root, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool StoredFileMatches(string path, string expectedHash)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumSourceBytes ||
                (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;
            var actual = SHA256.HashData(File.ReadAllBytes(path));
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          FormatException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
