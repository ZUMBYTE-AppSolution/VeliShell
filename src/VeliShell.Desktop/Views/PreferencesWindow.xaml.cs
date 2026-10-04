using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
    private static string L(string key) => LocalizationService.Current.Get(key);
    public PreferencesWindow(App app)
    {
        _app = app;
        LocalizationService.Current.Apply(_app.Preferences.Language);
        InitializeComponent();
        _app.PreferencesChanged += SyncUi;
        _app.UpdateStateChanged += UpdateUpdateStatus;
        _app.Themes.Changed += UpdateThemeStatus;
        Loaded += (_, _) => { SyncUi(); UpdateDiagnostics(); };
        Closed += (_, _) =>
        {
            _app.PreferencesChanged -= SyncUi;
            _app.UpdateStateChanged -= UpdateUpdateStatus;
            _app.Themes.Changed -= UpdateThemeStatus;
        };
        SyncUi();
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
        SizeSlider.Value = s.IconSize;
        SizeLabel.Text = $"{s.IconSize:0} DIP";
        ReducedMotionSwitch.IsChecked = s.ReducedMotion;
        MagnificationSwitch.IsChecked = s.Magnification;
        MagnificationSwitch.IsEnabled = !s.ReducedMotion;
        RunningSwitch.IsChecked = s.ShowRunningApps;
        AutoHideSwitch.IsChecked = s.AutoHide;
        TopmostSwitch.IsChecked = s.AlwaysOnTop;
        HideTaskbarSwitch.IsChecked = s.HideTaskbar;
        try
        {
            var startup = StartupRegistrationService.GetStatus();
            UserStartupSwitch.IsChecked = startup.UserLoginEnabled;
            StartupStatus.Text = L(startup.UserLoginEnabled ? "Startup.StatusEnabled" : "Startup.StatusDisabled");
            ServiceStatus.Text = L(startup.BackgroundServiceInstalled ? "Startup.ServiceInstalled" : "Startup.ServiceUnavailable");
        }
        catch (Exception ex)
        {
            App.Log("Could not query startup registration", ex);
            UserStartupSwitch.IsChecked = false;
            StartupStatus.Text = L("Common.ServiceError") + " " + ex.Message;
            ServiceStatus.Text = L("Startup.ServiceUnavailable");
        }
        TaskbarStatus.Text = _app.Taskbars.LastStatus;
        var hasConsent = s.OnlineIconConsentVersion >= Settings.CurrentOnlineIconConsentVersion;
        var hasLegacyIconKey = ApiKeyStore.HasMacOsIconsKey;
        OnlineIconSwitch.IsChecked = s.OnlineIcons == OnlineIconMode.AutomaticExactMatches;
        OnlineIconSwitch.IsEnabled = !_iconSearchBusy;
        FindOnlineIconsButton.IsEnabled = !_iconSearchBusy;
        OnlineIconStatus.Text = hasConsent
            ? L("Apps.OnlineEnabled")
            : L("Apps.OnlineDisabled");
        LegacyIconKeyStatus.Text = hasLegacyIconKey
            ? L("Apps.LegacyKeyPresent")
            : L("Apps.LegacyKeyAbsent");
        RemoveLegacyIconKeyButton.IsEnabled = hasLegacyIconKey;
        UpdateUpdateStatus();
        UpdateThemeStatus();
        RenderPins();
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
        AppearancePage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        DockPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        WindowsPage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        InfoPage.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch
        {
            1 => L("Nav.Dock"),
            2 => L("Nav.Apps"),
            3 => L("Nav.Windows"),
            4 => L("Nav.Info"),
            _ => L("Nav.Appearance")
        };
        PageScroll.ScrollToTop();
        if (page == 4) UpdateDiagnostics();
    }
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
    private void UpdateUpdateStatus()
    {
        if (UpdateStatus is null || CheckUpdatesButton is null) return;
        UpdateStatus.Text = _app.GetUpdateStatusText();
        CheckUpdatesButton.IsEnabled = _app.UpdateState != UpdateUiState.Checking;
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
    private void UserStartup_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enable = UserStartupSwitch.IsChecked == true;
        try
        {
            StartupRegistrationService.SetUserLoginEnabled(enable);
            _app.UpdatePreferences(s => s.Startup = enable ? StartupMode.UserLogin : StartupMode.Disabled);
        }
        catch (Exception ex)
        {
            App.Log("Could not change startup registration", ex);
            UserStartupSwitch.IsChecked = !enable;
            MessageBox.Show(this, L("Common.ServiceError") + "\n\n" + ex.Message,
                L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SyncUi();
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
    private void RemoveIconKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApiKeyStore.DeleteMacOsIconsKey();
            SyncUi();
        }
        catch (Exception ex)
        {
            App.Log("Could not remove the macOSicons API key", ex);
            MessageBox.Show(this, L("Apps.RemoveKeyFailed"),
                L("Common.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void OnlineIcons_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Consent updates and re-syncs the settings window. Preserve the state
        // the user actually clicked so the first opt-in also enables automatic
        // matching instead of being reset to the on-demand default.
        var enableAutomaticMatching = OnlineIconSwitch.IsChecked == true;
        if (enableAutomaticMatching && !EnsureOnlineIconConsent())
        {
            OnlineIconSwitch.IsChecked = false;
            return;
        }
        _app.UpdatePreferences(s => s.OnlineIcons = enableAutomaticMatching
            ? OnlineIconMode.AutomaticExactMatches
            : OnlineIconMode.OnDemand);
    }
    private async void FindOnlineIcons_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureOnlineIconConsent()) return;
        await FindOnlineIconsAsync(_app.Preferences.Pins);
    }
    private async Task FindOnlineIconsAsync(IEnumerable<Pin> candidates)
    {
        var eligiblePins = candidates.Where(MacOsIconGalleryService.IsEligibleAppPin).ToList();
        if (eligiblePins.Count == 0)
        {
            OnlineIconStatus.Text = L("Apps.NoEligiblePins");
            return;
        }
        var pins = eligiblePins
            .Where(pin => pin.Icon is null || MacOsIconGalleryService.TryLoad(pin.Icon) is null)
            .ToList();
        if (pins.Count == 0)
        {
            OnlineIconStatus.Text = L("Apps.AllIconsValid");
            return;
        }

        _iconSearchBusy = true;
        FindOnlineIconsButton.IsEnabled = false;
        OnlineIconSwitch.IsEnabled = false;
        OnlineIconStatus.Text = string.Format(LocalizationService.Current.ActiveCulture, L("Apps.Searching"), pins.Count);
        try
        {
            var result = await _app.FindAndApplyOnlineIconsAsync(pins);
            OnlineIconStatus.Text =
                string.Format(LocalizationService.Current.ActiveCulture, L("Apps.SearchResult"), result.Applied, result.NoMatch, result.Failed) +
                (result.Failed > 0 && !string.IsNullOrWhiteSpace(result.Message) ? " " + result.Message : "");
            RenderPins();
        }
        catch (OperationCanceledException)
        {
            OnlineIconStatus.Text = L("Apps.SearchCanceled");
        }
        finally
        {
            _iconSearchBusy = false;
            FindOnlineIconsButton.IsEnabled = true;
            OnlineIconSwitch.IsEnabled = true;
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
    private void OpenIconGallery_Click(object sender, RoutedEventArgs e) =>
        LaunchService.Open("https://www.macosicongallery.com/");
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
            var row = new Grid { Margin = new Thickness(2, 6, 2, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(37) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Image { Source = IconService.For(pin.Id, pin.Target, pin.Icon), Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left });
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            var attribution = MacOsIconGalleryService.TryGetAttribution(pin.Icon);
            var displayName = LocalizationService.Current.DisplayPinName(pin);
            labels.Children.Add(new TextBlock { Text = displayName, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = attribution is null ? pin.Target : pin.Target + "\n" + attribution.Text });
            if (attribution is not null)
            {
                var credit = new TextBlock { Text = attribution.Text, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
                credit.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                labels.Children.Add(credit);
            }
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            void Add(string glyph, string label, Action action)
            {
                var b = new Button { Content = glyph, Width = 26, Height = 25, Padding = new Thickness(0), Margin = new Thickness(2,0,0,0), ToolTip = label };
                AutomationProperties.SetName(b, label + ": " + displayName);
                b.Click += (_, _) => action(); buttons.Children.Add(b);
            }
            Add("‹", L("Common.MoveLeft"), () => _app.Dock.MovePin(pin.Id, -1));
            Add("›", L("Common.MoveRight"), () => _app.Dock.MovePin(pin.Id, 1));
            if (pin.Icon is not null)
                Add("↺", L("Common.RemoveOnlineIcon"), () => _app.UpdatePreferences(s =>
                {
                    var index = s.Pins.FindIndex(p => p.Id == pin.Id);
                    if (index >= 0) s.Pins[index] = s.Pins[index] with { Icon = null };
                }));
            Add("×", L("Common.Remove"), () => _app.UpdatePreferences(s => s.Pins.RemoveAll(p => p.Id == pin.Id)));
            Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
            PinList.Children.Add(row);
        }
        if (PinList.Children.Count == 0) PinList.Children.Add(new TextBlock { Text = L("Common.None"), Margin = new Thickness(8,12,8,12) });
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
            L(ApiKeyStore.HasMacOsIconsKey ? "Diagnostics.LegacyPresent" : "Diagnostics.LegacyAbsent"),
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
