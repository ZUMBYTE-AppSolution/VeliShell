using System.Windows;
using VeliShell.Core;
using VeliShell.Desktop.Views;

namespace VeliShell.Desktop.Services;

internal static class NativeFramesUtility
{
    internal const string WpfFixture = "--native-wpf-fixture";
    internal const string AppPreview = "--native-app-preview";
    internal const string VmCheck = "--native-app-vm-check";
    internal static void Run(string[] args, App app)
    {
        if (args[0] == NativeFramesController.Guard) { NativeFramesGuard.Run(args, app); return; }
        if (args[0] == WpfFixture) { RunWpfFixture(args, app); return; }
        // Safe QA entry point: the supervisor admits only byte-identical bundled
        // ClassicSample processes from this package directory, never user apps.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
        var settings = new Settings { FirstRunCompleted = true, Appearance = args.ElementAtOrDefault(1) == "dark" ? Appearance.Dark : Appearance.Light, GlassGloss = 80, GlassTransparency = 75 };
        using var theme = new ThemeService(); theme.Apply(settings.Appearance, settings.GlassGloss, settings.GlassTransparency);
        if(args[0]==VmCheck) {
            if(!BuildIdentity.IsTest || !NativeFramesVmCheckWindow.IsVirtualMachine()) {
                MessageBox.Show(LocalizationService.Current.Get("VmCheck.VmOnly"),"VeliShell");app.Shutdown(2);return;
            }
            var check=new NativeFramesVmCheckWindow(theme);app.MainWindow=check;check.Closed+=(_,_)=>app.Shutdown();check.Show();
            var dispatcher=new System.Windows.Threading.DispatcherFrame();app.Exit+=(_,_)=>dispatcher.Continue=false;System.Windows.Threading.Dispatcher.PushFrame(dispatcher);return;
        }
        NativeFrameTarget[]? targets=null;
        if(args[0]==AppPreview) {
            if(!BuildIdentity.IsTest || args.Length is <3 or >10) { app.Shutdown(2);return; }
            var selected=new List<NativeFrameTarget>();
            foreach(var value in args.Skip(2)) {
                if(!long.TryParse(value,out var handle)) { app.Shutdown(2);return; }
                var candidate=ClassicFrameSession.Inspect((nint)handle,modern:true,crossBitness:true);
                if(candidate is null || candidate.Adapter==0) { app.Shutdown(2);return; }
                selected.Add(new(handle,candidate.Pid,candidate.Started));
            }
            targets=selected.ToArray();
        }
        using var controller = new NativeFramesController(preview: true,targets:targets);
        var window = new NativeFramesWindow(settings, controller, theme, update => { update(settings); settings.Normalize(); controller.Update(settings, theme.IsDark); }, preview: true,realAppPreview:targets is not null);
        app.MainWindow = window; window.Closed += (_, _) => app.Shutdown(); window.Show();
        var loop = new System.Windows.Threading.DispatcherFrame(); app.Exit += (_, _) => loop.Continue = false; System.Windows.Threading.Dispatcher.PushFrame(loop);
    }
    private static void RunWpfFixture(string[] args, App app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        bool custom = args.ElementAtOrDefault(1) == "custom";
        var window = new Window { Title = custom ? "Independent WPF Custom Caption" : "Independent WPF Standard Caption", Width = 800, Height = 480, Left = 120, Top = 160, Topmost = true, Background = System.Windows.Media.Brushes.White };
        var body = new System.Windows.Controls.StackPanel();
        if (custom) {
            window.WindowStyle = WindowStyle.None;
            System.Windows.Shell.WindowChrome.SetWindowChrome(window, new System.Windows.Shell.WindowChrome { CaptionHeight = 40, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(8), UseAeroCaptionButtons = false });
            var search = new System.Windows.Controls.TextBox { Text = "App-owned search field in the title bar", Height = 38, Margin = new Thickness(8) };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(search, true); body.Children.Add(search);
        }
        body.Children.Add(new System.Windows.Controls.TextBlock { Text = "Independent WPF application content", Margin = new Thickness(20), Foreground = System.Windows.Media.Brushes.Black });
        body.Children.Add(new System.Windows.Controls.TextBox { Text = "Editable content must survive styling and rollback.", Height = 180, Margin = new Thickness(20), Foreground = System.Windows.Media.Brushes.Black, Background = System.Windows.Media.Brushes.White });
        window.Content = body; app.MainWindow = window;
        window.Closed += (_, _) => app.Shutdown(); window.Show();
        Native.NativePrototypeMethods.SetProp(new System.Windows.Interop.WindowInteropHelper(window).Handle,"VeliShell.WpfSample",1);
        var loop = new System.Windows.Threading.DispatcherFrame(); app.Exit += (_, _) => loop.Continue = false; System.Windows.Threading.Dispatcher.PushFrame(loop);
    }
}
