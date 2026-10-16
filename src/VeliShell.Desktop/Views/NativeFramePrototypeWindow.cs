using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Views;

/// <summary>One app, one HWND, one native frame. Never a floating replacement titlebar.</summary>
internal sealed class NativeFramePrototypeWindow : Window
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly RowDefinition _caption = new() { Height = new GridLength(0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _root;
    private readonly Border _captionPlate;
    private readonly ThemeService _theme;
    private CompositionGlassRenderer? _glass;
    private nint _handle;
    private HwndSource? _source;
    private bool _native;
    private bool _armed;
    private int _hover = -1;
    private static string L(string key) => LocalizationService.Current.Get(key);
    internal bool IsNative => _native;
    internal NativeFramePrototypeWindow(ThemeService theme)
    {
        _theme = theme; Title = L("Prototype.Title"); Width = 860; Height = 590; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "TextPrimary");
        var layout = new Grid(); layout.RowDefinitions.Add(_caption); layout.RowDefinitions.Add(new RowDefinition());
        var title = new Grid { Height = 38 };
        var lights = new TrafficLights { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        title.Children.Add(lights);
        title.Children.Add(new TextBlock { Text = "VeliShell · Native Preview", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        var rollback = new Button { Content = L("Prototype.Restore"), Padding = new Thickness(10, 2, 10, 2), MinHeight = 25, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        rollback.Click += (_, _) => Restore(); title.Children.Add(rollback);
        _captionPlate = new Border { Child = title }; _captionPlate.SetResourceReference(Border.BackgroundProperty, "GlassPanelAcrylicSurface"); layout.Children.Add(_captionPlate);
        var body = new Grid { Margin = new Thickness(28, 22, 28, 28) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };
        sidebar.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/VeliShell;component/Assets/VeliShellMark.png")), Width = 60, Height = 60, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 22) });
        foreach (var key in new[] { "Prototype.Navigation", "Prototype.Frame", "Prototype.Safety" }) sidebar.Children.Add(new TextBlock { Text = L(key), FontSize = 13, Margin = new Thickness(0, 0, 0, 18) });
        body.Children.Add(sidebar);
        var content = new StackPanel(); Grid.SetColumn(content, 1);
        content.Children.Add(new TextBlock { Text = L("Prototype.Heading"), FontSize = 27, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var intro = new TextBlock { Text = L("Prototype.Description"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 22) }; intro.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); content.Children.Add(intro);
        content.Children.Add(new TextBlock { Text = L("Prototype.Input"), Margin = new Thickness(0, 0, 0, 6) });
        content.Children.Add(new TextBox { Text = L("Prototype.Sample"), MinHeight = 42, Margin = new Thickness(0, 0, 0, 18) });
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 20) };
        foreach (var dark in new[] { false, true })
        {
            var button = new Button { Content = L(dark ? "Appearance.Dark" : "Appearance.Light"), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) =>
            {
                var was = _native; if (was) Restore();
                var gloss = Application.Current.TryFindResource("ShellGlassGloss") is double g ? g : 50;
                var transparency = Application.Current.TryFindResource("ShellGlassTransparency") is double t ? t : 50;
                _theme.Apply(dark ? Appearance.Dark : Appearance.Light, gloss, transparency); if (was) Attach();
            }; buttons.Children.Add(button);
        }
        var apply = new Button { Content = L("Prototype.Apply"), Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += (_, _) =>
        {
            try { Attach(); }
            catch (Exception error) { App.Log("Native frame reactivation failed", error); Restore(); _status.Text = L("Prototype.AttachFailed"); }
        }; buttons.Children.Add(apply);
        content.Children.Add(buttons);
        _status.Margin = new Thickness(0, 0, 0, 14); content.Children.Add(_status);
        var emergency = new TextBlock { Text = L("Prototype.Emergency"), TextWrapping = TextWrapping.Wrap, FontSize = 12 }; emergency.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); content.Children.Add(emergency);
        body.Children.Add(content);
        var reading = new Border { Child = body }; reading.SetResourceReference(Border.BackgroundProperty, "WindowBackdropGradient"); Grid.SetRow(reading, 1); layout.Children.Add(reading);
        _root = new Border { Child = layout, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        _root.SetResourceReference(Border.BorderBrushProperty, "GlassPanelStroke"); RoundedClip.SetRadius(layout, 11); Content = _root;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle; _source = HwndSource.FromHwnd(_handle);
            if (!NativePrototypeMethods.SetProp(_handle, NativePrototypeMethods.OptIn, 1) || !NativePrototypeMethods.SetProp(_handle, "VeliShell.NativePrototype." + _session.ToString("N"), 1))
                throw new InvalidOperationException("The own-window opt-in marker could not be set.");
            _source!.AddHook(ObserveRecovery);
        };
        Loaded += async (_, _) =>
        {
            try { _armed = await NativePrototypeSession.ArmAsync(_handle, _session); if (_armed && IsLoaded) Attach(); else _status.Text = L("Prototype.GuardFailed"); }
            catch (Exception error) { App.Log("Native prototype could not arm recovery", error); _status.Text = L("Prototype.GuardFailed"); }
        };
        Closing += (_, _) => { if (_native) Restore(); };
        var guardCheck = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        guardCheck.Tick += (_, _) =>
        {
            if (!_native) return;
            try { using var ready = EventWaitHandle.OpenExisting(NativePrototypeSession.EventName(_session, "ready")); }
            catch (WaitHandleCannotBeOpenedException) { Restore(); _armed = false; _status.Text = L("Prototype.GuardFailed"); }
        };
        Loaded += (_, _) => guardCheck.Start();
        Closed += (_, _) => guardCheck.Stop();
        SizeChanged += (_, _) => ResizeGlass();
        MouseMove += (_, e) =>
        {
            var point = e.GetPosition(this);
            var hover = point.Y is >= 6 and <= 32 && point.X is >= 13 and < 79 ? (int)((point.X - 13) / 22) : -1;
            if (_hover != hover) { _hover = hover; ResizeGlass(); }
        };
        MouseLeave += (_, _) => { if (_hover != -1) { _hover = -1; ResizeGlass(); } };
        Closed += (_, _) => { _source?.RemoveHook(ObserveRecovery); NativePrototypeSession.End(_session); };
    }
    private nint ObserveRecovery(nint window, int message, nint wp, nint lp, ref bool handled)
    { if (message == NativePrototypeMethods.RestoreMessage) { OnRestored(); handled = true; } return 0; }
    private void Attach()
    {
        try { AttachCore(); }
        catch (Exception error)
        {
            App.Log("Native frame activation failed", error);
            if (_native) Restore();
            _status.Text = L("Prototype.AttachFailed");
        }
    }
    private void AttachCore()
    {
        if (_native || _handle == 0) return;
        if (!_armed) { _status.Text = L("Prototype.GuardFailed"); return; }
        using var armed = EventWaitHandle.OpenExisting(NativePrototypeSession.EventName(_session, "ready"));
        if (!armed.WaitOne(0)) return;
        using var recover = EventWaitHandle.OpenExisting(NativePrototypeSession.EventName(_session, "restore"));
        if (recover.WaitOne(0)) { _status.Text = L("Prototype.RestartToApply"); return; }
        if (NativePrototypeMethods.VeliFrameApiVersion() != 1) throw new InvalidOperationException("Native prototype DLL version mismatch. Extract the complete package.");
        var result = NativePrototypeMethods.VeliFrameAttach(_handle, _theme.IsDark ? 1 : 0, SystemParameters.HighContrast ? 1 : 0);
        _native = result != 0;
        if (_native)
        {
            _caption.Height = new GridLength(38); _source!.CompositionTarget.BackgroundColor = Colors.Transparent;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemParameters.HighContrast)
            {
                try
                {
                    // The same renderer as the Dock, attached directly to this HWND, beneath its WPF content.
                    _glass = new CompositionGlassRenderer(_handle);
                    var tint = Application.Current.TryFindResource("ShellAcrylicTint") is Color color ? color : Colors.Transparent;
                    var gloss = Application.Current.TryFindResource("ShellGlassGloss") is double g ? g : 50;
                    var transparency = Application.Current.TryFindResource("ShellGlassTransparency") is double t ? t : 50;
                    _glass.Apply(tint, gloss, transparency); ResizeGlass();
                }
                catch (Exception error) when (error is System.Runtime.InteropServices.COMException or PlatformNotSupportedException or TypeLoadException)
                { _glass?.Dispose(); _glass = null; App.Log("In-window composition unavailable; retaining native acrylic", error); }
            }
        }
        _captionPlate.SetResourceReference(Border.BackgroundProperty, result == 1 && _glass is null ? "WindowBackdropGradient" : "GlassPanelAcrylicSurface");
        _status.Text = L(_glass is not null ? "Prototype.CompositionActive" : result == 2 ? "Prototype.Active" : result == 1 ? "Prototype.Fallback" : "Prototype.AttachFailed");
    }
    internal bool Restore()
    {
        if (!_native) return true;
        _glass?.Dispose(); _glass = null;
        if (NativePrototypeMethods.VeliFrameRestore(_handle) == 0) return false;
        OnRestored(); return true;
    }
    private void OnRestored()
    {
        _glass?.Dispose(); _glass = null;
        _native = false; _caption.Height = new GridLength(0);
        _source!.CompositionTarget.BackgroundColor = _theme.IsDark ? Color.FromRgb(18, 22, 32) : Color.FromRgb(245, 247, 252);
        _status.Text = L("Prototype.Restored");
    }
    private void ResizeGlass()
    {
        if (_glass is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX); var height = (int)Math.Ceiling(38 * dpi.DpiScaleY);
        _glass.Resize(width, height, (float)(12 * dpi.DpiScaleX), (float)(12 * dpi.DpiScaleY), squareBottom: true);
        var plateColor = _captionPlate.Background is LinearGradientBrush gradient ? gradient.GradientStops[0].Color : Colors.Transparent;
        var plate = (uint)(plateColor.A << 24 | plateColor.R << 16 | plateColor.G << 8 | plateColor.B);
        _glass.PaintPrototypeCaption(width, height, (float)dpi.DpiScaleX, _theme.IsDark, plate, _hover, "VeliShell · Native Preview", L("Prototype.Restore"));
    }
}
