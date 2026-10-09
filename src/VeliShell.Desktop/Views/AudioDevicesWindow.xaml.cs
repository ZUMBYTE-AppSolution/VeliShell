using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class AudioDevicesWindow : Window
{
    internal AudioDevicesWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDevices();
        Deactivated += (_, _) => Close();
    }

    internal void ShowRelativeTo(FrameworkElement anchor, Window owner, bool topmost)
    {
        Owner = owner;
        Topmost = topmost;
        MenuPanelPlacement.PlaceBelow(anchor, this);
        Show();
        Activate();
    }

    private void RefreshDevices()
    {
        DevicesPanel.Children.Clear();
        var devices = SystemControlStatusService.ReadOutputDevices();
        if (devices.Count == 0)
        {
            Height = 250;
            DevicesPanel.Children.Add(new TextBlock
            {
                Text = L("AudioDevices.None"), TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(8, 14, 8, 14)
            });
            return;
        }

        Height = 470;

        foreach (var device in devices)
        {
            var content = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new VeliSymbol { Symbol = VeliSymbolKind.Sound, Width = 19, Height = 19,
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

            var card = new Border { Child = content, Padding = new Thickness(13, 12, 13, 12),
                Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1) };
            card.SetResourceReference(BackgroundProperty, "GlassCardSurface");
            card.SetResourceReference(BorderBrushProperty, "GlassCardStroke");
            DevicesPanel.Children.Add(card);
        }
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        Close();
        LaunchService.Open("ms-settings:sound");
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private static string L(string key) => LocalizationService.Current.Get(key);
}
