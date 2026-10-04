using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VeliShell.Core;
using VeliShell.UpdateService;

var failures = 0;
var count = 0;

async Task TestAsync(string name, Func<Task> run)
{
    count++;
    try
    {
        await run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}: {error.Message}");
    }
}

void Check(bool condition, string? message = null)
{
    if (!condition) throw new InvalidOperationException(message ?? "Assertion failed.");
}

static string ReleaseJson(
    string tag = "v0.4.0",
    string releasePage = "https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.4.0",
    bool draft = false,
    bool prerelease = false) => $$"""
    {
      "tag_name": "{{tag}}",
      "html_url": "{{releasePage}}",
      "published_at": "2026-10-03T09:30:00Z",
      "draft": {{draft.ToString().ToLowerInvariant()}},
      "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
      "name": "Ignored display name",
      "body": "Ignored release notes",
      "assets": [
        {
          "name": "VeliShell-0.4.0-win-x64.msi",
          "browser_download_url": "https://attacker.invalid/never-requested"
        }
      ]
    }
    """;

await TestAsync("latest-release request is fixed, anonymous metadata-only GET", async () =>
{
    RequestObservation? observed = null;
    var calls = 0;
    using var httpClient = new HttpClient(new StubHandler(request =>
    {
        calls++;
        observed = RequestObservation.From(request);
        return JsonResponse(HttpStatusCode.OK, ReleaseJson());
    }));
    using var client = new GitHubReleaseMetadataClient(httpClient);

    var result = await client.CheckAsync(SemanticVersion.Parse("0.3.0"));

    Check(calls == 1, "The client made more than one request.");
    var request = observed ?? throw new InvalidOperationException("No request was observed.");
    Check(request.Method == HttpMethod.Get);
    Check(request.Uri == UpdateServiceDefaults.LatestReleaseApi);
    Check(request.Authorization is null, "The public check must not send credentials.");
    Check(request.ContentLength is null, "A metadata GET must not upload a request body.");
    Check(request.Accept.Contains("application/vnd.github+json", StringComparison.Ordinal));
    Check(request.ApiVersion == "2026-03-10");
    Check(result.State == ReleaseCheckState.UpdateAvailable);
    Check(result.Release?.Version == SemanticVersion.Parse("0.4.0"));
});

await TestAsync("release asset URLs are never followed", async () =>
{
    var calls = 0;
    using var httpClient = new HttpClient(new StubHandler(_ =>
    {
        calls++;
        return JsonResponse(HttpStatusCode.OK, ReleaseJson());
    }));
    using var client = new GitHubReleaseMetadataClient(httpClient);

    await client.CheckAsync(SemanticVersion.Parse("0.3.0"));
    Check(calls == 1, "An asset or release-page request was made.");
});

await TestAsync("equal release is up to date", async () =>
{
    using var httpClient = new HttpClient(new StubHandler(_ =>
        JsonResponse(HttpStatusCode.OK, ReleaseJson(tag: "v0.3.0", releasePage:
            "https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.3.0"))));
    using var client = new GitHubReleaseMetadataClient(httpClient);
    var result = await client.CheckAsync(SemanticVersion.Parse("0.3.0"));
    Check(result.State == ReleaseCheckState.UpToDate);
});

await TestAsync("404 means no published release", async () =>
{
    using var httpClient = new HttpClient(new StubHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.NotFound)));
    using var client = new GitHubReleaseMetadataClient(httpClient);
    var result = await client.CheckAsync(SemanticVersion.Parse("0.3.0"));
    Check(result.State == ReleaseCheckState.NoPublishedRelease);
    Check(result.Release is null);
});

await TestAsync("redirects are not accepted as release metadata", async () =>
{
    using var httpClient = new HttpClient(new StubHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.invalid/latest") }
        }));
    using var client = new GitHubReleaseMetadataClient(httpClient);
    var error = await CaptureAsync<ReleaseCheckException>(() =>
        client.CheckAsync(SemanticVersion.Parse("0.3.0")));
    Check(error.Code == "http-302");
});

await TestAsync("foreign release page is rejected", async () =>
{
    using var httpClient = new HttpClient(new StubHandler(_ => JsonResponse(
        HttpStatusCode.OK,
        ReleaseJson(releasePage: "https://attacker.invalid/VeliShell/releases/tag/v0.4.0"))));
    using var client = new GitHubReleaseMetadataClient(httpClient);
    var error = await CaptureAsync<ReleaseCheckException>(() =>
        client.CheckAsync(SemanticVersion.Parse("0.3.0")));
    Check(error.Code == "invalid-metadata");
});

await TestAsync("draft and prerelease metadata are rejected", async () =>
{
    foreach (var json in new[]
             {
                 ReleaseJson(draft: true),
                 ReleaseJson(tag: "v0.4.0-rc.1", prerelease: true)
             })
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            JsonResponse(HttpStatusCode.OK, json)));
        using var client = new GitHubReleaseMetadataClient(httpClient);
        var error = await CaptureAsync<ReleaseCheckException>(() =>
            client.CheckAsync(SemanticVersion.Parse("0.3.0")));
        Check(error.Code == "invalid-metadata");
    }
});

