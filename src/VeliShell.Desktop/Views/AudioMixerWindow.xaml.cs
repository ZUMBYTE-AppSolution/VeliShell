using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class AudioMixerWindow : Window
{
    private readonly ShellBackdropHost _backdrop;
    private bool _loading;
    private string _endpointId = "";
    private int _sessionGroupCount;
    internal FrameworkElement? Anchor { get; set; }

    internal AudioMixerWindow()
    {
        InitializeComponent();
        _backdrop = new ShellBackdropHost(this, GlassPlate, 24);
        if (Application.Current is App app) app.Themes.Changed += RefreshBackdrop;
        Loaded += (_, _) => { _backdrop.Apply(); RefreshEndpoints(); };
        Closed += (_, _) =>
        {
            if (Application.Current is App app) app.Themes.Changed -= RefreshBackdrop;
            _backdrop.Dispose();
        };
    }

    private void RefreshBackdrop() => _backdrop.Apply();

    private void RefreshEndpoints()
    {
        _loading = true;
        var devices = SystemControlStatusService.ReadOutputDevices();
        var selected = _endpointId;
        OutputChoice.Items.Clear();
        foreach (var device in devices)
            OutputChoice.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        var choice = OutputChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag as string, selected, StringComparison.Ordinal)) ??
            OutputChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                devices.Any(device => device.IsDefault && string.Equals(device.Id, item.Tag as string,
                    StringComparison.Ordinal)));
        OutputChoice.SelectedItem = choice ?? OutputChoice.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _endpointId = (OutputChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _loading = false;
        RefreshSessions();
    }

    private void RefreshSessions()
    {
        SessionsPanel.Children.Clear();
        var device = SystemControlStatusService.ReadOutputDevices().FirstOrDefault(item =>
            string.Equals(item.Id, _endpointId, StringComparison.Ordinal));
        _loading = true;
        MasterSlider.IsEnabled = device?.VolumePercent is not null;
        MasterSlider.Value = device?.VolumePercent ?? 0;
        MasterVolumeLabel.Text = device?.VolumePercent is { } master ? $"{master:0}%" : "—";
        _loading = false;
        if (device is null)
        {
            _sessionGroupCount = 0;
            ResizeToContent();
            if (IsLoaded && Anchor is not null) MenuPanelPlacement.PlaceBelow(Anchor, this);
            return;
        }
        var sessions = AudioSessionService.Read(device.Id);
        var groups = sessions.GroupBy(session => session.GroupKey,
                StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.ToArray()).ToArray();
        _sessionGroupCount = groups.Length;
        ResizeToContent();
        if (IsLoaded && Anchor is not null) MenuPanelPlacement.PlaceBelow(Anchor, this);
        if (sessions.Count == 0)
        {
            var empty = new TextBlock { Text = L("AudioDevices.NoSessions"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 8, 5, 8) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            SessionsPanel.Children.Add(empty);
        }
        foreach (var group in groups) AddSession(device.Id, group);
    }

    private void AddSession(string deviceId, IReadOnlyList<AudioAppSession> group)
    {
        var session = group[0];
        var sessionIds = group.Select(item => item.Id).ToArray();
        var active = group.Where(item => item.IsActive).ToArray();
        var content = new StackPanel();
        var heading = new DockPanel();
        var levelLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        levelLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        DockPanel.SetDock(levelLabel, Dock.Right);
        heading.Children.Add(levelLabel);
        var nameLabel = new TextBlock { Text = session.Name, FontWeight = FontWeights.SemiBold,
            FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        heading.Children.Add(nameLabel);
        content.Children.Add(heading);
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var slider = new Slider { Minimum = 0, Maximum = 100,
            Value = (active.Length > 0 ? active : group).Average(item => item.VolumePercent),
            IsMoveToPointEnabled = true, Margin = new Thickness(0, 0, 14, 0) };
        levelLabel.Text = $"{slider.Value:0}%";
        AutomationProperties.SetName(slider, session.Name + " · " + L("ControlCenter.Volume"));
        slider.ValueChanged += (_, args) =>
        {
            levelLabel.Text = $"{args.NewValue:0}%";
            if (IsLoaded && !AudioSessionService.TrySetVolumes(deviceId, sessionIds, args.NewValue))
                RefreshSessions();
        };
        row.Children.Add(slider);
        var mute = new CheckBox { Style = (Style)FindResource("CompactToggleSwitch"),
            IsChecked = group.All(item => item.IsMuted), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L("ControlCenter.Mute") };
        AutomationProperties.SetName(mute, session.Name + " · " + L("ControlCenter.Mute"));
        mute.Click += (_, _) =>
        {
            if (!AudioSessionService.TrySetMuted(deviceId, sessionIds, mute.IsChecked == true))
                RefreshSessions();
        };
        var muteControl = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        var muteLabel = new TextBlock { Text = L("ControlCenter.Mute"),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 10.5,
            Margin = new Thickness(0, 0, 4, 0) };
        muteLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        muteControl.Children.Add(muteLabel);
        muteControl.Children.Add(mute);
        Grid.SetColumn(muteControl, 1);
        row.Children.Add(muteControl);
        content.Children.Add(row);
        var card = new Border { Child = content, Style = (Style)FindResource("GlassListRow"),
            Padding = new Thickness(13), Margin = new Thickness(0, 0, 0, 8) };
        SessionsPanel.Children.Add(card);
    }

    private void Sessions_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) ResizeToContent();
    }

    private void ResizeToContent()
    {
        var visible = SessionsExpander?.IsExpanded == true ? _sessionGroupCount : 0;
        var maximum = Math.Max(400, Math.Min(760, SystemParameters.WorkArea.Height - 48));
        Height = Math.Clamp(385 + visible * 82, 400, maximum);
        if (IsLoaded && Anchor is not null) MenuPanelPlacement.PlaceBelow(Anchor, this);
    }

    private void OutputChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputChoice.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        _endpointId = id;
        RefreshSessions();
    }

    private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || string.IsNullOrWhiteSpace(_endpointId)) return;
        MasterVolumeLabel.Text = $"{e.NewValue:0}%";
        if (!SystemControlStatusService.TrySetEndpointVolume(_endpointId, e.NewValue)) RefreshSessions();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshEndpoints();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
    private static string L(string key) => LocalizationService.Current.Get(key);
}
