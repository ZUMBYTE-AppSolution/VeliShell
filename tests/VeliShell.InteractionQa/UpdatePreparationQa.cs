using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VeliShell.Core;
using VeliShell.Desktop.Services;
using VeliShell.Desktop.Views;
using VeliShell.Desktop.Native;

internal static partial class Program
{
    private static int VerifyUpdatePreparation()
    {
        var app = new SafePreviewApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
        typeof(VeliShell.Desktop.App).GetProperty("Preferences")!.SetValue(app, new Settings { Pins = [], ShowRunningApps = false });
        using var themes = new ThemeService();
        typeof(VeliShell.Desktop.App).GetProperty("Themes")!.SetValue(app, themes);
        var output = Path.Combine(AppContext.BaseDirectory, "update-preparation-renders", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = 0;
        void Check(bool pass, string message) { Require(pass, message); checks++; }
        void IoFails(Action action, string message)
        { var rejected = false; try { action(); } catch (IOException) { rejected = true; } Check(rejected, message); }
        try
        {
            using (var controller = new NativeFramesController(preview: true))
            {
                var scope = controller.SuspendForUpdate();
                Check((bool)RequireField(typeof(NativeFramesController), "_preparingUpdate").GetValue(controller)!, "No reactivation guard during update preparation.");
                var rejected = controller.EnableAsync(new Settings { NativeFramesConsentVersion = 1 }, false, true);
                Check(rejected.IsFaulted, "Another settings window could enable styling during an update.");
                scope.Dispose(); scope.Dispose();
                Check(!(bool)RequireField(typeof(NativeFramesController), "_preparingUpdate").GetValue(controller)!, "Failed/cancelled update kept its temporary gate.");
            }
            var trace = new List<string>();
            var waiting = new TaskCompletionSource<bool>();
            var task = UpdateInstallationPreparation.RunAsync(true, () => trace.Add("disable"), _ => { trace.Add("restore"); return waiting.Task; },
                () => { lock (trace) trace.Add("locks"); return false; }, () => trace.Add("install"));
            Check(!task.IsCompleted && trace.SequenceEqual(new[] { "disable", "restore" }), "Installer did not wait for restoration.");
            waiting.SetResult(true); PumpStartTask(task);
            Check(task.Result == UpdatePreparationResult.Started && trace.SequenceEqual(new[] { "disable", "restore", "locks", "install" }), "Invalid update preparation order.");
            foreach (var (confirmed, restored, blocked, expected) in new[] {
                (false, true, false, UpdatePreparationResult.Cancelled),
                (true, false, false, UpdatePreparationResult.RestorationPending),
                (true, true, true, UpdatePreparationResult.LegacyAppsOpen) })
            {
                var installs = 0; var disables = 0;
                var attempt = UpdateInstallationPreparation.RunAsync(confirmed, () => disables++, _ => Task.FromResult(restored), () => blocked, () => installs++);
                PumpStartTask(attempt);
                Check(attempt.Result == expected && installs == 0 && disables == (confirmed ? 1 : 0), "Unconfirmed/pending/legacy update launched installer.");
            }
            using (var cancel = new CancellationTokenSource())
            {
                var installs = 0; cancel.Cancel();
                var cancelled = UpdateInstallationPreparation.RunAsync(true, () => installs++, _ => Task.FromResult(true), () => false, () => installs++, cancel.Token);
                try { PumpStartTask(cancelled); } catch (OperationCanceledException) { }
                Check(cancelled.IsCanceled && installs == 0, "Pre-cancelled update changed preferences or installed.");
            }
            using (var cancel = new CancellationTokenSource())
            {
                var installs = 0;
                var cancelled = UpdateInstallationPreparation.RunAsync(true, () => { }, _ => { cancel.Cancel(); return Task.FromResult(true); }, () => false, () => installs++, cancel.Token);
                try { PumpStartTask(cancelled); } catch (OperationCanceledException) { }
                Check(cancelled.IsCanceled && installs == 0, "Closing update during restoration did not cancel installation.");
            }
            using (var cancel = new CancellationTokenSource())
            {
                var installs = 0;
                var cancelled = UpdateInstallationPreparation.RunAsync(true, () => { }, _ => Task.FromResult(true),
                    () => { cancel.Cancel(); return false; }, () => installs++, cancel.Token);
                try { PumpStartTask(cancelled); } catch (OperationCanceledException) { }
                Check(cancelled.IsCanceled && installs == 0, "Late cancellation during the lock scan launched installation.");
            }
            foreach (var failAt in new[] { "disable", "restore", "locks", "install" })
            {
                var launches = 0;
                void Fail(string stage) { if (failAt == stage) throw new IOException("QA failure at " + stage); }
                var failed = UpdateInstallationPreparation.RunAsync(true, () => Fail("disable"), _ => { Fail("restore"); return Task.FromResult(true); },
                    () => { Fail("locks"); return false; }, () => { Fail("install"); launches++; });
                try { PumpStartTask(failed); } catch (IOException) { }
                Check(failed.IsFaulted && launches == 0, "A preparation failure still launched installation: " + failAt);
            }

            // Keep own native executable paths below CreateProcess's path limit;
            // this checkout's deep build/output path is intentionally not a runtime install.
            var shortRuntimeRoot = Directory.CreateTempSubdirectory("VeliShell-UpdateQa-").FullName;
            var source = Path.Combine(output, "packaged-source"); var runtime = Path.Combine(shortRuntimeRoot, "runtime");
            Directory.CreateDirectory(source);
            foreach (var name in new[] { "VeliShell.NativeFrame.dll", "VeliShell.NativeFrame.x86.dll", "VeliShell.FrameBridge.x86.exe" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(source, name));
            var cache = new NativeFrameRuntimeCache(source, runtime);
            var library = cache.Library(false); var library32 = cache.Library(true); var bridge = cache.Bridge;
            foreach (var staged in new[] { library, library32, bridge })
            {
                Check(NativeFrameRuntimeCache.IsWithin(staged, runtime) && !NativeFrameRuntimeCache.IsWithin(staged, source), "Runtime was staged in MSI payload.");
                Check(HashUpdateFile(staged) == HashUpdateFile(Path.Combine(source, Path.GetFileName(staged))), "Staged binary differs from packaged binary.");
                Check(!NativeFramesController.IsInstallerBlockingModule(Path.GetFileName(staged), staged, source), "Cached runtime incorrectly blocks this install.");
            }
            Check(Path.GetDirectoryName(bridge) == Path.GetDirectoryName(library32), "x86 bridge cannot load its paired DLL.");
            Check(cache.Library(false) == new NativeFrameRuntimeCache(source, runtime).Library(false), "Identical bundles do not reuse their immutable directory.");
            Check(NativeFramesController.IsInstallerBlockingModule("VeliShell.NativeFrame.dll", Path.Combine(source, "VeliShell.NativeFrame.dll"), source), "Legacy installed DLL was not blocked.");
            Check(NativeFramesController.IsInstallerBlockingModule("VeliShell.NativeFrame.x86.dll", Path.Combine(source, "VeliShell.NativeFrame.x86.dll"), source), "Legacy x86 DLL was not blocked.");
            Check(!NativeFramesController.IsInstallerBlockingModule("unrelated.dll", Path.Combine(source, "unrelated.dll"), source), "An unrelated app module blocks updates.");
            Check(!NativeFramesController.IsInstallerBlockingModule("VeliShell.NativeFrame.dll", source + "-other/VeliShell.NativeFrame.dll", source), "Path prefix confused a different installation.");
            Check(NativeFrameRuntimeCache.IsWithin(source, Path.GetPathRoot(source)!), "Volume-root containment lost its separator boundary.");
            using (cache.PinBundle(false))
            {
                IoFails(() => { using var write = new FileStream(library, FileMode.Open, FileAccess.Write, FileShare.None); }, "Verified cache could change during loading.");
                Check(NativePrototypeMethods.VeliFrameApiVersion() > 0, "Packaged native probe did not load.");
                var loaded = new NativeFrameRuntime(library);
                using (var ownProcess = Process.GetCurrentProcess())
                    Check(ownProcess.Modules.Cast<ProcessModule>().Any(module => string.Equals(module.FileName, library, StringComparison.OrdinalIgnoreCase)),
                        "Cache loading reused the installed module instead of mapping the exact runtime copy.");
                var candidate = new ClassicFrameSession.Candidate(0, 0, 0, "", "", "QA invalid window");
                Check(loaded.Begin(candidate, Guid.NewGuid(), new NativeFramesConfig(false, false, 50, 50, [], true, 0)) == 0 && loaded.BeginError != 0,
                    "Actual cached x64 exports could not be called or admitted an invalid target.");
                using var rootWrite = new FileStream(Path.Combine(source, "VeliShell.NativeFrame.dll"), FileMode.Open, FileAccess.Write, FileShare.None);
                Check(rootWrite.CanWrite, "Loading cached adapter locks the install/source copy.");
            }
            using (cache.PinBundle(true))
            {
                var start = new ProcessStartInfo(bridge) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                using var process = Process.Start(start) ?? throw new IOException("Own cached x86 import probe did not start.");
                Check(process.WaitForExit(5000) && process.ExitCode == 2, "Cached x86 bridge failed its no-target argument gate / import loading.");
            }
            var broken = new NativeFrameRuntimeCache(source, Path.Combine(shortRuntimeRoot, "tampered-runtime"));
            var damaged = broken.Library(false); File.AppendAllText(damaged, "QA changed cached bytes");
            IoFails(() => { using var pin = broken.PinBundle(false); }, "Cached payload tampering was not rejected before execution.");
            IoFails(() => new NativeFrameRuntimeCache(source, Path.Combine(source, "bad-cache")).Library(false), "Cache inside install directory was accepted.");
            var originalHash = HashUpdateFile(library);
            using (var append = new FileStream(Path.Combine(source, "VeliShell.NativeFrame.dll"), FileMode.Append)) append.WriteByte(0);
            var next = new NativeFrameRuntimeCache(source, runtime).Library(false);
            Check(next != library && HashUpdateFile(library) == originalHash, "New binary overwrote an already mapped old runtime.");

            using var updates = new GitHubReleaseUpdateService();
            var release = new UpdateRelease(SemanticVersion.Parse("0.14.3"), "v0.14.3", "QA update", "QA only: installer never launched.",
                DateTimeOffset.UtcNow, new Uri("https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.14.3"),
                new UpdateAsset("VeliShell-0.14.3-win-x64.msi", 1, new Uri("https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/download/v0.14.3/VeliShell-0.14.3-win-x64.msi"), new string('a', 64)));
            foreach (var language in new[] { UiLanguage.German, UiLanguage.English })
            foreach (var dark in new[] { false, true })
            {
                LocalizationService.Current.Apply(language); themes.Apply(dark ? Appearance.Dark : Appearance.Light);
                var window = new UpdateWindow(app, updates, release, false);
                RequireField(typeof(UpdateWindow), "_package").SetValue(window, new VerifiedUpdatePackage("not-launched.msi", release,
                    new UpdatePackageVerification(new string('a', 64), AuthenticodeStatus.Valid, "QA publisher", null)));
                RequireMethod(typeof(UpdateWindow), "SetInstallingUi").Invoke(window, [true]);
                var primary = (Button)window.FindName("PrimaryButton"); var later = (Button)window.FindName("LaterButton");
                var label = (TextBlock)window.FindName("ProgressText"); var progress = (ProgressBar)window.FindName("DownloadProgress");
                Check(!primary.IsEnabled && !later.IsEnabled && progress.IsIndeterminate && label.Text.Length > 40,
                    "Update preparation has no readable progress / allows repeated installation.");
                var root = (FrameworkElement)window.Content;
                foreach (var size in new[] { new Size(680, 610), new Size(580, 500) })
                {
                    root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout();
                    Check(primary.ActualHeight >= 24 && primary.ActualWidth >= 150, "Update preparation button was clipped.");
                    var statusBounds = label.TransformToAncestor(root).TransformBounds(new Rect(label.RenderSize));
                    Check(label.ActualHeight > 10 && statusBounds.Top >= 0 && statusBounds.Bottom <= size.Height && statusBounds.Bottom < primary.TransformToAncestor(root).Transform(new Point()).Y,
                        "Preparation status is hidden below the changelog or behind update buttons.");
                    SaveIconBackgroundRender(root, Path.Combine(output, $"update-{language}-{(dark ? "dark" : "light")}-{size.Width}.png"), (int)size.Width, (int)size.Height);
                }
                Check(new WindowInteropHelper(window).Handle == 0, "Headless update QA showed a window.");
                RequireMethod(typeof(UpdateWindow), "SetInstallingUi").Invoke(window, [false]);
                Check(primary.IsEnabled && later.IsEnabled && !progress.IsIndeterminate, "Failed/cancelled preparation left update controls disabled.");
                window.Close();
            }
            Console.WriteLine($"PASS: {checks} update preparation checks: confirmation/stop/wait/lock/start ordering, failure/cancellation gates, immutable x86/x64 bundles and native import probes.");
            Console.WriteLine("PASS: DE/EN dark/light preparing UI. No real installer, user preferences, target windows, input, taskbar or recovery hotkeys changed. Native probes used only own cache copies and an invalid target.");
            Console.WriteLine(output); app.Shutdown(); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL update preparation QA: " + error); app.Shutdown(1); return 1; }
    }
    private static string HashUpdateFile(string path)
    { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
}