await TestAsync("oversized metadata is rejected before reading", async () =>
{
    using var httpClient = new HttpClient(new StubHandler(_ =>
    {
        var content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }));
    using var client = new GitHubReleaseMetadataClient(httpClient);
    var error = await CaptureAsync<ReleaseCheckException>(() =>
        client.CheckAsync(SemanticVersion.Parse("0.3.0")));
    Check(error.Code == "metadata-too-large");
});

await TestAsync("stalled metadata body is bounded by the inactivity timeout", async () =>
{
    using var httpClient = new HttpClient(new StubHandler(_ => BlockingJsonResponse()));
    using var client = new GitHubReleaseMetadataClient(
        httpClient,
        bodyReadInactivityTimeout: TimeSpan.FromMilliseconds(120));
    var timer = Stopwatch.StartNew();

    var error = await CaptureAsync<ReleaseCheckException>(() =>
        client.CheckAsync(SemanticVersion.Parse("0.3.0")));

    Check(error.Code == "timeout");
    Check(timer.Elapsed < TimeSpan.FromSeconds(5),
        "The inactivity timeout did not stop the stalled metadata read promptly.");
});

await TestAsync("service-stop cancellation wins over the body inactivity timeout", async () =>
{
    using var httpClient = new HttpClient(new StubHandler(_ => BlockingJsonResponse()));
    using var client = new GitHubReleaseMetadataClient(
        httpClient,
        bodyReadInactivityTimeout: TimeSpan.FromSeconds(5));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

    var error = await CaptureAsync<OperationCanceledException>(() =>
        client.CheckAsync(SemanticVersion.Parse("0.3.0"), cancellation.Token));

    Check(error.CancellationToken == cancellation.Token);
});

var tempRoot = Path.Combine(Path.GetTempPath(), "VeliShellUpdateServiceTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
try
{
    await TestAsync("status file is concise and atomically replaced", async () =>
    {
        var path = Path.Combine(tempRoot, "status", "update-status.json");
        var store = new AtomicUpdateStatusStore(path);
        var release = new ReleaseMetadata(
            SemanticVersion.Parse("0.4.0"),
            new Uri("https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.4.0"),
            DateTimeOffset.Parse("2026-10-03T09:30:00Z"));
        var first = UpdateStatusSnapshot.FromResult(
            new ReleaseCheckResult(
                ReleaseCheckState.UpdateAvailable,
                SemanticVersion.Parse("0.3.0"),
                release),
            DateTimeOffset.Parse("2026-10-04T07:00:00Z"));
        await store.WriteAsync(first);

        var second = UpdateStatusSnapshot.Failed(
            SemanticVersion.Parse("0.3.0"),
            DateTimeOffset.Parse("2026-10-04T08:00:00Z"),
            "network");
        await store.WriteAsync(second);

        var bytes = await File.ReadAllBytesAsync(path);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Check(root.GetProperty("schemaVersion").GetInt32() == 1);
        Check(root.GetProperty("state").GetString() == "checkFailed");
        Check(root.GetProperty("installedVersion").GetString() == "0.3.0");
        Check(root.GetProperty("errorCode").GetString() == "network");
        Check(!root.TryGetProperty("latestVersion", out _));
        Check(!root.TryGetProperty("body", out _));
        Check(!root.TryGetProperty("assets", out _));
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0);
    });

    await TestAsync("readers see only complete status documents during replacement", async () =>
    {
        var path = Path.Combine(tempRoot, "stress", "update-status.json");
        var store = new AtomicUpdateStatusStore(path);
        await store.WriteAsync(UpdateStatusSnapshot.Failed(
            SemanticVersion.Parse("0.3.0"),
            DateTimeOffset.UtcNow,
            "initial"));

        using var stop = new CancellationTokenSource();
        var invalidReads = 0;
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // Atomic replacement on Windows requires readers to share delete
                    // access. This keeps the old handle readable while its directory
                    // entry is swapped for the complete new document.
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        16 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var document = await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: stop.Token);
                    if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
                        Interlocked.Increment(ref invalidReads);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    // A reader can briefly hold a non-delete-sharing handle; retry it.
                }
                catch (JsonException)
                {
                    Interlocked.Increment(ref invalidReads);
                }
            }
        });

        for (var index = 0; index < 40; index++)
        {
            await store.WriteAsync(UpdateStatusSnapshot.Failed(
                SemanticVersion.Parse("0.3.0"),
                DateTimeOffset.UtcNow,
                index % 2 == 0 ? "network" : "timeout"));
        }
        stop.Cancel();
        await reader;
        Check(invalidReads == 0, "A reader observed partial JSON.");
    });

    await TestAsync("status replacement survives a transient non-sharing reader", async () =>
    {
        var path = Path.Combine(tempRoot, "sharing", "update-status.json");
        var store = new AtomicUpdateStatusStore(path);
        await store.WriteAsync(UpdateStatusSnapshot.Failed(
            SemanticVersion.Parse("0.3.0"),
            DateTimeOffset.UtcNow,
            "initial"));

        Task replacement;
        await using (var blocker = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            replacement = store.WriteAsync(UpdateStatusSnapshot.Failed(
                SemanticVersion.Parse("0.3.0"),
                DateTimeOffset.UtcNow,
                "replacement"));

            await Task.Delay(650);
            Check(!replacement.IsCompletedSuccessfully,
                "The test reader did not block replacement as expected on Windows.");
        }

        await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Check(document.RootElement.GetProperty("errorCode").GetString() == "replacement");
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0);
    });

    await TestAsync("blocked status replacement honors cancellation and cleans its temporary file", async () =>
    {
        var path = Path.Combine(tempRoot, "cancelled-sharing", "update-status.json");
        var store = new AtomicUpdateStatusStore(path);
        await store.WriteAsync(UpdateStatusSnapshot.Failed(
            SemanticVersion.Parse("0.3.0"),
            DateTimeOffset.UtcNow,
            "initial"));

        await using (var blocker = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            var error = await CaptureAsync<OperationCanceledException>(() =>
                store.WriteAsync(UpdateStatusSnapshot.Failed(
                    SemanticVersion.Parse("0.3.0"),
                    DateTimeOffset.UtcNow,
                    "cancelled"),
                    cancellation.Token));
            Check(error.CancellationToken == cancellation.Token);
        }

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Check(document.RootElement.GetProperty("errorCode").GetString() == "initial");
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0);
    });
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); }
    catch { /* Best-effort cleanup of test-only temporary files. */ }
}

