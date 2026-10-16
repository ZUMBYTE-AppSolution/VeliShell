using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Threading;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

internal sealed class ClassicFrameWindow : VeliShellWindow
{
    private readonly ListBox _list = new() { MinHeight = 170, MaxHeight = 260 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 8) };
    private readonly Button _apply;
    private readonly Button _restore;
    private readonly ThemeService _theme;
    private Guid? _session;
    private nint _target;
    private bool _busy, _restoring;
    private static string L(string key) => LocalizationService.Current.Get(key);
    internal ClassicFrameWindow(ThemeService theme)
    {
        _theme = theme; Title = L("Classic.Title"); Width = 840; Height = 660; MinWidth = 640; MinHeight = 570;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) }); layout.RowDefinitions.Add(new RowDefinition());
        var header = new Grid();
        var lights = new TrafficLights { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        WindowChrome.SetIsHitTestVisibleInChrome(lights, true); header.Children.Add(lights);
        header.Children.Add(new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(header);
        var body = new StackPanel { Margin = new Thickness(28, 12, 28, 24) };
        body.Children.Add(new TextBlock { Text = L("Classic.Heading"), FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var intro = new TextBlock { Text = L("Classic.Description"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); body.Children.Add(intro);
        body.Children.Add(_list);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        var refresh = new Button { Content = L("Common.Refresh"), Margin = new Thickness(0, 0, 8, 8) };
        refresh.Click += (_, _) => Refresh(); actions.Children.Add(refresh);
        _apply = new Button { Content = L("Classic.Apply"), Margin = new Thickness(0, 0, 8, 8) }; _apply.Click += Apply; actions.Children.Add(_apply);
        _restore = new Button { Content = L("Prototype.Restore"), IsEnabled = false, Margin = new Thickness(0, 0, 8, 8) };
        _restore.Click += (_, _) => Restore(); actions.Children.Add(_restore); body.Children.Add(actions); body.Children.Add(_status);
        var note = new TextBlock { Text = L("Classic.Limits") + "\n\n" + L("Prototype.Emergency"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); body.Children.Add(note);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var plate = new Border { Child = layout, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        plate.SetResourceReference(Border.BackgroundProperty, "WindowBackdropGradient"); plate.SetResourceReference(Border.BorderBrushProperty, "GlassPanelStroke");
        RoundedClip.SetRadius(layout, 13); Content = plate;
        Loaded += (_, _) => Refresh();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            if (_session is null || _busy) return;
            if (NativePrototypeMethods.GetProp(_target, NativePrototypeMethods.ClassicActive) == 0)
            { _session = null; _apply.IsEnabled = true; _restore.IsEnabled = false; _status.Text = L("Classic.Restored"); }
        };
        Loaded += (_, _) => timer.Start(); Closed += (_, _) => { timer.Stop(); Restore(); };
    }
    private void Refresh()
    {
        _list.ItemsSource = ClassicFrameSession.Enumerate();
        if (_session is null) _status.Text = _list.Items.Count == 0 ? L("Classic.NoWindows") : L("Classic.Select");
    }
    private async void Apply(object sender, RoutedEventArgs args)
    {
        if (_busy || _session is not null || _list.SelectedItem is not ClassicFrameSession.Candidate target) return;
        if (MessageBox.Show(this, string.Format(L("Classic.Consent"), target.Title, target.Path, target.Pid), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; _apply.IsEnabled = false;
        try
        {
            int Read(string key) => Application.Current.TryFindResource(key) is double value ? (int)value : 50;
            _session = await ClassicFrameSession.BeginAsync(target, _theme.IsDark, Read("ShellGlassGloss"), Read("ShellGlassTransparency"));
            _target = target.Window; _restoring = false; _restore.IsEnabled = true; _status.Text = L("Classic.Active");
            if (!IsLoaded) Restore();
        }
        catch (Exception error) { App.Log("Classic frame opt-in failed", error); _status.Text = error.Message; _apply.IsEnabled = true; }
        finally { _busy = false; }
    }
    private void Restore()
    {
        if (_session is not Guid id || _restoring) return;
        _restoring = true; ClassicFrameSession.Signal(id); _status.Text = L("Classic.Restoring");
    }
}
