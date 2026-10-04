using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VeliShell.Core;

namespace VeliShell.UpdateService;

public enum ReleaseCheckState
{
    NoPublishedRelease,
    UpToDate,
    UpdateAvailable
}

public sealed record ReleaseMetadata(
    SemanticVersion Version,
    Uri ReleasePage,
    DateTimeOffset PublishedAtUtc);

public sealed record ReleaseCheckResult(
    ReleaseCheckState State,
    SemanticVersion InstalledVersion,
    ReleaseMetadata? Release);

public sealed class ReleaseCheckException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public interface IReleaseMetadataSource
{
    Task<ReleaseCheckResult> CheckAsync(
        SemanticVersion installedVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads only the fixed public GitHub latest-release endpoint. It has no asset download,
/// process launch, installation, authentication, telemetry, or application-data upload path.
/// </summary>
public sealed class GitHubReleaseMetadataClient : IReleaseMetadataSource, IDisposable
{
    private const string Owner = "ZUMBYTE-AppSolution";
    private const string Repository = "VeliShell";
    private const string GitHubApiVersion = "2026-03-10";
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan DefaultBodyReadInactivityTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly TimeSpan _bodyReadInactivityTimeout;
    private readonly bool _ownsClient;

    public GitHubReleaseMetadataClient(
        HttpClient? client = null,
        TimeSpan? bodyReadInactivityTimeout = null)
    {
        _bodyReadInactivityTimeout = bodyReadInactivityTimeout ?? DefaultBodyReadInactivityTimeout;
        if (_bodyReadInactivityTimeout <= TimeSpan.Zero ||
            _bodyReadInactivityTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(
                nameof(bodyReadInactivityTimeout),
                "The response-body inactivity timeout must be finite and greater than zero.");

        if (client is null)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PreAuthenticate = false,
                UseCookies = false,
                UseDefaultCredentials = false
            };
            _client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _ownsClient = true;
        }
        else
        {
            _client = client;
        }
    }

    public async Task<ReleaseCheckResult> CheckAsync(
        SemanticVersion installedVersion,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateServiceDefaults.LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("VeliShell-UpdateService/0.3 (+https://github.com/ZUMBYTE-AppSolution/VeliShell)");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ReleaseCheckResult(ReleaseCheckState.NoPublishedRelease, installedVersion, null);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ReleaseCheckException(
                $"http-{(int)response.StatusCode}",
                "The GitHub release metadata request did not succeed.");

        try
        {
            var metadataBytes = await ReadLimitedAsync(
                response.Content,
                _bodyReadInactivityTimeout,
                cancellationToken);
            var release = Parse(metadataBytes);
            var state = release.Version > installedVersion
                ? ReleaseCheckState.UpdateAvailable
                : ReleaseCheckState.UpToDate;
            return new ReleaseCheckResult(state, installedVersion, release);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReleaseCheckException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidDataException)
        {
            throw new ReleaseCheckException(
                "invalid-metadata",
                "GitHub returned release metadata that does not match the expected public-release schema.",
                error);
        }
    }

    public static ReleaseMetadata Parse(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(
            utf8Json.ToArray(),
            new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The release metadata root must be an object.");
        if (RequiredBoolean(root, "draft") || RequiredBoolean(root, "prerelease"))
            throw new InvalidDataException("The latest release must be a published stable release.");

        var tagName = RequiredString(root, "tag_name");
        if (!SemanticVersion.TryParse(tagName, out var version) || version.IsPrerelease)
            throw new InvalidDataException("The release tag is not a stable semantic version.");

        if (!DateTimeOffset.TryParse(
                RequiredString(root, "published_at"),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var publishedAt))
            throw new InvalidDataException("The release publication time is invalid.");

        if (!Uri.TryCreate(RequiredString(root, "html_url"), UriKind.Absolute, out var releasePage) ||
            !IsOfficialReleasePage(releasePage, tagName))
            throw new InvalidDataException("The release page is outside the official repository.");

        return new ReleaseMetadata(version, releasePage, publishedAt);
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumMetadataBytes)
            throw new ReleaseCheckException(
                "metadata-too-large",
                "The GitHub release metadata exceeded the accepted size.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await ReadWithInactivityTimeoutAsync(
                source,
                buffer.AsMemory(),
                inactivityTimeout,
                cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > MaximumMetadataBytes)
                throw new ReleaseCheckException(
                    "metadata-too-large",
                    "The GitHub release metadata exceeded the accepted size.");
            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static async ValueTask<int> ReadWithInactivityTimeoutAsync(
        Stream source,
        Memory<byte> buffer,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCancellation.CancelAfter(inactivityTimeout);
        try
        {
            return await source.ReadAsync(buffer, readCancellation.Token);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The release metadata check was canceled by the service.",
                error,
                cancellationToken);
        }
        catch (OperationCanceledException error) when (readCancellation.IsCancellationRequested)
        {
            throw new ReleaseCheckException(
                "timeout",
                "The GitHub release metadata body stopped responding.",
                error);
        }
    }

    private static bool IsOfficialReleasePage(Uri uri, string tagName)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        const string tagMarker = "/releases/tag/";
        var prefix = $"/{Owner}/{Repository}{tagMarker}";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var encodedTag = uri.AbsolutePath[prefix.Length..];
        return !encodedTag.Contains('/') &&
               string.Equals(Uri.UnescapeDataString(encodedTag), tagName, StringComparison.Ordinal);
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"The release metadata is missing '{propertyName}'.");
        return value.GetString()!;
    }

    private static bool RequiredBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"The release metadata is missing '{propertyName}'.");
        return value.GetBoolean();
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