await TestAsync("monitor maps network failure to a data-free status", async () =>
{
    var statusStore = new RecordingStatusStore();
    var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-04T08:00:00Z"));
    var monitor = new UpdateMonitor(
        new ThrowingReleaseSource(new HttpRequestException("local path or user data must not escape")),
        statusStore,
        SemanticVersion.Parse("0.3.0"),
        TimeSpan.FromHours(12),
        time);

    await monitor.CheckOnceAsync();
    Check(statusStore.Status?.State == UpdateStatusState.CheckFailed);
    Check(statusStore.Status?.ErrorCode == "network");
    Check(statusStore.Status?.InstalledVersion == "0.3.0");
    Check(statusStore.Status?.LatestVersion is null);
    Check(statusStore.Status?.ReleasePage is null);
});

await TestAsync("service-stop cancellation is not written as a failed check", async () =>
{
    var statusStore = new RecordingStatusStore();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var monitor = new UpdateMonitor(
        new CancelledReleaseSource(),
        statusStore,
        SemanticVersion.Parse("0.3.0"),
        TimeSpan.FromHours(12));

    await CaptureAsync<OperationCanceledException>(() =>
        monitor.CheckOnceAsync(cancellation.Token));
    Check(statusStore.Status is null);
});

Console.WriteLine($"{count - failures}/{count} tests passed.");
return failures == 0 ? 0 : 1;

static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

static HttpResponseMessage BlockingJsonResponse()
{
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new BlockingReadStream())
    };
    response.Content.Headers.ContentLength = 128;
    return response;
}

static async Task<TException> CaptureAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException error)
    {
        return error;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

file sealed record RequestObservation(
    HttpMethod Method,
    Uri? Uri,
    string? Authorization,
    long? ContentLength,
    string Accept,
    string? ApiVersion)
{
    public static RequestObservation From(HttpRequestMessage request) => new(
        request.Method,
        request.RequestUri,
        request.Headers.Authorization?.ToString(),
        request.Content?.Headers.ContentLength,
        string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
        request.Headers.TryGetValues("X-GitHub-Api-Version", out var values)
            ? values.SingleOrDefault()
            : null);
}

file sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(respond(request));
}

file sealed class BlockingReadStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}

file sealed class RecordingStatusStore : IUpdateStatusStore
{
    public UpdateStatusSnapshot? Status { get; private set; }

    public Task WriteAsync(UpdateStatusSnapshot status, CancellationToken cancellationToken = default)
    {
        Status = status;
        return Task.CompletedTask;
    }
}

file sealed class ThrowingReleaseSource(Exception error) : IReleaseMetadataSource
{
    public Task<ReleaseCheckResult> CheckAsync(
        SemanticVersion installedVersion,
        CancellationToken cancellationToken = default) => Task.FromException<ReleaseCheckResult>(error);
}

file sealed class CancelledReleaseSource : IReleaseMetadataSource
{
    public Task<ReleaseCheckResult> CheckAsync(
        SemanticVersion installedVersion,
        CancellationToken cancellationToken = default) => Task.FromCanceled<ReleaseCheckResult>(cancellationToken);
}

file sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
