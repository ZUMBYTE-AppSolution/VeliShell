using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class AudioDevicesWindow : Window, ITransientPanel
{
    private bool _closing;
    private readonly ShellBackdropHost _backdrop;
    private FrameworkElement? _anchor;
    private int _outputCount;
    private int _inputCount;

    internal AudioDevicesWindow()
    {
        InitializeComponent();
        _backdrop = new ShellBackdropHost(this, GlassPlate, 24);
        Loaded += (_, _) => RefreshBackdrop();
        if (Application.Current is App app) app.Themes.Changed += RefreshBackdrop;
        Loaded += (_, _) => RefreshDevices();
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CloseOnce));
        Closing += (_, _) => _closing = true;
        Closed += (_, _) =>
        {
            if (Application.Current is App app) app.Themes.Changed -= RefreshBackdrop;
            _backdrop.Dispose();
        };
    }

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    internal void ShowRelativeTo(FrameworkElement anchor, Window owner, bool topmost)
    {
        _anchor = anchor;
        Owner = owner;
        Topmost = topmost;
        MenuPanelPlacement.PlaceBelow(anchor, this);
        Show();
        Activate();
    }

    private void RefreshDevices()
    {
        DevicesPanel.Children.Clear();
        InputsPanel.Children.Clear();
        var devices = SystemControlStatusService.ReadOutputDevices();
        var inputs = SystemControlStatusService.ReadInputDevices();
        _outputCount = devices.Count;
        _inputCount = inputs.Count;
        if (devices.Count == 0) AddEmpty(DevicesPanel);
        foreach (var device in devices)
            AddDevice(DevicesPanel, device, input: false);
        if (inputs.Count == 0) AddEmpty(InputsPanel);
        foreach (var input in inputs)
            AddDevice(InputsPanel, input, input: true);
        var longestName = devices.Concat(inputs).Select(device => device.Name.Length).DefaultIfEmpty(0).Max();
        Width = Math.Clamp(280 + longestName * 7, 520, 700);
        ResizeToContent();
    }

    private void Sections_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (e.RoutedEvent == Expander.ExpandedEvent)
        {
            if (ReferenceEquals(sender, OutputsExpander)) InputsExpander.IsExpanded = false;
            if (ReferenceEquals(sender, InputsExpander)) OutputsExpander.IsExpanded = false;
        }
        ResizeToContent();
    }

    private void ResizeToContent()
    {
        var visible = (OutputsExpander?.IsExpanded == true ? Math.Max(1, _outputCount) : 0) +
                      (InputsExpander?.IsExpanded == true ? Math.Max(1, _inputCount) : 0);
        var maximum = Math.Max(380, Math.Min(760, SystemParameters.WorkArea.Height - 48));
        Height = Math.Clamp(262 + visible * 103, 380, maximum);
        if (IsLoaded && !_closing && _anchor is not null)
            MenuPanelPlacement.PlaceBelow(_anchor, this);
    }

    private void AddEmpty(Panel panel)
    {
        var label = new TextBlock
        {
            Text = L("AudioDevices.None"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 4, 8, 12)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        panel.Children.Add(label);
    }

    private void AddDevice(Panel panel, AudioOutputEndpoint device, bool input)
    {
            var content = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new VeliSymbol { Symbol = input ? VeliSymbolKind.Microphone : VeliSymbolKind.Sound,
                Width = 19, Height = 19,
                Margin = new Thickness(0, 0, 10, 0) };
            icon.SetResourceReference(ForegroundProperty, "TextPrimary");
            header.Children.Add(icon);
            var title = new TextBlock { Text = device.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            if (device.IsDefault)
            {
                var current = new TextBlock { Text = L("AudioDevices.Default"), FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                current.SetResourceReference(ForegroundProperty, "Accent");
                Grid.SetColumn(current, 2);
                header.Children.Add(current);
            }
            else
            {
                var select = new Button { Content = L("AudioDevices.SetDefault"),
                    FontSize = 10.5, Padding = new Thickness(7, 4, 7, 4),
                    Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                select.Click += (_, _) =>
                {
                    var switched = AudioDefaultDeviceService.TrySetDefault(device.Id, input);
                    RefreshDevices();
                    if (!switched) MessageBox.Show(L("AudioDevices.SwitchFailed"), L("Common.ErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                };
                Grid.SetColumn(select, 2);
                header.Children.Add(select);
            }
            content.Children.Add(header);

            if (device.VolumePercent is { } volume)
            {
                var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var slider = new Slider { Minimum = 0, Maximum = 100, Value = volume,
                    IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 14, 0) };
                AutomationProperties.SetName(slider, device.Name + " · " + L("ControlCenter.Volume"));
                slider.ValueChanged += (_, args) =>
                {
                    if (!IsLoaded) return;
                    if (!SystemControlStatusService.TrySetEndpointVolume(device.Id, args.NewValue))
                        RefreshDevices();
                };
                row.Children.Add(slider);
                var mute = new CheckBox { Style = (Style)FindResource("CompactToggleSwitch"),
                    IsChecked = device.IsMuted,
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = L("ControlCenter.Mute") };
                AutomationProperties.SetName(mute, device.Name + " · " + L("ControlCenter.Mute"));
                mute.Click += (_, _) =>
                {
                    if (!SystemControlStatusService.TrySetEndpointMuted(device.Id, mute.IsChecked == true))
                        RefreshDevices();
                };
                var muteControl = new StackPanel { Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center };
                muteControl.Children.Add(new TextBlock { Text = L("ControlCenter.Mute"),
                    VerticalAlignment = VerticalAlignment.Center, FontSize = 10.5,
                    Margin = new Thickness(0, 0, 4, 0) });
                muteControl.Children.Add(mute);
                Grid.SetColumn(muteControl, 1);
                row.Children.Add(muteControl);
                content.Children.Add(row);
            }

            var card = new Border { Child = content, Style = (Style)FindResource("GlassListRow"),
                Padding = new Thickness(13, 12, 13, 12), Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(card);
    }

    private void OpenMixer_Click(object sender, RoutedEventArgs e)
    {
        var mixer = new AudioMixerWindow
        {
            Anchor = _anchor,
            Owner = Owner,
            Topmost = Topmost
        };
        if (_anchor is not null) MenuPanelPlacement.PlaceBelow(_anchor, mixer);
        else { mixer.Left = Left; mixer.Top = Top; }
        CloseOnce();
        mixer.Show();
        mixer.Activate();
    }

    private void RefreshBackdrop() => _backdrop.ApplyMaterial("GlassPanelAcrylicSurface", "GlassPanelSurface");

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseOnce();
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
}
