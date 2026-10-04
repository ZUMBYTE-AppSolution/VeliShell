using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeliShell.UpdateService;

public interface IUpdateStatusStore
{
    Task WriteAsync(UpdateStatusSnapshot status, CancellationToken cancellationToken = default);
}

/// <summary>Replaces the public status file atomically from a temporary file on the same volume.</summary>
public sealed class AtomicUpdateStatusStore : IUpdateStatusStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _statusPath;

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

    private static async Task ReplaceAtomicallyAsync(
        string temporaryPath,
        string statusPath,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 6;
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
            catch (IOException) when (attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), cancellationToken);
            }
        }
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
