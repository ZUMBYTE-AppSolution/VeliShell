using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VeliShell.Desktop.Services;

internal static class SteamIconService
{
    // Use the icon embedded in this installation's steam.exe. Only its colors
    // are desaturated in memory; VeliShell does not draw or bundle a logo.
    internal static BitmapSource? FromExecutable(string executable)
    {
        try
        {
            return IconService.ForOriginalWindowsIcon("app", executable) is BitmapSource bitmap
                ? Desaturate(bitmap)
                : null;
        }
        catch (Exception exception)
        {
            App.Log("Could not load the installed Steam icon", exception);
            return null;
        }
    }

    internal static BitmapSource Desaturate(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var gray = (byte)Math.Clamp((int)Math.Round(
                pixels[index + 2] * 0.2126 + pixels[index + 1] * 0.7152 + pixels[index] * 0.0722), 0, 255);
            pixels[index] = gray;
            pixels[index + 1] = gray;
            pixels[index + 2] = gray;
            // The source alpha stays untouched, including the icon's outline.
        }
        var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight,
            bitmap.DpiX, bitmap.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
