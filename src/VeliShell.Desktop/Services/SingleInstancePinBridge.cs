using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

/// <summary>
/// A same-user, same-session bridge for the Explorer verb. The binary,
/// length-prefixed protocol preserves every valid Unicode file-name character.
/// </summary>
internal sealed class SingleInstancePinBridge : IDisposable
{
    private const int MaximumMessageBytes = ShellPinCommand.MaximumPathLength * 4;
    private readonly Func<string, bool> _queuePin;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    internal SingleInstancePinBridge(Func<string, bool> queuePin) => _queuePin = queuePin;

    internal static string PipeName => $"Zumbyte.VeliShell.PinToDock.v1.{Process.GetCurrentProcess().SessionId}";

    internal void Start() => _serverTask ??= RunServerLoopAsync(_shutdown.Token);

    internal static async Task<bool> ForwardAsync(string path, TimeSpan timeout)
    {
        if (!ShellPinCommand.TryNormalizeExistingPath(path, out var normalized)) return false;
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            await WritePathAsync(client, normalized, cancellation.Token).ConfigureAwait(false);
            var response = new byte[1];
            await client.ReadExactlyAsync(response, cancellation.Token).ConfigureAwait(false);
            return response[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
                                          or UnauthorizedAccessException)
        {
            App.Log("Could not forward the Explorer pin command to the running instance", exception);
            return false;
        }
    }

    private async Task RunServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var path = await ReadPathAsync(server, requestTimeout.Token).ConfigureAwait(false);
                var accepted = path is not null
                               && ShellPinCommand.TryNormalizeExistingPath(path, out var normalized)
                               && _queuePin(normalized);
                await server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, requestTimeout.Token)
                    .ConfigureAwait(false);
                await server.FlushAsync(requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or DecoderFallbackException
                                              or OperationCanceledException)
            {
                App.Log("Explorer pin-command bridge rejected a request", exception);
            }
        }
    }

    private static async Task WritePathAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var content = Encoding.UTF8.GetBytes(path);
        if (content.Length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid path size.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, content.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadPathAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid path size.");
        var content = new byte[length];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(content);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        // The asynchronous server owns token registrations until its pending
        // WaitForConnection call observes cancellation. Keep this tiny source
        // alive for the remaining process lifetime instead of racing disposal.
    }
}
