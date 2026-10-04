using System.Reflection;
using VeliShell.Core;

namespace VeliShell.UpdateService;

public static class UpdateServiceDefaults
{
    public const string ServiceName = "VeliShell.UpdateService";
    public const string StatusDirectoryName = "VeliShell";
    public const string StatusFileName = "update-status.json";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    public static Uri LatestReleaseApi { get; } = new(
        "https://api.github.com/repos/ZUMBYTE-AppSolution/VeliShell/releases/latest",
        UriKind.Absolute);

    public static string StatusFilePath
    {
        get
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
                throw new InvalidOperationException("Windows did not provide the common application-data directory.");

            return Path.Combine(programData, StatusDirectoryName, StatusFileName);
        }
    }

    public static SemanticVersion InstalledVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ??
                          typeof(UpdateServiceDefaults).Assembly.GetName().Version ??
                          new Version(0, 0, 0);
            return new SemanticVersion(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0));
        }
    }
}
