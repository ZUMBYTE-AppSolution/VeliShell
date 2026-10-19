using System.Collections.Concurrent;
using System.Windows.Media;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal readonly record struct StartIconRequest(string Id, string Target, IconReference? Icon, DockIconStyle Style, int Pixels);
internal readonly record struct StartIconResult(ImageSource Source, bool Freeform);

/// <summary>A bounded STA shell-reader, never on the opening/input dispatcher.</summary>
internal sealed class StartIconLoader : IDisposable
{
    internal static StartIconLoader Shared { get; } = new();
    private readonly BlockingCollection<(StartIconRequest Request, CancellationToken Token, TaskCompletionSource<StartIconResult?> Completion)> _queue = new(96);
    private readonly Dictionary<StartIconRequest, StartIconResult> _cache = [];
    private readonly Queue<StartIconRequest> _order = [];
    private readonly Func<StartIconRequest, StartIconResult> _resolve;
    private readonly object _gate = new();
    private Thread? _worker;
    private bool _disposed;
    private long _cachedAt = Environment.TickCount64;

    internal StartIconLoader(Func<StartIconRequest, StartIconResult>? resolve = null)
    {
        _resolve = resolve ?? Resolve;
        if (resolve is null && System.Windows.Application.Current is { } app) app.Exit += (_, _) => Dispose();
    }

    internal Task<StartIconResult?> LoadAsync(StartIconRequest request, CancellationToken token)
    {
        lock (_gate)
        {
            if (_disposed || token.IsCancellationRequested) return Task.FromResult<StartIconResult?>(null);
            if (Environment.TickCount64 - _cachedAt > 300_000)
            {
                _cache.Clear(); _order.Clear(); _cachedAt = Environment.TickCount64;
            }
            if (_cache.TryGetValue(request, out var cached)) return Task.FromResult<StartIconResult?>(cached);
            var result = new TaskCompletionSource<StartIconResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.TryAdd((request, token, result))) { result.SetResult(null); return result.Task; }
            if (_worker is null)
            {
                _worker = new Thread(Read) { IsBackground = true, Name = "VeliShell Start icons" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
            return result.Task;
        }
    }

    private void Read()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            StartIconResult? result = null;
            try
            {
                if (!work.Token.IsCancellationRequested)
                {
                    lock (_gate) if (_cache.TryGetValue(work.Request, out var cached)) result = cached;
                    if (result is null)
                    {
                        var loaded = _resolve(work.Request);
                        if (!loaded.Source.IsFrozen) loaded.Source.Freeze();
                        result = loaded;
                        lock (_gate)
                        {
                            if (!_cache.ContainsKey(work.Request)) _order.Enqueue(work.Request);
                            _cache[work.Request] = loaded;
                            while (_order.Count > 128) _cache.Remove(_order.Dequeue());
                        }
                    }
                }
            }
            catch (Exception error) { App.Log("Start icon unavailable", error); }
            work.Completion.TrySetResult(work.Token.IsCancellationRequested ? null : result);
        }
    }

    private static StartIconResult Resolve(StartIconRequest request) => new(
        IconService.Resolve(request.Id, request.Target, request.Icon, request.Style, request.Pixels).Source,
        request.Id is "folder" or "virtual-folder" or "folder-shortcut" || System.IO.Directory.Exists(request.Target));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            // Never join a potentially slow shell-extension call from the UI.
            while (_queue.TryTake(out var work)) work.Completion.TrySetResult(null);
            _cache.Clear(); _order.Clear();
        }
    }
}
