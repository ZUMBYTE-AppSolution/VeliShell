using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

public partial class VirtualFolderNameWindow : Window
{
    private readonly ShellBackdropHost _backdrop;
    private readonly ThemeService? _theme;
    internal string ResultName { get; private set; } = "";

    internal VirtualFolderNameWindow(string? currentName = null)
    {
        InitializeComponent();
        TitleText.Text = LocalizationService.Current.Get(currentName is null
            ? "FolderPopover.NameTitle" : "FolderPopover.RenameTitle");
        NameBox.Text = currentName ?? "";
        _backdrop = new ShellBackdropHost(this, GlassPlate, 21);
        if (Application.Current is App { Themes: not null } host)
        {
            _theme = host.Themes;
            _theme.Changed += RefreshBackdrop;
        }
        Loaded += (_, _) =>
        {
            CenterOnOwnerDisplay();
            RefreshBackdrop();
            NameBox.Focus();
            NameBox.SelectAll();
        };
        Closed += (_, _) =>
        {
            if (_theme is not null) _theme.Changed -= RefreshBackdrop;
            _backdrop.Dispose();
        };
    }

    private void RefreshBackdrop() => _backdrop.ApplyMaterial("GlassPanelAcrylicSurface", "GlassPanelSurface");

    private void CenterOnOwnerDisplay()
    {
        var source = PresentationSource.FromVisual(Owner ?? this);
        var ownerHandle = new WindowInteropHelper(Owner ?? this).Handle;
        var monitor = NativeMethods.MonitorFromWindow(ownerHandle, 2);
        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        var workArea = SystemParameters.WorkArea;
        if (source?.CompositionTarget is not null && monitor != 0 &&
            NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            var topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
            var bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
            workArea = new Rect(topLeft, bottomRight);
        }
        Left = workArea.Left + Math.Max(0, (workArea.Width - ActualWidth) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - ActualHeight) / 2);
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
