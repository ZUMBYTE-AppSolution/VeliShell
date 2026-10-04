using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeliShell.UpdateService;

public interface IUpdateStatusStore
{
    Task WriteAsync(UpdateStatusSnapshot status, CancellationToken cancellationToken = default);
}

/// <summary>
/// Replaces the public status file atomically from a temporary file on the same volume.
/// Long-lived Windows readers must open the status file with <see cref="FileShare.Delete"/>
/// so that they can finish reading the old document while its directory entry is replaced.
/// </summary>
public sealed class AtomicUpdateStatusStore : IUpdateStatusStore
{
    private const int MaximumReplacementAttempts = 50;
    private const int MaximumRetryDelayMilliseconds = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _statusPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public AtomicUpdateStatusStore(string statusPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusPath);
        _statusPath = Path.GetFullPath(statusPath);
    }

    public async Task WriteAsync(
        UpdateStatusSnapshot status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_statusPath) ??
                            throw new InvalidOperationException("The update status path has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_statusPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(status, SerializerOptions);
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                await ReplaceAtomicallyAsync(temporaryPath, _statusPath, cancellationToken);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task ReplaceAtomicallyAsync(
        string temporaryPath,
        string statusPath,
        CancellationToken cancellationToken)
    {
        var retryDelayMilliseconds = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(statusPath))
                {
                    File.Replace(temporaryPath, statusPath, destinationBackupFileName: null);
                    return;
                }

                try
                {
                    File.Move(temporaryPath, statusPath);
                    return;
                }
                catch (IOException) when (File.Exists(statusPath))
                {
                    File.Replace(temporaryPath, statusPath, destinationBackupFileName: null);
                    return;
                }
            }
            catch (IOException error) when (
                attempt < MaximumReplacementAttempts &&
                IsTransientReplacementFailure(error))
            {
                // Windows cannot replace a file while a reader has it open without
                // FileShare.Delete. Antivirus and indexing handles are normally brief,
                // so wait without ever publishing a partially written document.
                await Task.Delay(retryDelayMilliseconds, cancellationToken);
                retryDelayMilliseconds = Math.Min(
                    retryDelayMilliseconds * 2,
                    MaximumRetryDelayMilliseconds);
            }
        }
    }

    private static bool IsTransientReplacementFailure(IOException error)
    {
        if (!OperatingSystem.IsWindows()) return true;

        // Win32 ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION. Retrying other
        // I/O errors would only hide permanent failures such as an invalid volume.
        var nativeError = error.HResult & 0xffff;
        return nativeError is 32 or 33;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // The final status was already committed or the original failure is more useful.
        }
        catch (UnauthorizedAccessException)
        {
            // The final status was already committed or the original failure is more useful.
        }
    }
}
