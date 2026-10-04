using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeliShell.Core;
using VeliShell.Desktop.Services;

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
        Console.WriteLine($"FAIL  {name}: {error.GetType().Name}: {error.Message}");
    }
}

await TestAsync("parser accepts one official release asset and preserves the changelog", async () =>
{
    var payload = Encoding.UTF8.GetBytes("installer-body");
    RequestObservation? observed = null;
    using var httpClient = Client(async (request, _) =>
    {
        observed = RequestObservation.From(request);
        return await Task.FromResult(JsonResponse(ReleaseJson(assets: [ValidAsset(payload)])));
    });
    using var service = new GitHubReleaseUpdateService(httpClient);

    var result = await service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0"));

    Check(result.State == UpdateCheckState.UpdateAvailable);
    var release = result.Release ?? throw new InvalidOperationException("Release was not parsed.");
    Check(release.Version == SemanticVersion.Parse("0.4.0"));
    Check(release.Changelog == "Fixed update tests.");
    Check(release.Installer.FileName == "VeliShell-0.4.0-win-x64.msi");
    Check(release.Installer.Size == payload.LongLength);
    Check(release.Installer.Sha256 == Sha256(payload));
    var request = observed ?? throw new InvalidOperationException("No metadata request was observed.");
    Check(request.Method == HttpMethod.Get);
    Check(request.Uri == new Uri("https://api.github.com/repos/ZUMBYTE-AppSolution/VeliShell/releases/latest"));
    Check(request.ApiVersion == "2026-03-10");
    Check(request.Accept.Contains("application/vnd.github+json", StringComparison.Ordinal));
    Check(request.UserAgent.Contains("VeliShell-Updater/0.3", StringComparison.Ordinal));
});

