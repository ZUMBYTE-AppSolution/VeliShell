using System.Net.NetworkInformation;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

/// <summary>
/// Optional, reversible shell companion. It never replaces Explorer. While it
/// is enabled, its own top-edge appbar reserves space through the Windows shell;
/// disabling it unregisters only that VeliShell-owned reservation.
/// </summary>
public partial class MenuBarWindow : Window
{
    private const uint AppBarCallbackMessage = NativeMethods.WmApp + 0x56;
    private readonly App _app;
    private readonly string? _displayDevice;
    private readonly ShellBackdropHost _backdrop;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _trayTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _trayCollapseTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private List<NativeWindow> _windows = [];
    private readonly BackgroundAppTracker _backgroundApps = BackgroundAppTracker.ForCurrentSession();
    private string _backgroundAppsSignature = "";
    private IReadOnlyList<BackgroundApp> _lastBackgroundApps = [];
    private IReadOnlyList<TrayAppIcon> _trayIcons = [];
    private string _traySignature = "";
    private bool _trayRefreshInProgress;
    private bool _refreshInProgress;
    private SteamClient? _steam;
    private string? _steamIconPath;
    private NativeWindow? _activeWindow;
    private HwndSource? _source;
    private nint _handle;
    private bool _workAreaReserved;
    private bool _closed;
    private bool _hidden;
    private ControlCenterWindow? _controlCenter;
    private AudioDevicesWindow? _audioDevices;
    private NotificationCenterWindow? _notificationCenter;
    private Window? _openPanel;
    private string? _lastNotifiedUpdateVersion;

    private static string L(string key) => LocalizationService.Current.Get(key);

    internal MenuBarWindow(App app) : this(app, null) { }

