using System.IO;
using System.Windows;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Views;

namespace VeliShell.Desktop;

public partial class App : Application
{
    private static string L(string key) => LocalizationService.Current.Get(key);
    private static string LF(string key, params object[] args) =>
        string.Format(LocalizationService.Current.ActiveCulture, L(key), args);
    private Mutex? _mutex;
    private bool _ownsMutex;
    private SingleInstancePinBridge? _pinBridge;
    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _updateTimer;
    private PreferencesWindow? _preferencesWindow;
    private MenuBarWindow? _menuBarWindow;
    private bool _saveErrorShown;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _updateCheckGate = new(1, 1);
    private readonly SemaphoreSlim _shellLayoutGate = new(1, 1);
    private int _emergencyRestoreRequests;
    private readonly GitHubReleaseUpdateService _updates = new();
    private UpdateWindow? _updateWindow;
    private SemanticVersion? _lastOfferedVersion;
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeliShell");
    private static string LegacyDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HarborDesktop");
    public SettingsStore Store { get; private set; } = null!;
    public Settings Preferences { get; private set; } = null!;
    public ThemeService Themes { get; private set; } = null!;
    public DockWindow Dock { get; private set; } = null!;
    internal TaskbarVisibilityService Taskbars { get; } = new();
    internal DesktopIconVisibilityService DesktopIcons { get; } = new();
    public string HotkeyStatus { get; set; } = LocalizationService.Current.Get("App.HotkeyPending");
    internal UpdateUiState UpdateState { get; private set; } = UpdateUiState.NotChecked;
    internal SemanticVersion? AvailableUpdateVersion { get; private set; }
    public event Action? PreferencesChanged;
    public event Action? UpdateStateChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var pinInvocationRequested = e.Args.Any(argument =>
            string.Equals(argument, ShellPinCommand.Option, StringComparison.OrdinalIgnoreCase));
        var hasPinCommand = ShellPinCommand.TryParse(e.Args, out var pendingPinPath);
        if (pinInvocationRequested && !hasPinCommand)
        {
            Log("Rejected an invalid Explorer pin command.");
            Shutdown(2);
            return;
        }

