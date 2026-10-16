using System.Windows;
using VeliShell.Core;
using VeliShell.Desktop.Views;

namespace VeliShell.Desktop.Services;

internal static class NativeFramesUtility
{
    internal static void Run(string[] args, App app)
    {
        if (args[0] == NativeFramesController.Guard) { NativeFramesGuard.Run(args, app); return; }
        // Safe QA entry point: the supervisor admits only byte-identical bundled
        // ClassicSample processes from this package directory, never user apps.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
        var settings = new Settings { FirstRunCompleted = true, Appearance = args.ElementAtOrDefault(1) == "dark" ? Appearance.Dark : Appearance.Light, GlassGloss = 80, GlassTransparency = 75 };
        using var theme = new ThemeService(); theme.Apply(settings.Appearance, settings.GlassGloss, settings.GlassTransparency);
        using var controller = new NativeFramesController(preview: true);
        var window = new NativeFramesWindow(settings, controller, theme, update => { update(settings); settings.Normalize(); controller.Update(settings, theme.IsDark); }, preview: true);
        app.MainWindow = window; window.Closed += (_, _) => app.Shutdown(); window.Show();
        var loop = new System.Windows.Threading.DispatcherFrame(); app.Exit += (_, _) => loop.Continue = false; System.Windows.Threading.Dispatcher.PushFrame(loop);
    }
}
