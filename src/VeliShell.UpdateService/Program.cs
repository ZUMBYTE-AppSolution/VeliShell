namespace VeliShell.UpdateService;

internal static class Program
{
    private static int Main()
    {
        using var releaseSource = new GitHubReleaseMetadataClient();
        var statusStore = new AtomicUpdateStatusStore(UpdateServiceDefaults.StatusFilePath);
        var monitor = new UpdateMonitor(
            releaseSource,
            statusStore,
            UpdateServiceDefaults.InstalledVersion,
            UpdateServiceDefaults.CheckInterval);
        var host = new WindowsServiceHost(UpdateServiceDefaults.ServiceName, monitor.RunAsync);
        return host.Run();
    }
}
