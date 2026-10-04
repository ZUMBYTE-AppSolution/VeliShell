using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeliShell.Desktop.Controls;

namespace VeliShell.IconBuilder;

internal static class Program
{
    private const string AppAssetName = "VeliShellApp";
    private const string MarkAssetName = "VeliShellMark";
    private const string AppSourceResource = "VeliShell.IconBuilder.Assets.VeliShellAppSource.png";
    private const string MarkSourceResource = "VeliShell.IconBuilder.Assets.VeliShellMarkSource.png";
    private const string AppSourceSha256 = "7c0d87459afc6c5491c5add6df898520db9dafcae89fc670a0bde0629502ec0d";
    private const string MarkSourceSha256 = "b75d6a84b89b46d011ff9aee698524199f5de9c3e6ef1bce5395e1569fbb649e";
    private const int MasterSize = 1024;
    private const double PlateRatio = 0.803;
    private const double MarkContentRatio = 0.88;
    private const byte CleanupAlphaThreshold = 16;
    private const byte MeaningfulAlphaThreshold = 128;
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private static readonly int[] QaSizes = [32, 58, 96];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var outputDirectory = ResolveOutputDirectory(args);
            Directory.CreateDirectory(outputDirectory);
            var appSource = LoadAndCleanSource(AppSourceResource, AppSourceSha256, "app icon");
            var markSource = LoadAndCleanSource(MarkSourceResource, MarkSourceSha256, "brand mark");

            var appPng = RenderAppPng(MasterSize, appSource);
            var appValidation = ValidateAppRaster(appPng, MasterSize);
            WriteIfChanged(Path.Combine(outputDirectory, AppAssetName + ".png"), appPng);

            var iconFrames = IconSizes
                .Select(size => new IconFrame(size, RenderAppPng(size, appSource)))
                .ToArray();
            foreach (var frame in iconFrames) ValidateAppRaster(frame.Png, frame.Size);
            var ico = BuildIco(iconFrames);
            WriteIfChanged(Path.Combine(outputDirectory, AppAssetName + ".ico"), ico);

            foreach (var size in QaSizes)
                ValidateAppRaster(RenderAppPng(size, appSource), size);

            var markPng = RenderMarkPng(markSource);
            var markValidation = ValidateMarkRaster(markPng);
            WriteIfChanged(Path.Combine(outputDirectory, MarkAssetName + ".png"), markPng);

