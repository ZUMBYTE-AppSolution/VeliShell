using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal static class MacOsIconsMaskService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // The caller must obtain a separate, explicit approval for uploading the
    // existing Windows icon. This is never invoked for folders or the bin.
    internal static async Task<IconReference> MaskAsync(Pin pin, CancellationToken cancellationToken = default)
    {
        var key = UserApiCredentials.Read(UserApiCredentials.MacOsIcons);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No macOSicons key is saved.");
        var artwork = IconService.ForOriginalWindowsIcon(pin.Id, pin.Target) ??
                      throw new InvalidDataException("The original Windows icon could not be read.");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(artwork, new Rect(0, 0, 512, 512));
        var rendered = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        using var input = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        encoder.Save(input);
        if (input.Length is 0 or > 5 * 1024 * 1024)
            throw new InvalidDataException("The source icon is too large.");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.macosicons.com/api/v1/editor/mask");
        request.Headers.Add("x-api-key", key);
        using var body = new MultipartFormDataContent();
        var imagePart = new ByteArrayContent(input.ToArray());
        imagePart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        body.Add(imagePart, "image", "icon.png");
        request.Content = body;
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 5 * 1024 * 1024)
            throw new InvalidDataException("The mask API did not return a usable image.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > 5 * 1024 * 1024)
                throw new InvalidDataException("The generated icon is too large.");
            output.Write(buffer, 0, count);
        }
        // Editor output belongs to the requesting user, unlike catalog icons.
        return CustomIconService.ImportPng(output.ToArray());
    }
}
