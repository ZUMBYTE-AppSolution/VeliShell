using System.IO;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

public enum UpdateCheckState
{
    NoPublishedRelease,
    UpToDate,
    UpdateAvailable
}

public enum AuthenticodeStatus
{
    NotChecked,
    Valid,
    NotSigned,
    InvalidOrUntrusted,
    PublisherNotAllowed
}

public sealed record UpdateAsset(
    string FileName,
    long Size,
    Uri DownloadUrl,
    string Sha256);

public sealed record UpdateRelease(
    SemanticVersion Version,
    string TagName,
    string DisplayName,
    string Changelog,
    DateTimeOffset PublishedAt,
    Uri ReleasePage,
    UpdateAsset Installer);

public sealed record UpdateCheckResult(
    UpdateCheckState State,
    SemanticVersion CurrentVersion,
    UpdateRelease? Release)
{
    public bool IsUpdateAvailable => State == UpdateCheckState.UpdateAvailable && Release is not null;
}

public sealed record UpdateVerificationPolicy(
    bool RequireTrustedAuthenticodeSignature,
    IReadOnlySet<string> AllowedPublisherThumbprints)
{
    /// <summary>
    /// Current public-release policy: SHA-256 is mandatory; a missing publisher
    /// signature is reported to the UI and requires a separate warning acknowledgement.
    /// </summary>
    public static UpdateVerificationPolicy PublicRelease { get; } = new(
        RequireTrustedAuthenticodeSignature: false,
        AllowedPublisherThumbprints: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public static UpdateVerificationPolicy SignedRelease { get; } = new(
        RequireTrustedAuthenticodeSignature: true,
        AllowedPublisherThumbprints: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Only for local development packages. It currently has the same verifier settings
    /// as unsigned public previews, but keeps the intent explicit at call sites.
    /// </summary>
    public static UpdateVerificationPolicy UnsignedDevelopmentBuild { get; } = new(
        RequireTrustedAuthenticodeSignature: false,
        AllowedPublisherThumbprints: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public sealed record UpdatePackageVerification(
    string Sha256,
    AuthenticodeStatus Authenticode,
    string? PublisherSubject,
    string? PublisherThumbprint);

public sealed record VerifiedUpdatePackage(
    string FilePath,
    UpdateRelease Release,
    UpdatePackageVerification Verification);

public sealed class UpdateSecurityException(string message) : IOException(message);
