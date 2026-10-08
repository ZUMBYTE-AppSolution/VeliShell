using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeliShell.Core;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal static class IconService
{
    private const int CacheCapacity = 64;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> CacheOrder = [];

    internal static ImageSource For(string id, string target = "", IconReference? icon = null)
    {
        // A user-selected local image is an explicit override in either icon
        // style. Online catalog artwork keeps the existing Mac-style behavior.
        if (CustomIconService.TryLoad(icon) is { } custom) return custom;
        var iconStyle = CurrentIconStyle();
        if (iconStyle == DockIconStyle.Mac)
        {
            if (OnlineIconService.TryLoad(icon) is { } online) return online;
            if (id == "velishell") return VeliShellAsset();
            if (id is "files" or "browser" or "notes" or "system" or "trash" or "trash-full" or "overflow" or "start")
                return id switch
                {
                    "trash" => BundledAsset("trash", "/VeliShell;component/Assets/SystemIcons/RecycleBinEmpty.png"),
                    "trash-full" => BundledAsset("trash-full", "/VeliShell;component/Assets/SystemIcons/RecycleBinFull.png"),
                    _ => BuiltIn(id)
                };
        }
        else if (id == "velishell")
        {
            return VeliShellAsset();
        }
        else if (id is "overflow" or "start")
        {
            return BuiltIn(id);
        }

        var path = ResolveShellTarget(id, target, iconStyle);
        if (string.IsNullOrWhiteSpace(path) ||
            !(path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(path) || Directory.Exists(path)))
            return BuiltIn("app");
        try
        {
            if (!path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) path = Path.GetFullPath(path);
            var pixels = RequestedPixelSize();
            var cacheKey = $"shell:{iconStyle}:{id}:{pixels}:{path}";
            if (TryGetCached(cacheKey, out var cached)) return cached;

            var image = ShellImage(path, pixels) ?? LegacyShellIcon(path) ?? BuiltIn("app");
            StoreCached(cacheKey, image);
            return image;
        }
        catch (Exception ex) { App.Log("Could not read an app icon", ex); }
        return BuiltIn("app");
    }

    private static DockIconStyle CurrentIconStyle()
    {
        try
        {
            if (Application.Current is App { Preferences: { } preferences }) return preferences.IconStyle;
        }
        catch (InvalidOperationException)
        {
            // A design-time or test host may not have a fully initialized app.
        }
        return DockIconStyle.Mac;
    }

    private static string ResolveShellTarget(string id, string target, DockIconStyle style)
    {
        var expanded = Environment.ExpandEnvironmentVariables(target ?? "").Trim();
        if (style != DockIconStyle.Windows) return expanded;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = id switch
        {
            "files" => [Path.Combine(windows, "explorer.exe")],
            "notes" => [Path.Combine(windows, "System32", "notepad.exe")],
            "system" => [Path.Combine(windows, "ImmersiveControlPanel", "SystemSettings.exe")],
            "browser" =>
            [
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")
            ],
            "trash" or "trash-full" => ["shell:RecycleBinFolder"],
            _ => Array.Empty<string>()
        };
        var known = candidates.FirstOrDefault(candidate =>
            candidate.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(candidate));
        if (!string.IsNullOrWhiteSpace(known)) return known;
        if (File.Exists(expanded) || Directory.Exists(expanded) ||
            expanded.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return expanded;

        if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            expanded.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return expanded;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, expanded);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        }
        return expanded;
    }

    private static ImageSource VeliShellAsset()
        => BundledAsset("velishell", "/VeliShell;component/Assets/VeliShellApp.png");

    private static ImageSource BundledAsset(string id, string packUri)
    {
        var cacheKey = "asset:" + id;
        if (TryGetCached(cacheKey, out var cached)) return cached;

        ImageSource image;
        try
        {
            var resourceUri = new Uri(packUri, UriKind.Relative);
            var resource = Application.GetResourceStream(resourceUri);
            if (resource?.Stream is null)
            {
                image = BuiltIn(id);
            }
            else
            {
                using var stream = resource.Stream;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or UriFormatException)
        {
            App.Log("Could not load the bundled " + id + " icon", exception);
            image = BuiltIn(id);
        }

        StoreCached(cacheKey, image);
        return image;
    }

    private static ImageSource? ShellImage(string path, int pixels)
    {
        NativeMethods.IShellItemImageFactory? factory = null;
        nint bitmapHandle = 0;
        try
        {
            var interfaceId = typeof(NativeMethods.IShellItemImageFactory).GUID;
            var result = NativeMethods.SHCreateItemFromParsingName(
                path,
                0,
                ref interfaceId,
                out factory);
            if (result < 0 || factory is null) return null;

            result = factory.GetImage(
                new NativeMethods.NativeSize { Width = pixels, Height = pixels },
                NativeMethods.ShellItemImageFactoryFlags.IconOnly |
                NativeMethods.ShellItemImageFactoryFlags.BiggerSizeOk,
                out bitmapHandle);
            if (result < 0 || bitmapHandle == 0) return null;

            var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                0,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != 0) NativeMethods.DeleteObject(bitmapHandle);
            if (factory is not null && Marshal.IsComObject(factory))
            {
                try { Marshal.ReleaseComObject(factory); }
                catch (Exception exception) when (exception is ArgumentException or InvalidComObjectException) { }
            }
        }
    }

    // A small compatibility fallback for unusual shell extensions. Normal
    // file, folder and shortcut icons use the DPI-aware image factory above.
    private static ImageSource? LegacyShellIcon(string path)
    {
        if (NativeMethods.SHGetFileInfo(
                path,
                0,
                out var info,
                (uint)Marshal.SizeOf<NativeMethods.ShellFileInfo>(),
                0x100) == 0 || info.Icon == 0) return null;
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.Icon);
        }
    }

    private static int RequestedPixelSize()
    {
        var iconSize = Settings.DefaultIconSize;
        var dpiScale = 1d;
        try
        {
            if (Application.Current is App app && app.Preferences is not null)
                iconSize = Math.Clamp(app.Preferences.IconSize, Settings.MinimumIconSize, Settings.MaximumIconSize);
            if (Application.Current?.MainWindow is { } window)
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                dpiScale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
            }
        }
        catch (InvalidOperationException)
        {
            // During early window construction WPF can temporarily have no DPI source.
        }

        if (!double.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1;
        // DockMath magnifies up to 1.42; 1.5 retains a little sampling headroom.
        var requested = (int)Math.Ceiling(iconSize * dpiScale * 1.5);
        var bucket = ((requested + 15) / 16) * 16;
        return Math.Clamp(bucket, 48, 512);
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static Pen Pen(string color, double thickness = 2) => new(Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private static ImageSource BuiltIn(string id)
    {
        var key = "builtin:" + id;
        if (TryGetCached(key, out var image)) return image;
        var group = new DrawingGroup();
        using (var d = group.Open())
        {
            var palette = id switch
            {
                "files" => ("#59CEFF", "#1277EC"),
                "browser" => ("#FFFFFF", "#DDEAF5"),
                "notes" => ("#FFFDF0", "#F4F1E6"),
                "system" => ("#D9DEE5", "#89929E"),
                "velishell" => ("#85C9FF", "#356BF1"),
                "trash" or "trash-full" => ("#F6F8FA", "#D2D9E2"),
                "overflow" => ("#EBECF0", "#ADB5C2"),
                "start" => ("#68C8FF", "#1870DF"),
                _ => ("#9A9CFC", "#6655D8")
            };
            var gradient = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(palette.Item1), (Color)ColorConverter.ConvertFromString(palette.Item2), 90);
            d.DrawRoundedRectangle(gradient, new Pen(Brush("#22000000"), 0.8), new Rect(3, 3, 58, 58), 13, 13);
            switch (id)
            {
                case "files":
                    d.DrawGeometry(Brush("#D6F3FF"), null, Geometry.Parse("M 13,23 L 13,18 Q 13,16 16,16 L 27,16 L 32,21 L 48,21 Q 51,21 51,24 L 51,46 Q 51,48 48,48 L 16,48 Q 13,48 13,45 Z"));
                    d.DrawRoundedRectangle(Brush("#FFFFFF"), null, new Rect(13, 26, 38, 22), 3, 3);
                    break;
                case "browser":
                    d.DrawEllipse(new LinearGradientBrush(Colors.DeepSkyBlue, Colors.RoyalBlue, 90), null, new Point(32, 32), 23, 23);
                    for (var i = 0; i < 12; i++)
                    {
                        var a = i * Math.PI / 6;
                        d.DrawLine(Pen("#D0FFFFFF", 1), new Point(32 + 17 * Math.Cos(a), 32 + 17 * Math.Sin(a)), new Point(32 + 20 * Math.Cos(a), 32 + 20 * Math.Sin(a)));
                    }
                    d.DrawGeometry(Brush("#FF655E"), null, Geometry.Parse("M 40,16 L 36,36 L 28,28 Z"));
                    d.DrawGeometry(Brush("#FFFFFF"), null, Geometry.Parse("M 24,48 L 28,28 L 36,36 Z"));
                    break;
                case "notes":
                    d.DrawRoundedRectangle(Brush("#FFCD42"), null, new Rect(3, 3, 58, 17), 12, 12);
                    d.DrawRectangle(Brush("#FFCD42"), null, new Rect(3, 13, 58, 9));
                    for (var y = 30; y <= 49; y += 7) d.DrawLine(Pen("#C7C4B9", 1), new Point(12, y), new Point(52, y));
                    break;
                case "system":
                    var gear = new StreamGeometry();
                    using (var g = gear.Open())
                    {
                        var points = Enumerable.Range(0, 48).Select(i =>
                        {
                            var a = i * Math.PI * 2 / 48;
                            var r = (i % 4 is 0 or 3) ? 21 : 17;
                            return new Point(32 + Math.Cos(a) * r, 32 + Math.Sin(a) * r);
                        }).ToArray();
                        g.BeginFigure(points[0], true, true);
                        g.PolyLineTo(points.Skip(1).ToArray(), true, false);
                    }
                    d.DrawGeometry(Brush("#505B6A"), null, gear);
                    d.DrawEllipse(Brush("#DDE2EA"), Pen("#E9EDF4", 2), new Point(32,32), 11,11);
                    d.DrawEllipse(Brush("#768291"), null, new Point(32,32), 5,5);
                    break;
                case "velishell":
                    // Original mark, drawn in code. No Apple icons or font files are bundled.
                    d.DrawLine(Pen("#FFFFFF", 5), new Point(21,19), new Point(21,45));
                    d.DrawLine(Pen("#FFFFFF", 5), new Point(43,19), new Point(43,45));
                    d.DrawLine(Pen("#FFFFFF", 5), new Point(21,32), new Point(43,32));
                    break;
                case "trash":
                case "trash-full":
                    if (id == "trash-full")
                    {
                        d.DrawGeometry(Brush("#89B7D8"), Pen("#627E95", 1.2), Geometry.Parse("M 23,25 L 27,16 L 34,23 L 39,17 L 43,27 Z"));
                        d.DrawGeometry(Brush("#F3D06A"), Pen("#A98D3C", 1.2), Geometry.Parse("M 20,29 L 24,20 L 32,27 L 39,21 L 45,31 Z"));
                    }
                    d.DrawGeometry(Brush("#EEFFFFFF"), Pen("#8C98A6", 2), Geometry.Parse("M 19,23 L 22,48 Q 22,51 26,51 L 39,51 Q 42,51 42,48 L 45,23 Z"));
                    if (id == "trash-full")
                    {
                        d.DrawGeometry(Brush("#A6CCE5"), null, Geometry.Parse("M 24,29 L 30,26 L 34,32 L 30,38 L 24,35 Z"));
                        d.DrawGeometry(Brush("#F0CF70"), null, Geometry.Parse("M 33,28 L 41,30 L 39,38 L 32,36 Z"));
                        d.DrawGeometry(Brush("#D5DCE5"), null, Geometry.Parse("M 27,39 L 35,35 L 41,42 L 37,47 L 28,46 Z"));
                    }
                    d.DrawLine(Pen("#8C98A6",2),new Point(17,20),new Point(47,20));
                    d.DrawRoundedRectangle(null,Pen("#8C98A6",2),new Rect(27,14,10,6),2,2);
                    for(var x=27; x<=37; x+=5) d.DrawLine(Pen("#C2CAD4",1.5),new Point(x,28),new Point(x,45));
                    break;
                case "overflow":
                    for (var x=20; x<=44; x+=12) d.DrawEllipse(Brush("#3D4959"),null,new Point(x,32),3,3);
                    break;
                case "start":
                    d.DrawGeometry(Brush("#F8FDFF"), null,
                        Geometry.Parse("M 15,18 L 30,16 L 30,30 L 15,30 Z M 34,15 L 49,13 L 49,30 L 34,30 Z M 15,34 L 30,34 L 30,48 L 15,46 Z M 34,34 L 49,34 L 49,51 L 34,49 Z"));
                    break;
                default:
                    d.DrawRoundedRectangle(null, Pen("#FFFFFF", 2.5), new Rect(15,17,34,30), 4,4);
                    d.DrawLine(Pen("#FFFFFF",2),new Point(15,25),new Point(49,25));
                    break;
            }
        }
        // Fix the drawing bounds for consistent icon alignment.
        group.Children.Insert(0, new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        group.Freeze();
        image = new DrawingImage(group);
        image.Freeze();
        StoreCached(key, image);
        return image;
    }

    private static bool TryGetCached(string key, out ImageSource image)
    {
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(key, out var entry))
            {
                image = null!;
                return false;
            }

            CacheOrder.Remove(entry.Node);
            CacheOrder.AddLast(entry.Node);
            image = entry.Image;
            return true;
        }
    }

    private static void StoreCached(string key, ImageSource image)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var existing))
            {
                existing.Image = image;
                CacheOrder.Remove(existing.Node);
                CacheOrder.AddLast(existing.Node);
                return;
            }

            while (Cache.Count >= CacheCapacity && CacheOrder.First is { } oldest)
            {
                CacheOrder.RemoveFirst();
                Cache.Remove(oldest.Value);
            }

            var node = CacheOrder.AddLast(key);
            Cache.Add(key, new CacheEntry(image, node));
        }
    }

    private sealed class CacheEntry(ImageSource image, LinkedListNode<string> node)
    {
        internal ImageSource Image { get; set; } = image;
        internal LinkedListNode<string> Node { get; } = node;
    }
}
