using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Controls;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Views;

internal static partial class Program
{
    private static int VerifyStartLauncherHeadless()
    {
        var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
        var settings = new Settings { FirstRunCompleted = true, Pins = Settings.Defaults() };
        typeof(VeliShell.Desktop.App).GetProperty("Preferences")!.SetValue(app, settings);
        using var themes = new ThemeService();
        typeof(VeliShell.Desktop.App).GetProperty("Themes")!.SetValue(app, themes);
        var entries = Enumerable.Range(0, 2400).Select(index => new SearchEntry($"App {index:0000}", $"qa-app:{index}", "app")).ToList();
        entries.Insert(0, new("Editor", "notepad.exe", "app"));
        entries.Insert(1, new("Editor Tools", "qa-app:editor", "app"));
        typeof(SearchCatalogIndex).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app.ProgramIndex, entries);
        var directory = Path.Combine(AppContext.BaseDirectory, "start-headless-renders"); Directory.CreateDirectory(directory);
        var checks = 0;
        void Check(bool pass, string message) { Require(pass, message); checks++; }
        try
        {
            var image = IconBackgroundGlyph(Colors.DodgerBlue);
            var request = new StartIconRequest("qa", "qa-native", null, DockIconStyle.Windows, 64);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var pendingCancel = new CancellationTokenSource())
            {
                var mainThread = Environment.CurrentManagedThreadId; var calls = 0; var sta = false;
                using var loader = new StartIconLoader(_ =>
                {
                    Interlocked.Increment(ref calls);
                    sta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && Environment.CurrentManagedThreadId != mainThread;
                    entered.Set(); release.Wait(TimeSpan.FromSeconds(3));
                    return new StartIconResult(image, false);
                });
                var watch = Stopwatch.StartNew();
                var pending = loader.LoadAsync(request, CancellationToken.None);
                watch.Stop();
                Check(entered.Wait(2000) && !pending.IsCompleted, "An icon read blocked its caller instead of running independently.");
                var cancelled = loader.LoadAsync(request with { Target = "cancelled" }, pendingCancel.Token);
                pendingCancel.Cancel(); release.Set();
                Check(pending.Wait(2000) && pending.Result?.Source == image && sta, "Frozen icon was not returned from a separate STA reader.");
                Check(cancelled.Wait(2000) && cancelled.Result is null && calls == 1, "A cancelled queued row still read its shell icon.");
                Check(loader.LoadAsync(request, CancellationToken.None).GetAwaiter().GetResult()?.Source == image && calls == 1, "Warm icons were not cached across menu openings.");
                for (var i = 0; i < 140; i++) loader.LoadAsync(request with { Target = "cache-" + i }, CancellationToken.None).GetAwaiter().GetResult();
                var cache = (System.Collections.IDictionary)typeof(StartIconLoader).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(loader)!;
                Check(cache.Count <= 128, "The icon cache is unbounded.");
                loader.Dispose();
                Check(loader.LoadAsync(request, CancellationToken.None).GetAwaiter().GetResult() is null, "Disposed readers still accept work.");
                Console.WriteLine($"PASS: blocked fake shell reader returned to caller in {watch.Elapsed.TotalMilliseconds:F1} ms; separate STA, cancellation, warm cache and bounded lifetime.");
            }
            using (var realLoader = new StartIconLoader())
            {
                var native = realLoader.LoadAsync(new("files", "explorer.exe", null, DockIconStyle.Windows, 96), CancellationToken.None);
                Check(native.Wait(5000) && native.Result is { Source.IsFrozen: true }, "Actual local shell icon could not be read/frozen off the dispatcher.");
            }
            using var fixtures = new StartIconLoader(request => new(IconService.Resolve(request.Id, "", style: DockIconStyle.Mac, pixelSize: 96).Source, false));
            foreach (var language in new[] { UiLanguage.German, UiLanguage.English })
            foreach (var dark in new[] { false, true })
            foreach (var material in new[] { 0d, 50d, 100d })
            {
                settings.Language = language; settings.Appearance = dark ? Appearance.Dark : Appearance.Light;
                LocalizationService.Current.Apply(language); themes.Apply(settings.Appearance, material, material);
                var watch = Stopwatch.StartNew(); var view = new StartLauncherWindow(app); watch.Stop();
                Check(new WindowInteropHelper(view).Handle == 0, "Constructing Start created a desktop HWND.");
                var host = typeof(StartLauncherWindow).GetField("_backdrop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                var plate = (Border)view.FindName("GlassPlate");
                Check((double)typeof(ShellBackdropHost).GetField("_cornerRadius", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)! == 24,
                    "Start does not use the common panel's rounded backdrop host.");
                Check(ReferenceEquals(plate.Background, app.FindResource("GlassPanelSurface")), "Start fallback does not follow the exact shared panel material.");
                var powers = (StackPanel)view.FindName("PowerActionsPanel");
                Check(powers.Children.OfType<Button>().Select(button => button.Tag).SequenceEqual(StartSessionActions.Commands.Select(command => (object)command.Id)), "Session actions missing from the actual menu.");
                var all = (Button)view.FindName("AllAppsButton"); all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var list = (ListBox)view.FindName("AppsList");
                Check(list.Items.Count == entries.Count && VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling,
                    "All apps is truncated or not recycling its rows.");
                var query = (TextBox)view.FindName("QueryBox"); query.Text = "Editor";
                typeof(StartLauncherWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(view, null);
                Check(list.Items.Count == 2 && list.Items[0] is SearchEntry { Name: "Editor" }, "Search uses a different order/count than the displayed results.");
                all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(query.Text == "" && ((ScrollViewer)view.FindName("PinnedSection")).Visibility == Visibility.Visible, "Back did not restore pinned apps.");
                var root = (Border)view.Content; view.Content = null; TextElement.SetForeground(root, view.Foreground);
                root.Measure(new Size(640, 670)); root.Arrange(new Rect(0, 0, 640, 670)); root.UpdateLayout();
                foreach (var icon in Descendants<DeferredAppIcon>(root)) { icon.Loader = fixtures; PumpStartTask(icon.RefreshAsync()); }
                root.UpdateLayout();
                var name = $"start-{language}-{(dark ? "dark" : "light")}-{material:0}";
                SaveStartRender(root, Path.Combine(directory, name + ".png"), 640, 670);
                var power = (Button)view.FindName("PowerButton"); power.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
                Check(((Border)view.FindName("PowerPanel")).Visibility == Visibility.Visible, "Power button did not expand the account/session menu.");
                var powerBounds = power.TransformToAncestor(root).TransformBounds(new Rect(power.RenderSize));
                Check(powerBounds.Bottom <= 670 && powerBounds.Right <= 640, "Footer power button is clipped.");
                if (material == 50) SaveStartRender(root, Path.Combine(directory, name + "-power.png"), 640, 670);
                power.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                root.Measure(new Size(640, 670)); root.Arrange(new Rect(0, 0, 640, 670)); root.UpdateLayout();
                var realized = Descendants<ListBoxItem>(list).Count();
                Check(realized > 0 && realized < 40, "All apps constructed too many rows / did not virtualize: " + realized);
                if (material == 50)
                {
                    foreach (var icon in Descendants<DeferredAppIcon>(list)) { icon.Loader = fixtures; PumpStartTask(icon.RefreshAsync()); }
                    root.UpdateLayout();
                    SaveStartRender(root, Path.Combine(directory, name + "-all.png"), 640, 670);
                }
                root.Measure(new Size(410, 380)); root.Arrange(new Rect(0, 0, 410, 380)); root.UpdateLayout();
                var compactPower = power.TransformToAncestor(root).TransformBounds(new Rect(power.RenderSize));
                Check(compactPower.Bottom <= 380 && compactPower.Right <= 410 && power.ActualHeight >= 30, "Power controls clipped on a compact monitor.");
                Check(new WindowInteropHelper(view).Handle == 0, "Offscreen rendering opened Start on the desktop.");
                // Exercise the same lookup as the older hosted render/anchor probe:
                // CloseOnce is now public through the transient-panel contract.
                var close = RequireMethod(typeof(StartLauncherWindow), nameof(StartLauncherWindow.CloseOnce));
                close.Invoke(view, null); close.Invoke(view, null);
                Console.WriteLine($"PASS {language} {(dark ? "dark" : "light")} {material:0}: constructed in {watch.Elapsed.TotalMilliseconds:F1} ms; shared material, all apps, search, back, power and offscreen rendering.");
            }
            foreach (var command in StartSessionActions.Commands)
            {
                var confirmed = 0; var sessions = new List<string>(); var locks = 0;
                StartSessionActions.ExecuteAsync(command.Id, _ => { confirmed++; return false; }, args => { sessions.Add(args); return Task.CompletedTask; }, () => locks++).GetAwaiter().GetResult();
                Check(command.Id == "lock" ? locks == 1 && confirmed == 0 : confirmed == 1 && sessions.Count == 0, "Cancel did not prevent a session operation.");
                StartSessionActions.ExecuteAsync(command.Id, _ => true, args => { sessions.Add(args); return Task.CompletedTask; }, () => locks++).GetAwaiter().GetResult();
                Check(command.Arguments is null ? locks == 2 : sessions.SequenceEqual(new[] { command.Arguments }), "Confirmed session command was not routed to the fixed safe command.");
            }
            var unsafeCalls = 0;
            StartSessionActions.ExecuteAsync("/s /f", _ => true, _ => { unsafeCalls++; return Task.CompletedTask; }, () => unsafeCalls++).GetAwaiter().GetResult();
            Check(unsafeCalls == 0, "Unlisted session command accepted.");
            Console.WriteLine($"PASS: {checks} Start checks. Session actions exercised only through fakes; no lock, shutdown, logout, app launch, real input or visible windows.");
            Console.WriteLine(directory); app.Shutdown(); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL Start headless QA: " + error); app.Shutdown(1); return 1; }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void PumpStartTask(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        Require(task.IsCompleted, "Deferred icon failed to complete."); task.GetAwaiter().GetResult();
    }

    private static void SaveStartRender(FrameworkElement content, string path, int width, int height)
    {
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
}
