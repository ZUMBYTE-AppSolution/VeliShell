using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

/// <summary>
/// Reads public VeliShell release metadata. It downloads only after the caller has
/// shown the changelog and applied the user's persisted update preference. Starting
/// an installer always requires a separate, current confirmation.
/// </summary>
public sealed class GitHubReleaseUpdateService : IDisposable
{
    private const string Owner = "ZUMBYTE-AppSolution";
    private const string Repository = "VeliShell";
    private const string ApiVersion = "2026-03-10";
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumChecksumBytes = 512 * 1024;
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan DefaultBodyReadInactivityTimeout = TimeSpan.FromSeconds(30);
    private static readonly Uri LatestReleaseApi = new(
        "https://api.github.com/repos/ZUMBYTE-AppSolution/VeliShell/releases/latest");
    private static readonly Uri LatestReleasePage = new(
        "https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/latest");
    private static readonly Uri ReleasesAtomFeed = new(
        "https://github.com/ZUMBYTE-AppSolution/VeliShell/releases.atom");

    private readonly HttpClient _client;
    private readonly TimeSpan _bodyReadInactivityTimeout;
    private readonly bool _ownsClient;

    public GitHubReleaseUpdateService(
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
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxAutomaticRedirections = 5
            };
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _ownsClient = true;
        }
        else
        {
            _client = client;
        }
    }

    public static SemanticVersion InstalledVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ??
                          typeof(GitHubReleaseUpdateService).Assembly.GetName().Version ??
                          new Version(0, 0, 0);
            return new SemanticVersion(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0));
        }
    }

    /// <summary>Checks metadata only. No release asset is downloaded here.</summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        SemanticVersion currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, LatestReleaseApi, "application/vnd.github+json");
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckState.NoPublishedRelease, currentVersion, null);
        if (ShouldUsePublicMetadataFallback(response.StatusCode))
        {
            response.Dispose();
            return await CheckViaPublicMetadataAsync(currentVersion, cancellationToken);
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub release check failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                null,
                response.StatusCode);

        var bytes = await ReadLimitedAsync(
            response.Content,
            MaximumMetadataBytes,
            _bodyReadInactivityTimeout,
            cancellationToken);
        var release = ParseRelease(bytes);
        return new UpdateCheckResult(
            release.Version > currentVersion ? UpdateCheckState.UpdateAvailable : UpdateCheckState.UpToDate,
            currentVersion,
            release);
    }

    /// <summary>
    /// GitHub's unauthenticated REST quota is shared by every user behind the same
    /// public IP address. When that small quota is exhausted, use public release
    /// pages and assets from the same official repository instead. The exact tag is
    /// pinned before checksums or installer metadata are read, preventing a moving
    /// "latest" alias from mixing two releases.
    /// </summary>
    private async Task<UpdateCheckResult> CheckViaPublicMetadataAsync(
        SemanticVersion currentVersion,
        CancellationToken cancellationToken)
    {
        using var latestRequest = CreateRequest(HttpMethod.Get, LatestReleasePage, "text/html");
        using var latestResponse = await _client.SendAsync(
            latestRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (latestResponse.StatusCode == HttpStatusCode.NotFound)
            return new UpdateCheckResult(UpdateCheckState.NoPublishedRelease, currentVersion, null);
        latestResponse.EnsureSuccessStatusCode();

        if (latestResponse.RequestMessage?.RequestUri is not { } releasePage ||
            !TryGetStableReleaseTag(releasePage, out var tag, out var version))
            throw new UpdateSecurityException(
                "GitHub's latest-release redirect did not resolve to the official VeliShell repository.");
        latestResponse.Dispose();

        // No changelog or package metadata is needed merely to report that this
        // installation is current. This keeps the rate-limit fallback lightweight.
        if (version <= currentVersion)
            return new UpdateCheckResult(UpdateCheckState.UpToDate, currentVersion, null);

        var atomBytes = await ReadOfficialMetadataAsync(
            ReleasesAtomFeed,
            "application/atom+xml",
            MaximumMetadataBytes,
            cancellationToken);
        var atomRelease = ParseAtomRelease(atomBytes, tag, releasePage);

        var checksumUri = OfficialReleaseAssetUri(tag, "SHA256SUMS.txt");
        var checksumBytes = await ReadOfficialAssetMetadataAsync(
            checksumUri,
            MaximumChecksumBytes,
            cancellationToken);
        var installerName = InstallerName(version);
        var sha256 = ParseSha256Checksums(checksumBytes, installerName);
        var installerUri = OfficialReleaseAssetUri(tag, installerName);
        var size = await ReadOfficialAssetSizeAsync(installerUri, cancellationToken);

        var installer = new UpdateAsset(installerName, size, installerUri, sha256);
        ValidateAsset(tag, version, installer);
        var release = new UpdateRelease(
            version,
            tag,
            atomRelease.DisplayName,
            atomRelease.Changelog,
            atomRelease.PublishedAt,
            releasePage,
            installer);
        return new UpdateCheckResult(UpdateCheckState.UpdateAvailable, currentVersion, release);
    }

    private async Task<byte[]> ReadOfficialMetadataAsync(
        Uri uri,
        string accept,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri, accept);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !IsExactGitHubEndpoint(finalUri, uri.AbsolutePath))
            throw new UpdateSecurityException("GitHub redirected release metadata to an unapproved endpoint.");
        return await ReadLimitedAsync(
            response.Content,
            maximumBytes,
            _bodyReadInactivityTimeout,
            cancellationToken);
    }

    private async Task<byte[]> ReadOfficialAssetMetadataAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri, "application/octet-stream");
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri || !IsAllowedAssetEndpoint(finalUri))
            throw new UpdateSecurityException("GitHub redirected release checksums to an unapproved endpoint.");
        return await ReadLimitedAsync(
            response.Content,
            maximumBytes,
            _bodyReadInactivityTimeout,
            cancellationToken);
    }

    private async Task<long> ReadOfficialAssetSizeAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var size = await ProbeOfficialAssetSizeAsync(uri, HttpMethod.Head, cancellationToken);
        if (size is null or <= 0)
            size = await ProbeOfficialAssetSizeAsync(uri, HttpMethod.Get, cancellationToken, useRange: true);
        if (size is null or <= 0 or > MaximumInstallerBytes)
            throw new UpdateSecurityException("GitHub did not provide a valid installer size.");
        return size.Value;
    }

    private async Task<long?> ProbeOfficialAssetSizeAsync(
        Uri uri,
        HttpMethod method,
        CancellationToken cancellationToken,
        bool useRange = false)
    {
        using var request = CreateRequest(method, uri, "application/octet-stream");
        if (useRange) request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.RequestMessage?.RequestUri is not { } finalUri || !IsAllowedAssetEndpoint(finalUri))
            throw new UpdateSecurityException("GitHub redirected installer metadata to an unapproved endpoint.");
        if (method == HttpMethod.Head && response.StatusCode == HttpStatusCode.MethodNotAllowed)
            return null;
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
    }

    private static AtomReleaseMetadata ParseAtomRelease(
        ReadOnlySpan<byte> utf8Xml,
        string tag,
        Uri releasePage)
    {
        using var source = new MemoryStream(utf8Xml.ToArray(), writable: false);
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            MaxCharactersInDocument = MaximumMetadataBytes
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        if (document.Root?.Name != atom + "feed")
            throw new InvalidDataException("GitHub returned an invalid release feed.");

        XElement? matchingEntry = null;
        foreach (var entry in document.Root.Elements(atom + "entry"))
        {
            var matches = entry.Elements(atom + "link").Any(link =>
                string.Equals((string?)link.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate((string?)link.Attribute("href"), UriKind.Absolute, out var linkUri) &&
                IsReleasePageForTag(linkUri, tag) &&
                linkUri == releasePage);
            if (!matches) continue;
            if (matchingEntry is not null)
                throw new InvalidDataException("GitHub returned duplicate entries for the latest release.");
            matchingEntry = entry;
        }
        if (matchingEntry is null)
            throw new InvalidDataException("GitHub's release feed does not contain the latest stable release.");

        var displayName = RequiredAtomValue(matchingEntry, atom + "title").Trim();
        if (displayName.Length == 0) displayName = "VeliShell " + tag.TrimStart('v', 'V');
        var content = matchingEntry.Element(atom + "content");
        if (content is null || !string.Equals(
                (string?)content.Attribute("type"), "html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub's release feed contains no supported changelog.");
        var changelog = HtmlToPlainText(content.Value);
        if (changelog.Length == 0)
            throw new InvalidDataException("The release has no changelog and cannot be offered in-app.");
        if (!DateTimeOffset.TryParse(
                RequiredAtomValue(matchingEntry, atom + "updated"),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var publishedAt))
            throw new InvalidDataException("GitHub's release feed contains an invalid publication date.");

        return new AtomReleaseMetadata(displayName, changelog, publishedAt);
    }

    private static string RequiredAtomValue(XElement element, XName name) =>
        element.Element(name)?.Value is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"GitHub's release feed is missing '{name.LocalName}'.");

    private static string HtmlToPlainText(string html)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromSeconds(1);
        var text = Regex.Replace(
            html,
            @"<(?:br\s*/?|/p|/div|/h[1-6]|/ul|/ol|/pre|/blockquote)\s*>",
            "\n",
            options,
            timeout);
        text = Regex.Replace(text, @"<li(?:\s[^>]*)?>", "• ", options, timeout);
        text = Regex.Replace(text, @"</li\s*>", "\n", options, timeout);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty, options, timeout);
        text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var result = new StringBuilder(text.Length);
        var previousWasBlank = true;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = Regex.Replace(rawLine, @"[\t ]+", " ", RegexOptions.CultureInvariant, timeout).Trim();
            if (line.Length == 0)
            {
                if (!previousWasBlank) result.AppendLine();
                previousWasBlank = true;
                continue;
            }
            result.AppendLine(line);
            previousWasBlank = false;
        }
        return result.ToString().Trim();
    }

    internal static string ParseSha256Checksums(ReadOnlySpan<byte> utf8Text, string expectedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFileName);
        if (Path.GetFileName(expectedFileName) != expectedFileName)
            throw new ArgumentException("The expected checksum filename must not contain a path.", nameof(expectedFileName));

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(utf8Text);
        string? match = null;
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } rawLine)
        {
            var line = rawLine.AsSpan().Trim();
            if (line.Length < 66 || !line[..64].ToArray().All(Uri.IsHexDigit) ||
                !char.IsWhiteSpace(line[64]))
                continue;
            var fileName = line[64..].TrimStart();
            if (!fileName.IsEmpty && fileName[0] == '*') fileName = fileName[1..];
            fileName = fileName.Trim();
            if (!fileName.Equals(expectedFileName.AsSpan(), StringComparison.Ordinal)) continue;
            if (match is not null)
                throw new InvalidDataException($"SHA256SUMS.txt contains duplicate entries for {expectedFileName}.");
            match = line[..64].ToString().ToLowerInvariant();
        }
        return match ?? throw new InvalidDataException(
            $"SHA256SUMS.txt does not contain a SHA-256 checksum for {expectedFileName}.");
    }

    /// <summary>
    /// Downloads only after confirmation, then verifies GitHub's SHA-256 digest and
    /// the configured Windows publisher policy before exposing the installer path.
    /// </summary>
    public async Task<VerifiedUpdatePackage> DownloadInstallerAsync(
        UpdateRelease release,
        string destinationDirectory,
        bool downloadAuthorizedByCurrentPreference,
        UpdateVerificationPolicy verificationPolicy,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(verificationPolicy);
        if (!downloadAuthorizedByCurrentPreference)
            throw new InvalidOperationException("The update download is not authorized by the current user preference.");

        ValidateAsset(release.TagName, release.Version, release.Installer);
        var directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, release.Installer.FileName);
        var partialPath = Path.Combine(directory, "." + release.Installer.FileName + ".partial-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var request = CreateRequest(HttpMethod.Get, release.Installer.DownloadUrl, "application/octet-stream");
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { } finalUri || !IsAllowedAssetEndpoint(finalUri))
                throw new UpdateSecurityException("GitHub redirected the installer to an unapproved endpoint.");
            if (response.Content.Headers.ContentLength is { } length && length != release.Installer.Size)
                throw new UpdateSecurityException("The installer size differs from the signed release metadata.");

            var actualHash = await DownloadAndHashAsync(
                response.Content,
                partialPath,
                release.Installer.Size,
                _bodyReadInactivityTimeout,
                progress,
                cancellationToken);
            if (!HashesMatch(actualHash, release.Installer.Sha256))
                throw new UpdateSecurityException("The downloaded installer failed SHA-256 verification.");

            var verification = AuthenticodeVerifier.Verify(partialPath, actualHash, verificationPolicy);
            if (verification.Authenticode is AuthenticodeStatus.InvalidOrUntrusted or
                AuthenticodeStatus.PublisherNotAllowed or
                AuthenticodeStatus.NotChecked)
                throw new UpdateSecurityException(
                    "The installer has an invalid, untrusted, or unapproved Windows signature.");
            if (verificationPolicy.RequireTrustedAuthenticodeSignature &&
                verification.Authenticode != AuthenticodeStatus.Valid)
                throw new UpdateSecurityException(
                    "The installer does not have a valid Windows signature from an allowed publisher.");

            File.Move(partialPath, finalPath, overwrite: true);
            return new VerifiedUpdatePackage(finalPath, release, verification);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    /// <summary>Starts Windows Installer visibly. There is intentionally no silent-install mode.</summary>
    public static void StartVerifiedInstaller(
        VerifiedUpdatePackage package,
        bool userConfirmedInstall,
        bool userAcceptedUnsignedPublisherWarning)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!userConfirmedInstall)
            throw new InvalidOperationException("Starting the installer requires a separate user confirmation.");
        if (package.Verification.Authenticode is AuthenticodeStatus.InvalidOrUntrusted or
            AuthenticodeStatus.PublisherNotAllowed or
            AuthenticodeStatus.NotChecked)
            throw new UpdateSecurityException(
                "An installer with an invalid, untrusted, or unapproved Windows signature cannot be started.");
        if (package.Verification.Authenticode != AuthenticodeStatus.Valid && !userAcceptedUnsignedPublisherWarning)
            throw new UpdateSecurityException(
                "The unsigned-publisher warning must be accepted before this verified installer can be started.");
        if (!File.Exists(package.FilePath))
            throw new FileNotFoundException("The verified installer no longer exists.", package.FilePath);

        Process.Start(new ProcessStartInfo
        {
            FileName = package.FilePath,
            UseShellExecute = true
        });
    }

    internal static UpdateRelease ParseRelease(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            throw new InvalidDataException("GitHub did not return a published stable release.");

        var tag = RequiredString(root, "tag_name");
        if (!SemanticVersion.TryParse(tag, out var version) || version.IsPrerelease)
            throw new InvalidDataException("The latest GitHub release tag is not a stable SemVer version.");
        var changelog = RequiredString(root, "body").Trim();
        if (changelog.Length == 0)
            throw new InvalidDataException("The release has no changelog and cannot be offered in-app.");

        var displayName = OptionalString(root, "name")?.Trim();
        if (string.IsNullOrEmpty(displayName)) displayName = "VeliShell " + version;
        var publishedAt = DateTimeOffset.Parse(
            RequiredString(root, "published_at"),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        var releasePage = RequireUri(RequiredString(root, "html_url"));
        if (!IsReleasePageForTag(releasePage, tag))
            throw new UpdateSecurityException("The release page is outside the official VeliShell repository.");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The release does not contain an asset list.");
        var expectedName = InstallerName(version);
        JsonElement? asset = null;
        foreach (var candidate in assets.EnumerateArray())
        {
            if (string.Equals(OptionalString(candidate, "name"), expectedName, StringComparison.Ordinal))
            {
                if (asset is not null)
                    throw new InvalidDataException("The release contains duplicate installer assets.");
                asset = candidate;
            }
        }
        if (asset is null)
            throw new InvalidDataException($"The release is missing {expectedName}.");

        var item = asset.Value;
        var size = item.GetProperty("size").GetInt64();
        var downloadUrl = RequireUri(RequiredString(item, "browser_download_url"));
        var digest = RequiredString(item, "digest");
        var installer = new UpdateAsset(expectedName, size, downloadUrl, ParseSha256Digest(digest));
        ValidateAsset(tag, version, installer);

        return new UpdateRelease(version, tag, displayName, changelog, publishedAt, releasePage, installer);
    }

    private static async Task<string> DownloadAndHashAsync(
        HttpContent content,
        string destination,
        long expectedSize,
        TimeSpan inactivityTimeout,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (expectedSize is <= 0 or > MaximumInstallerBytes)
            throw new UpdateSecurityException("The installer size is outside the accepted range.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await ReadWithInactivityTimeoutAsync(
                source,
                buffer.AsMemory(),
                inactivityTimeout,
                cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > expectedSize || total > MaximumInstallerBytes)
                throw new UpdateSecurityException("The installer download exceeded its declared size.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report((double)total / expectedSize);
        }
        await target.FlushAsync(cancellationToken);
        if (total != expectedSize)
            throw new UpdateSecurityException("The installer download is incomplete.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maximumBytes,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declaredLength && declaredLength > maximumBytes)
            throw new InvalidDataException("The GitHub release metadata is unexpectedly large.");
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
            if (destination.Length + read > maximumBytes)
                throw new InvalidDataException("The GitHub release metadata is unexpectedly large.");
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
                "The update transfer was canceled by the caller.",
                error,
                cancellationToken);
        }
        catch (OperationCanceledException error) when (readCancellation.IsCancellationRequested)
        {
            throw new UpdateTransferTimeoutException(inactivityTimeout, error);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string accept)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd(
            $"VeliShell-Updater/{InstalledVersion} (+https://github.com/ZUMBYTE-AppSolution/VeliShell)");
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        return request;
    }

    private static void ValidateAsset(string tag, SemanticVersion version, UpdateAsset asset)
    {
        var expectedName = InstallerName(version);
        if (!string.Equals(asset.FileName, expectedName, StringComparison.Ordinal) ||
            Path.GetFileName(asset.FileName) != asset.FileName)
            throw new UpdateSecurityException("The release installer name is invalid.");
        if (asset.Size is <= 0 or > MaximumInstallerBytes)
            throw new UpdateSecurityException("The release installer size is outside the accepted range.");
        if (!IsOfficialAssetUrl(asset.DownloadUrl, tag, expectedName))
            throw new UpdateSecurityException("The installer is not hosted on the official VeliShell GitHub release.");
        if (asset.Sha256.Length != 64 || asset.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new UpdateSecurityException("The release has no valid SHA-256 digest.");
    }

    private static string InstallerName(SemanticVersion version) => $"VeliShell-{version}-win-x64.msi";

    private static bool ShouldUsePublicMetadataFallback(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool TryGetStableReleaseTag(
        Uri uri,
        out string tag,
        out SemanticVersion version)
    {
        const string marker = "/releases/tag/";
        tag = string.Empty;
        version = default;
        var expectedPrefix = $"/{Owner}/{Repository}{marker}";
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        tag = Uri.UnescapeDataString(uri.AbsolutePath[expectedPrefix.Length..]);
        return tag.Length > 0 &&
               !tag.Contains('/') &&
               SemanticVersion.TryParse(tag, out version) &&
               !version.IsPrerelease &&
               IsReleasePageForTag(uri, tag);
    }

    private static bool IsReleasePageForTag(Uri uri, string tag) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals(
            $"/{Owner}/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}",
            StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsExactGitHubEndpoint(Uri uri, string expectedPath) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static Uri OfficialReleaseAssetUri(string tag, string fileName) => new(
        $"https://github.com/{Owner}/{Repository}/releases/download/" +
        $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}");

    private static bool IsOfficialAssetUrl(Uri uri, string tag, string fileName) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals(
            $"/{Owner}/{Repository}/releases/download/{tag}/{fileName}",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedAssetEndpoint(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool HashesMatch(string leftHex, string rightHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(leftHex),
                Convert.FromHexString(rightHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ParseSha256Digest(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UpdateSecurityException("GitHub did not provide a SHA-256 asset digest.");
        var hash = digest[prefix.Length..].ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new UpdateSecurityException("GitHub returned an invalid SHA-256 asset digest.");
        return hash;
    }

    private static Uri RequireUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidDataException("GitHub returned an invalid release URI.");
        return uri;
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
            throw new InvalidDataException($"GitHub release metadata is missing '{property}'.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* A later cleanup pass may remove a locked partial download. */ }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private sealed record AtomReleaseMetadata(
        string DisplayName,
        string Changelog,
        DateTimeOffset PublishedAt);
}

public sealed class UpdateTransferTimeoutException(TimeSpan inactivityTimeout, Exception innerException)
    : TimeoutException(
        $"The update transfer received no data for {inactivityTimeout.TotalSeconds:0.###} seconds.",
        innerException)
{
    public TimeSpan InactivityTimeout { get; } = inactivityTimeout;
}
