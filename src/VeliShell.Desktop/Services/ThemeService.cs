using System.Windows;
using System.Windows.Media;
using VeliShell.Core;
using Microsoft.Win32;

namespace VeliShell.Desktop.Services;

public sealed class ThemeService : IDisposable
{
    private Appearance _appearance;
    private double _gloss = 50;
    private double _transparency = 50;
    private string _key = "";
    public bool IsDark { get; private set; }
    public event Action? Changed;

    public ThemeService() => SystemEvents.UserPreferenceChanged += OnSystemChanged;

    public void Apply(Appearance appearance, double gloss = 50, double transparency = 50)
    {
        _appearance = appearance;
        _gloss = double.IsFinite(gloss) ? Math.Clamp(gloss, 0, 100) : 50;
        _transparency = double.IsFinite(transparency) ? Math.Clamp(transparency, 0, 100) : 50;
        var dark = appearance == Appearance.Dark || (appearance == Appearance.System && SystemIsDark());
        var key = $"{dark}:{SystemParameters.HighContrast}:{_gloss:0}:{_transparency:0}";
        if (_key == key) return;
        _key = key;
        IsDark = dark;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/VeliShell;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        };
        if (!SystemParameters.HighContrast) ApplyGlassMaterial(dictionary, _gloss, _transparency);
        if (SystemParameters.HighContrast)
        {
            foreach (var name in new[]
                     {
                         "WindowSurface", "SidebarSurface", "CardSurface", "DockSurface", "MenuSurface",
                         "WindowBackdropGradient", "SidebarGradient", "CardSurfaceGradient", "GlassMenuSurface",
                         "GlassMenuAcrylicSurface", "GlassPanelAcrylicSurface",
                         "DockSurfaceGradient", "DockAcrylicSurfaceGradient", "DockMilkOverlay", "DockHover",
                         "MenuBarAcrylicSurface"
                     })
                dictionary[name] = SystemColors.WindowBrush;
            foreach (var name in new[]
                     {
                         "ControlSurface", "HoverSurface", "AccentSoft", "ControlSurfaceGradient",
                         "ControlHoverGradient", "NavHoverGradient"
                     })
                dictionary[name] = SystemColors.ControlBrush;
            foreach (var name in new[] { "TextPrimary", "TextSecondary", "DockIndicator" })
                dictionary[name] = SystemColors.WindowTextBrush;
            foreach (var name in new[] { "Divider", "CardStroke", "ControlStrokeHover", "DockStroke", "DockInnerStroke", "DockHighlight" })
                dictionary[name] = SystemColors.WindowTextBrush;
            dictionary["Accent"] = SystemColors.HighlightBrush;
            dictionary["AccentStroke"] = SystemColors.HighlightBrush;
            dictionary["AccentGradient"] = SystemColors.HighlightBrush;
            dictionary["AccentGradientHover"] = SystemColors.HighlightBrush;
            dictionary["AmbientGlowGradient"] = Brushes.Transparent;
            dictionary["DockDropMarker"] = SystemColors.HighlightBrush;
            dictionary["AccentText"] = SystemColors.HighlightTextBrush;
            dictionary["SwitchOff"] = SystemColors.GrayTextBrush;
            dictionary["ControlThumb"] = SystemColors.HighlightTextBrush;
            dictionary["ControlThumbStroke"] = SystemColors.WindowTextBrush;
            dictionary["AccentGlowColor"] = Colors.Transparent;
            dictionary["SubtleShadowColor"] = Colors.Transparent;
            dictionary["ShellAcrylicTint"] = Colors.Transparent;
        }
        Application.Current.Resources.MergedDictionaries[0] = dictionary;
        Changed?.Invoke();
    }

    private static void ApplyGlassMaterial(ResourceDictionary dictionary, double gloss, double transparency)
    {
        // 50/50 keeps the authored appearance. At the transparent end the
        // panels still retain enough tint for text contrast.
        var opacityScale = 1 + (50 - transparency) * 0.012;
        foreach (var key in new[] { "DockSurfaceGradient", "DockAcrylicSurfaceGradient",
                     "MenuBarSurface", "MenuBarAcrylicSurface", "GlassPanelSurface", "GlassPanelAcrylicSurface",
                     "GlassMenuSurface", "GlassMenuAcrylicSurface",
                     "WindowBackdropGradient", "SidebarGradient", "CardSurfaceGradient",
                     "ControlSurfaceGradient" })
        {
            if (dictionary[key] is not LinearGradientBrush original) continue;
            var brush = original.CloneCurrentValue();
            foreach (var stop in brush.GradientStops)
                stop.Color = WithAlpha(stop.Color, opacityScale);
            brush.Freeze();
            dictionary[key] = brush;
        }
        var sheenScale = 0.35 + gloss * 0.013;
        if (dictionary["DockMilkOverlay"] is RadialGradientBrush milk)
        {
            var brush = milk.CloneCurrentValue();
            foreach (var stop in brush.GradientStops)
                stop.Color = WithAlpha(stop.Color, sheenScale);
            brush.Freeze();
            dictionary["DockMilkOverlay"] = brush;
        }
        foreach (var key in new[] { "DockHighlight", "DockInnerStroke", "GlassPanelStroke" })
        {
            if (dictionary[key] is not SolidColorBrush original) continue;
            var brush = new SolidColorBrush(WithAlpha(original.Color, sheenScale));
            brush.Freeze();
            dictionary[key] = brush;
        }
        if (dictionary["ShellAcrylicTint"] is Color tint)
            dictionary["ShellAcrylicTint"] = WithAlpha(tint, opacityScale);
    }

    private static Color WithAlpha(Color color, double scale) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * scale), 0, 255), color.R, color.G, color.B);

    private static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    private void OnSystemChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(new Action(() => Apply(_appearance, _gloss, _transparency)));
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnSystemChanged;
}
