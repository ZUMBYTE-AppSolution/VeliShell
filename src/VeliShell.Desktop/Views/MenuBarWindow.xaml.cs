using System.Net.NetworkInformation;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private List<NativeWindow> _windows = [];
    private readonly BackgroundAppTracker _backgroundApps = BackgroundAppTracker.ForCurrentSession();
    private string _backgroundAppsSignature = "";
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

    internal MenuBarWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _app.PreferencesChanged += ApplyPreferences;
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
            UpdateClockAndIndicators();
            await RefreshWindowsAsync();
            _clockTimer.Start();
            _windowTimer.Start();
        };
        _clockTimer.Tick += (_, _) => UpdateClockAndIndicators();
        _windowTimer.Tick += async (_, _) => await RefreshWindowsAsync();
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
            _hideTimer.Stop();
            ClosePanels();
            _app.PreferencesChanged -= ApplyPreferences;
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
        Topmost = _app.Preferences.MenuBarAlwaysOnTop;
        if (_openPanel is not null) _openPanel.Topmost = Topmost;
        PositionBar();
        SetHidden(false);
        if (_app.Preferences.MenuBarAutoHide && _openPanel is null) _hideTimer.Start();
    }

    private void PositionBar()
    {
        if (_handle == 0 || _closed) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        var success = _workAreaReserved
            ? _app.Taskbars.UpdateMenuBar(_handle, height, out _)
            : _app.Taskbars.RegisterMenuBar(_handle, AppBarCallbackMessage, height, out _);
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
            var (windows, steam, background) = await Task.Run(() =>
            {
                var windows = WindowCatalog.Read();
                return (windows, SteamClientService.ReadRunning(), _backgroundApps.Update(windows));
            });
            if (_closed) return;
            _windows = windows;
            RenderBackgroundApps(background);
            _steam = steam;
            if (steam is null)
            {
                _steamIconPath = null;
                SteamIcon.Source = null;
            }
            else if (!string.Equals(_steamIconPath, steam.Executable, StringComparison.OrdinalIgnoreCase))
            {
                _steamIconPath = steam.Executable;
                SteamIcon.Source = SteamIconService.FromExecutable(steam.Executable);
            }
            SteamButton.Visibility = steam is not null && SteamIcon.Source is not null
                ? Visibility.Visible : Visibility.Collapsed;
            var foreground = NativeMethods.GetForegroundWindow();
            _activeWindow = windows.FirstOrDefault(window => window.Handle == foreground);
            ActiveAppLabel.Text = _activeWindow is null
                ? L("Product.Name")
                : FriendlyProcessName(_activeWindow);
            var fullscreen = WindowCatalog.ForegroundIsFullscreenOnPrimary();
            if (fullscreen) ClosePanels();
            Visibility = fullscreen || !_workAreaReserved ? Visibility.Hidden : Visibility.Visible;
        }
        catch (Exception exception)
        {
            App.Log("Menu bar window enumeration failed", exception);
        }
        finally { _refreshInProgress = false; }
    }

    private void RenderBackgroundApps(IReadOnlyList<BackgroundApp> apps)
    {
        const int maximumVisibleIcons = 6;
        var signature = string.Join("|", apps.Select(app =>
            $"{app.ProcessId}:{app.StartTimeUtcTicks}:{app.Name}:{app.IconTarget}"));
        if (signature == _backgroundAppsSignature) return;
        _backgroundAppsSignature = signature;
        BackgroundAppsPanel.Children.Clear();
        var overflow = new List<BackgroundApp>();
        var visibleCount = 0;

        foreach (var app in apps)
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
                    Content = new Image
                    {
                        Source = icon,
                        Width = 17,
                        Height = 17,
                        Stretch = Stretch.Uniform,
                        SnapsToDevicePixels = true
                    }
                };
                RenderOptions.SetBitmapScalingMode((Image)button.Content, BitmapScalingMode.HighQuality);
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
        if (_app.UpdateState == UpdateUiState.Available && _app.AvailableUpdateVersion is { } version)
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
        Add(menu, L("MenuBar.RestoreTaskbar"),
            () => _ = _app.SetTaskbarHiddenAsync(false), _app.Preferences.HideTaskbar || _app.Taskbars.IsHidden);
        menu.Items.Add(new Separator());
        Add(menu, L("MenuBar.Quit"), () => _app.RequestExit());
        menu.IsOpen = true;
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
            var matching = _windows.Where(window => string.Equals(
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
        foreach (var window in _windows.Take(24))
        {
            var prefix = window.Handle == foreground ? "•  " : "   ";
            Add(menu, prefix + TrimTitle(window.Title), () => WindowCatalog.Activate(window.Handle));
        }
        if (menu.Items.Count == 0) Add(menu, L("MenuBar.NoWindows"), () => { }, enabled: false);
        menu.IsOpen = true;
    }

    private static string TrimTitle(string title) => title.Length > 72 ? title[..69] + "…" : title;

    private void Network_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:network-status");
    private void Sound_Click(object sender, RoutedEventArgs e) => ShowAudioDevices();
    private void Power_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:batterysaver");
    private void Clock_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-clock:");

    private void ControlCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_controlCenter is { IsVisible: true })
        {
            _controlCenter.Close();
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
            _audioDevices.Close();
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
            _notificationCenter.Close();
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
        var panel = _openPanel;
        _openPanel = null;
        if (panel is { IsVisible: true }) panel.Close();
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
