using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Views;

namespace VeliShell.Desktop.Services;

/// <summary>Only the disposable, own-process demo opts in. No foreign window is enumerated or modified.</summary>
internal static class NativePrototypeSession
{
    internal const string Demo = "--native-frame-demo";
    private const string Guard = "--native-frame-guard";
    private const string Recover = "--restore-native-prototype";
    private const string DirectoryName = "native-prototype";
    private static string Folder => Path.Combine(App.DataDirectory, DirectoryName);
    private static string Text(string key) => LocalizationService.Current.Get(key);
    internal static bool IsUtility(string[] args) => args.Length > 0 && args[0] is Demo or Guard or Recover or ClassicFrameSession.Manager or ClassicFrameSession.Guard or NativeFramesController.Guard or NativeFramesController.Preview;
    internal static Process Start(string[] arguments)
    {
        var exe = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var value in arguments) start.ArgumentList.Add(value);
        return Process.Start(start) ?? throw new IOException("The native prototype helper did not start.");
    }
    internal static void Launch(App app)
    {
        if (!BuildIdentity.IsTest || PackageIdentityService.HasIdentity) return;
        using var process = Start([Demo, app.Themes.IsDark ? "dark" : "light",
            app.Preferences.GlassGloss.ToString(CultureInfo.InvariantCulture), app.Preferences.GlassTransparency.ToString(CultureInfo.InvariantCulture)]);
    }
    internal static void Run(string[] args, App app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (args[0] is NativeFramesController.Guard or NativeFramesController.Preview) { NativeFramesUtility.Run(args, app); return; }
        if (args[0] == Recover) { var count = SignalRecovery(); MessageBox.Show(count > 0 ? Text("Prototype.RecoverySent") : Text("Prototype.NoSession"), Text("Prototype.Title")); app.Shutdown(); return; }
        if (!BuildIdentity.IsTest) { app.Shutdown(2); return; }
        if (args[0] is ClassicFrameSession.Manager or ClassicFrameSession.Guard) { ClassicFrameSession.Run(args, app); return; }
        if (args[0] == Guard) { RunGuard(args, app); return; }
        if (args[0] == Recover)
        {
            var count = SignalRecovery();
            MessageBox.Show(count > 0 ? Text("Prototype.RecoverySent") : Text("Prototype.NoSession"), Text("Prototype.Title"));
            app.Shutdown(); return;
        }
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
        var theme = new ThemeService();
        var dark = args.ElementAtOrDefault(1) == "dark";
        static double Read(string? value) => double.TryParse(value, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? Math.Clamp(number, 0, 100) : 50;
        theme.Apply(dark ? Appearance.Dark : Appearance.Light, Read(args.ElementAtOrDefault(2)), Read(args.ElementAtOrDefault(3)));
        var window = new NativeFramePrototypeWindow(theme);
        app.MainWindow = window;
        window.Closed += (_, _) => { theme.Dispose(); app.Shutdown(); };
        window.Show();
    }
    internal static int SignalRecovery() => SignalSessions("restore") + ClassicFrameSession.RecoverAll() + NativeFramesController.RecoverAll();
    internal static int RequestClose() => SignalSessions("close") + ClassicFrameSession.RecoverAll();
    private static int SignalSessions(string action)
    {
        try { return SignalSessionsCore(action); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { App.Log("Could not signal native test session", error); return 0; }
    }
    private static int SignalSessionsCore(string action)
    {
        if (!Directory.Exists(Folder)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.session").Take(32))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) continue;
            try { using var signal = EventWaitHandle.OpenExisting(EventName(id, action)); signal.Set(); count++; }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
        }
        return count;
    }
    internal static string EventName(Guid id, string suffix) => @"Local\VeliShell.NativePrototype." + id.ToString("N") + "." + suffix;
    internal static async Task<bool> ArmAsync(nint hwnd, Guid id)
    {
        Directory.CreateDirectory(Folder);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(id, "ready"));
        using var process = Process.GetCurrentProcess();
        using var guard = Start([Guard, process.Id.ToString(CultureInfo.InvariantCulture), process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), id.ToString("N")]);
        var armed = await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(8)));
        if (!armed) return false;
        File.WriteAllText(Path.Combine(Folder, id.ToString("N") + ".session"), process.Id.ToString(CultureInfo.InvariantCulture));
        return true;
    }
    internal static void End(Guid id)
    {
        try { File.Delete(Path.Combine(Folder, id.ToString("N") + ".session")); }
        catch (IOException error) { App.Log("Could not remove prototype session marker", error); }
    }
    private static void RunGuard(string[] args, App app)
    {
        if (args.Length != 5 || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var ticks) ||
            !long.TryParse(args[3], out var value) || !Guid.TryParseExact(args[4], "N", out var id)) { app.Shutdown(2); return; }
        Process target;
        try
        {
            target = Process.GetProcessById(pid);
            NativeMethods.GetWindowThreadProcessId((nint)value, out var windowPid);
            if (windowPid != pid || target.StartTime.ToUniversalTime().Ticks != ticks ||
                !string.Equals(target.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                NativePrototypeMethods.GetProp((nint)value, "VeliShell.NativePrototype." + id.ToString("N")) != 1)
            { target.Dispose(); app.Shutdown(2); return; }
        }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { app.Shutdown(2); return; }
        using var recovery = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(id, "restore"));
        using var close = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(id, "close"));
        using var ready = EventWaitHandle.OpenExisting(EventName(id, "ready"));
        using var source = new HwndSource(new HwndSourceParameters("VeliShell Native Prototype Recovery " + id.ToString("N")) { ParentWindow = (nint)(-3), Width = 0, Height = 0 });
        const int hotkey = 0x5650;
        if (!NativeMethods.RegisterHotKey(source.Handle, hotkey, 0x4000 | 1 | 2 | 4, 0x7B))
        { target.Dispose(); app.Shutdown(3); return; } // Ctrl+Alt+Shift+F12; fail closed on collision.
        source.AddHook((nint h, int message, nint wp, nint lp, ref bool handled) =>
        { if (message == 0x312 && wp == hotkey) { recovery.Set(); handled = true; } return 0; });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (target.HasExited || !NativeMethods.IsWindow((nint)value) ||
                NativePrototypeMethods.GetProp((nint)value, "VeliShell.NativePrototype." + id.ToString("N")) != 1)
            { timer.Stop(); app.Shutdown(); return; }
            if (recovery.WaitOne(0))
            {
                // Never wait on a potentially hung test UI on the recovery dispatch thread.
                NativePrototypeMethods.PostMessage((nint)value, NativePrototypeMethods.RestoreMessage, 0, 0);
            }
            if (close.WaitOne(0)) NativePrototypeMethods.PostMessage((nint)value, 0x10, 0, 0);
        };
        app.Exit += (_, _) => { timer.Stop(); NativeMethods.UnregisterHotKey(source.Handle, hotkey); target.Dispose(); };
        // Local objects must outlive this method: the dispatcher owns the timer and closures;
        // shutdown disposes native handles, not return from startup.
        app.Exit += (_, _) => { source.Dispose(); recovery.Dispose(); ready.Dispose(); };
        timer.Start(); ready.Set();
        GC.KeepAlive(source);
        // App.OnStartup returns after this call, so run the independent recovery loop here.
        var frame = new DispatcherFrame();
        app.Exit += (_, _) => frame.Continue = false;
        Dispatcher.PushFrame(frame);
    }
}
