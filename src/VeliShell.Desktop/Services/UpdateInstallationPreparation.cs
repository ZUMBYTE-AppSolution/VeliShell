namespace VeliShell.Desktop.Services;

internal enum UpdatePreparationResult { Cancelled, RestorationPending, LegacyAppsOpen, Started }

internal static class UpdateInstallationPreparation
{
    internal static async Task<UpdatePreparationResult> RunAsync(bool confirmed, Action disableStyling,
        Func<CancellationToken, Task<bool>> restore, Func<bool> hasInstallerBlockingApps, Action startInstaller,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed) return UpdatePreparationResult.Cancelled;
        cancellationToken.ThrowIfCancellationRequested();
        disableStyling();
        if (!await restore(cancellationToken)) return UpdatePreparationResult.RestorationPending;
        cancellationToken.ThrowIfCancellationRequested();
        if (await Task.Run(hasInstallerBlockingApps, cancellationToken)) return UpdatePreparationResult.LegacyAppsOpen;
        cancellationToken.ThrowIfCancellationRequested();
        startInstaller();
        return UpdatePreparationResult.Started;
    }
}