await TestAsync("parser rejects a release page outside the official repository", async () =>
{
    using var service = ServiceForJson(ReleaseJson(
        assets: [ValidAsset([1])],
        releasePage: "https://attacker.invalid/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.4.0"));
    await ExpectAsync<UpdateSecurityException>(() =>
        service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
});

await TestAsync("parser rejects non-default GitHub ports", async () =>
{
    using var service = ServiceForJson(ReleaseJson(
        assets: [ValidAsset([1])],
        releasePage: "https://github.com:444/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.4.0"));
    await ExpectAsync<UpdateSecurityException>(() =>
        service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
});

await TestAsync("parser rejects a missing installer asset", async () =>
{
    var other = new AssetSpec(
        "VeliShell-0.4.0-win-x64-portable.zip",
        12,
        "https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/download/v0.4.0/VeliShell-0.4.0-win-x64-portable.zip",
        "sha256:" + new string('0', 64));
    using var service = ServiceForJson(ReleaseJson(assets: [other]));
    await ExpectAsync<InvalidDataException>(() =>
        service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
});

await TestAsync("parser rejects duplicate installer assets", async () =>
{
    var asset = ValidAsset([1, 2, 3]);
    using var service = ServiceForJson(ReleaseJson(assets: [asset, asset]));
    await ExpectAsync<InvalidDataException>(() =>
        service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
});

await TestAsync("parser rejects missing or malformed SHA-256 digests", async () =>
{
    var missingDigest = ValidAsset([1]) with { Digest = "" };
    using (var service = ServiceForJson(ReleaseJson(assets: [missingDigest])))
    {
        await ExpectAsync<InvalidDataException>(() =>
            service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
    }

    foreach (var digest in new[] { "sha512:" + new string('0', 64), "sha256:1234", "sha256:" + new string('z', 64) })
    {
        var asset = ValidAsset([1]) with { Digest = digest };
        using var service = ServiceForJson(ReleaseJson(assets: [asset]));
        await ExpectAsync<UpdateSecurityException>(() =>
            service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
    }
});

await TestAsync("parser rejects zero and oversized installer metadata", async () =>
{
    foreach (var size in new[] { 0L, 512L * 1024 * 1024 + 1 })
    {
        var asset = ValidAsset([1]) with { Size = size };
        using var service = ServiceForJson(ReleaseJson(assets: [asset]));
        await ExpectAsync<UpdateSecurityException>(() =>
            service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
    }
});

await TestAsync("parser rejects an installer hosted outside the official release", async () =>
{
    var asset = ValidAsset([1]) with { DownloadUrl = "https://attacker.invalid/VeliShell-0.4.0-win-x64.msi" };
    using var service = ServiceForJson(ReleaseJson(assets: [asset]));
    await ExpectAsync<UpdateSecurityException>(() =>
        service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
});

await TestAsync("download gate prevents every request and filesystem write", async () =>
{
    var calls = 0;
    using var httpClient = Client((_, _) =>
    {
        calls++;
        throw new InvalidOperationException("The handler must not run.");
    });
    using var service = new GitHubReleaseUpdateService(httpClient);
    var directory = NewTempPath();
    try
    {
        await ExpectAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(
            ReleaseFor([1, 2, 3]),
            directory,
            downloadAuthorizedByCurrentPreference: false,
            UpdateVerificationPolicy.PublicRelease));
        Check(calls == 0);
        Check(!Directory.Exists(directory));
    }
    finally
    {
        DeleteDirectory(directory);
    }
});

await TestAsync("foreign final redirect host is rejected before body download", async () =>
{
    var payload = new byte[] { 1, 2, 3 };
    using var httpClient = Client((_, _) => Task.FromResult(DownloadResponse(
        payload,
        new Uri("https://attacker.invalid/release.msi"))));
    using var service = new GitHubReleaseUpdateService(httpClient);
    var directory = NewTempPath();
    try
    {
        await ExpectAsync<UpdateSecurityException>(() => service.DownloadInstallerAsync(
            ReleaseFor(payload), directory, true, UpdateVerificationPolicy.PublicRelease));
        AssertNoDownloadResidue(directory);
    }
    finally
    {
        DeleteDirectory(directory);
    }
});

await TestAsync("approved GitHub CDN final host proceeds to hash verification", async () =>
{
    var payload = new byte[] { 1, 2, 3 };
    using var httpClient = Client((_, _) => Task.FromResult(DownloadResponse(
        payload,
        new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/file.msi"))));
    using var service = new GitHubReleaseUpdateService(httpClient);
    var directory = NewTempPath();
    try
    {
        var release = ReleaseFor(payload, expectedSha256: new string('0', 64));
        var error = await ExpectAsync<UpdateSecurityException>(() => service.DownloadInstallerAsync(
            release, directory, true, UpdateVerificationPolicy.PublicRelease));
        Check(error.Message.Contains("SHA-256", StringComparison.Ordinal));
        AssertNoDownloadResidue(directory);
    }
    finally
    {
        DeleteDirectory(directory);
    }
});

await TestAsync("SHA mismatch removes the partial installer", async () =>
{
    var payload = Encoding.UTF8.GetBytes("tampered body");
    using var httpClient = Client((_, _) => Task.FromResult(DownloadResponse(
        payload,
        new Uri("https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/download/v0.4.0/VeliShell-0.4.0-win-x64.msi"))));
    using var service = new GitHubReleaseUpdateService(httpClient);
    var directory = NewTempPath();
    try
    {
        await ExpectAsync<UpdateSecurityException>(() => service.DownloadInstallerAsync(
            ReleaseFor(payload, expectedSha256: new string('f', 64)),
            directory,
            true,
            UpdateVerificationPolicy.PublicRelease));
        AssertNoDownloadResidue(directory);
    }
    finally
    {
        DeleteDirectory(directory);
    }
});

await TestAsync("declared response length mismatch is rejected without a partial file", async () =>
{
    var payload = new byte[] { 1, 2, 3 };
    var response = DownloadResponse(
        payload,
        new Uri("https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/download/v0.4.0/VeliShell-0.4.0-win-x64.msi"));
    response.Content.Headers.ContentLength = payload.Length + 1;
    using var httpClient = Client((_, _) => Task.FromResult(response));
    using var service = new GitHubReleaseUpdateService(httpClient);
    var directory = NewTempPath();
    try
    {
        await ExpectAsync<UpdateSecurityException>(() => service.DownloadInstallerAsync(
            ReleaseFor(payload), directory, true, UpdateVerificationPolicy.PublicRelease));
        AssertNoDownloadResidue(directory);
    }
    finally
    {
        DeleteDirectory(directory);
    }
});

await TestAsync("stalled installer body times out and removes its partial file", async () =>
{
    using var httpClient = Client((_, _) => Task.FromResult(BlockingResponse(3)));
    using var service = new GitHubReleaseUpdateService(
        httpClient,
        bodyReadInactivityTimeout: TimeSpan.FromMilliseconds(120));
    var directory = NewTempPath();
    var timer = Stopwatch.StartNew();
    try
    {
        var error = await ExpectAsync<UpdateTransferTimeoutException>(() => service.DownloadInstallerAsync(
            ReleaseFor([1, 2, 3]), directory, true, UpdateVerificationPolicy.PublicRelease));
        Check(error.InactivityTimeout == TimeSpan.FromMilliseconds(120));
        Check(timer.Elapsed < TimeSpan.FromSeconds(5), "The inactivity timeout did not stop the stalled read promptly.");
        AssertNoDownloadResidue(directory);
    }
    finally
    {
        DeleteDirectory(directory);
    }
});

await TestAsync("caller cancellation remains cancellation and removes its partial file", async () =>
{
    using var httpClient = Client((_, _) => Task.FromResult(BlockingResponse(3)));
    using var service = new GitHubReleaseUpdateService(
        httpClient,
        bodyReadInactivityTimeout: TimeSpan.FromSeconds(5));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
    var directory = NewTempPath();
    try
    {
        var error = await ExpectAsync<OperationCanceledException>(() => service.DownloadInstallerAsync(
            ReleaseFor([1, 2, 3]),
            directory,
            true,
            UpdateVerificationPolicy.PublicRelease,
            cancellationToken: cancellation.Token));
        Check(error.CancellationToken == cancellation.Token);
        AssertNoDownloadResidue(directory);
    }
    finally
    {
        DeleteDirectory(directory);
    }
});

await TestAsync("stalled metadata body uses the same inactivity timeout", async () =>
{
    using var httpClient = Client((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingReadStream()),
            RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.github.com/repos/ZUMBYTE-AppSolution/VeliShell/releases/latest")
        };
        response.Content.Headers.ContentLength = 32;
        return Task.FromResult(response);
    });
    using var service = new GitHubReleaseUpdateService(
        httpClient,
        bodyReadInactivityTimeout: TimeSpan.FromMilliseconds(120));
    await ExpectAsync<UpdateTransferTimeoutException>(() =>
        service.CheckForUpdateAsync(SemanticVersion.Parse("0.3.0")));
});

await TestAsync("installer launch gates reject safely before process start", async () =>
{
    var directory = NewTempPath();
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "fake.msi");
    await File.WriteAllBytesAsync(path, [1, 2, 3]);
    var release = ReleaseFor([1, 2, 3]);
    try
    {
        var unsigned = new VerifiedUpdatePackage(
            path,
            release,
            new UpdatePackageVerification(release.Installer.Sha256, AuthenticodeStatus.NotSigned, null, null));
        await ExpectAsync<InvalidOperationException>(() => Task.Run(() =>
            GitHubReleaseUpdateService.StartVerifiedInstaller(unsigned, false, false)));
        await ExpectAsync<UpdateSecurityException>(() => Task.Run(() =>
            GitHubReleaseUpdateService.StartVerifiedInstaller(unsigned, true, false)));

        var invalid = unsigned with
        {
            Verification = unsigned.Verification with { Authenticode = AuthenticodeStatus.InvalidOrUntrusted }
        };
        await ExpectAsync<UpdateSecurityException>(() => Task.Run(() =>
            GitHubReleaseUpdateService.StartVerifiedInstaller(invalid, true, true)));

        var missing = unsigned with
        {
            FilePath = Path.Combine(directory, "missing.msi"),
            Verification = unsigned.Verification with { Authenticode = AuthenticodeStatus.Valid }
        };
        await ExpectAsync<FileNotFoundException>(() => Task.Run(() =>
            GitHubReleaseUpdateService.StartVerifiedInstaller(missing, true, false)));
    }
    finally
    {
        DeleteDirectory(directory);
    }
});

Console.WriteLine($"{count - failures}/{count} updater tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string? message = null)
{
    if (!condition) throw new InvalidOperationException(message ?? "Assertion failed.");
}

static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
    new(new StubHandler(respond)) { Timeout = Timeout.InfiniteTimeSpan };

static GitHubReleaseUpdateService ServiceForJson(string json)
{
    var client = Client((_, _) => Task.FromResult(JsonResponse(json)));
    return new GitHubReleaseUpdateService(client);
}

static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

static HttpResponseMessage DownloadResponse(byte[] payload, Uri finalUri) => new(HttpStatusCode.OK)
{
    Content = new ByteArrayContent(payload),
    RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri)
};

static HttpResponseMessage BlockingResponse(long declaredSize)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new BlockingReadStream()),
        RequestMessage = new HttpRequestMessage(
            HttpMethod.Get,
            "https://release-assets.githubusercontent.com/github-production-release-asset/file.msi")
    };
    response.Content.Headers.ContentLength = declaredSize;
    return response;
}

static AssetSpec ValidAsset(byte[] payload) => new(
    "VeliShell-0.4.0-win-x64.msi",
    payload.LongLength,
    "https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/download/v0.4.0/VeliShell-0.4.0-win-x64.msi",
    "sha256:" + Sha256(payload));

static UpdateRelease ReleaseFor(
    byte[] payload,
    string? expectedSha256 = null,
    long? size = null,
    Uri? assetUri = null) => new(
        SemanticVersion.Parse("0.4.0"),
        "v0.4.0",
        "VeliShell 0.4.0",
        "Fixed update tests.",
        DateTimeOffset.Parse("2026-10-03T09:30:00Z"),
        new Uri("https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.4.0"),
        new UpdateAsset(
            "VeliShell-0.4.0-win-x64.msi",
            size ?? payload.LongLength,
            assetUri ?? new Uri("https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/download/v0.4.0/VeliShell-0.4.0-win-x64.msi"),
            expectedSha256 ?? Sha256(payload)));

static string ReleaseJson(
    IReadOnlyList<AssetSpec> assets,
    string releasePage = "https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.4.0") =>
    JsonSerializer.Serialize(new
    {
        tag_name = "v0.4.0",
        html_url = releasePage,
        published_at = "2026-10-03T09:30:00Z",
        draft = false,
        prerelease = false,
        name = "VeliShell 0.4.0",
        body = "Fixed update tests.",
        assets = assets.Select(asset => new
        {
            name = asset.Name,
            size = asset.Size,
            browser_download_url = asset.DownloadUrl,
            digest = asset.Digest
        })
    });

static string Sha256(byte[] bytes) =>
    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static string NewTempPath() =>
    Path.Combine(Path.GetTempPath(), "VeliShellDesktopUpdaterTests-" + Guid.NewGuid().ToString("N"));

static void AssertNoDownloadResidue(string directory)
{
    if (!Directory.Exists(directory)) return;
    Check(Directory.GetFiles(directory, "*.partial-*", SearchOption.TopDirectoryOnly).Length == 0,
        "A partial download was left behind.");
    Check(!File.Exists(Path.Combine(directory, "VeliShell-0.4.0-win-x64.msi")),
        "An unverified final installer was left behind.");
}

static void DeleteDirectory(string directory)
{
    try
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
    catch
    {
        // Best-effort cleanup of test-only temporary files.
    }
}

static async Task<TException> ExpectAsync<TException>(Func<Task> action)
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

file sealed record AssetSpec(string Name, long Size, string DownloadUrl, string Digest);

file sealed record RequestObservation(
    HttpMethod Method,
    Uri? Uri,
    string Accept,
    string UserAgent,
    string? ApiVersion)
{
    internal static RequestObservation From(HttpRequestMessage request) => new(
        request.Method,
        request.RequestUri,
        string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
        request.Headers.UserAgent.ToString(),
        request.Headers.TryGetValues("X-GitHub-Api-Version", out var values)
            ? values.SingleOrDefault()
            : null);
}

file sealed class StubHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => respond(request, cancellationToken);
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
