using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class PreferencesWindow : VeliShellWindow
{
    private readonly App _app;
    private bool _loading = true;
    private bool _iconSearchBusy;
    private int _currentPage;
    private readonly bool _onboarding;
    private const int LastPage = 5;
    private static string L(string key) => LocalizationService.Current.Get(key);
    public PreferencesWindow(App app, bool onboarding = false)
    {
        _app = app;
        _onboarding = onboarding;
        LocalizationService.Current.Apply(_app.Preferences.Language);
        InitializeComponent();
        _app.PreferencesChanged += SyncUi;
        _app.UpdateStateChanged += UpdateUpdateStatus;
        _app.Themes.Changed += UpdateThemeStatus;
        NotificationCenterService.Current.WindowsAccessChanged += WindowsNotificationAccessChanged;
        Loaded += (_, _) => { SyncUi(); UpdateDiagnostics(); UpdateApiKeyStatus(); };
        Closed += (_, _) =>
        {
            _app.PreferencesChanged -= SyncUi;
            _app.UpdateStateChanged -= UpdateUpdateStatus;
            _app.Themes.Changed -= UpdateThemeStatus;
            NotificationCenterService.Current.WindowsAccessChanged -= WindowsNotificationAccessChanged;
        };
        SyncUi();
        if (_onboarding)
        {
            Height = 720;
            OnboardingPanel.Visibility = Visibility.Visible;
            NavigationPanel.IsHitTestVisible = false;
            foreach (var nav in NavigationPanel.Children.OfType<RadioButton>())
                nav.IsTabStop = false;
        }
        ShowPage(0);
    }

    private void SyncUi()
    {
        _loading = true;
        var s = _app.Preferences;
        LightChoice.IsChecked = s.Appearance == Appearance.Light;
        DarkChoice.IsChecked = s.Appearance == Appearance.Dark;
        SystemChoice.IsChecked = s.Appearance == Appearance.System;
        foreach (var item in LanguageChoice.Items.OfType<ComboBoxItem>())
            item.IsSelected = string.Equals(item.Tag as string, s.Language.ToString(), StringComparison.Ordinal);
        foreach (var item in UpdateModeChoice.Items.OfType<ComboBoxItem>())
            item.IsSelected = string.Equals(item.Tag as string, s.Updates.ToString(), StringComparison.Ordinal);
        UpdateModeChoice.IsEnabled = !PackageIdentityService.HasIdentity;
        foreach (var item in IconStyleChoice.Items.OfType<ComboBoxItem>())
            item.IsSelected = string.Equals(item.Tag as string, s.IconStyle.ToString(), StringComparison.Ordinal);
        SizeSlider.Value = s.IconSize;
        SizeLabel.Text = $"{s.IconSize:0} DIP";
        ReducedMotionSwitch.IsChecked = s.ReducedMotion;
        MagnificationSwitch.IsChecked = s.Magnification;
        MagnificationSwitch.IsEnabled = !s.ReducedMotion;
        RunningSwitch.IsChecked = s.ShowRunningApps;
        AutoHideSwitch.IsChecked = s.AutoHide;
        TopmostSwitch.IsChecked = s.AlwaysOnTop;
        HideTaskbarSwitch.IsChecked = s.HideTaskbar;
        DesktopIconsSwitch.IsChecked = s.HideDesktopIcons;
        MenuBarSwitch.IsChecked = s.MenuBarEnabled;
        MenuBarAutoHideSwitch.IsChecked = s.MenuBarAutoHide;
        MenuBarAutoHideSwitch.IsEnabled = s.MenuBarEnabled;
        MenuBarTopmostSwitch.IsChecked = s.MenuBarAlwaysOnTop;
        MenuBarTopmostSwitch.IsEnabled = s.MenuBarEnabled;
        WindowsNotificationsSwitch.IsChecked = s.WindowsNotificationsEnabled;
        _ = SyncStartupUiAsync();
        TaskbarStatus.Text = _app.Taskbars.LastStatus;
        DesktopIconsStatus.Text = _app.DesktopIcons.LastStatus;
        UpdateWindowsNotificationUi();
        var hasConsent = s.OnlineIconConsentVersion >= Settings.CurrentOnlineIconConsentVersion;
        var macIconsActive = s.IconStyle == DockIconStyle.Mac;
        FindOnlineIconsButton.IsEnabled = macIconsActive && !_iconSearchBusy;
        OnlineIconStatus.Text = !macIconsActive
            ? L("Apps.OnlineRequiresMacStyle")
            : hasConsent
            ? L("Apps.OnlineEnabled")
            : L("Apps.OnlineDisabled");
        UpdateUpdateStatus();
        UpdateThemeStatus();
        RenderPins();
        RenderSystemIcons();
        RenderRunningIcons();
        _loading = false;
    }

    private void UpdateThemeStatus()
    {
        if (ThemeStatus is null) return;
        ThemeStatus.Text = _app.Preferences.Appearance == Appearance.System
            ? L(_app.Themes.IsDark ? "Theme.SystemDark" : "Theme.SystemLight")
            : L("Theme.Explicit");
    }
    private void ShowPage(int page)
    {
        _currentPage = page;
        var wasLoading = _loading;
        _loading = true;
        foreach (var nav in NavigationPanel.Children.OfType<RadioButton>())
            nav.IsChecked = nav.Tag is string tag && tag == page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _loading = wasLoading;
        AppearancePage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        DockPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        OnlinePage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        WindowsPage.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
        InfoPage.Visibility = page == LastPage ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch
        {
            1 => L("Nav.Dock"),
            2 => L("Nav.Apps"),
            3 => L("Nav.Online"),
            4 => L("Nav.Windows"),
            LastPage => L("Nav.Info"),
            _ => L("Nav.Appearance")
        };
        PageScroll.ScrollToTop();
        if (page == 2) RenderRunningIcons();
        if (page == LastPage) UpdateDiagnostics();
        if (_onboarding) UpdateOnboardingStep();
    }
    private void UpdateOnboardingStep()
    {
        OnboardingStep.Text = string.Format(LocalizationService.Current.ActiveCulture,
            L("Onboarding.Step"), _currentPage + 1, LastPage + 1, PageTitle.Text);
        OnboardingExplanation.Text = L($"Onboarding.Explain{_currentPage}");
        OnboardingBack.IsEnabled = _currentPage > 0;
        OnboardingNext.Content = L(_currentPage == LastPage ? "Onboarding.Finish" : "Onboarding.Next");
    }

    private void OnboardingBack_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 0) ShowPage(_currentPage - 1);
    }

    private void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage == 3 &&
            !SavePendingKey(UserApiCredentials.MacOsIcons, MacOsIconsKeyBox)) return;
        if (_currentPage < LastPage) { ShowPage(_currentPage + 1); return; }
        _app.CompleteOnboarding();
        Close();
    }

    private bool SavePendingKey(string target, PasswordBox box) =>
        box.Password.Length == 0 || SaveApiKey(target, box);
    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading && sender is RadioButton { Tag: string value } && int.TryParse(value, out var page)) ShowPage(page);
    }
    private void Appearance_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading && sender is RadioButton { Tag: string value } && Enum.TryParse<Appearance>(value, out var mode))
            _app.UpdatePreferences(s => s.Appearance = mode);
    }
    private void LanguageChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageChoice.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !Enum.TryParse<UiLanguage>(value, out var language)) return;
        LocalizationService.Current.Apply(language);
        _app.UpdatePreferences(s => s.Language = language);
        ShowPage(_currentPage);
        UpdateThemeStatus();
        UpdateDiagnostics();
        RenderPins();
    }
    private void UpdateModeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UpdateModeChoice.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !Enum.TryParse<UpdateMode>(value, out var mode)) return;
        _app.UpdatePreferences(s => s.Updates = mode);
    }
    private void IconStyleChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IconStyleChoice.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !Enum.TryParse<DockIconStyle>(value, out var iconStyle)) return;
        _app.UpdatePreferences(s => s.IconStyle = iconStyle);
    }
    private void UpdateUpdateStatus()
    {
        if (UpdateStatus is null || CheckUpdatesButton is null) return;
        UpdateStatus.Text = _app.GetUpdateStatusText();
        CheckUpdatesButton.IsEnabled = !PackageIdentityService.HasIdentity &&
                                       _app.UpdateState != UpdateUiState.Checking;
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        await _app.CheckForUpdatesAsync(userInitiated: true, this);
        UpdateUpdateStatus();
    }
    private void Size_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading) _app.UpdatePreferences(s => s.IconSize = e.NewValue);
    }
    private void ReducedMotion_Click(object sender, RoutedEventArgs e) => _app.UpdatePreferences(s => s.ReducedMotion = ReducedMotionSwitch.IsChecked == true);
    private void Magnification_Click(object sender, RoutedEventArgs e) => _app.UpdatePreferences(s => s.Magnification = MagnificationSwitch.IsChecked == true);
    private void Running_Click(object sender, RoutedEventArgs e) => _app.UpdatePreferences(s => s.ShowRunningApps = RunningSwitch.IsChecked == true);
    private void AutoHide_Click(object sender, RoutedEventArgs e) => _app.UpdatePreferences(s => s.AutoHide = AutoHideSwitch.IsChecked == true);
    private void Topmost_Click(object sender, RoutedEventArgs e) => _app.UpdatePreferences(s => s.AlwaysOnTop = TopmostSwitch.IsChecked == true);
    private void OpenSearch_Click(object sender, RoutedEventArgs e) => _app.ShowSearch();
    private async void MenuBar_Click(object sender, RoutedEventArgs e)
    {
        MenuBarSwitch.IsEnabled = false;
        try
        {
            await _app.SetMenuBarEnabledAsync(MenuBarSwitch.IsChecked == true);
            MenuBarSwitch.IsChecked = _app.Preferences.MenuBarEnabled;
        }
        finally
        {
            MenuBarSwitch.IsEnabled = true;
        }
    }
    private void MenuBarAutoHide_Click(object sender, RoutedEventArgs e) => _app.UpdatePreferences(s => s.MenuBarAutoHide = MenuBarAutoHideSwitch.IsChecked == true);
    private void MenuBarTopmost_Click(object sender, RoutedEventArgs e) => _app.UpdatePreferences(s => s.MenuBarAlwaysOnTop = MenuBarTopmostSwitch.IsChecked == true);
    private async void WindowsNotifications_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enable = WindowsNotificationsSwitch.IsChecked == true;
        WindowsNotificationsSwitch.IsEnabled = false;
        RefreshWindowsNotificationsButton.IsEnabled = false;
        try
        {
            if (!enable)
            {
                NotificationCenterService.Current.DisableWindowsNotifications();
                _app.UpdatePreferences(settings => settings.WindowsNotificationsEnabled = false);
                return;
            }

            var access = await NotificationCenterService.Current.RequestWindowsAccessAsync();
            var allowed = access.State == WindowsNotificationAccessState.Allowed;
            _app.UpdatePreferences(settings => settings.WindowsNotificationsEnabled = allowed);
        }
        finally
        {
            UpdateWindowsNotificationUi();
            WindowsNotificationsSwitch.IsChecked = _app.Preferences.WindowsNotificationsEnabled;
        }
    }

    private async void RefreshWindowsNotifications_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindowsNotificationsButton.IsEnabled = false;
        try
        {
            await NotificationCenterService.Current.RefreshWindowsNotificationsAsync();
        }
        finally
        {
            UpdateWindowsNotificationUi();
        }
    }

    private void OpenNotificationPrivacy_Click(object sender, RoutedEventArgs e) =>
        LaunchService.Open("ms-settings:privacy-notifications");

    private void WindowsNotificationAccessChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateWindowsNotificationUi);
            return;
        }
        UpdateWindowsNotificationUi();
    }

    private void UpdateWindowsNotificationUi()
    {
        if (WindowsNotificationsStatus is null || WindowsNotificationsSwitch is null ||
            RefreshWindowsNotificationsButton is null) return;
        var access = NotificationCenterService.Current.WindowsAccess;
        WindowsNotificationsStatus.Text = L(access.State switch
        {
            WindowsNotificationAccessState.Requesting => "Notifications.AccessRequesting",
            WindowsNotificationAccessState.Allowed => "Notifications.AccessAllowed",
            WindowsNotificationAccessState.Denied => "Notifications.AccessDenied",
            WindowsNotificationAccessState.Revoked => "Notifications.AccessRevoked",
            WindowsNotificationAccessState.Unsupported => "Notifications.AccessUnsupported",
            WindowsNotificationAccessState.Error => "Notifications.AccessError",
            WindowsNotificationAccessState.NotRequested => "Notifications.AccessNotRequested",
            _ => "Notifications.AccessDisabled"
        });
        WindowsNotificationsSwitch.IsEnabled = access.State is not
            (WindowsNotificationAccessState.Requesting or WindowsNotificationAccessState.Unsupported);
        RefreshWindowsNotificationsButton.IsEnabled =
            _app.Preferences.WindowsNotificationsEnabled &&
            access.State == WindowsNotificationAccessState.Allowed;
    }
    private async void UserStartup_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enable = UserStartupSwitch.IsChecked == true;
        UserStartupSwitch.IsEnabled = false;
        try
        {
            await StartupRegistrationService.SetUserLoginEnabledAsync(enable);
            _app.UpdatePreferences(s => s.Startup = enable ? StartupMode.UserLogin : StartupMode.Disabled);
        }
        catch (Exception ex)
        {
            App.Log("Could not change startup registration", ex);
            UserStartupSwitch.IsChecked = !enable;
            MessageBox.Show(this, L("Common.ServiceError") + "\n\n" + ex.Message,
                L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            await SyncStartupUiAsync();
        }
    }

    private async Task SyncStartupUiAsync()
    {
        if (UserStartupSwitch is null || StartupStatus is null) return;
        UserStartupSwitch.IsEnabled = false;
        try
        {
            var userLoginEnabled = await StartupRegistrationService.IsUserLoginEnabledAsync();
            UserStartupSwitch.IsChecked = userLoginEnabled;
            StartupStatus.Text = L(userLoginEnabled ? "Startup.StatusEnabled" : "Startup.StatusDisabled");
        }
        catch (Exception ex)
        {
            App.Log("Could not query startup registration", ex);
            UserStartupSwitch.IsChecked = false;
            StartupStatus.Text = L("Common.ServiceError") + " " + ex.Message;
        }
        finally
        {
            UserStartupSwitch.IsEnabled = true;
        }
    }
    private async void HideTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        await _app.SetTaskbarHiddenAsync(HideTaskbarSwitch.IsChecked == true);
        SyncUi();
    }
    private async void RestoreTaskbar_Click(object sender, RoutedEventArgs e)
    {
        await _app.SetTaskbarHiddenAsync(false);
        SyncUi();
    }
    private void DesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        DesktopIconsSwitch.IsEnabled = false;
        try
        {
            _app.SetDesktopIconsHidden(DesktopIconsSwitch.IsChecked == true);
            DesktopIconsSwitch.IsChecked = _app.Preferences.HideDesktopIcons;
        }
        finally
        {
            DesktopIconsSwitch.IsEnabled = true;
            DesktopIconsStatus.Text = _app.DesktopIcons.LastStatus;
        }
    }
    private void RestoreDesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        _app.SetDesktopIconsHidden(false);
        SyncUi();
    }
    private async void FindOnlineIcons_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureOnlineIconConsent()) return;
        await FindOnlineIconsAsync(_app.Preferences.Pins);
    }
    private async Task FindOnlineIconsAsync(IEnumerable<Pin> candidates)
    {
        var eligiblePins = candidates.Where(AppStoreIconService.IsEligibleAppPin).ToList();
        if (eligiblePins.Count == 0)
        {
            OnlineIconStatus.Text = L("Apps.NoEligiblePins");
            return;
        }
        var pins = eligiblePins
            .Where(pin => pin.Icon is null || OnlineIconService.TryLoad(pin.Icon) is null)
            .ToList();
        if (pins.Count == 0)
        {
            OnlineIconStatus.Text = L("Apps.AllIconsValid");
            return;
        }

        _iconSearchBusy = true;
        FindOnlineIconsButton.IsEnabled = false;
        OnlineIconStatus.Text = string.Format(LocalizationService.Current.ActiveCulture, L("Apps.Searching"), pins.Count);
        try
        {
            var applied = 0;
            var noMatch = 0;
            var failed = 0;
            var canceled = false;
            var windows = await Task.Run(WindowCatalog.Read);
            string? firstError = null;
            foreach (var pin in pins)
            {
                var result = await ChooseOnlineIconForPinAsync(pin, allowSkip: true, windows: windows);
                switch (result)
                {
                    case IconChoiceResult.Applied: applied++; break;
                    case IconChoiceResult.Skipped: noMatch++; break;
                    case IconChoiceResult.NoMatch: noMatch++; break;
                    case IconChoiceResult.Failed: failed++; firstError ??= OnlineIconStatus.Text; break;
                    case IconChoiceResult.Canceled: canceled = true; break;
                }
                if (result == IconChoiceResult.Canceled) break;
            }
            OnlineIconStatus.Text = (canceled ? L("Apps.SearchCanceled") + " " : "") +
                string.Format(LocalizationService.Current.ActiveCulture,
                L("Apps.SearchResult"), applied, noMatch, failed) +
                (failed > 0 && !string.IsNullOrWhiteSpace(firstError) ? " " + firstError : "");
            RenderPins();
        }
        catch (OperationCanceledException)
        {
            OnlineIconStatus.Text = L("Apps.SearchCanceled");
        }
        finally
        {
            _iconSearchBusy = false;
            FindOnlineIconsButton.IsEnabled = _app.Preferences.IconStyle == DockIconStyle.Mac;
        }
    }

    private async Task<IconChoiceResult> ChooseOnlineIconForPinAsync(
        Pin pin,
        Action<Settings, IconReference>? applyOverride = null,
        bool allowSkip = false,
        IReadOnlyList<NativeWindow>? windows = null)
    {
        if (!EnsureOnlineIconConsent()) return IconChoiceResult.Canceled;
        if (_app.Preferences.IconStyle != DockIconStyle.Mac)
        {
            OnlineIconStatus.Text = L("Apps.OnlineRequiresMacStyle");
            return IconChoiceResult.Failed;
        }
        windows ??= await Task.Run(WindowCatalog.Read);
        var queries = IconSearchQueries.ForPin(pin, windows);
        if (queries.Count == 0) return IconChoiceResult.NoMatch;
        return await ChooseOnlineIconAsync(
            LocalizationService.Current.DisplayPinName(pin),
            queries,
            applyOverride ?? ((settings, icon) =>
            {
                var index = settings.Pins.FindIndex(item =>
                    string.Equals(item.Id, pin.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) settings.Pins[index] = settings.Pins[index] with { Icon = icon };
            }), allowSkip, pin);
    }

    private async Task<IconChoiceResult> ChooseOnlineIconAsync(
        string displayName,
        string query,
        Action<Settings, IconReference> apply) =>
        await ChooseOnlineIconAsync(displayName, [query], apply, allowSkip: false);

    private async Task<IconChoiceResult> ChooseOnlineIconAsync(
        string displayName,
        IReadOnlyList<string> queries,
        Action<Settings, IconReference> apply,
        bool allowSkip,
        Pin? fallbackPin = null)
    {
        try
        {
            OnlineIconStatus.Text = string.Format(LocalizationService.Current.ActiveCulture,
                L("Apps.SearchingOne"), displayName);
            var searchErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var searches = queries.Select(async query =>
            {
                try { return await AppStoreIconService.SearchAsync(query); }
                catch (AppStoreIconServiceException exception)
                {
                    searchErrors.Enqueue(exception);
                    return Array.Empty<AppStoreIconSearchHit>();
                }
            });
            var macSearches = UserApiCredentials.Read(UserApiCredentials.MacOsIcons) is null
                ? Array.Empty<Task<IReadOnlyList<AppStoreIconSearchHit>>>()
                : queries.Take(4).Select(async query =>
                {
                    try { return await MacOsIconsApiService.SearchAsync(query); }
                    catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                      InvalidDataException or
                                                      System.Text.Json.JsonException)
                    {
                        App.Log("macOSicons search is unavailable", exception);
                        searchErrors.Enqueue(exception);
                        return Array.Empty<AppStoreIconSearchHit>();
                    }
                }).ToArray();
            var hits = (await Task.WhenAll(searches.Concat(macSearches)))
                .SelectMany(result => result)
                .DistinctBy(hit => hit.Provider + ":" + hit.PreviewUrl.AbsoluteUri,
                    StringComparer.Ordinal).Take(40).ToArray();
            if (hits.Length == 0)
            {
                if (!searchErrors.IsEmpty)
                {
                    OnlineIconStatus.Text = L("Apps.SearchProviderFailed");
                    return IconChoiceResult.Failed;
                }
                if (fallbackPin is null ||
                    UserApiCredentials.Read(UserApiCredentials.MacOsIcons) is null)
                    return IconChoiceResult.NoMatch;
                var answer = MessageBox.Show(this,
                    string.Format(LocalizationService.Current.ActiveCulture,
                        L("Apps.MaskFallbackQuestion"), displayName),
                    L("Apps.MaskFallbackTitle"), MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return IconChoiceResult.Canceled;
                if (answer == MessageBoxResult.No)
                    return allowSkip ? IconChoiceResult.Skipped : IconChoiceResult.NoMatch;
                try
                {
                    var icon = await MacOsIconsMaskService.MaskAsync(fallbackPin);
                    _app.UpdatePreferences(settings => apply(settings, icon));
                    return IconChoiceResult.Applied;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                  InvalidDataException or InvalidOperationException or
                                                  NotSupportedException)
                {
                    App.Log("macOSicons mask generation failed", exception);
                    OnlineIconStatus.Text = L("Apps.MaskFailed");
                    return IconChoiceResult.Failed;
                }
            }
            var picker = new IconPickerWindow(
                displayName, string.Join(" · ", queries), hits, allowSkip) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedHit is null)
                return picker.Skipped ? IconChoiceResult.Skipped : IconChoiceResult.Canceled;

            OnlineIconStatus.Text = L("Apps.DownloadingSelection");
            var download = picker.SelectedHit.Provider == MacOsIconsApiService.Provider
                ? await MacOsIconsApiService.DownloadAsync(picker.SelectedHit)
                : await AppStoreIconService.DownloadAsync(picker.SelectedHit);
            if (download.Icon is null)
            {
                OnlineIconStatus.Text = download.Error ?? L("ItunesSearch.UnsafeImage");
                return IconChoiceResult.Failed;
            }
            _app.UpdatePreferences(settings => apply(settings, download.Icon));
            return IconChoiceResult.Applied;
        }
        catch (AppStoreIconServiceException ex)
        {
            OnlineIconStatus.Text = ex.Message;
            return IconChoiceResult.Failed;
        }
        catch (OperationCanceledException)
        {
            return IconChoiceResult.Canceled;
        }
        catch (Exception ex)
        {
            App.Log("Online icon picker failed", ex);
            OnlineIconStatus.Text = L("Apps.SearchProviderFailed");
            return IconChoiceResult.Failed;
        }
    }
    private bool EnsureOnlineIconConsent()
    {
        if (_app.Preferences.OnlineIconConsentVersion >= Settings.CurrentOnlineIconConsentVersion) return true;
        var answer = MessageBox.Show(this, L("Apps.ConsentBody"), L("Apps.ConsentTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        _app.UpdatePreferences(s =>
        {
            s.OnlineIconConsentVersion = Settings.CurrentOnlineIconConsentVersion;
            if (s.OnlineIcons == OnlineIconMode.Disabled) s.OnlineIcons = OnlineIconMode.OnDemand;
        });
        return true;
    }
    private void OpenIconSource_Click(object sender, RoutedEventArgs e) =>
        LaunchService.Open(
            "https://performance-partners.apple.com/resources/documentation/itunes-store-web-service-search-api/");

    private void OpenMacOsIconsApi_Click(object sender, RoutedEventArgs e) =>
        LaunchService.Open("https://macosicons.com/developers");

    private void UpdateApiKeyStatus()
    {
        try
        {
            MacOsIconsKeyStatus.Text = L(UserApiCredentials.Read(UserApiCredentials.MacOsIcons) is null
                ? "Common.KeyMissing" : "Common.KeyStored");
        }
        catch (Exception exception)
        {
            App.Log("Could not read macOSicons credential status", exception);
            MacOsIconsKeyStatus.Text = L("Common.KeyError");
        }
        try
        {
            var legacyBraveKeyPresent = UserApiCredentials.Read(UserApiCredentials.BraveSearch) is not null;
            LegacyBraveKeyCard.Visibility = legacyBraveKeyPresent ? Visibility.Visible : Visibility.Collapsed;
            BraveKeyStatus.Text = L(legacyBraveKeyPresent ? "Common.KeyStored" : "Common.KeyMissing");
        }
        catch (Exception exception)
        {
            App.Log("Could not read obsolete Brave credential status", exception);
            BraveKeyStatus.Text = L("Common.KeyError");
            LegacyBraveKeyCard.Visibility = Visibility.Visible;
        }
    }

    private void SaveMacOsIconsKey_Click(object sender, RoutedEventArgs e) =>
        SaveApiKey(UserApiCredentials.MacOsIcons, MacOsIconsKeyBox);
    private void RemoveBraveKey_Click(object sender, RoutedEventArgs e) =>
        RemoveApiKey(UserApiCredentials.BraveSearch);
    private void RemoveMacOsIconsKey_Click(object sender, RoutedEventArgs e) =>
        RemoveApiKey(UserApiCredentials.MacOsIcons);

    private bool SaveApiKey(string target, PasswordBox box)
    {
        try
        {
            UserApiCredentials.Save(target, box.Password);
            box.Clear();
            return true;
        }
        catch (Exception exception)
        {
            App.Log("Could not save a user API credential", exception);
            MessageBox.Show(this, L("Common.KeyError"), L("Common.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { UpdateApiKeyStatus(); }
    }

    private void RemoveApiKey(string target)
    {
        try { UserApiCredentials.Delete(target); }
        catch (Exception exception)
        {
            App.Log("Could not remove a user API credential", exception);
            MessageBox.Show(this, L("Common.KeyError"), L("Common.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateApiKeyStatus();
    }

    private enum IconChoiceResult { Applied, Skipped, NoMatch, Failed, Canceled }
    private void OpenLocalLicenses_Click(object sender, RoutedEventArgs e)
    {
        var localDirectory = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-LICENSES");
        LaunchService.Open(Directory.Exists(localDirectory)
            ? localDirectory
            : "https://github.com/ZUMBYTE-AppSolution/VeliShell/tree/main/THIRD-PARTY-LICENSES");
    }
    private void OpenDeveloperWebsite_Click(object sender, RoutedEventArgs e) =>
        LaunchService.Open("https://www.zumbyte.de/");
    private void OpenSupport_Click(object sender, RoutedEventArgs e) =>
        LaunchService.Open("https://www.zumbyte.de/support.php");
    private void OpenSourceCode_Click(object sender, RoutedEventArgs e) =>
        LaunchService.Open("https://github.com/ZUMBYTE-AppSolution/VeliShell");
    private void OpenWindowsSettings_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:");
    private void AddProgram_Click(object sender, RoutedEventArgs e) => _app.Dock.AddPrograms();
    private void AddFolder_Click(object sender, RoutedEventArgs e) => _app.Dock.AddFolder();
    private void AddVirtualFolder_Click(object sender, RoutedEventArgs e) => _app.Dock.CreateVirtualFolder();
    private void ShowDockPage_Click(object sender, RoutedEventArgs e)
    {
        // Selecting the navigation item keeps both the page and the sidebar in sync.
        var parent = (Panel)AppearanceNav.Parent;
        var entry = parent.Children.OfType<RadioButton>().FirstOrDefault(r => r.Tag as string == "1");
        if (entry is not null) entry.IsChecked = true;
    }
    private void PinSearch_Changed(object sender, TextChangedEventArgs e) { if (!_loading) RenderPins(); }

    private void RenderPins()
    {
        PinList.Children.Clear();
        var query = PinSearch.Text.Trim();
        foreach (var pin in _app.Preferences.Pins.Where(p =>
                     LocalizationService.Current.DisplayPinName(p).Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            var displayName = LocalizationService.Current.DisplayPinName(pin);
            PinList.Children.Add(CreateIconRow(
                displayName,
                pin.Target,
                IconService.IdForPin(pin),
                pin.Icon,
                chooseLocal: () => ChooseLocalIconForPin(pin),
                chooseOnline: _app.Preferences.IconStyle == DockIconStyle.Mac &&
                              AppStoreIconService.IsEligibleAppPin(pin)
                    ? () => _ = ChooseOnlineIconFromRowAsync(pin)
                    : null,
                reset: pin.Icon is null ? null : () => ResetPinIcon(pin.Id),
                addExtraButtons: buttons =>
                {
                    if (pin.Kind == PinKind.VirtualFolder)
                    {
                        AddTextButton(buttons, L("FolderPopover.AddApps"), displayName,
                            () => _app.Dock.AddProgramsToVirtualFolder(pin.Id));
                        AddTextButton(buttons, L("FolderPopover.RenameTitle"), displayName,
                            () => _app.Dock.RenameVirtualFolder(pin));
                        AddFolderModeSelector(buttons, pin, displayName);
                    }
                    else if (Directory.Exists(pin.Target))
                        AddFolderModeSelector(buttons, pin, displayName);
                    AddCompactButton(buttons, "‹", L("Common.MoveLeft"), displayName,
                        () => _app.Dock.MovePin(pin.Id, -1));
                    AddCompactButton(buttons, "›", L("Common.MoveRight"), displayName,
                        () => _app.Dock.MovePin(pin.Id, 1));
                    AddCompactButton(buttons, "×", L("Common.Remove"), displayName,
                        () => _app.Dock.RemovePin(pin));
                }));
        }
        if (PinList.Children.Count == 0) PinList.Children.Add(new TextBlock { Text = L("Common.None"), Margin = new Thickness(8,12,8,12) });
    }

    private void AddFolderModeSelector(Panel buttons, Pin pin, string displayName)
    {
        var selector = new Button
        {
            Content = L("FolderPopover.DisplayMode") + ": " + L("FolderPopover.Mode." + pin.FolderMode),
            Padding = new Thickness(8, 3, 8, 3), MinHeight = 26,
            Margin = new Thickness(0, 0, 6, 5)
        };
        AutomationProperties.SetName(selector, L("FolderPopover.DisplayMode") + ": " + displayName);
        var menu = new ContextMenu();
        foreach (var mode in Enum.GetValues<FolderDisplayMode>().Where(mode =>
                     pin.Kind != PinKind.VirtualFolder ||
                     mode is FolderDisplayMode.AppLauncher or FolderDisplayMode.CompactAppLauncher))
        {
            var choice = new MenuItem
            {
                Header = L("FolderPopover.Mode." + mode),
                IsCheckable = true, IsChecked = mode == pin.FolderMode
            };
            choice.Click += (_, _) => _app.Dock.SetFolderMode(pin.Id, mode);
            menu.Items.Add(choice);
        }
        selector.ContextMenu = menu;
        selector.Click += (_, _) => menu.IsOpen = true;
        buttons.Children.Add(selector);
    }

    private void RenderSystemIcons()
    {
        if (SystemIconList is null) return;
        SystemIconList.Children.Clear();
        AddSystemIconRow(
            "start", L("Apps.WindowsStartDockIcon"), "start", "", null,
            buttons => AddTextButton(buttons,
                L(_app.Preferences.ShowWindowsStartDockItem
                    ? "Apps.HideWindowsStartDockItem" : "Apps.ShowWindowsStartDockItem"),
                L("Apps.WindowsStartDockIcon"),
                () => _app.UpdatePreferences(settings =>
                    settings.ShowWindowsStartDockItem = !settings.ShowWindowsStartDockItem)));
        AddSystemIconRow(
            "velishell",
            L("Apps.VeliShellDockIcon"),
            "velishell",
            "",
            "System Settings",
            buttons => AddTextButton(
                buttons,
                L(_app.Preferences.ShowVeliShellDockItem
                    ? "Apps.HideVeliShellDockItem"
                    : "Apps.ShowVeliShellDockItem"),
                L("Apps.VeliShellDockIcon"),
                () => _app.UpdatePreferences(settings =>
                    settings.ShowVeliShellDockItem = !settings.ShowVeliShellDockItem)));
        AddSystemIconRow("overflow", L("Apps.OverflowDockIcon"), "overflow", "", "Launchpad");
        AddSystemIconRow("trash-empty", L("Apps.RecycleBinEmptyIcon"), "trash", "shell:RecycleBinFolder", "Empty Trash");
        AddSystemIconRow("trash-full", L("Apps.RecycleBinFullIcon"), "trash-full", "shell:RecycleBinFolder", "Full Trash");
    }

    private void AddSystemIconRow(
        string settingsKey,
        string displayName,
        string iconId,
        string target,
        string? onlineQuery,
        Action<Panel>? addExtraButtons = null)
    {
        var icon = _app.Preferences.GetDockIconOverride(settingsKey);
        SystemIconList.Children.Add(CreateIconRow(
            displayName,
            target,
            iconId,
            icon,
            chooseLocal: () => ChooseLocalIconForDockElement(settingsKey),
            chooseOnline: onlineQuery is not null && _app.Preferences.IconStyle == DockIconStyle.Mac
                ? () => _ = ChooseOnlineIconForDockElementAsync(settingsKey, displayName, onlineQuery)
                : null,
            reset: icon is null ? null : () => ResetDockIcon(settingsKey),
            addExtraButtons: addExtraButtons));
    }

    private void RenderRunningIcons()
    {
        if (RunningIconList is null) return;
        RunningIconList.Children.Clear();
        if (!_app.Preferences.ShowRunningApps)
        {
            RunningIconList.Children.Add(SecondaryMessage(L("Apps.RunningIconsDisabled")));
            return;
        }

        var running = _app.Dock.GetConfigurableRunningApps();
        foreach (var app in running)
        {
            var displayName = LocalizationService.Current.DisplayPinName(app);
            var persistedIcon = _app.Preferences.GetDockIconOverride(app.Id);
            RunningIconList.Children.Add(CreateIconRow(
                displayName,
                app.Target,
                app.Id,
                app.Icon,
                chooseLocal: () => ChooseLocalIconForDockElement(app.Id),
                chooseOnline: _app.Preferences.IconStyle == DockIconStyle.Mac &&
                              AppStoreIconService.IsEligibleAppPin(app)
                    ? () => _ = ChooseOnlineIconForRunningAppAsync(app)
                    : null,
                reset: persistedIcon is null ? null : () => ResetDockIcon(app.Id)));
        }
        if (RunningIconList.Children.Count == 0)
            RunningIconList.Children.Add(SecondaryMessage(L("Apps.NoRunningIcons")));
    }

    private Grid CreateIconRow(
        string displayName,
        string target,
        string iconId,
        IconReference? icon,
        Action chooseLocal,
        Action? chooseOnline,
        Action? reset,
        Action<WrapPanel>? addExtraButtons = null)
    {
        var row = new Grid { Margin = new Thickness(2, 7, 2, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(39) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var preview = new Image
        {
            Source = IconService.For(iconId, target, icon),
            Width = 30,
            Height = 30,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
        Grid.SetRowSpan(preview, 2);
        row.Children.Add(preview);

        var labels = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        labels.Children.Add(new TextBlock
        {
            Text = displayName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = string.IsNullOrWhiteSpace(target) ? displayName : target
        });
        labels.Children.Add(SecondaryMessage(IconDescription(icon), new Thickness(0, 2, 0, 0)));
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);

        var buttons = new WrapPanel
        {
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AddTextButton(buttons, L(icon is null ? "Apps.ChooseLocalIcon" : "Apps.ReplaceLocalIcon"), displayName, chooseLocal);
        if (chooseOnline is not null)
            AddTextButton(buttons, L("Apps.ChooseOnlineIcon"), displayName, chooseOnline);
        if (reset is not null)
            AddTextButton(buttons, L("Apps.ResetIcon"), displayName, reset);
        addExtraButtons?.Invoke(buttons);
        Grid.SetRow(buttons, 1);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        return row;
    }

    private static TextBlock SecondaryMessage(string text, Thickness? margin = null)
    {
        var message = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = margin ?? new Thickness(8, 12, 8, 12)
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return message;
    }

    private string IconDescription(IconReference? icon)
    {
        if (CustomIconService.IsCustom(icon)) return L("Apps.LocalIconActive");
        return OnlineIconService.TryGetAttribution(icon)?.Text ?? L("Apps.DefaultIconActive");
    }

    private static void AddTextButton(Panel buttons, string label, string itemName, Action action)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(8, 3, 8, 3),
            MinHeight = 26,
            Margin = new Thickness(0, 0, 6, 5),
            ToolTip = label
        };
        AutomationProperties.SetName(button, label + ": " + itemName);
        button.Click += (_, _) => action();
        buttons.Children.Add(button);
    }

    private static void AddCompactButton(Panel buttons, string glyph, string label, string itemName, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 27,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 5),
            ToolTip = label
        };
        AutomationProperties.SetName(button, label + ": " + itemName);
        button.Click += (_, _) => action();
        buttons.Children.Add(button);
    }

    private void ChooseLocalIconForPin(Pin pin)
    {
        var icon = ChooseLocalIcon();
        if (icon is null) return;
        _app.UpdatePreferences(settings =>
        {
            var index = settings.Pins.FindIndex(candidate =>
                string.Equals(candidate.Id, pin.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) settings.Pins[index] = settings.Pins[index] with { Icon = icon };
        });
    }

    private void ResetPinIcon(string pinId) => _app.UpdatePreferences(settings =>
    {
        var index = settings.Pins.FindIndex(candidate =>
            string.Equals(candidate.Id, pinId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) settings.Pins[index] = settings.Pins[index] with { Icon = null };
    });

    private void ChooseLocalIconForDockElement(string settingsKey)
    {
        var icon = ChooseLocalIcon();
        if (icon is null) return;
        _app.UpdatePreferences(settings => settings.DockIconOverrides[settingsKey] = icon);
    }

    private IconReference? ChooseLocalIcon()
    {
        var picker = new OpenFileDialog
        {
            Title = L("Apps.LocalIconDialogTitle"),
            Filter = L("Apps.LocalIconFilter"),
            CheckFileExists = true,
            Multiselect = false,
            DereferenceLinks = true
        };
        if (picker.ShowDialog(this) != true) return null;
        try
        {
            return CustomIconService.Import(picker.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or NotSupportedException or ArgumentException or
                                          FormatException or InvalidOperationException or OverflowException or
                                          System.Runtime.InteropServices.COMException)
        {
            App.Log("Could not import a custom dock icon", exception);
            MessageBox.Show(this, L("Apps.LocalIconInvalid"), L("Common.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private void ResetDockIcon(string settingsKey) =>
        _app.UpdatePreferences(settings => settings.DockIconOverrides.Remove(settingsKey));

    private void RefreshRunningIcons_Click(object sender, RoutedEventArgs e) => RenderRunningIcons();
    private async Task ChooseOnlineIconFromRowAsync(Pin pin)
    {
        var result = await ChooseOnlineIconForPinAsync(pin);
        OnlineIconStatus.Text = result switch
        {
            IconChoiceResult.Applied => L("Apps.SelectionApplied"),
            IconChoiceResult.NoMatch => L("Apps.NoSelectionResults"),
            IconChoiceResult.Canceled => L("Apps.SelectionCanceled"),
            _ => OnlineIconStatus.Text
        };
        RenderPins();
    }

    private async Task ChooseOnlineIconForDockElementAsync(
        string settingsKey,
        string displayName,
        string query)
    {
        if (!CanChooseOnlineIcon()) return;
        var result = await ChooseOnlineIconAsync(
            displayName,
            query,
            (settings, icon) => settings.DockIconOverrides[settingsKey] = icon);
        ShowIconChoiceResult(result);
        RenderSystemIcons();
    }

    private async Task ChooseOnlineIconForRunningAppAsync(Pin app)
    {
        if (!CanChooseOnlineIcon()) return;
        var result = await ChooseOnlineIconForPinAsync(
            app,
            (settings, icon) => settings.DockIconOverrides[app.Id] = icon);
        ShowIconChoiceResult(result);
        RenderRunningIcons();
    }

    private bool CanChooseOnlineIcon()
    {
        if (_app.Preferences.IconStyle != DockIconStyle.Mac)
        {
            OnlineIconStatus.Text = L("Apps.OnlineRequiresMacStyle");
            return false;
        }
        return EnsureOnlineIconConsent();
    }

    private void ShowIconChoiceResult(IconChoiceResult result)
    {
        OnlineIconStatus.Text = result switch
        {
            IconChoiceResult.Applied => L("Apps.SelectionApplied"),
            IconChoiceResult.NoMatch => L("Apps.NoSelectionResults"),
            IconChoiceResult.Canceled => L("Apps.SelectionCanceled"),
            _ => OnlineIconStatus.Text
        };
    }
    private void ResetPins_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L("Apps.ResetPinsQuestion"),
            L("Apps.ResetPinsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _app.UpdatePreferences(s => s.Pins = Settings.Defaults());
    }
    private string DiagnosticText()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var appearance = L(_app.Preferences.Appearance switch
        {
            Appearance.Light => "Appearance.Light",
            Appearance.Dark => "Appearance.Dark",
            _ => "Appearance.System"
        });
        var language = L(_app.Preferences.Language switch
        {
            UiLanguage.German => "Language.German",
            UiLanguage.English => "Language.English",
            _ => "Language.System"
        });
        var online = L(_app.Preferences.OnlineIcons switch
        {
            OnlineIconMode.OnDemand => "Diagnostics.OnDemand",
            OnlineIconMode.AutomaticExactMatches => "Diagnostics.Automatic",
            _ => "Diagnostics.Off"
        });
        return string.Format(LocalizationService.Current.ActiveCulture, L("Diagnostics.Template"),
            Environment.OSVersion.Version, Environment.Version, RuntimeInformation.OSArchitecture,
            RenderCapability.Tier >> 16, dpi.DpiScaleX * 100, appearance, language,
            _app.Preferences.Pins.Count, _app.Taskbars.LastStatus, online,
            L(_app.Preferences.OnlineIconConsentVersion >= Settings.CurrentOnlineIconConsentVersion
                ? "Diagnostics.Approved" : "Diagnostics.NotApproved"),
            _app.Store.FilePath);
    }
    private void UpdateDiagnostics()
    {
        Diagnostics.Text = DiagnosticText();
        Hotkeys.Text = LocalizationService.Current.IsKnownValue("Dock.HotkeysReady", _app.HotkeyStatus)
            ? L("Dock.HotkeysReady")
            : LocalizationService.Current.IsKnownValue("Dock.HotkeysUnavailable", _app.HotkeyStatus)
                ? L("Dock.HotkeysUnavailable")
                : _app.HotkeyStatus;
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(App.DataDirectory, "logs"); Directory.CreateDirectory(path); LaunchService.Open(path);
        }
        catch (Exception ex) { ShowFileError(ex); }
    }
    private void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(App.DataDirectory, "logs"); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "diagnose-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"), DiagnosticText());
            LaunchService.Open(path);
        }
        catch (Exception ex) { ShowFileError(ex); }
    }
    private void ShowFileError(Exception ex) { App.Log("File operation failed", ex); MessageBox.Show(this, ex.Message, L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Information); }
    private void Quit_Click(object sender, RoutedEventArgs e) => _app.RequestExit();
}