    internal MenuBarWindow(App app, string? displayDevice)
    {
        _app = app;
        _displayDevice = displayDevice;
        InitializeComponent();
        _backdrop = new ShellBackdropHost(this, MenuBody, 0);
        _app.PreferencesChanged += ApplyPreferences;
        if (_app.Themes is not null) _app.Themes.Changed += ApplyBackdrop;
        _app.UpdateStateChanged += HandleUpdateStateChanged;
        NotificationCenterService.Current.Changed += UpdateNotificationIndicator;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExToolWindow));
            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WindowHook);
            PositionBar();
        };
        Loaded += async (_, _) =>
        {
            ApplyPreferences();
            ApplyBackdrop();
            UpdateClockAndIndicators();
            await RefreshWindowsAsync();
            await RefreshTrayIconsAsync();
            _clockTimer.Start();
            _windowTimer.Start();
            _trayTimer.Start();
        };
        _clockTimer.Tick += (_, _) => UpdateClockAndIndicators();
        _windowTimer.Tick += async (_, _) => await RefreshWindowsAsync();
        _trayTimer.Tick += async (_, _) => await RefreshTrayIconsAsync();
        _trayCollapseTimer.Tick += (_, _) => TrayOverflowPopup.IsOpen = false;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_app.Preferences.MenuBarAutoHide && !IsMouseOver && _openPanel is null) SetHidden(true);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _clockTimer.Stop();
            _windowTimer.Stop();
            _trayTimer.Stop();
            _trayCollapseTimer.Stop();
            _hideTimer.Stop();
            ClosePanels();
            _app.PreferencesChanged -= ApplyPreferences;
            if (_app.Themes is not null) _app.Themes.Changed -= ApplyBackdrop;
            _backdrop.Dispose();
            _app.UpdateStateChanged -= HandleUpdateStateChanged;
            NotificationCenterService.Current.Changed -= UpdateNotificationIndicator;
            if (_handle != 0) _app.Taskbars.ReleaseMenuBar(_handle);
            _workAreaReserved = false;
            _source?.RemoveHook(WindowHook);
            _source = null;
        };
        HandleUpdateStateChanged();
    }

    internal bool WorkAreaReserved => _workAreaReserved;

    private void ApplyPreferences()
    {
        if (_closed) return;
        _backgroundAppsSignature = "\0";
        _traySignature = "\0";
        RenderTrayIcons();
        Topmost = _app.Preferences.MenuBarAlwaysOnTop;
        if (_openPanel is not null) _openPanel.Topmost = Topmost;
        PositionBar();
        SetHidden(false);
        if (_app.Preferences.MenuBarAutoHide && _openPanel is null) _hideTimer.Start();
    }

    private void ApplyBackdrop()
    {
        if (_handle == 0 || _closed) return;
        MenuBody.SetResourceReference(Border.BackgroundProperty,
            _backdrop.Apply() ? "MenuBarAcrylicSurface" : "MenuBarSurface");
    }

    private void PositionBar()
    {
        if (_handle == 0 || _closed) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        if (!NativeMethods.TryMonitorForDevice(_displayDevice, out var monitor))
        {
            _workAreaReserved = false;
            Visibility = Visibility.Hidden;
            return;
        }
        var area = monitor.Monitor;
        NativeMethods.SetWindowPos(_handle, 0, area.Left, area.Top,
            area.Right - area.Left, height, 0x0004 | 0x0010);
        _backdrop.Sync();
        var success = _workAreaReserved
            ? _app.Taskbars.UpdateMenuBar(_handle, height, out _)
            : _app.Taskbars.RegisterMenuBarOnDisplay(_handle, AppBarCallbackMessage, height,
                _displayDevice, out _);
        if (success)
        {
            _workAreaReserved = true;
            return;
        }

        _workAreaReserved = false;
        Visibility = Visibility.Hidden;
        App.Log("The VeliShell menu bar could not reserve its work area.");
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == AppBarCallbackMessage && wParam == NativeMethods.AbnPosChanged)
        {
            Dispatcher.BeginInvoke(new Action(PositionBar), DispatcherPriority.Background);
        }
        else if (message is 0x007E or 0x02E0) // WM_DISPLAYCHANGE / WM_DPICHANGED
        {
            Dispatcher.BeginInvoke(new Action(PositionBar), DispatcherPriority.Background);
        }
        return 0;
    }

    private async Task RefreshWindowsAsync()
    {
        if (_closed || _refreshInProgress) return;
        _refreshInProgress = true;
        try
        {
            var windows = await _app.ReadWindowsAsync();
            var (steam, background) = await Task.Run(() =>
                (SteamClientService.ReadRunning(), _backgroundApps.Update(windows)));
            if (_closed) return;
            _windows = windows;
            _lastBackgroundApps = background;
            RenderBackgroundApps(background);
            _steam = steam;
            if (steam is null)
            {
                _steamIconPath = null;
                SteamGlyph.OpacityMask = null;
            }
            else if (!string.Equals(_steamIconPath, steam.Executable, StringComparison.OrdinalIgnoreCase))
            {
                _steamIconPath = steam.Executable;
                var icon = SteamIconService.FromExecutable(steam.Executable);
                SteamGlyph.OpacityMask = icon is null ? null : new ImageBrush(icon) { Stretch = Stretch.Uniform };
            }
            SteamButton.Visibility = steam is not null && SteamGlyph.OpacityMask is not null &&
                                     !_trayIcons.Any(icon => string.Equals(icon.Executable, steam.Executable,
                                         StringComparison.OrdinalIgnoreCase))
                ? Visibility.Visible : Visibility.Collapsed;
            var foreground = NativeMethods.GetForegroundWindow();
            _activeWindow = VisibleWindows().FirstOrDefault(window => window.Handle == foreground);
            ActiveAppLabel.Text = _activeWindow is null
                ? L("Product.Name")
                : FriendlyProcessName(_activeWindow);
            var fullscreen = WindowCatalog.ForegroundIsFullscreenOnDisplay(_displayDevice);
            if (fullscreen) ClosePanels();
            Visibility = fullscreen || !_workAreaReserved ? Visibility.Hidden : Visibility.Visible;
        }
        catch (Exception exception)
        {
            App.Log("Menu bar window enumeration failed", exception);
        }
        finally { _refreshInProgress = false; }
    }

    private async Task RefreshTrayIconsAsync()
    {
        if (_closed || _trayRefreshInProgress) return;
        _trayRefreshInProgress = true;
        try
        {
            var icons = await Task.Run(TrayIconCatalog.Read);
            if (_closed) return;
            _trayIcons = icons;
            RenderTrayIcons();
            RenderBackgroundApps(_lastBackgroundApps);
            if (_steam is { } steam)
                SteamButton.Visibility = SteamGlyph.OpacityMask is not null &&
                    !icons.Any(icon => string.Equals(icon.Executable, steam.Executable,
                        StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error) { App.Log("Tray icon mirror refresh failed", error); }
        finally { _trayRefreshInProgress = false; }
    }

    private void RenderTrayIcons()
    {
        var maximumVisibleIcons = ActualWidth is > 0 and < 1100 ? 3 : 6;
        var hiddenIds = _app.Preferences.HiddenTrayIconIds.ToHashSet(StringComparer.Ordinal);
        var preferredIds = _app.Preferences.PreferredTrayIconIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        var orderedIcons = _trayIcons.OrderBy(icon => preferredIds.GetValueOrDefault(icon.Id, int.MaxValue))
            .ToArray();
        var signature = $"{maximumVisibleIcons}|" + string.Join("|", orderedIcons.Select(icon =>
            $"{icon.Id}:{icon.Name}:{icon.SnapshotHash}:{hiddenIds.Contains(icon.Id)}"));
        if (signature == _traySignature) return;
        _traySignature = signature;
        TrayIconsPanel.Children.Clear();
        TrayOverflowItemsPanel.Children.Clear();
        var visibleCount = 0;
        foreach (var icon in orderedIcons)
        {
            var hidden = hiddenIds.Contains(icon.Id) || visibleCount >= maximumVisibleIcons;
            var button = CreateTrayIconButton(icon, hidden);
            if (button is null) continue;
            if (hidden) TrayOverflowItemsPanel.Children.Add(button);
            else
            {
                TrayIconsPanel.Children.Add(button);
                visibleCount++;
            }
        }
        TrayChevronButton.Visibility = TrayOverflowItemsPanel.Children.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (TrayChevronButton.Visibility != Visibility.Visible) TrayOverflowPopup.IsOpen = false;
    }

    private Button? CreateTrayIconButton(TrayAppIcon icon, bool hidden)
    {
        try
        {
            using var stream = new MemoryStream(icon.Snapshot, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            var button = new Button
            {
                Style = (Style)FindResource("MenuBarButton"),
                Padding = new Thickness(6, 4, 6, 4),
                ToolTip = hidden ? $"{icon.Name} · {L("MenuBar.TrayShow")}" :
                    string.Format(LocalizationService.Current.ActiveCulture,
                        L("MenuBar.TrayRunning"), icon.Name),
                Content = MonochromeIcon(image)
            };
            if (hidden) button.Width = button.Height = 32;
            System.Windows.Automation.AutomationProperties.SetName(button, icon.Name);
            button.Click += (_, _) =>
            {
                TrayOverflowPopup.IsOpen = false;
                if (File.Exists(icon.Executable)) LaunchService.Open(icon.Executable);
            };
            button.PreviewMouseRightButtonUp += (_, args) =>
            {
                args.Handled = true;
                ShowTrayIconMenu(button, icon, hidden);
            };
            button.ContextMenuOpening += (_, args) =>
            {
                args.Handled = true;
                ShowTrayIconMenu(button, icon, hidden);
            };
            return button;
        }
        catch (Exception error) when (error is NotSupportedException or IOException or ArgumentException or
                                      InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            App.Log("Could not render a cached Windows notification icon", error);
            return null;
        }
    }

    private Border MonochromeIcon(ImageSource source)
    {
        var glyph = new Border
        {
            Width = 17, Height = 17,
            OpacityMask = new ImageBrush(source) { Stretch = Stretch.Uniform },
            SnapsToDevicePixels = true
        };
        glyph.SetResourceReference(Border.BackgroundProperty, "TextPrimary");
        RenderOptions.SetBitmapScalingMode(glyph, BitmapScalingMode.HighQuality);
        return glyph;
    }

    private void ShowTrayIconMenu(FrameworkElement anchor, TrayAppIcon icon, bool hidden)
    {
        if (hidden)
        {
            ToggleTrayIcon(icon.Id, hide: false);
            TrayOverflowPopup.IsOpen = false;
            return;
        }
        var menu = NewMenu(anchor);
        var observedApp = _lastBackgroundApps.FirstOrDefault(app =>
            string.Equals(app.Executable, icon.Executable, StringComparison.OrdinalIgnoreCase));
        if (observedApp is not null) AddBackgroundAppActions(menu, observedApp);
        else
        {
            Add(menu, L("MenuBar.TrayOpen"), () =>
            {
                if (File.Exists(icon.Executable)) LaunchService.Open(icon.Executable);
            }, File.Exists(icon.Executable));
            var folder = Path.GetDirectoryName(icon.Executable);
            Add(menu, L("MenuBar.BackgroundAppFolder"), () =>
            {
                if (folder is not null && Directory.Exists(folder)) LaunchService.Open(folder);
            }, folder is not null && Directory.Exists(folder));
        }
        menu.Items.Add(new Separator());
        Add(menu, L("MenuBar.TrayHide"), () => ToggleTrayIcon(icon.Id, hide: true));
        menu.IsOpen = true;
    }

    private void ToggleTrayIcon(string id, bool hide)
    {
        _app.UpdatePreferences(settings =>
        {
            settings.HiddenTrayIconIds.RemoveAll(candidate =>
                string.Equals(candidate, id, StringComparison.Ordinal));
            settings.PreferredTrayIconIds.RemoveAll(candidate =>
                string.Equals(candidate, id, StringComparison.Ordinal));
            if (hide) settings.HiddenTrayIconIds.Add(id);
            else settings.PreferredTrayIconIds.Insert(0, id);
        });
    }

    private void TrayChevron_Click(object sender, RoutedEventArgs e) =>
        TrayOverflowPopup.IsOpen = !TrayOverflowPopup.IsOpen;

    private void TrayOverflow_Opened(object sender, EventArgs e)
    {
        _hideTimer.Stop();
        SetHidden(false);
        _trayCollapseTimer.Stop();
        _trayCollapseTimer.Start();
    }

    private void TrayOverflow_Closed(object sender, EventArgs e)
    {
        _trayCollapseTimer.Stop();
        if (_app.Preferences.MenuBarAutoHide && !IsMouseOver) _hideTimer.Start();
    }

    private void MenuBar_Deactivated(object sender, EventArgs e) => TrayOverflowPopup.IsOpen = false;

    private void RenderBackgroundApps(IReadOnlyList<BackgroundApp> apps)
    {
        var totalIconBudget = ActualWidth is > 0 and < 1100 ? 4 : 6;
        var maximumVisibleIcons = Math.Max(1, totalIconBudget - TrayIconsPanel.Children.Count);
        var trayExecutables = _trayIcons.Select(icon => icon.Executable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherApps = apps.Where(app => !trayExecutables.Contains(app.Executable)).ToArray();
        var signature = $"{maximumVisibleIcons}|" + string.Join("|", otherApps.Select(app =>
            $"{app.ProcessId}:{app.StartTimeUtcTicks}:{app.Name}:{app.IconTarget}"));
        if (signature == _backgroundAppsSignature) return;
        _backgroundAppsSignature = signature;
        BackgroundAppsPanel.Children.Clear();
        var overflow = new List<BackgroundApp>();
        var visibleCount = 0;

        foreach (var app in otherApps)
        {
            if (visibleCount >= maximumVisibleIcons)
            {
                overflow.Add(app);
                continue;
            }
            try
            {
                // Use the actual installed app or web-app shortcut icon. No
                // synthetic menu-bar artwork is generated or bundled.
                var icon = IconService.ForOriginalWindowsIcon("app", app.IconTarget);
                if (icon is null)
                {
                    overflow.Add(app);
                    continue;
                }
                var button = new Button
                {
                    Style = (Style)FindResource("MenuBarButton"),
                    Padding = new Thickness(6, 4, 6, 4),
                    ToolTip = string.Format(LocalizationService.Current.ActiveCulture,
                        L("MenuBar.BackgroundAppRunning"), app.Name),
                    Content = MonochromeIcon(icon)
                };
                System.Windows.Automation.AutomationProperties.SetName(button, app.Name);
                button.Click += (_, _) => OpenBackgroundApp(app);
                button.MouseRightButtonUp += (_, args) =>
                {
                    args.Handled = true;
                    ShowBackgroundAppMenu(button, app);
                };
                button.ContextMenuOpening += (_, args) =>
                {
                    args.Handled = true;
                    ShowBackgroundAppMenu(button, app);
                };
                BackgroundAppsPanel.Children.Add(button);
                visibleCount++;
            }
            catch (Exception exception)
            {
                App.Log("Could not show a background app icon", exception);
                overflow.Add(app);
            }
        }

        if (overflow.Count == 0) return;
        var more = new Button
        {
            Style = (Style)FindResource("MenuBarButton"),
            Content = $"+{overflow.Count}",
            ToolTip = L("MenuBar.MoreBackgroundApps")
        };
        System.Windows.Automation.AutomationProperties.SetName(more, L("MenuBar.MoreBackgroundApps"));
        more.Click += (_, _) =>
        {
            var menu = NewMenu(more);
            foreach (var app in overflow)
            {
                var item = new MenuItem { Header = app.Name };
                AddBackgroundAppActions(item, app);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        BackgroundAppsPanel.Children.Add(more);
    }

    private void ShowBackgroundAppMenu(FrameworkElement anchor, BackgroundApp app)
    {
        var menu = NewMenu(anchor);
        AddBackgroundAppActions(menu, app);
        menu.IsOpen = true;
    }

    private void AddBackgroundAppActions(ItemsControl menu, BackgroundApp app)
    {
        var running = _backgroundApps.IsStillRunning(app);
        Add(menu, L("MenuBar.BackgroundAppOpen"), () => OpenBackgroundApp(app),
            running && File.Exists(app.LaunchTarget));
        var folder = Path.GetDirectoryName(app.LaunchTarget);
        Add(menu, L("MenuBar.BackgroundAppFolder"), () =>
        {
            if (folder is not null && Directory.Exists(folder)) LaunchService.Open(folder);
        }, folder is not null && Directory.Exists(folder));
        menu.Items.Add(new Separator());
        var canClose = running && BackgroundAppCommands.CanRequestClose(app);
        var quit = Add(menu, L("MenuBar.BackgroundAppQuit"), () => QuitBackgroundApp(app), canClose);
        if (running && !canClose)
        {
            quit.ToolTip = L("MenuBar.BackgroundAppNoCloseWindow");
            ToolTipService.SetShowOnDisabled(quit, true);
        }
        Add(menu, L("MenuBar.BackgroundAppForceQuit"), () => _ = ForceQuitBackgroundAppAsync(app), running);
    }

    private void QuitBackgroundApp(BackgroundApp app)
    {
        var result = BackgroundAppCommands.RequestClose(app);
        if (result == BackgroundAppCommandResult.Requested) _ = RefreshWindowsAsync();
        else ShowBackgroundAppCommandResult(app, result);
    }

    private async Task ForceQuitBackgroundAppAsync(BackgroundApp app)
    {
        var question = string.Format(LocalizationService.Current.ActiveCulture,
            L("MenuBar.BackgroundAppForceQuitConfirm"), app.Name, app.ProcessId);
        if (MessageBox.Show(this, question, app.Name, MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var result = await Task.Run(() => BackgroundAppCommands.ForceQuit(app));
        if (result == BackgroundAppCommandResult.Requested) await RefreshWindowsAsync();
        else ShowBackgroundAppCommandResult(app, result);
    }

    private void ShowBackgroundAppCommandResult(BackgroundApp app, BackgroundAppCommandResult result)
    {
        var key = result switch
        {
            BackgroundAppCommandResult.NotRunning => "MenuBar.BackgroundAppNotRunning",
            BackgroundAppCommandResult.NoCloseWindow => "MenuBar.BackgroundAppNoCloseWindow",
            _ => "MenuBar.BackgroundAppActionFailed"
        };
        MessageBox.Show(this, L(key), app.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        _ = RefreshWindowsAsync();
    }

    private void OpenBackgroundApp(BackgroundApp app)
    {
        if (!_backgroundApps.IsStillRunning(app) || !File.Exists(app.LaunchTarget))
        {
            _ = RefreshWindowsAsync();
            return;
        }
        LaunchService.Open(app.LaunchTarget);
    }

    private void UpdateClockAndIndicators()
    {
        ClockLabel.Text = DateTime.Now.ToString("ddd d MMM  HH:mm", LocalizationService.Current.ActiveCulture);
        var online = NetworkInterface.GetIsNetworkAvailable();
        NetworkButton.Opacity = online ? 1 : 0.52;
        if (GetSystemPowerStatus(out var status) && status.BatteryLifePercent <= 100)
        {
            PowerLabel.Text = $"{status.BatteryLifePercent}%";
            PowerButton.Visibility = Visibility.Visible;
        }
        else
        {
            PowerLabel.Text = "";
            PowerButton.Visibility = Visibility.Collapsed;
        }
    }

    private void HandleUpdateStateChanged()
    {
        if (_displayDevice is null && _app.UpdateState == UpdateUiState.Available && _app.AvailableUpdateVersion is { } version)
        {
            var versionText = version.ToString();
            if (!string.Equals(_lastNotifiedUpdateVersion, versionText, StringComparison.Ordinal))
            {
                _lastNotifiedUpdateVersion = versionText;
                NotificationCenterService.Current.Publish(
                    $"update-{versionText}",
                    L("Notifications.UpdateTitle"),
                    string.Format(LocalizationService.Current.ActiveCulture,
                        L("Notifications.UpdateMessage"), versionText),
                    VeliShellNotificationKind.Update);
            }
        }
        UpdateNotificationIndicator();
    }

    private void UpdateNotificationIndicator()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateNotificationIndicator);
            return;
        }
        var unread = NotificationCenterService.Current.UnreadCount;
        NotificationBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationBadgeLabel.Text = unread > 9 ? "9+" : unread.ToString(LocalizationService.Current.ActiveCulture);
        NotificationCenterButton.ToolTip = unread > 0
            ? string.Format(LocalizationService.Current.ActiveCulture, L("MenuBar.NotificationUnread"), unread)
            : L("MenuBar.NotificationCenter");
    }

    private static string FriendlyProcessName(NativeWindow window)
    {
        if (window.WebApp is { } webApp) return webApp.Name;
        if (!string.IsNullOrWhiteSpace(window.DisplayName)) return window.DisplayName;
        if (window.ProcessName.StartsWith("pid-", StringComparison.Ordinal)) return window.Title;
        var value = window.ProcessName.Replace('_', ' ').Trim();
        return value.Length == 0 ? window.Title : value;
    }

    private ContextMenu NewMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -4
        };
        menu.Closed += (_, _) =>
        {
            if (_app.Preferences.MenuBarAutoHide && !IsMouseOver) _hideTimer.Start();
        };
        _hideTimer.Stop();
        SetHidden(false);
        return menu;
    }

    private static MenuItem Add(ItemsControl menu, string label, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    private void AppMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(AppMenuButton);
        Add(menu, L("Search.Open"), _app.ShowSearch);
        Add(menu, L("MenuBar.Settings"), _app.ShowPreferences);
        Add(menu, L("MenuBar.CheckUpdates"), () => _ = _app.CheckForUpdatesAsync(true, this));
        menu.Items.Add(new Separator());
        var forceQuit = new MenuItem { Header = L("MenuBar.ForceQuitApps") };
        var running = _backgroundApps.VisibleApps(_windows)
            .Concat(_lastBackgroundApps)
            .Concat(SteamForceQuitEntry() is { } steamEntry ? [steamEntry] : [])
            .DistinctBy(app => app.ProcessId)
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(40).ToArray();
        foreach (var app in running)
            Add(forceQuit, app.Name, () => _ = ForceQuitBackgroundAppAsync(app));
        if (running.Length == 0)
            Add(forceQuit, L("MenuBar.NoForceQuitApps"), () => { }, enabled: false);
        menu.Items.Add(forceQuit);
        Add(menu, L("MenuBar.Run"), ShowRunDialog);
        menu.Items.Add(new Separator());
        Add(menu, L("MenuBar.Lock"), () =>
        {
            if (!NativeMethods.LockWorkStation())
                MessageBox.Show(this, L("MenuBar.SystemActionFailed"), L("Common.ErrorTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        });
        Add(menu, L("MenuBar.SignOut"), () => _ = ConfirmSystemActionAsync("MenuBar.SignOut", "/l"));
        Add(menu, L("MenuBar.Restart"), () => _ = ConfirmSystemActionAsync("MenuBar.Restart", "/r /t 0"));
        Add(menu, L("MenuBar.Shutdown"), () => _ = ConfirmSystemActionAsync("MenuBar.Shutdown", "/s /t 0"));
        menu.Items.Add(new Separator());
        Add(menu, L("MenuBar.RestoreTaskbar"),
            () => _ = _app.SetTaskbarHiddenAsync(false), _app.Preferences.HideTaskbar || _app.Taskbars.IsHidden);
        menu.Items.Add(new Separator());
        Add(menu, L("MenuBar.Quit"), () => _app.RequestExit());
        menu.IsOpen = true;
    }

    private BackgroundApp? SteamForceQuitEntry()
    {
        var steam = SteamClientService.ReadRunning();
        if (steam is null) return null;
        try
        {
            using var process = Process.GetProcessById(steam.ProcessId);
            using var current = Process.GetCurrentProcess();
            if (process.HasExited || process.SessionId != current.SessionId) return null;
            return new BackgroundApp(steam.ProcessId, process.StartTime.ToUniversalTime().Ticks,
                L("MenuBar.Steam"), steam.Executable, steam.Executable, steam.Executable);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void ShowRunDialog()
    {
        var dialog = new RunWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var (target, arguments) = ParseRunCommand(
            Environment.ExpandEnvironmentVariables(dialog.Command));
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target)
            {
                Arguments = arguments,
                UseShellExecute = true
            });
            if (process is null) throw new InvalidOperationException("Windows did not open the target.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          FileNotFoundException or ArgumentException or NotSupportedException or
                                          System.Security.SecurityException)
        {
            App.Log("Run target could not be opened", exception);
            MessageBox.Show(this, L("MenuBar.RunFailed"), L("Common.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static (string Target, string Arguments) ParseRunCommand(string command)
    {
        command = command.Trim();
        if (File.Exists(command) || Directory.Exists(command) ||
            (Uri.TryCreate(command, UriKind.Absolute, out var uri) && !uri.IsFile))
            return (command, "");
        if (command.StartsWith('"'))
        {
            var closing = command.IndexOf('"', 1);
            if (closing > 1)
                return (command[1..closing], command[(closing + 1)..].Trim());
        }
        // An unquoted existing path can contain spaces. Prefer the longest
        // existing prefix before falling back to a bare executable name.
        for (var index = command.Length - 1; index > 0; index--)
            if (char.IsWhiteSpace(command[index]))
            {
                var prefix = command[..index].Trim();
                if (File.Exists(prefix) || Directory.Exists(prefix))
                    return (prefix, command[(index + 1)..].Trim());
            }
        var split = command.IndexOfAny([' ', '\t']);
        return split > 0
            ? (command[..split], command[(split + 1)..].Trim())
            : (command, "");
    }

    private async Task ConfirmSystemActionAsync(string labelKey, string arguments)
    {
        var label = L(labelKey).TrimEnd(' ', '…');
        var question = string.Format(LocalizationService.Current.ActiveCulture,
            L("MenuBar.SystemActionConfirm"), label);
        if (MessageBox.Show(this, question, L("Product.Name"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _app.PerformSessionActionAsync(arguments);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _app.ShowSearch();

    private void Steam_Click(object sender, RoutedEventArgs e) =>
        SendSteamCommand(SteamClientCommand.Open);

    private void Steam_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowSteamMenu();
    }

    private void Steam_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        ShowSteamMenu();
    }

    private void ShowSteamMenu()
    {
        if (_steam is null) return;
        var menu = NewMenu(SteamButton);
        Add(menu, L("MenuBar.SteamOpen"), () => SendSteamCommand(SteamClientCommand.Open));
        Add(menu, L("MenuBar.SteamSettings"), () => SendSteamCommand(SteamClientCommand.Settings));
        menu.Items.Add(new Separator());
        Add(menu, L("MenuBar.SteamQuit"), () =>
        {
            if (MessageBox.Show(this, L("MenuBar.SteamQuitConfirm"), L("MenuBar.Steam"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                SendSteamCommand(SteamClientCommand.Quit);
        });
        menu.IsOpen = true;
    }

    private void SendSteamCommand(SteamClientCommand command)
    {
        if (_steam is not { } steam) return;
        try { SteamClientService.Send(steam, command); }
        catch (Exception exception)
        {
            App.Log("Steam menu action failed", exception);
            MessageBox.Show(this, L("MenuBar.SteamActionFailed"), L("MenuBar.Steam"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ActiveApp_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(ActiveAppButton);
        if (_activeWindow is null)
        {
            Add(menu, L("MenuBar.NoActiveWindow"), () => { }, enabled: false);
        }
        else
        {
            var matching = VisibleWindows().Where(window => string.Equals(
                window.Executable.Length > 0 ? window.Executable : window.ProcessName,
                _activeWindow.Executable.Length > 0 ? _activeWindow.Executable : _activeWindow.ProcessName,
                StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
            foreach (var window in matching)
                Add(menu, TrimTitle(window.Title), () => WindowCatalog.Activate(window.Handle));
        }
        menu.IsOpen = true;
    }

    private void WindowsMenu_Click(object sender, RoutedEventArgs e)
    {
        var anchor = (FrameworkElement)sender;
        var menu = NewMenu(anchor);
        var foreground = NativeMethods.GetForegroundWindow();
        foreach (var window in VisibleWindows().Take(24))
        {
            var prefix = window.Handle == foreground ? "•  " : "   ";
            Add(menu, prefix + TrimTitle(window.Title), () => WindowCatalog.Activate(window.Handle));
        }
        if (menu.Items.Count == 0) Add(menu, L("MenuBar.NoWindows"), () => { }, enabled: false);
        menu.IsOpen = true;
    }

    private static string TrimTitle(string title) => title.Length > 72 ? title[..69] + "…" : title;

    private IReadOnlyList<NativeWindow> VisibleWindows() =>
        _app.Preferences.MenuBarOnAllDisplays && _app.Preferences.RunningAppsPerDisplay
            ? WindowCatalog.OnDisplay(_windows, _displayDevice)
            : _windows;

    private void Network_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:network-status");
    private void Sound_Click(object sender, RoutedEventArgs e) => ShowAudioDevices();
    private void Power_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:batterysaver");
    private void Clock_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-clock:");

    private void ControlCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_controlCenter is { IsVisible: true })
        {
            _controlCenter.CloseOnce();
            return;
        }

        ClosePanels();
        _hideTimer.Stop();
        SetHidden(false);
        var panel = new ControlCenterWindow();
        _controlCenter = panel;
        _openPanel = panel;
        panel.OpenAudioDevicesRequested += ShowAudioDevices;
        panel.Closed += (_, _) => PanelClosed(panel);
        panel.ShowRelativeTo(ControlCenterButton, this, Topmost);
    }

    private void ShowAudioDevices()
    {
        if (_audioDevices is { IsVisible: true })
        {
            _audioDevices.CloseOnce();
            return;
        }

        ClosePanels();
        _hideTimer.Stop();
        SetHidden(false);
        var panel = new AudioDevicesWindow();
        _audioDevices = panel;
        _openPanel = panel;
        panel.Closed += (_, _) => PanelClosed(panel);
        panel.ShowRelativeTo(SoundButton, this, Topmost);
    }

    private void NotificationCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_notificationCenter is { IsVisible: true })
        {
            _notificationCenter.CloseOnce();
            return;
        }

        ClosePanels();
        _hideTimer.Stop();
        SetHidden(false);
        var panel = new NotificationCenterWindow(NotificationCenterService.Current);
        _notificationCenter = panel;
        _openPanel = panel;
        panel.Closed += (_, _) => PanelClosed(panel);
        panel.ShowRelativeTo(NotificationCenterButton, this, Topmost);
    }

    private void PanelClosed(Window panel)
    {
        if (ReferenceEquals(_controlCenter, panel)) _controlCenter = null;
        if (ReferenceEquals(_audioDevices, panel)) _audioDevices = null;
        if (ReferenceEquals(_notificationCenter, panel)) _notificationCenter = null;
        if (ReferenceEquals(_openPanel, panel)) _openPanel = null;
        if (!_closed && _app.Preferences.MenuBarAutoHide && !IsMouseOver) _hideTimer.Start();
    }

    private void ClosePanels()
    {
        TrayOverflowPopup.IsOpen = false;
        var panel = _openPanel;
        _openPanel = null;
        if (panel is { IsVisible: true })
        {
            if (panel is ITransientPanel transient) transient.CloseOnce();
            else panel.Close();
        }
        _controlCenter = null;
        _audioDevices = null;
        _notificationCenter = null;
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hideTimer.Stop();
        SetHidden(false);
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_app.Preferences.MenuBarAutoHide && _openPanel is null) _hideTimer.Start();
    }

    private void SetHidden(bool hidden)
    {
        _hidden = hidden && _app.Preferences.MenuBarAutoHide;
        RevealHandle.Visibility = _hidden ? Visibility.Visible : Visibility.Collapsed;
        MenuBody.IsHitTestVisible = !_hidden;
        var target = _hidden ? -29d : 0d;
        if (_app.Preferences.ReducedMotion)
        {
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Slide.Y = target;
        }
        else
        {
            Slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        if (_app.Preferences.ReducedMotion) _backdrop.Sync();
        else _backdrop.FollowMotion(150);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
