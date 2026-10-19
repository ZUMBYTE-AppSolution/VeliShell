using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class UpdateWindow : VeliShellWindow
{
    private readonly App _app;
    private readonly GitHubReleaseUpdateService _updates;
    private readonly UpdateRelease _release;
    private CancellationTokenSource? _downloadCancellation;
    private VerifiedUpdatePackage? _package;
    private bool _downloading;
    private bool _verifying;
    private bool _installing;
    private CancellationTokenSource? _installCancellation;
    private int _downloadPercent;
    private double _downloadMiB;
    private double _downloadSpeed;

    private static string L(string key) => LocalizationService.Current.Get(key);
    private static string LF(string key, params object[] args) =>
        string.Format(LocalizationService.Current.ActiveCulture, L(key), args);

    internal UpdateWindow(
        App app,
        GitHubReleaseUpdateService updates,
        UpdateRelease release,
        bool automaticDownload)
    {
        _app = app;
        _updates = updates;
        _release = release;
        InitializeComponent();
        ProductIcon.Source = IconService.For("velishell");
        ReleaseNotes.Text = (release.Version.IsPrerelease != BuildIdentity.IsTest
            ? L(release.Version.IsPrerelease ? "Update.TestConsent" : "Update.StableConsent") + "\n\n" : "") + release.Changelog;
        LocalizationService.Current.Changed += ApplyLocalizedText;
        ApplyLocalizedText();
        Closed += (_, _) =>
        {
            LocalizationService.Current.Changed -= ApplyLocalizedText;
            _downloadCancellation?.Cancel();
            _installCancellation?.Cancel();
        };
        if (automaticDownload)
        {
            Loaded += async (_, _) =>
            {
                // Let WPF paint the changelog before the opted-in automatic
                // download starts. Installation always remains a separate click.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (IsVisible) await DownloadAsync();
            };
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (_downloading)
        {
            _downloadCancellation?.Cancel();
            return;
        }

        if (_package is not null)
        {
            await StartInstallerAsync();
            return;
        }

        await DownloadAsync();
    }

    private async Task DownloadAsync()
    {
        if (_downloading || _package is not null) return;
        _downloading = true;
        _downloadCancellation = new CancellationTokenSource();
        ProgressPanel.Visibility = Visibility.Visible;
        VerificationPanel.Visibility = Visibility.Collapsed;
        DownloadProgress.Value = 0;
        _downloadPercent = 0;
        _verifying = false;
        ProgressText.Text = LF("Update.Downloading", _downloadPercent);
        PrimaryButton.Content = L("Update.CancelDownload");
        LaterButton.IsEnabled = false;

        try
        {
            var watch = Stopwatch.StartNew();
            var progress = new Progress<double>(value =>
            {
                var percent = Math.Clamp((int)Math.Round(value * 100), 0, 100);
                _downloadPercent = percent;
                _downloadMiB = Math.Clamp(value, 0, 1) * _release.Installer.Size / 1048576d;
                _downloadSpeed = watch.Elapsed.TotalSeconds < 0.5 ? 0 :
                    _downloadMiB / watch.Elapsed.TotalSeconds;
                DownloadProgress.Value = percent;
                ProgressText.Text = LF("Update.DownloadDetail", percent, _downloadMiB,
                    _release.Installer.Size / 1048576d, _downloadSpeed);
            });
            var directory = Path.Combine(App.DataDirectory, "updates", "v" + _release.Version);
            _package = await _updates.DownloadInstallerAsync(
                _release,
                directory,
                downloadAuthorizedByCurrentPreference: true,
                UpdateVerificationPolicy.PublicRelease,
                progress,
                _downloadCancellation.Token);

            _verifying = true;
            ProgressText.Text = L("Update.Verifying");
            ShowVerification(_package.Verification);
        }
        catch (OperationCanceledException) when (_downloadCancellation?.IsCancellationRequested == true)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException exception)
        {
            App.Log("Secure update download timed out", exception);
            MessageBox.Show(
                this,
                LF("Update.DownloadFailed", L("Update.SafeFailureDetail")),
                L("Update.ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            App.Log("Secure update download failed", exception);
            MessageBox.Show(
                this,
                LF("Update.DownloadFailed", L("Update.SafeFailureDetail")),
                L("Update.ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _downloading = false;
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            LaterButton.IsEnabled = true;
            if (_package is null) PrimaryButton.Content = L("Update.Download");
        }
    }

    private void ShowVerification(UpdatePackageVerification verification)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        VerificationPanel.Visibility = Visibility.Visible;
        PrimaryButton.Content = L("Update.Install");

        if (verification.Authenticode == AuthenticodeStatus.Valid)
        {
            VerificationText.Text = L("Update.HashVerified") + "\n" +
                                    LF("Update.SignedPublisher", verification.PublisherSubject ?? L("Update.PublisherUnknown"));
            UnsignedPanel.Visibility = Visibility.Collapsed;
            PrimaryButton.IsEnabled = true;
            return;
        }

        VerificationText.Text = L("Update.HashVerified");
        UnsignedPanel.Visibility = Visibility.Visible;
        UnsignedAcknowledgement.IsChecked = false;
        PrimaryButton.IsEnabled = false;
    }

    private void ApplyLocalizedText()
    {
        VersionLine.Text = LF(
            "Update.VersionLine",
            _release.Version,
            _release.PublishedAt.ToLocalTime().ToString("d", LocalizationService.Current.ActiveCulture));

        if (_installing)
        {
            PrimaryButton.Content = L("Update.PreparingInstall");
            ProgressText.Text = L("Update.PreparingFrames");
            return;
        }

        if (_package is not null)
        {
            PrimaryButton.Content = L("Update.Install");
            VerificationText.Text = _package.Verification.Authenticode == AuthenticodeStatus.Valid
                ? L("Update.HashVerified") + "\n" +
                  LF("Update.SignedPublisher", _package.Verification.PublisherSubject ?? L("Update.PublisherUnknown"))
                : L("Update.HashVerified");
        }
        else if (_downloading)
        {
            PrimaryButton.Content = L("Update.CancelDownload");
            ProgressText.Text = _verifying
                ? L("Update.Verifying")
                : LF("Update.DownloadDetail", _downloadPercent, _downloadMiB,
                    _release.Installer.Size / 1048576d, _downloadSpeed);
        }
        else
        {
            PrimaryButton.Content = L("Update.Download");
        }
    }

    private void SetInstallingUi(bool installing)
    {
        _installing = installing;
        PrimaryButton.IsEnabled = !installing && (_package?.Verification.Authenticode == AuthenticodeStatus.Valid || UnsignedAcknowledgement.IsChecked == true);
        LaterButton.IsEnabled = !installing;
        UnsignedAcknowledgement.IsEnabled = !installing;
        ProgressPanel.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = installing;
        ApplyLocalizedText();
    }

    private async Task StartInstallerAsync()
    {
        if (_package is null || _installing) return;
        var acceptedUnsigned = _package.Verification.Authenticode == AuthenticodeStatus.Valid ||
                               UnsignedAcknowledgement.IsChecked == true;
        if (MessageBox.Show(this, LF("Update.InstallConfirm", _release.Version),
                L("Update.ReadyTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) !=
            MessageBoxResult.Yes) return;
        _installCancellation = new CancellationTokenSource();
        SetInstallingUi(true);
        try
        {
            using var updateScope = _app.NativeFrames?.SuspendForUpdate();
            var result = await UpdateInstallationPreparation.RunAsync(true,
                _app.DisableNativeFramesForUpdate,
                token => _app.NativeFrames?.PrepareForUpdateAsync(token) ?? Task.Run(() => !NativeFramesController.HasPendingRestoration(), token),
                NativeFramesController.HasLoadedApps,
                () => GitHubReleaseUpdateService.StartVerifiedInstallerWithRestart(_package, true, acceptedUnsigned),
                _installCancellation.Token);
            if (result == UpdatePreparationResult.Started)
            { Close(); _app.RequestExit(); }
            else if (IsVisible)
                MessageBox.Show(this, L(result == UpdatePreparationResult.LegacyAppsOpen ? "NativeFrames.UpdateBlocked" : "Update.RestorationPending"),
                    L("Update.ReadyTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (_installCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            App.Log("Could not start verified update installer", exception);
            if (IsVisible) MessageBox.Show(this, LF("Update.InstallFailed", L("Update.SafeFailureDetail")),
                L("Update.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _installCancellation.Dispose();
            _installCancellation = null;
            SetInstallingUi(false);
        }
    }

    private void UnsignedAcknowledgement_Changed(object sender, RoutedEventArgs e)
    {
        if (!_installing && _package is not null && _package.Verification.Authenticode != AuthenticodeStatus.Valid)
            PrimaryButton.IsEnabled = UnsignedAcknowledgement.IsChecked == true;
    }

    private void ReleasePage_Click(object sender, RoutedEventArgs e) => LaunchService.Open(_release.ReleasePage.ToString());

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        _downloadCancellation?.Cancel();
        Close();
    }
}
