using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class ControlCenterWindow : Window, ITransientPanel
{
    internal event Action? OpenAudioDevicesRequested;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _updatingStatus;
    private bool _closing;

    internal ControlCenterWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshStatus();
            _statusTimer.Start();
        };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CloseOnce));
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => _statusTimer.Stop();
    }

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    internal void ShowRelativeTo(FrameworkElement anchor, Window owner, bool topmost)
    {
        Owner = owner;
        Topmost = topmost;
        MenuPanelPlacement.PlaceBelow(anchor, this);
        Show();
        Activate();
    }

    private void RefreshStatus()
    {
        var status = SystemControlStatusService.Read();
        NetworkStatus.Text = L(status.IsNetworkAvailable
            ? "ControlCenter.Online"
            : "ControlCenter.Offline");
        PowerStatus.Text = status.BatteryPercent is { } percentage
            ? string.Format(LocalizationService.Current.ActiveCulture,
                L(status.IsPluggedIn ? "ControlCenter.PowerConnected" : "ControlCenter.BatteryRemaining"), percentage)
            : L("ControlCenter.PowerUnknown");

        _updatingStatus = true;
        try
        {
            VolumeSlider.IsEnabled = status.MasterVolume is not null;
            MuteSwitch.IsEnabled = status.MasterVolume is not null;
            if (status.MasterVolume is { } volume)
            {
                VolumeSlider.Value = volume;
                MuteSwitch.IsChecked = status.IsMuted;
                VolumeLabel.Text = status.IsMuted
                    ? L("ControlCenter.Muted")
                    : $"{Math.Round(volume):0}%";
            }
            else
            {
                MuteSwitch.IsChecked = false;
                VolumeLabel.Text = L("ControlCenter.VolumeUnavailable");
            }
        }
        finally
        {
            _updatingStatus = false;
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingStatus || !IsLoaded) return;
        if (SystemControlStatusService.TrySetMasterVolume(e.NewValue))
        {
            if (MuteSwitch.IsChecked == true)
                SystemControlStatusService.TrySetMuted(false);
            MuteSwitch.IsChecked = false;
            VolumeLabel.Text = $"{Math.Round(e.NewValue):0}%";
        }
        else
        {
            RefreshStatus();
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingStatus) return;
        if (!SystemControlStatusService.TrySetMuted(MuteSwitch.IsChecked == true))
            RefreshStatus();
        else
            VolumeLabel.Text = MuteSwitch.IsChecked == true
                ? L("ControlCenter.Muted")
                : $"{Math.Round(VolumeSlider.Value):0}%";
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
    private void Network_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:network-status");
    private void Bluetooth_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:bluetooth");
    private void Focus_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:quiethours");
    private void Display_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:display");
    private void Power_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:powersleep");
    private void Settings_Click(object sender, RoutedEventArgs e) => LaunchService.Open("ms-settings:");
    private void AudioDevices_Click(object sender, RoutedEventArgs e) => OpenAudioDevicesRequested?.Invoke();
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseOnce();
    }
}
