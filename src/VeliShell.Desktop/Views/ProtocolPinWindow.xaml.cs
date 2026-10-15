using System.Windows;
using System.Windows.Input;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class ProtocolPinWindow : Window
{
    internal string ProtocolTarget { get; private set; } = "";

    internal ProtocolPinWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ProtocolBox.Focus();
    }

    private void Save()
    {
        var target = ProtocolBox.Text.Trim();
        if (!StableLaunchTargetService.IsRegisteredProtocol(target))
        {
            ValidationMessage.Text = LocalizationService.Current.Get("Dock.PinProtocolInvalid");
            ProtocolBox.Focus();
            return;
        }
        ProtocolTarget = target;
        DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Enter) { Save(); e.Handled = true; }
    }
}
