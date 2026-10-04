using System.Windows;
using System.Windows.Controls;

namespace VeliShell.Desktop.Controls;

public partial class TrafficLights : UserControl
{
    public TrafficLights() => InitializeComponent();
    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) window.WindowState = WindowState.Minimized;
    }
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
