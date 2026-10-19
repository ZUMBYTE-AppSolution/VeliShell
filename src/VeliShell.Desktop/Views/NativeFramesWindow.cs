using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

internal sealed class NativeFramesWindow : VeliShellWindow
{
    private readonly NativeFramesController _controller;
    private readonly Settings _settings;
    private readonly ThemeService _theme;
    private readonly Action<Action<Settings>> _save;
    private readonly CheckBox _enabled = new();
    private readonly CheckBox _materials = new();
    private readonly CheckBox _modern = new();
    private readonly ListBox _exceptions = new() { Height = 76 };
    private readonly ListBox _windows = new() { Height = 235 };
    private readonly TextBox _entry = new() { MinWidth = 330, Margin = new Thickness(0, 0, 8, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
    private bool _busy, _syncing;
    private static string L(string key) => LocalizationService.Current.Get(key);
    internal NativeFramesWindow(Settings settings, NativeFramesController controller, ThemeService theme, Action<Action<Settings>> save, bool preview = false, bool realAppPreview = false)
    {
        _settings = settings; _controller = controller; _theme = theme; _save = save;
        Title = L("NativeFrames.Title"); Width = 880; Height = 900; MinWidth = 700; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MaxHeight = SystemParameters.WorkArea.Height;
        _exceptions.SetResourceReference(StyleProperty,"NativeFramesList"); _windows.SetResourceReference(StyleProperty,"NativeFramesList");
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) }); layout.RowDefinitions.Add(new RowDefinition());
        layout.SetResourceReference(Panel.BackgroundProperty,"WindowBackdropGradient");
        var header = new Grid();
        var lights = new TrafficLights { Margin = new Thickness(14, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        WindowChrome.SetIsHitTestVisibleInChrome(lights, true); header.Children.Add(lights);
        header.Children.Add(new TextBlock { Text = Title, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold }); layout.Children.Add(header);
        var body = new StackPanel { Margin = new Thickness(28, 12, 28, 24) };
        body.Children.Add(new TextBlock { Text = L("NativeFrames.Heading"), FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(Description(realAppPreview ? "NativeFrames.RealPreview" : preview ? "NativeFrames.Preview" : "NativeFrames.Description"));
        _enabled.Content = L("NativeFrames.Enable"); _enabled.SetResourceReference(StyleProperty, "ToggleSwitch"); _enabled.Margin = new Thickness(0, 16, 0, 0); _enabled.Checked += Toggle; _enabled.Unchecked += Toggle; body.Children.Add(_enabled); body.Children.Add(_status);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_enabled, "NativeFramesEnable");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_entry, "NativeFramesException");
        _modern.Content = L("NativeFrames.Modern"); _modern.SetResourceReference(StyleProperty,"ToggleSwitch"); _modern.Margin = new Thickness(0,0,0,10);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_modern,"NativeFramesModern");
        _modern.Checked += ChangeModern; _modern.Unchecked += ChangeModern;
        body.Children.Add(_modern); body.Children.Add(Description("NativeFrames.ModernHint"));
        _materials.Content = L("NativeFrames.Materials"); _materials.SetResourceReference(StyleProperty, "ToggleSwitch"); _materials.Margin = new Thickness(0, 0, 0, 10);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_materials, "NativeFramesMaterials");
        _materials.Checked += ChangeMaterials; _materials.Unchecked += ChangeMaterials; body.Children.Add(_materials); body.Children.Add(Description("NativeFrames.MaterialsHint"));
        var rollback = new Button { Content = L("NativeFrames.Rollback"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
        rollback.Click += (_, _) => { _save(s => s.NativeFramesEnabled = false); _controller.Stop(); Refresh(); }; body.Children.Add(rollback);
        body.Children.Add(new TextBlock { Text = L("NativeFrames.Exceptions"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(Description("NativeFrames.ExceptionsHint")); body.Children.Add(_exceptions);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; row.Children.Add(_entry);
        var add = new Button { Content = L("NativeFrames.Add"), Margin = new Thickness(0, 0, 8, 8) }; add.Click += (_, _) => { var entry = _entry.Text.Trim(); _save(s => s.NativeFrameExclusions.Add(entry)); _entry.Clear(); _controller.Update(_settings, _theme.IsDark); Refresh(); }; row.Children.Add(add);
        var remove = new Button { Content = L("Common.Remove"), Margin = new Thickness(0, 0, 8, 8) }; remove.Click += (_, _) => { if (_exceptions.SelectedItem is string selected) { _save(s => s.NativeFrameExclusions.Remove(selected)); _controller.Update(_settings, _theme.IsDark); Refresh(); } }; row.Children.Add(remove); body.Children.Add(row);
        body.Children.Add(new TextBlock { Text = L("NativeFrames.Windows"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) }); body.Children.Add(_windows);
        var exclude = new Button { Content = L("NativeFrames.ExcludeSelected"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 12) };
        exclude.Click += (_, _) => { if (_windows.SelectedItem is NativeFrameStatus selected) { _save(s => s.NativeFrameExclusions.Add(System.IO.Path.GetFileName(selected.Path))); _controller.Update(_settings, _theme.IsDark); Refresh(); } }; body.Children.Add(exclude);
        body.Children.Add(Description("NativeFrames.Safety"));
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        header.SetResourceReference(Panel.BackgroundProperty,"CardSurfaceGradient");
        // Opaque readable base: text in another window must never bleed through
        // a settings/diagnostic surface, even at maximum shell transparency.
        var plate = new Border { Child = layout, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) }; plate.SetResourceReference(Border.BackgroundProperty, "WindowSurface"); plate.SetResourceReference(Border.BorderBrushProperty, "GlassPanelStroke"); RoundedClip.SetRadius(layout, 13); Content = plate;
        _controller.Changed += Refresh; Closed += (_, _) => _controller.Changed -= Refresh; Loaded += (_, _) => Refresh();
    }
    private static TextBlock Description(string key)
    {
        var text = new TextBlock { Text = L(key), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontSize = 12 }; text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); return text;
    }
    private void Refresh()
    {
        if (_syncing) return; _syncing = true;
        try {
            if (!_busy && _controller.Status.Stopping && _settings.NativeFramesEnabled) _save(s => s.NativeFramesEnabled = false);
            _enabled.IsChecked = _settings.NativeFramesEnabled;
            _enabled.IsEnabled = !_busy && NativeFramesController.Supported && !_controller.Restoring;
            _materials.IsChecked = _settings.NativeModernMaterialsEnabled;
            _materials.IsEnabled = !_busy && NativeFramesController.Supported;
            _modern.IsChecked = _settings.NativeModernFramesEnabled;
            _modern.IsEnabled = !_busy && NativeFramesController.Supported && !_controller.Restoring;
            _status.Text = L(!NativeFramesController.Supported ? "NativeFrames.Unsupported" : _busy ? "NativeFrames.Starting" : _controller.Status.Stopping ? "NativeFrames.Restoring" : _controller.Active ? "NativeFrames.Active" : "NativeFrames.Off");
            if (_controller.Status.Error is { } error) _status.Text += "\n" + error;
            var selected = _exceptions.SelectedItem as string; _exceptions.ItemsSource = _settings.NativeFrameExclusions.ToArray(); _exceptions.SelectedItem = selected;
            var selectedWindow = _windows.SelectedItem as NativeFrameStatus;
            if (!(_windows.ItemsSource is NativeFrameStatus[] previous && previous.SequenceEqual(_controller.Status.Windows))) _windows.ItemsSource = _controller.Status.Windows;
            if (selectedWindow is not null) _windows.SelectedItem = _controller.Status.Windows.FirstOrDefault(row => row.Window == selectedWindow.Window && row.Pid == selectedWindow.Pid && row.Started == selectedWindow.Started);
        } finally { _syncing = false; }
    }
    private async void ChangeModern(object sender,RoutedEventArgs args)
    {
        if (_syncing || _busy) return;
        bool enable = _modern.IsChecked == true; _busy = true; _modern.IsEnabled = false;
        await Dispatcher.Yield(DispatcherPriority.Background);
        try {
            if (enable && MessageBox.Show(this,L("NativeFrames.ModernConsent"),Title,MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
            _save(s=> { s.NativeModernFramesEnabled=enable; if(enable)s.NativeModernFramesConsentVersion=1; });
            _controller.Update(_settings,_theme.IsDark);
        } finally { _busy=false;Refresh(); }
    }
    private async void ChangeMaterials(object sender, RoutedEventArgs args)
    {
        if (_syncing || _busy) return;
        bool enable = _materials.IsChecked == true;
        _busy = true; _materials.IsEnabled = false;
        await Dispatcher.Yield(DispatcherPriority.Background);
        try {
            if (enable && MessageBox.Show(this, L("NativeFrames.MaterialConsent"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _save(s => { s.NativeModernMaterialsEnabled = enable; if (enable) s.NativeModernMaterialsConsentVersion = 1; });
            _controller.Update(_settings, _theme.IsDark);
        } finally { _busy = false; Refresh(); }
    }
    private async void Toggle(object sender, RoutedEventArgs args)
    {
        if (_busy || _syncing) return;
        if (_enabled.IsChecked != true) { _save(s => s.NativeFramesEnabled = false); _controller.Stop(); Refresh(); return; }
        _busy = true; _enabled.IsEnabled = false;
        // Leave the automation/toggle provider call before opening a modal dialog.
        await Dispatcher.Yield(DispatcherPriority.Background);
        try {
            if (MessageBox.Show(this, L("NativeFrames.Consent"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _settings.NativeFramesConsentVersion = 1;
            await _controller.EnableAsync(_settings, _theme.IsDark, explicitConsent: true);
            _save(s => { s.NativeFramesEnabled = true; s.NativeFramesConsentVersion = 1; });
        } catch (Exception error) { App.Log("Native window styling was not enabled", error); _save(s => s.NativeFramesEnabled = false); MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; Refresh(); }
    }
}
