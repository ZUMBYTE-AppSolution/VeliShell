using VeliShell.Core;

namespace VeliShell.UpdateService;

public enum UpdateStatusState
{
    NoPublishedRelease,
    UpToDate,
    UpdateAvailable,
    CheckFailed
}

public sealed record UpdateStatusSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required UpdateStatusState State { get; init; }
    public required DateTimeOffset CheckedAtUtc { get; init; }
    public required string InstalledVersion { get; init; }
    public string? LatestVersion { get; init; }
    public Uri? ReleasePage { get; init; }
    public DateTimeOffset? PublishedAtUtc { get; init; }
    public string? ErrorCode { get; init; }

    public static UpdateStatusSnapshot FromResult(
        ReleaseCheckResult result,
        DateTimeOffset checkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new UpdateStatusSnapshot
        {
            State = result.State switch
            {
                ReleaseCheckState.NoPublishedRelease => UpdateStatusState.NoPublishedRelease,
                ReleaseCheckState.UpToDate => UpdateStatusState.UpToDate,
                ReleaseCheckState.UpdateAvailable => UpdateStatusState.UpdateAvailable,
                _ => throw new ArgumentOutOfRangeException(nameof(result))
            },
            CheckedAtUtc = checkedAtUtc.ToUniversalTime(),
            InstalledVersion = result.InstalledVersion.ToString(),
            LatestVersion = result.Release?.Version.ToString(),
            ReleasePage = result.Release?.ReleasePage,
            PublishedAtUtc = result.Release?.PublishedAtUtc.ToUniversalTime()
        };
    }

    public static UpdateStatusSnapshot Failed(
        SemanticVersion installedVersion,
        DateTimeOffset checkedAtUtc,
        string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new UpdateStatusSnapshot
        {
            State = UpdateStatusState.CheckFailed,
            CheckedAtUtc = checkedAtUtc.ToUniversalTime(),
            InstalledVersion = installedVersion.ToString(),
            ErrorCode = errorCode
        };
    }
}