            PrintSource("App source", appSource, AppSourceSha256);
            PrintSource("Mark source", markSource, MarkSourceSha256);
            Console.WriteLine(
                $"{AppAssetName}.png: {MasterSize}x{MasterSize}, SHA-256 {Hash(appPng)}");
            Console.WriteLine(
                $"Plate: p={AppIconMask.ContinuousCornerExponent:0.00}, geometry {MasterSize * PlateRatio:0.###} px " +
                $"({PlateRatio:P1}), alpha>0 x={appValidation.AnyAlphaBounds.Left}..{appValidation.AnyAlphaBounds.Right}, " +
                $"y={appValidation.AnyAlphaBounds.Top}..{appValidation.AnyAlphaBounds.Bottom} " +
                $"({appValidation.AnyAlphaBounds.Width}x{appValidation.AnyAlphaBounds.Height}), " +
                $"mask mismatch {appValidation.BinaryMaskMismatch:P4}");
            Console.WriteLine(
                $"{AppAssetName}.ico: {string.Join(", ", IconSizes.Select(size => $"{size}x{size}"))}, " +
                $"SHA-256 {Hash(ico)}");
            Console.WriteLine(
                $"{MarkAssetName}.png: {MasterSize}x{MasterSize}, content " +
                $"{markValidation.VisibleBounds.Width}x{markValidation.VisibleBounds.Height}, " +
                $"components={markValidation.ComponentCount}, SHA-256 {Hash(markPng)}");
            Console.WriteLine($"Dock QA sizes: {string.Join(", ", QaSizes.Select(size => $"{size} DIP"))}");
            Console.WriteLine($"Output: {outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string ResolveOutputDirectory(IReadOnlyList<string> args)
    {
        if (args.Count > 0) return Path.GetFullPath(args[0]);
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var candidate = Path.Combine(cursor.FullName, "src", "VeliShell.Desktop", "Assets");
            if (Directory.Exists(candidate)) return candidate;
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/VeliShell.Desktop/Assets. Pass the output directory as the first argument.");
    }

    private static SourceAsset LoadAndCleanSource(
        string resourceName,
        string expectedHash,
        string label)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Missing embedded {label} source {resourceName}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var hash = Hash(bytes);
        if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected {label} source hash {hash}.");

        using var imageStream = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(
            imageStream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        if (converted.PixelWidth != 1254 || converted.PixelHeight != 1254)
            throw new InvalidDataException(
                $"Expected a 1254x1254 {label} source, got {converted.PixelWidth}x{converted.PixelHeight}.");

        var pixels = CopyPixels(converted);
        var cleanup = KeepLargestAlphaComponent(
            pixels,
            converted.PixelWidth,
            converted.PixelHeight,
            CleanupAlphaThreshold);
        var cleaned = new WriteableBitmap(
            converted.PixelWidth,
            converted.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32,
            null);
        cleaned.WritePixels(
            new Int32Rect(0, 0, cleaned.PixelWidth, cleaned.PixelHeight),
            pixels,
            cleaned.PixelWidth * 4,
            0);
        cleaned.Freeze();

        var visibleBounds = FindAlphaBounds(
            pixels,
            cleaned.PixelWidth,
            cleaned.PixelHeight,
            CleanupAlphaThreshold);
        var meaningfulBounds = FindAlphaBounds(
            pixels,
            cleaned.PixelWidth,
            cleaned.PixelHeight,
            MeaningfulAlphaThreshold);
        return new SourceAsset(cleaned, visibleBounds, meaningfulBounds, cleanup);
    }

    private static ComponentCleanup KeepLargestAlphaComponent(
        byte[] pixels,
        int width,
        int height,
        byte threshold)
    {
        var pixelCount = width * height;
        var componentIds = new int[pixelCount];
        var queue = new int[pixelCount];
        var componentId = 0;
        var largestId = 0;
        var largestCount = 0;
        var candidateCount = 0;

        for (var index = 0; index < pixelCount; index++)
        {
            if (componentIds[index] != 0 || pixels[index * 4 + 3] < threshold) continue;
            componentId++;
            var head = 0;
            var tail = 0;
            var count = 0;
            componentIds[index] = componentId;
            queue[tail++] = index;
            while (head < tail)
            {
                var current = queue[head++];
                count++;
                var x = current % width;
                var y = current / width;
                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    var neighborY = y + offsetY;
                    if (neighborY < 0 || neighborY >= height) continue;
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0) continue;
                        var neighborX = x + offsetX;
                        if (neighborX < 0 || neighborX >= width) continue;
                        var neighbor = neighborY * width + neighborX;
                        if (componentIds[neighbor] != 0
                            || pixels[neighbor * 4 + 3] < threshold) continue;
                        componentIds[neighbor] = componentId;
                        queue[tail++] = neighbor;
                    }
                }
            }

