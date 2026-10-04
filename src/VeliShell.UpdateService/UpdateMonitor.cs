using System.IO;
using VeliShell.Core;

namespace VeliShell.UpdateService;

public sealed class UpdateMonitor(
    IReleaseMetadataSource releaseSource,
    IUpdateStatusStore statusStore,
    SemanticVersion installedVersion,
    TimeSpan checkInterval,
    TimeProvider? timeProvider = null)
{
    private readonly IReleaseMetadataSource _releaseSource = releaseSource ??
        throw new ArgumentNullException(nameof(releaseSource));
    private readonly IUpdateStatusStore _statusStore = statusStore ??
        throw new ArgumentNullException(nameof(statusStore));
    private readonly SemanticVersion _installedVersion = installedVersion;
    private readonly TimeSpan _checkInterval = checkInterval > TimeSpan.Zero
        ? checkInterval
        : throw new ArgumentOutOfRangeException(nameof(checkInterval));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CheckOnceAsync(cancellationToken);
            await Task.Delay(_checkInterval, _timeProvider, cancellationToken);
        }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        UpdateStatusSnapshot status;
        try
        {
            var result = await _releaseSource.CheckAsync(_installedVersion, cancellationToken);
            status = UpdateStatusSnapshot.FromResult(result, _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            status = Failure("timeout");
        }
        catch (ReleaseCheckException error)
        {
            status = Failure(error.Code);
        }
        catch (HttpRequestException)
        {
            status = Failure("network");
        }
        catch (IOException)
        {
            status = Failure("network");
        }

        await _statusStore.WriteAsync(status, cancellationToken);
    }

    private UpdateStatusSnapshot Failure(string errorCode) =>
        UpdateStatusSnapshot.Failed(_installedVersion, _timeProvider.GetUtcNow(), errorCode);
}
