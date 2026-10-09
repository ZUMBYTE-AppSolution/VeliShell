using System.IO;
using System.Net.Http;

namespace VeliShell.Desktop.Services;

internal static class BoundedHttpContent
{
    internal static async Task<byte[]> ReadAsync(
        HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The response exceeds the permitted size.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximumBytes)
                throw new InvalidDataException("The response exceeds the permitted size.");
            output.Write(buffer, 0, count);
        }
    }
}