        _mutex = new Mutex(true, @"Local\Zumbyte.VeliShell.0.3", out _ownsMutex);
        if (!_ownsMutex)
        {
            if (!hasPinCommand || !SingleInstancePinBridge.ForwardAsync(
                    pendingPinPath, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult())
                MessageBox.Show(L("App.AlreadyRunning"), L("Common.ErrorTitle"));
            Shutdown(); return;
        }
        _pinBridge = new SingleInstancePinBridge(QueuePinFromShell);
        _pinBridge.Start();
        // A second rejected process must never touch the active owner's shell
        // state. Recover non-persistent work areas only after mutex ownership.
        Taskbars.RecoverWorkAreasAfterOwnershipConfirmed();
        if (!DesktopIcons.RecoverAfterOwnershipConfirmed())
            Log(DesktopIcons.LastStatus);
        DispatcherUnhandledException += (_, args) =>
        {
            Log("Unhandled UI exception", args.Exception);
            MessageBox.Show(LF("App.FatalError", DataDirectory, args.Exception.Message),
                L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            RequestExit(1, forceAfterRestoreFailure: true);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Taskbars.Restore();
            DesktopIcons.Restore();
            Log("Unhandled exception", args.ExceptionObject as Exception);
        };
        MigrateLegacySettings();
        Store = new SettingsStore(DataDirectory);
        Preferences = Store.Load();
        LocalizationService.Current.Apply(Preferences.Language);
        if (Store.LoadWarning is not null) Log(Store.LoadWarning);
        Themes = new ThemeService();
        Themes.Apply(Preferences.Appearance);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        Dock = new DockWindow(this);
        MainWindow = Dock;
        Dock.Show();
        if (Preferences.WindowsNotificationsEnabled &&
            NotificationCenterService.Current.WindowsAccess.State ==
            WindowsNotificationAccessState.Unsupported)
        {
            // A setting carried over from a packaged build must not appear as
            // active in an unpackaged MSI/portable build that cannot declare
            // the Windows capability.
            Preferences.WindowsNotificationsEnabled = false;
            SaveNow();
        }
        // A saved opt-in may resume an already granted listener, but this path
        // never opens the Windows privacy prompt. Only the explicit Settings
        // toggle calls RequestAccessAsync.
        _ = NotificationCenterService.Current.ConfigureWindowsNotificationsAsync(
            Preferences.WindowsNotificationsEnabled);
        if (Preferences.HideDesktopIcons)
            SetDesktopIconsHidden(true, showError: false);
        if (hasPinCommand) AddShellPin(pendingPinPath);
        try
        {
            if (!PackageIdentityService.HasIdentity && Environment.ProcessPath is { } executablePath)
                ShellVerbRegistrationService.EnsureRegisteredForCurrentUser(executablePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            Log("Could not register the Explorer pin command for the current user", exception);
        }
        if (!SyncMenuBar())
        {
            Preferences.MenuBarEnabled = false;
            SyncMenuBar();
            SaveNow();
            Log("The saved menu-bar preference was disabled because Windows did not accept its work-area reservation.");
        }
        if (PackageIdentityService.HasIdentity)
        {
            SetUpdateState(UpdateUiState.StoreManaged);
        }
        else
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _updateTimer.Tick += async (_, _) =>
            {
                if (Preferences.Updates != UpdateMode.Manual && _updateWindow is null)
                    await CheckForUpdatesAsync(userInitiated: false, Dock);
            };
            _updateTimer.Start();
        }
        if (!Preferences.FirstRunCompleted)
        {
            ShowPreferences();
            Preferences.FirstRunCompleted = true;
            SaveNow();
        }
        if (!PackageIdentityService.HasIdentity && Preferences.Updates != UpdateMode.Manual)
            _ = CheckForUpdatesAsync(userInitiated: false, Dock);
        Log($"VeliShell {GitHubReleaseUpdateService.InstalledVersion} started. Windows {Environment.OSVersion.Version}");
    }

    private bool QueuePinFromShell(string path)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return false;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_exiting && Dock is not null) AddShellPin(path);
        });
        return true;
    }

    private void AddShellPin(string path)
    {
        var alreadyPinned = Preferences.Pins.Any(pin =>
            ShellPinCommand.RefersToSameExistingPath(pin.Target, path));
        if (!alreadyPinned) Dock.AddPaths([path]);
    }

    private static void MigrateLegacySettings()
    {
        try
        {
            var destination = Path.Combine(DataDirectory, "settings.json");
            if (File.Exists(destination) || !Directory.Exists(LegacyDataDirectory)) return;

            Directory.CreateDirectory(DataDirectory);
            foreach (var fileName in new[] { "settings.json", "settings.json.bak" })
            {
                var source = Path.Combine(LegacyDataDirectory, fileName);
                var target = Path.Combine(DataDirectory, fileName);
                if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
            }
            Log("Imported settings from the legacy Harbor Desktop data directory.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log("Could not import legacy settings", exception);
        }
    }

    public void UpdatePreferences(Action<Settings> update)
    {
        update(Preferences);
        Preferences.Normalize();
        if (LocalizationService.Current.Preference != Preferences.Language)
            LocalizationService.Current.Apply(Preferences.Language);
        Themes.Apply(Preferences.Appearance);
        PreferencesChanged?.Invoke();
        SyncMenuBar();
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    public void ShowPreferences()
    {
        if (_preferencesWindow is null)
        {
            _preferencesWindow = new PreferencesWindow(this);
            _preferencesWindow.Closed += (_, _) => _preferencesWindow = null;
        }
        _preferencesWindow.Show();
        if (_preferencesWindow.WindowState == WindowState.Minimized) _preferencesWindow.WindowState = WindowState.Normal;
        _preferencesWindow.Activate();
    }

    private bool SyncMenuBar()
    {
        if (Preferences.MenuBarEnabled)
        {
            if (_menuBarWindow is null)
            {
                _menuBarWindow = new MenuBarWindow(this);
                _menuBarWindow.Closed += (_, _) => _menuBarWindow = null;
            }
            if (!_menuBarWindow.IsVisible) _menuBarWindow.Show();
            return _menuBarWindow.WorkAreaReserved;
        }
        else if (_menuBarWindow is not null)
        {
            var window = _menuBarWindow;
            _menuBarWindow = null;
            window.Close();
        }
        return true;
    }

    internal string GetUpdateStatusText() => UpdateState switch
    {
        UpdateUiState.Checking => L("Update.Checking"),
        UpdateUiState.UpToDate => L("Update.UpToDate"),
        UpdateUiState.NoRelease => L("Update.NoRelease"),
        UpdateUiState.Available when AvailableUpdateVersion is { } version =>
            LF("Update.AvailableStatus", version),
        UpdateUiState.StoreManaged => L("Update.StoreManaged"),
        UpdateUiState.Failed => L("Update.CheckFailedStatus"),
        _ => L("Update.NotChecked")
    };

    internal async Task CheckForUpdatesAsync(bool userInitiated, Window? owner = null)
    {
        if (PackageIdentityService.HasIdentity)
        {
            SetUpdateState(UpdateUiState.StoreManaged);
            if (userInitiated)
                MessageBox.Show(owner ?? Dock, L("Update.StoreManaged"), L("Update.WindowTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!await _updateCheckGate.WaitAsync(0)) return;
        SetUpdateState(UpdateUiState.Checking);
        try
        {
            var result = await _updates.CheckForUpdateAsync(
                GitHubReleaseUpdateService.InstalledVersion,
                _shutdown.Token);
            switch (result.State)
            {
                case UpdateCheckState.NoPublishedRelease:
                    SetUpdateState(UpdateUiState.NoRelease);
                    if (userInitiated)
                        MessageBox.Show(owner ?? Dock, L("Update.NoRelease"), L("Update.WindowTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case UpdateCheckState.UpToDate:
                    SetUpdateState(UpdateUiState.UpToDate);
                    if (userInitiated)
                        MessageBox.Show(owner ?? Dock, L("Update.UpToDate"), L("Update.WindowTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case UpdateCheckState.UpdateAvailable when result.Release is { } release:
                    AvailableUpdateVersion = release.Version;
                    SetUpdateState(UpdateUiState.Available, keepAvailableVersion: true);
                    if (userInitiated || _lastOfferedVersion is null ||
                        _lastOfferedVersion.Value.CompareTo(release.Version) != 0)
                    {
                        _lastOfferedVersion = release.Version;
                        ShowUpdateWindow(
                            release,
                            owner,
                            automaticDownload: !userInitiated && Preferences.Updates == UpdateMode.AutomaticDownload);
                    }
                    break;
                default:
                    throw new InvalidDataException("The update service returned an incomplete result.");
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (OperationCanceledException exception)
        {
            Log("Update metadata check timed out", exception);
            SetUpdateState(UpdateUiState.Failed);
            if (userInitiated)
                MessageBox.Show(owner ?? Dock,
                    LF("Update.CheckFailed", L("Update.SafeFailureDetail")),
                    L("Update.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            // Release metadata is remote, untrusted input. Any parse, protocol or
            // validation failure must stay inside the updater and never take down
            // the dock's UI thread or its taskbar-recovery path.
            Log("Update metadata check failed", exception);
            SetUpdateState(UpdateUiState.Failed);
            if (userInitiated)
                MessageBox.Show(owner ?? Dock,
                    LF("Update.CheckFailed", L("Update.SafeFailureDetail")),
                    L("Update.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateCheckGate.Release();
        }
    }

    private void ShowUpdateWindow(UpdateRelease release, Window? owner, bool automaticDownload)
    {
        if (_updateWindow is not null)
        {
            if (_updateWindow.WindowState == WindowState.Minimized)
                _updateWindow.WindowState = WindowState.Normal;
            _updateWindow.Activate();
            return;
        }

        _updateWindow = new UpdateWindow(this, _updates, release, automaticDownload);
        var actualOwner = owner is { IsVisible: true } ? owner : Dock;
        if (actualOwner is { IsVisible: true }) _updateWindow.Owner = actualOwner;
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    private void SetUpdateState(UpdateUiState state, bool keepAvailableVersion = false)
    {
        UpdateState = state;
        if (!keepAvailableVersion) AvailableUpdateVersion = null;
        UpdateStateChanged?.Invoke();
    }

    public async Task<bool> SetTaskbarHiddenAsync(
        bool hidden,
        bool showError = true,
        bool recoverExistingHiddenTaskbars = false)
    {
        var gateEntered = false;
        try
        {
            await _shellLayoutGate.WaitAsync(_shutdown.Token);
            gateEntered = true;
            return await SetTaskbarHiddenCoreAsync(hidden, showError, recoverExistingHiddenTaskbars);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (gateEntered) _shellLayoutGate.Release();
        }
    }

    private async Task<bool> SetTaskbarHiddenCoreAsync(
        bool hidden,
        bool showError,
        bool recoverExistingHiddenTaskbars)
    {
        if (hidden && !Dock.TaskbarRecoveryHotkeyAvailable)
        {
            // A persisted hide request may mean the previous VeliShell process was
            // terminated while the shell window was hidden. Even when the
            // emergency hotkey is now unavailable, recover that state before
            // disabling the option; otherwise the one startup capable of
            // adopting the orphaned taskbar would discard its marker.
            var recovered = !recoverExistingHiddenTaskbars ||
                            await Taskbars.RestoreAsync(recoverCurrentShellTaskbars: true);
            var remainsHidden = ShouldPersistTaskbarHidePreference(
                hideRequested: false,
                operationConfirmed: recovered,
                taskbarStillHidden: Taskbars.IsHidden);
            UpdatePreferences(s => s.HideTaskbar = remainsHidden);
            if (!recovered) Log(Taskbars.LastStatus);
            if (showError)
                MessageBox.Show(
                    recovered
                        ? L("App.TaskbarHotkeyBlocked")
                        : LF("App.TaskbarRecoveryUnconfirmed", Taskbars.LastStatus),
                    L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var success = hidden
            ? await Taskbars.HideAsync(recoverExistingHiddenTaskbars)
            : await Taskbars.RestoreAsync(recoverCurrentShellTaskbars: true);
        // Keep the persisted recovery marker while Windows still reports a
        // taskbar hidden by this session. If VeliShell is terminated before a
        // failed hide attempt can be rolled back, the next start can then adopt
        // and restore that already-hidden shell window.
        var applied = ShouldPersistTaskbarHidePreference(hidden, success, Taskbars.IsHidden);
        if (Preferences.HideTaskbar != applied)
            UpdatePreferences(s => s.HideTaskbar = applied);
        else
            PreferencesChanged?.Invoke();

        if (!success)
        {
            Log(Taskbars.LastStatus);
            if (showError)
                MessageBox.Show(Taskbars.LastStatus, L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return success;
    }

    public bool SetDesktopIconsHidden(bool hidden, bool showError = true)
    {
        var success = hidden ? DesktopIcons.Hide() : DesktopIcons.Restore();
        var applied = ShouldPersistDesktopIconHidePreference(hidden, success, DesktopIcons.IsHidden);
        if (Preferences.HideDesktopIcons != applied)
            UpdatePreferences(settings => settings.HideDesktopIcons = applied);
        else
            PreferencesChanged?.Invoke();

        if (!success)
        {
            Log(DesktopIcons.LastStatus);
            if (showError)
                MessageBox.Show(DesktopIcons.LastStatus, L("Common.ErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return success;
    }

    internal async Task<bool> SetMenuBarEnabledAsync(bool enabled, bool showError = true)
    {
        var gateEntered = false;
        try
        {
            await _shellLayoutGate.WaitAsync(_shutdown.Token);
            gateEntered = true;
            if (Preferences.MenuBarEnabled == enabled &&
                (!enabled || _menuBarWindow?.WorkAreaReserved == true))
                return true;

            // Taskbar work-area snapshots must be restored while the old menu
            // appbar is still registered. After changing the appbar we capture
            // a fresh baseline and re-hide, preserving both reservations.
            var rehideTaskbar = Preferences.HideTaskbar || Taskbars.IsHidden;
            if (Taskbars.IsHidden && !await Taskbars.RestoreAsync())
            {
                if (showError)
                    MessageBox.Show(Taskbars.LastStatus, L("Common.ErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            UpdatePreferences(settings => settings.MenuBarEnabled = enabled);
            var reservationConfirmed = !enabled || _menuBarWindow?.WorkAreaReserved == true;
            if (!reservationConfirmed)
            {
                UpdatePreferences(settings => settings.MenuBarEnabled = false);
                if (showError)
                    MessageBox.Show(L("MenuBar.ReservationFailed"), L("Common.ErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var shouldRehideTaskbar = ShouldRehideTaskbarAfterMenuBarChange(
                rehideTaskbar,
                Volatile.Read(ref _emergencyRestoreRequests) > 0);
            if (rehideTaskbar && !shouldRehideTaskbar && Preferences.HideTaskbar)
                UpdatePreferences(settings => settings.HideTaskbar = false);

            var taskbarConfirmed = !shouldRehideTaskbar ||
                await SetTaskbarHiddenCoreAsync(
                    true,
                    showError,
                    recoverExistingHiddenTaskbars: false);
            if (ShouldUndoMenuBarRehide(
                    shouldRehideTaskbar,
                    Volatile.Read(ref _emergencyRestoreRequests) > 0))
            {
                // The hotkey can arrive while HideAsync is in its verification
                // delays. Restore before releasing the gate so the completed
                // menu transition cannot hand an immediately re-hidden shell to
                // the queued emergency operation.
                taskbarConfirmed = await Taskbars.RestoreAsync(recoverCurrentShellTaskbars: true);
                UpdatePreferences(settings =>
                    settings.HideTaskbar = !taskbarConfirmed && Taskbars.IsHidden);
            }
            return reservationConfirmed && taskbarConfirmed;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (gateEntered) _shellLayoutGate.Release();
        }
    }

    private static bool ShouldRehideTaskbarAfterMenuBarChange(
        bool taskbarWasRequestedHidden,
        bool emergencyRestorePending) =>
        taskbarWasRequestedHidden && !emergencyRestorePending;

    private static bool ShouldUndoMenuBarRehide(
        bool menuRehideWasAttempted,
        bool emergencyRestorePending) =>
        menuRehideWasAttempted && emergencyRestorePending;

    public async void RestoreTaskbarFromEmergencyHotkey()
    {
        Interlocked.Increment(ref _emergencyRestoreRequests);
        var gateEntered = false;
        var restored = false;
        var status = L("Taskbar.RestoreUnconfirmed");
        try
        {
            // Do not cancel an explicit recovery request during shutdown. It is
            // safer to complete shell restoration than to abandon it while a
            // menu-bar transition owns the layout gate.
            await _shellLayoutGate.WaitAsync();
            gateEntered = true;
            restored = await Taskbars.RestoreAsync(recoverCurrentShellTaskbars: true);
            status = Taskbars.LastStatus;
            UpdatePreferences(s => s.HideTaskbar = !restored && Taskbars.IsHidden);
        }
        catch (Exception exception)
        {
            Log("Emergency taskbar restoration failed", exception);
            status = Taskbars.LastStatus;
            try
            {
                if (Preferences is not null)
                    UpdatePreferences(s => s.HideTaskbar = Taskbars.IsHidden);
            }
            catch (Exception preferenceException)
            {
                Log("Could not retain emergency taskbar recovery state", preferenceException);
            }
        }
        finally
        {
            if (gateEntered) _shellLayoutGate.Release();
            Interlocked.Decrement(ref _emergencyRestoreRequests);
        }

        if (_shutdown.IsCancellationRequested) return;
        try
        {
            ShowPreferences();
            MessageBox.Show(
                restored ? L("App.EmergencyRestored") : status,
                L("App.EmergencyTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            Log("Could not present emergency taskbar recovery status", exception);
        }
    }

    private bool _exiting;
    public async void RequestExit(int exitCode = 0, bool forceAfterRestoreFailure = false)
    {
        if (_exiting) return;
        _exiting = true;
        var taskbarRestored = false;
        var desktopIconsRestored = false;
        try
        {
            taskbarRestored = await Taskbars.RestoreAsync();
        }
        catch (Exception ex)
        {
            Log("Taskbar restoration during shutdown failed", ex);
        }

        try
        {
            desktopIconsRestored = DesktopIcons.Restore();
        }
        catch (Exception ex)
        {
            Log("Desktop-icon restoration during shutdown failed", ex);
        }

        var restored = taskbarRestored && desktopIconsRestored;
        var shellElementStillHidden = Taskbars.IsHidden || DesktopIcons.IsHidden;
        var restorationStatus = Taskbars.IsHidden ? Taskbars.LastStatus : DesktopIcons.LastStatus;
        if (ShouldPostponeShutdown(restored, shellElementStillHidden, forceAfterRestoreFailure))
        {
            _exiting = false;
            Log("Shutdown postponed because shell restoration is not confirmed. " + restorationStatus);
            try { ShowPreferences(); }
            catch (Exception ex) { Log("Could not show recovery controls", ex); }
            MessageBox.Show(
                LF("App.ShutdownBlocked", restorationStatus),
                L("App.ShutdownBlockedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!restored && shellElementStillHidden)
            Log("Forced shutdown continues without confirmed shell restoration. " + restorationStatus);
        Shutdown(exitCode);
    }

    private static bool ShouldPostponeShutdown(
        bool restorationConfirmed,
        bool taskbarStillHidden,
        bool forceAfterRestoreFailure) =>
        !restorationConfirmed && taskbarStillHidden && !forceAfterRestoreFailure;

    private static bool ShouldPersistTaskbarHidePreference(
        bool hideRequested,
        bool operationConfirmed,
        bool taskbarStillHidden) =>
        hideRequested
            ? operationConfirmed || taskbarStillHidden
            : !operationConfirmed && taskbarStillHidden;

    private static bool ShouldPersistDesktopIconHidePreference(
        bool hideRequested,
        bool operationConfirmed,
        bool desktopIconsStillHiddenByThisSession) =>
        hideRequested
            ? operationConfirmed || desktopIconsStillHiddenByThisSession
            : !operationConfirmed && desktopIconsStillHiddenByThisSession;

    private void SaveNow()
    {
        try
        {
            Store?.Save(Preferences);
            _saveErrorShown = false;
        }
        catch (Exception ex)
        {
            Log("Could not save preferences", ex);
            if (!_exiting && !_saveErrorShown)
            {
                _saveErrorShown = true;
                MessageBox.Show(
                    LF("App.SaveFailed", DataDirectory),
                    L("Common.ErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    public static void Log(string message, Exception? exception = null)
    {
        try
        {
            var directory = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "velishell-" + DateTime.Today.ToString("yyyy-MM-dd") + ".log");
            File.AppendAllText(path, $"{DateTime.Now:O}  {message}\n{(exception is null ? "" : exception + "\n")}");
        }
        catch { /* Logging must never prevent normal shutdown. */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _saveTimer?.Stop();
        _updateTimer?.Stop();
        _shutdown.Cancel();
        Taskbars.Restore();
        DesktopIcons.Restore();
        if (Preferences is not null) SaveNow();
        Themes?.Dispose();
        Taskbars.Dispose();
        DesktopIcons.Dispose();
        NotificationCenterService.Current.Dispose();
        _updates.Dispose();
        _pinBridge?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}


internal enum UpdateUiState
{
    NotChecked,
    Checking,
    UpToDate,
    NoRelease,
    Available,
    StoreManaged,
    Failed
}
