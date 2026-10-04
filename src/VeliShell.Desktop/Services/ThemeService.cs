using System.Windows;
using System.Windows.Media;
using VeliShell.Core;
using Microsoft.Win32;

namespace VeliShell.Desktop.Services;

public sealed class ThemeService : IDisposable
{
    private Appearance _appearance;
    private string _key = "";
    public bool IsDark { get; private set; }
    public event Action? Changed;

    public ThemeService() => SystemEvents.UserPreferenceChanged += OnSystemChanged;

    public void Apply(Appearance appearance)
    {
        _appearance = appearance;
        var dark = appearance == Appearance.Dark || (appearance == Appearance.System && SystemIsDark());
        var key = $"{dark}:{SystemParameters.HighContrast}";
        if (_key == key) return;
        _key = key;
        IsDark = dark;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/VeliShell;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        };
        if (SystemParameters.HighContrast)
        {
            foreach (var name in new[]
                     {
                         "WindowSurface", "SidebarSurface", "CardSurface", "DockSurface", "MenuSurface",
                         "WindowBackdropGradient", "SidebarGradient", "CardSurfaceGradient",
                         "DockSurfaceGradient", "DockMilkOverlay", "DockHover"
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
        }
        Application.Current.Resources.MergedDictionaries[0] = dictionary;
        Changed?.Invoke();
    }

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
            dispatcher.BeginInvoke(new Action(() => Apply(_appearance)));
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnSystemChanged;
}
