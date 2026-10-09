using System.Windows;
using System.Windows.Input;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class VirtualFolderNameWindow : Window
{
    internal string ResultName { get; private set; } = "";

    internal VirtualFolderNameWindow(string? currentName = null)
    {
        InitializeComponent();
        TitleText.Text = LocalizationService.Current.Get(currentName is null
            ? "FolderPopover.NameTitle" : "FolderPopover.RenameTitle");
        NameBox.Text = currentName ?? "";
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Save()
    {
        var name = NameBox.Text.Trim();
        if (name.Length is < 1 or > 64)
        {
            NameBox.Focus();
            return;
        }
        ResultName = name;
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
