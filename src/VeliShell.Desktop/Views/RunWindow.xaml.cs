using System.Windows;
using System.Windows.Input;

namespace VeliShell.Desktop.Views;

public partial class RunWindow : Window
{
    internal string Command => CommandBox.Text.Trim();

    internal RunWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CommandBox.Focus();
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (Command.Length > 0) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Enter && Command.Length > 0) { DialogResult = true; e.Handled = true; }
    }
}
