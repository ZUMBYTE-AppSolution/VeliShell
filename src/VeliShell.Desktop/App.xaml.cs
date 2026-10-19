using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Views;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop;

public partial class App : Application
{
    private static string L(string key) => LocalizationService.Current.Get(key);
    private static string LF(string key, params object[] args) =>
        string.Format(LocalizationService.Current.ActiveCulture, L(key), args);
    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _prototypeUtility;
    private SingleInstancePinBridge? _pinBridge;
    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _updateTimer;
    private PreferencesWindow? _preferencesWindow;
    private SearchWindow? _searchWindow;
    private MenuBarWindow? _menuBarWindow;
    private readonly Dictionary<string, DockWindow> _secondaryDocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MenuBarWindow> _secondaryMenuBars = new(StringComparer.OrdinalIgnoreCase);
    private bool _saveErrorShown;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _updateCheckGate = new(1, 1);
    private readonly SemaphoreSlim _shellLayoutGate = new(1, 1);
    private int _emergencyRestoreRequests;
    private readonly GitHubReleaseUpdateService _updates = new();
    private Task<List<NativeWindow>>? _windowSnapshotTask;
    private long _windowSnapshotStartedAt;
    internal SearchCatalogIndex ProgramIndex { get; } = new();
    private UpdateWindow? _updateWindow;
    private SemanticVersion? _lastOfferedVersion;
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        BuildIdentity.ProfileName);
    private static string LegacyDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HarborDesktop");
    public SettingsStore Store { get; private set; } = null!;
    public Settings Preferences { get; private set; } = null!;
    public ThemeService Themes { get; private set; } = null!;
    internal NativeFramesController? NativeFrames { get; private set; }
    public DockWindow Dock { get; private set; } = null!;
    internal TaskbarVisibilityService Taskbars { get; } = new();
    internal DesktopIconVisibilityService DesktopIcons { get; } = new();
    public string HotkeyStatus { get; set; } = LocalizationService.Current.Get("App.HotkeyPending");
    public string SearchHotkeyStatus { get; set; } = LocalizationService.Current.Get("App.HotkeyPending");
    internal UpdateUiState UpdateState { get; private set; } = UpdateUiState.NotChecked;
    internal SemanticVersion? AvailableUpdateVersion { get; private set; }
    public event Action? PreferencesChanged;
    public event Action? UpdateStateChanged;

    internal Task<List<NativeWindow>> ReadWindowsAsync()
    {
        var now = Environment.TickCount64;
        if (_windowSnapshotTask is null ||
            (_windowSnapshotTask.IsCompleted && now - _windowSnapshotStartedAt >= 1000))
        {
            _windowSnapshotStartedAt = now;
            _windowSnapshotTask = Task.Run(WindowCatalog.Read);
        }
        return _windowSnapshotTask;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (NativePrototypeSession.IsUtility(e.Args))
        {
            _prototypeUtility = true;
            try { NativePrototypeSession.Run(e.Args, this); }
            catch (Exception error) { Log("Native prototype utility failed", error); Shutdown(2); }
            return;
        }
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
        var freshProfile = !File.Exists(Path.Combine(DataDirectory, "settings.json"));
        Preferences = Store.Load();
        if (BuildIdentity.IsTest && freshProfile) Preferences.UpdateSource = UpdateChannel.Test;
        LocalizationService.Current.Apply(Preferences.Language);
        ProgramIndex.Start();
        if (Store.LoadWarning is not null) Log(Store.LoadWarning);
        Themes = new ThemeService();
        Themes.Apply(Preferences.Appearance, Preferences.GlassGloss, Preferences.GlassTransparency);
        NativeFrames = new NativeFramesController();
        Themes.Changed += () => NativeFrames?.Update(Preferences, Themes.IsDark);
        if (Preferences.NativeFramesEnabled) _ = ResumeNativeFramesAsync();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        Dock = new DockWindow(this);
        MainWindow = Dock;
        Dock.Show();
        StabilizeLegacyExecutablePins();
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
            if (!BuildIdentity.IsTest && !PackageIdentityService.HasIdentity && Environment.ProcessPath is { } executablePath)
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
        SyncDisplayWindows();
        if (Preferences.MenuBarEnabled && Preferences.MenuBarOnAllDisplays &&
            !AllMenuBarReservationsConfirmed())
        {
            Preferences.MenuBarOnAllDisplays = false;
            SyncDisplayWindows();
            SaveNow();
            Log("The saved all-display menu-bar preference was disabled because Windows did not reserve every display.");
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
                if (Preferences.FirstRunCompleted && Preferences.Updates != UpdateMode.Manual && _updateWindow is null)
                    await CheckForUpdatesAsync(userInitiated: false, Dock);
            };
            _updateTimer.Start();
        }
        if (!Preferences.FirstRunCompleted) ShowPreferences();
        if (Preferences.FirstRunCompleted && !PackageIdentityService.HasIdentity && Preferences.Updates != UpdateMode.Manual)
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
        if (BuildIdentity.IsTest) return;
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
        Themes.Apply(Preferences.Appearance, Preferences.GlassGloss, Preferences.GlassTransparency);
        NativeFrames?.Update(Preferences, Themes.IsDark);
        PreferencesChanged?.Invoke();
        SyncMenuBar();
        SyncDisplayWindows();
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }
    private async Task ResumeNativeFramesAsync()
    {
        try { await NativeFrames!.EnableAsync(Preferences, Themes.IsDark, explicitConsent: false); }
        catch (Exception error) { Log("Native windows did not resume", error); Preferences.NativeFramesEnabled = false; Store.Save(Preferences); }
    }
    internal void DisableNativeFramesForUpdate()
    {
        UpdatePreferences(settings => settings.NativeFramesEnabled = false);
        NativeFrames?.Stop();
        // Persist before starting an installer/watcher, not on a debounce tick.
        // A failed/cancelled update must not silently re-enable the adapter.
        _saveTimer?.Stop();
        Store.Save(Preferences);
    }

    private void StabilizeLegacyExecutablePins()
    {
        var candidates = Preferences.Pins
            .Where(pin => pin.Kind == PinKind.Item &&
                pin.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                Path.IsPathFullyQualified(pin.Target))
            .Select(pin => (pin.Id, pin.Target, pin.Name)).ToArray();
        if (candidates.Length == 0) return;
        var worker = new Thread(() =>
        {
            try
            {
                var replacements = candidates
                    .Select(pin => (pin.Id, OldTarget: pin.Target,
                        NewTarget: StableLaunchTargetService.PreferShortcut(pin.Target, pin.Name)))
                    .Where(change => !string.Equals(change.OldTarget, change.NewTarget,
                        StringComparison.OrdinalIgnoreCase)).ToArray();
                if (replacements.Length == 0 || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_exiting) return;
                    UpdatePreferences(settings =>
                    {
                        for (var index = 0; index < settings.Pins.Count; index++)
                        {
                            var pin = settings.Pins[index];
                            var change = replacements.FirstOrDefault(item =>
                                string.Equals(item.Id, pin.Id, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(item.OldTarget, pin.Target, StringComparison.OrdinalIgnoreCase));
                            if (change.NewTarget is not null)
                                settings.Pins[index] = pin with { Target = change.NewTarget };
                        }
                    });
                }));
            }
            catch (Exception exception) { Log("Could not upgrade old executable pins", exception); }
        }) { IsBackground = true, Name = "VeliShell shortcut migration" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public void ShowPreferences()
    {
        if (_preferencesWindow is null)
        {
            _preferencesWindow = new PreferencesWindow(this, onboarding: !Preferences.FirstRunCompleted);
            _preferencesWindow.Closed += (_, _) => _preferencesWindow = null;
        }
        _preferencesWindow.Show();
        if (_preferencesWindow.WindowState == WindowState.Minimized) _preferencesWindow.WindowState = WindowState.Normal;
        _preferencesWindow.Activate();
    }

    public void ShowSearch()
    {
        if (_searchWindow is null)
        {
            _searchWindow = new SearchWindow(this);
            _searchWindow.Closed += (_, _) => _searchWindow = null;
        }
        _searchWindow.FocusQuery();
    }

    internal void CompleteOnboarding()
    {
        if (Preferences.FirstRunCompleted) return;
        UpdatePreferences(settings => settings.FirstRunCompleted = true);
        SaveNow();
        if (!PackageIdentityService.HasIdentity && Preferences.Updates != UpdateMode.Manual)
            _ = CheckForUpdatesAsync(userInitiated: false, Dock);
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
        if (!Preferences.MenuBarEnabled)
        {
            foreach (var window in _secondaryMenuBars.Values.ToArray()) window.Close();
            _secondaryMenuBars.Clear();
        }
        return true;
    }

    internal void SyncDisplayWindows()
    {
        if (_exiting || Dock is null || !NativeMethods.TryAllMonitors(out var monitors)) return;
        var secondaryDevices = monitors.Where(monitor => (monitor.Info.Flags & 1) == 0)
            .Select(monitor => monitor.Info.Device)
            .Where(device => !string.IsNullOrWhiteSpace(device))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (device, window) in _secondaryDocks.ToArray())
            if (!Preferences.DockOnAllDisplays || !secondaryDevices.Contains(device))
            {
                _secondaryDocks.Remove(device);
                window.Close();
            }
        if (Preferences.DockOnAllDisplays)
            foreach (var device in secondaryDevices)
                if (!_secondaryDocks.ContainsKey(device))
                {
                    DockWindow? window = null;
                    try
                    {
                        window = new DockWindow(this, device);
                        window.Closed += (_, _) =>
                        {
                            if (_secondaryDocks.TryGetValue(device, out var current) &&
                                ReferenceEquals(current, window)) _secondaryDocks.Remove(device);
                        };
                        window.Show();
                        _secondaryDocks[device] = window;
                    }
                    catch (Exception exception)
                    {
                        Log($"Dock could not open on {device}", exception);
                        window?.Close();
                    }
                }

        foreach (var (device, window) in _secondaryMenuBars.ToArray())
            if (!Preferences.MenuBarEnabled || !Preferences.MenuBarOnAllDisplays ||
                !secondaryDevices.Contains(device) || !window.WorkAreaReserved)
            {
                _secondaryMenuBars.Remove(device);
                window.Close();
            }
        if (Preferences.MenuBarEnabled && Preferences.MenuBarOnAllDisplays)
            foreach (var device in secondaryDevices)
                if (!_secondaryMenuBars.ContainsKey(device))
                {
                    MenuBarWindow? window = null;
                    try
                    {
                        window = new MenuBarWindow(this, device);
                        window.Closed += (_, _) =>
                        {
                            if (_secondaryMenuBars.TryGetValue(device, out var current) &&
                                ReferenceEquals(current, window)) _secondaryMenuBars.Remove(device);
                        };
                        window.Show();
                        if (window.WorkAreaReserved) _secondaryMenuBars[device] = window;
                        else
                        {
                            window.Close();
                            Log($"Menu bar could not reserve space on {device}.");
                        }
                    }
                    catch (Exception exception)
                    {
                        Log($"Menu bar could not open on {device}", exception);
                        window?.Close();
                    }
                }
    }

    private bool AllMenuBarReservationsConfirmed()
    {
        if (_menuBarWindow?.WorkAreaReserved != true) return false;
        if (!NativeMethods.TryAllMonitors(out var monitors)) return false;
        return monitors.Where(monitor => (monitor.Info.Flags & 1) == 0)
            .All(monitor => _secondaryMenuBars.TryGetValue(monitor.Info.Device, out var bar) &&
                            bar.WorkAreaReserved);
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
                Preferences.UpdateSource,
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
                            automaticDownload: !userInitiated && Preferences.Updates == UpdateMode.AutomaticDownload &&
                                release.Version.IsPrerelease == BuildIdentity.IsTest);
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

    internal async Task ChangeUpdateChannelAsync(UpdateChannel channel, Window owner)
    {
        if (PackageIdentityService.HasIdentity) return;
        if (_updateWindow is not null || !await _updateCheckGate.WaitAsync(0))
        { MessageBox.Show(owner, L("Update.ChannelBusy"), L("Update.Channel")); return; }
        try
        {
            UpdatePreferences(settings => settings.UpdateSource = channel);
            SaveNow(); _lastOfferedVersion = null; SetUpdateState(UpdateUiState.NotChecked);
        }
        finally { _updateCheckGate.Release(); }
        await CheckForUpdatesAsync(userInitiated: true, owner);
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

    internal async Task<bool> SetMenuBarOnAllDisplaysAsync(bool enabled)
    {
        if (!Preferences.MenuBarEnabled)
        {
            UpdatePreferences(settings => settings.MenuBarOnAllDisplays = enabled);
            return true;
        }
        var gateEntered = false;
        try
        {
            await _shellLayoutGate.WaitAsync(_shutdown.Token);
            gateEntered = true;
            if (Preferences.MenuBarOnAllDisplays == enabled) return true;
            var rehideTaskbar = Preferences.HideTaskbar || Taskbars.IsHidden;
            if (Taskbars.IsHidden && !await Taskbars.RestoreAsync())
            {
                MessageBox.Show(Taskbars.LastStatus, L("Common.ErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            UpdatePreferences(settings => settings.MenuBarOnAllDisplays = enabled);
            var reserved = !enabled ? _menuBarWindow?.WorkAreaReserved == true
                : AllMenuBarReservationsConfirmed();
            if (!reserved)
            {
                UpdatePreferences(settings => settings.MenuBarOnAllDisplays = false);
                MessageBox.Show(L("MenuBar.ReservationFailed"), L("Common.ErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            var shouldRehide = ShouldRehideTaskbarAfterMenuBarChange(rehideTaskbar,
                Volatile.Read(ref _emergencyRestoreRequests) > 0);
            if (rehideTaskbar && !shouldRehide && Preferences.HideTaskbar)
                UpdatePreferences(settings => settings.HideTaskbar = false);
            var taskbarConfirmed = !shouldRehide || await SetTaskbarHiddenCoreAsync(true,
                showError: true, recoverExistingHiddenTaskbars: false);
            if (ShouldUndoMenuBarRehide(shouldRehide,
                Volatile.Read(ref _emergencyRestoreRequests) > 0))
            {
                taskbarConfirmed = await Taskbars.RestoreAsync(recoverCurrentShellTaskbars: true);
                UpdatePreferences(settings => settings.HideTaskbar = !taskbarConfirmed && Taskbars.IsHidden);
            }
            return reserved && taskbarConfirmed;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return false; }
        finally { if (gateEntered) _shellLayoutGate.Release(); }
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
    internal bool IsExiting => _exiting;
    internal async Task PerformSessionActionAsync(string arguments)
    {
        // Only the fixed commands presented by the menu are accepted. Never
        // forward user-provided command text to the session manager.
        if (arguments is not ("/l" or "/r /t 0" or "/s /t 0") || _exiting) return;
        _exiting = true;
        bool taskbarRestored;
        bool desktopRestored;
        try
        {
            taskbarRestored = await Taskbars.RestoreAsync();
            desktopRestored = DesktopIcons.Restore();
        }
        catch (Exception exception)
        {
            Log("Shell restoration before Windows session action failed", exception);
            taskbarRestored = false;
            desktopRestored = false;
        }
        if (!taskbarRestored || !desktopRestored)
        {
            _exiting = false;
            MessageBox.Show(LF("App.ShutdownBlocked", Taskbars.IsHidden
                    ? Taskbars.LastStatus : DesktopIcons.LastStatus),
                L("App.ShutdownBlockedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var shutdownExecutable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            using var process = Process.Start(new ProcessStartInfo(shutdownExecutable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) throw new InvalidOperationException("Windows did not accept the session action.");
            Shutdown();
        }
        catch (Exception exception)
        {
            _exiting = false;
            Log("Windows session action failed", exception);
            MessageBox.Show(L("MenuBar.SystemActionFailed"), L("Common.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
        if (_prototypeUtility) { _updates.Dispose(); ProgramIndex.Dispose(); base.OnExit(e); return; }
        NativeFrames?.Dispose();
        if (BuildIdentity.IsTest) NativePrototypeSession.RequestClose();
        ProgramIndex.Dispose();
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
