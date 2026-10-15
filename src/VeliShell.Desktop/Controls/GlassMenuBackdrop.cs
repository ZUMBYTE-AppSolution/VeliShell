using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VeliShell.Desktop.Services;

namespace VeliShell.Desktop.Controls;

/// <summary>
/// Adds the same bounded native blur used by dock/panels underneath menu
/// popups. The XAML plate remains responsible for antialiased corners and
/// for the user-controlled gloss/opacity material.
/// </summary>
public static class GlassMenuBackdrop
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(GlassMenuBackdrop), new PropertyMetadata(false, EnabledChanged));

    private static readonly ConditionalWeakTable<FrameworkElement, BackdropState> States = new();

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is ContextMenu menu)
        {
            menu.Opened -= MenuOpened;
            menu.Closed -= MenuClosed;
            if ((bool)e.NewValue)
            {
                menu.Opened += MenuOpened;
                menu.Closed += MenuClosed;
            }
            else if (States.TryGetValue(menu, out var state)) state.Close();
        }
        else if (element is Popup popup)
        {
            popup.Opened -= PopupOpened;
            popup.Closed -= PopupClosed;
            if ((bool)e.NewValue)
            {
                popup.Opened += PopupOpened;
                popup.Closed += PopupClosed;
            }
            else if (States.TryGetValue(popup, out var state)) state.Close();
        }
    }

    private static void MenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            OpenAfterLayout(menu, menu, () => menu.IsOpen);
    }

    private static void MenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && States.TryGetValue(menu, out var state)) state.Close();
    }

    private static void PopupOpened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: FrameworkElement plate } popup)
            OpenAfterLayout(popup, plate, () => popup.IsOpen);
    }

    private static void PopupClosed(object? sender, EventArgs e)
    {
        if (sender is Popup popup && States.TryGetValue(popup, out var state)) state.Close();
    }

    private static void OpenAfterLayout(FrameworkElement owner, FrameworkElement plate, Func<bool> stillOpen)
    {
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!stillOpen() || !GetEnabled(owner)) return;
            States.GetValue(owner, _ => new BackdropState()).Open(plate);
        }));
    }

    private sealed class BackdropState
    {
        private ShellBackdropHost? _backdrop;
        private ThemeService? _theme;

        internal void Open(FrameworkElement plate)
        {
            Close();
            _backdrop = new ShellBackdropHost(plate, plate, 14);
            _backdrop.ApplyMaterial("GlassMenuAcrylicSurface", "GlassMenuSurface");
            if (Application.Current is App { Themes: not null } app)
            {
                _theme = app.Themes;
                _theme.Changed += Refresh;
            }
        }

        internal void Close()
        {
            if (_theme is not null) _theme.Changed -= Refresh;
            _theme = null;
            _backdrop?.Dispose();
            _backdrop = null;
        }

        private void Refresh() => _backdrop?.ApplyMaterial("GlassMenuAcrylicSurface", "GlassMenuSurface");
    }
}
