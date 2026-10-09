namespace VeliShell.Desktop.Services;

internal sealed class SearchCatalogIndex : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMinutes(15));
    private IReadOnlyList<SearchEntry> _snapshot = [];
    private Task? _work;

    internal IReadOnlyList<SearchEntry> Snapshot => Volatile.Read(ref _snapshot);
    internal event Action? Changed;

    internal void Start() => _work ??= Task.Run(async () =>
    {
        try
        {
            do
            {
                try
                {
                    var entries = SearchCatalogService.Discover();
                    Volatile.Write(ref _snapshot, entries);
                    Changed?.Invoke();
                }
                catch (Exception exception)
                {
                    App.Log("Could not refresh the program search index", exception);
                }
            } while (await _timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    });

    public void Dispose()
    {
        _lifetime.Cancel();
        _timer.Dispose();
        _lifetime.Dispose();
    }
}