            candidateCount += count;
            if (count <= largestCount) continue;
            largestCount = count;
            largestId = componentId;
        }

        if (largestId == 0) throw new InvalidDataException("Source has no visible alpha component.");
        var removedVisiblePixels = 0;
        for (var index = 0; index < pixelCount; index++)
        {
            if (componentIds[index] == largestId) continue;
            var pixelOffset = index * 4;
            if (pixels[pixelOffset + 3] != 0) removedVisiblePixels++;
            pixels[pixelOffset] = 0;
            pixels[pixelOffset + 1] = 0;
            pixels[pixelOffset + 2] = 0;
            pixels[pixelOffset + 3] = 0;
        }

        return new ComponentCleanup(
            componentId,
            largestCount,
            candidateCount - largestCount,
            removedVisiblePixels);
    }

    private static byte[] RenderAppPng(int pixelSize, SourceAsset source)
    {
        var bitmap = RenderAppBitmap(pixelSize, source);
        return EncodePng(bitmap);
    }

    private static BitmapSource RenderAppBitmap(int pixelSize, SourceAsset source)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
        {
            var outputScale = pixelSize / (double)MasterSize;
            drawing.PushTransform(new ScaleTransform(outputScale, outputScale));
            var plateSide = MasterSize * PlateRatio;
            var plateOrigin = (MasterSize - plateSide) / 2d;
            var plateBounds = new Rect(plateOrigin, plateOrigin, plateSide, plateSide);
            // A one-percent overscan makes the generated source's antialiased edge
            // fully cover the canonical plate before the exact shared mask is applied.
            var cropSide = Math.Min(source.MeaningfulBounds.Width, source.MeaningfulBounds.Height) * 0.99;
            var centerX = (source.MeaningfulBounds.Left + source.MeaningfulBounds.Right + 1) / 2d;
            var centerY = (source.MeaningfulBounds.Top + source.MeaningfulBounds.Bottom + 1) / 2d;
            var crop = new Rect(centerX - cropSide / 2d, centerY - cropSide / 2d, cropSide, cropSide);
            var sourceScale = plateSide / cropSide;
            var sourceTarget = new Rect(
                plateOrigin - crop.X * sourceScale,
                plateOrigin - crop.Y * sourceScale,
                source.Bitmap.PixelWidth * sourceScale,
                source.Bitmap.PixelHeight * sourceScale);

            drawing.DrawImage(source.Bitmap, sourceTarget);
            drawing.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return ApplySharedMask(bitmap, pixelSize);
    }

    private static BitmapSource ApplySharedMask(BitmapSource source, int pixelSize)
    {
        var sourcePixels = CopyPixels(source);
        var pixels = (byte[])sourcePixels.Clone();
        var mask = RenderMaskPixels(pixelSize);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var sourceAlpha = sourcePixels[offset + 3];
            var maskAlpha = mask[offset + 3];
            if (maskAlpha == 0)
            {
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                pixels[offset + 3] = 0;
                continue;
            }

            if (sourceAlpha == 0)
            {
                var pixel = offset / 4;
                var nearest = FindNearestVisiblePixel(
                    sourcePixels,
                    pixelSize,
                    pixelSize,
                    pixel % pixelSize,
                    pixel / pixelSize);
                if (nearest < 0)
                    throw new InvalidDataException(
                        $"Normalized {pixelSize}px source cannot cover the shared mask.");
                var nearestOffset = nearest * 4;
                sourceAlpha = sourcePixels[nearestOffset + 3];
                for (var channel = 0; channel < 3; channel++)
                {
                    var straightColor = sourcePixels[nearestOffset + channel] * 255d / sourceAlpha;
                    pixels[offset + channel] = (byte)Math.Clamp(
                        Math.Round(straightColor * maskAlpha / 255d),
                        0,
                        maskAlpha);
                }
                pixels[offset + 3] = maskAlpha;
                continue;
            }

            for (var channel = 0; channel < 3; channel++)
            {
                var straightColor = pixels[offset + channel] * 255d / sourceAlpha;
                pixels[offset + channel] = (byte)Math.Clamp(
                    Math.Round(straightColor * maskAlpha / 255d),
                    0,
                    maskAlpha);
            }
            pixels[offset + 3] = maskAlpha;
        }

        var result = new WriteableBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32, null);
        result.WritePixels(new Int32Rect(0, 0, pixelSize, pixelSize), pixels, pixelSize * 4, 0);
        result.Freeze();
        return result;
    }

    private static int FindNearestVisiblePixel(
        byte[] pixels,
        int width,
        int height,
        int originX,
        int originY)
    {
        var maximumRadius = Math.Max(width, height);
        for (var radius = 1; radius <= maximumRadius; radius++)
        {
            var left = Math.Max(0, originX - radius);
            var top = Math.Max(0, originY - radius);
            var right = Math.Min(width - 1, originX + radius);
            var bottom = Math.Min(height - 1, originY + radius);
            for (var x = left; x <= right; x++)
            {
                var topPixel = top * width + x;
                if (pixels[topPixel * 4 + 3] != 0) return topPixel;
                var bottomPixel = bottom * width + x;
                if (pixels[bottomPixel * 4 + 3] != 0) return bottomPixel;
            }
            for (var y = top + 1; y < bottom; y++)
            {
                var leftPixel = y * width + left;
                if (pixels[leftPixel * 4 + 3] != 0) return leftPixel;
                var rightPixel = y * width + right;
                if (pixels[rightPixel * 4 + 3] != 0) return rightPixel;
            }
        }
        return -1;
    }

    private static byte[] RenderMarkPng(SourceAsset source)
    {
        var scale = MasterSize * MarkContentRatio
                    / Math.Max(source.VisibleBounds.Width, source.VisibleBounds.Height);
        var centerX = (source.VisibleBounds.Left + source.VisibleBounds.Right + 1) / 2d;
        var centerY = (source.VisibleBounds.Top + source.VisibleBounds.Bottom + 1) / 2d;
        var target = new Rect(
            MasterSize / 2d - centerX * scale,
            MasterSize / 2d - centerY * scale,
            source.Bitmap.PixelWidth * scale,
            source.Bitmap.PixelHeight * scale);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(source.Bitmap, target);
        var bitmap = new RenderTargetBitmap(MasterSize, MasterSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return EncodePng(bitmap);
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static RasterValidation ValidateAppRaster(byte[] png, int pixelSize)
    {
        var decoded = DecodePng(png);
        if (decoded.Width != pixelSize || decoded.Height != pixelSize)
            throw new InvalidDataException(
                $"Rendered PNG is {decoded.Width}x{decoded.Height}, expected {pixelSize}x{pixelSize}.");
        var anyBounds = FindAlphaBounds(decoded.Pixels, decoded.Width, decoded.Height, 1);
        var meaningfulBounds = FindAlphaBounds(
            decoded.Pixels,
            decoded.Width,
            decoded.Height,
            MeaningfulAlphaThreshold);
        RequireCentered(anyBounds, pixelSize, "any-alpha");
        RequireCentered(meaningfulBounds, pixelSize, "alpha>=128");

        var expectedMask = RenderMaskPixels(pixelSize);
        var outsideMaskAlpha = 0;
        var binaryMismatch = 0;
        for (var pixel = 0; pixel < pixelSize * pixelSize; pixel++)
        {
            var offset = pixel * 4 + 3;
            var actualAlpha = decoded.Pixels[offset];
            var maskAlpha = expectedMask[offset];
            if (maskAlpha == 0 && actualAlpha != 0) outsideMaskAlpha++;
            if ((actualAlpha >= MeaningfulAlphaThreshold) != (maskAlpha >= MeaningfulAlphaThreshold))
                binaryMismatch++;
        }
        if (outsideMaskAlpha != 0)
            throw new InvalidDataException(
                $"Rendered {pixelSize}px icon has {outsideMaskAlpha} alpha pixels outside the p=" +
                $"{AppIconMask.ContinuousCornerExponent:0.00} mask.");
        var mismatchFraction = binaryMismatch / (double)(pixelSize * pixelSize);
        if (binaryMismatch != 0)
            throw new InvalidDataException(
                $"Rendered {pixelSize}px icon differs from the p={AppIconMask.ContinuousCornerExponent:0.00} " +
                $"mask in {binaryMismatch} pixels ({mismatchFraction:P4}).");
        return new RasterValidation(anyBounds, meaningfulBounds, mismatchFraction);
    }

    private static MarkValidation ValidateMarkRaster(byte[] png)
    {
        var decoded = DecodePng(png);
        if (decoded.Width != MasterSize || decoded.Height != MasterSize)
            throw new InvalidDataException("Brand mark output is not 1024x1024.");
        var visible = FindAlphaBounds(
            decoded.Pixels,
            decoded.Width,
            decoded.Height,
            CleanupAlphaThreshold);
        var meaningful = FindAlphaBounds(
            decoded.Pixels,
            decoded.Width,
            decoded.Height,
            MeaningfulAlphaThreshold);
        RequireCentered(visible, MasterSize, "brand-mark visible");
        RequireCentered(meaningful, MasterSize, "brand-mark meaningful");
        var ratio = Math.Max(visible.Width, visible.Height) / (double)MasterSize;
        if (Math.Abs(ratio - MarkContentRatio) > 0.005)
            throw new InvalidDataException(
                $"Brand mark uses {ratio:P2} of its canvas, expected {MarkContentRatio:P1}.");
        var components = CountAlphaComponents(
            decoded.Pixels,
            decoded.Width,
            decoded.Height,
            CleanupAlphaThreshold);
        if (components != 1)
            throw new InvalidDataException($"Brand mark output contains {components} alpha components.");
        return new MarkValidation(visible, meaningful, components);
    }

    private static int CountAlphaComponents(byte[] pixels, int width, int height, byte threshold)
    {
        var count = 0;
        var visited = new bool[width * height];
        var queue = new int[width * height];
        for (var index = 0; index < visited.Length; index++)
        {
            if (visited[index] || pixels[index * 4 + 3] < threshold) continue;
            count++;
            var head = 0;
            var tail = 0;
            visited[index] = true;
            queue[tail++] = index;
            while (head < tail)
            {
                var current = queue[head++];
                var x = current % width;
                var y = current / width;
                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    var neighborY = y + offsetY;
                    if (neighborY < 0 || neighborY >= height) continue;
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0) continue;
                        var neighborX = x + offsetX;
                        if (neighborX < 0 || neighborX >= width) continue;
                        var neighbor = neighborY * width + neighborX;
                        if (visited[neighbor] || pixels[neighbor * 4 + 3] < threshold) continue;
                        visited[neighbor] = true;
                        queue[tail++] = neighbor;
                    }
                }
            }
        }
        return count;
    }

    private static byte[] RenderMaskPixels(int pixelSize)
    {
        var plateSide = MasterSize * PlateRatio;
        var plateOrigin = (MasterSize - plateSide) / 2d;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(pixelSize / (double)MasterSize, pixelSize / (double)MasterSize));
            drawing.DrawGeometry(
                Brushes.White,
                null,
                CreateSharedSquareMask(new Rect(plateOrigin, plateOrigin, plateSide, plateSide)));
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return CopyPixels(bitmap);
    }

    private static void RequireCentered(AlphaBounds bounds, int size, string label)
    {
        if (Math.Abs((bounds.Left + bounds.Right) - (size - 1)) > 1
            || Math.Abs((bounds.Top + bounds.Bottom) - (size - 1)) > 1)
            throw new InvalidDataException(
                $"Rendered {size}px {label} bounds are not centered: " +
                $"x={bounds.Left}..{bounds.Right}, y={bounds.Top}..{bounds.Bottom}.");
    }

    private static Geometry CreateSharedSquareMask(Rect bounds)
    {
        if (Math.Abs(bounds.Width - bounds.Height) > 0.001)
            throw new ArgumentException("The shared app-icon mask requires square bounds.", nameof(bounds));
        var geometry = AppIconMask.Create(bounds.Width).Clone();
        geometry.Transform = new TranslateTransform(bounds.X, bounds.Y);
        geometry.Freeze();
        return geometry;
    }

    private static byte[] BuildIco(IReadOnlyList<IconFrame> frames)
    {
        const int directoryHeaderSize = 6;
        const int directoryEntrySize = 16;
        var imageOffset = directoryHeaderSize + frames.Count * directoryEntrySize;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);
        foreach (var frame in frames)
        {
            writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
            writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)frame.Png.Length);
            writer.Write((uint)imageOffset);
            imageOffset += frame.Png.Length;
        }
        foreach (var frame in frames) writer.Write(frame.Png);
        writer.Flush();
        return stream.ToArray();
    }

    private static DecodedPng DecodePng(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return new DecodedPng(converted.PixelWidth, converted.PixelHeight, CopyPixels(converted));
    }

    private static byte[] CopyPixels(BitmapSource source)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static AlphaBounds FindAlphaBounds(
        byte[] pixels,
        int width,
        int height,
        byte threshold)
    {
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        var stride = width * 4;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] < threshold) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < left || bottom < top)
            throw new InvalidDataException($"Image has no pixels at alpha threshold {threshold}.");
        return new AlphaBounds(left, top, right, bottom);
    }

    private static void PrintSource(string label, SourceAsset source, string hash) =>
        Console.WriteLine(
            $"{label}: {source.Bitmap.PixelWidth}x{source.Bitmap.PixelHeight}, SHA-256 {hash}, " +
            $"alpha>={MeaningfulAlphaThreshold} {source.MeaningfulBounds.Width}x{source.MeaningfulBounds.Height}, " +
            $"components {source.Cleanup.ComponentCount}, removed {source.Cleanup.RemovedVisiblePixels} pixels");

    private static void WriteIfChanged(string path, byte[] contents)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(contents)) return;
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string Hash(byte[] contents) =>
        Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

    private sealed record SourceAsset(
        BitmapSource Bitmap,
        AlphaBounds VisibleBounds,
        AlphaBounds MeaningfulBounds,
        ComponentCleanup Cleanup);
    private sealed record IconFrame(int Size, byte[] Png);
    private sealed record DecodedPng(int Width, int Height, byte[] Pixels);
    private sealed record RasterValidation(
        AlphaBounds AnyAlphaBounds,
        AlphaBounds MeaningfulAlphaBounds,
        double BinaryMaskMismatch);
    private sealed record MarkValidation(
        AlphaBounds VisibleBounds,
        AlphaBounds MeaningfulBounds,
        int ComponentCount);
    private readonly record struct ComponentCleanup(
        int ComponentCount,
        int LargestComponentPixels,
        int DisconnectedCandidatePixels,
        int RemovedVisiblePixels);
    private readonly record struct AlphaBounds(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left + 1;
        internal int Height => Bottom - Top + 1;
    }
}
