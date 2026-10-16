using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Native;
using VeliShell.Desktop.Views;

namespace VeliShell.Desktop.Services;

// No saved/automatic allowlist: the user authorizes one exact window in this session.
internal static class ClassicFrameSession
{
    internal const string Manager = "--classic-frames";
    internal const string Guard = "--classic-frame-guard";
    internal static string Folder => Path.Combine(App.DataDirectory, "classic-frames");
    internal static string Event(Guid id, string kind) => @"Local\VeliShell.ClassicFrame." + id.ToString("N") + "." + kind;
    internal sealed record Candidate(nint Window, int Pid, long Started, string Path, string Hash, string Title, bool Is32Bit = false, bool Material = false, int Adapter = 0)
    { public override string ToString() => $"{Title}  ·  {System.IO.Path.GetFileName(Path)}  ·  PID {Pid}"; }
    internal static Candidate? Inspect(nint window, bool includeHash = true, bool crossBitness = false, bool material = false, bool modern = false)
    {
        try
        {
            var reason = NativePrototypeMethods.VeliClassicReason(window, crossBitness ? 1 : 0);
            if (modern ? NativePrototypeMethods.VeliModernReason(window) is not (0 or 100) : material ? NativePrototypeMethods.VeliMaterialEligible(window) != 1 : reason != 0 && !(crossBitness && reason == 100)) return null;
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            using var target = Process.GetProcessById((int)pid);
            var path = target.MainModule?.FileName;
            if (path is null) return null;
            var title = new System.Text.StringBuilder(512); NativeMethods.GetWindowText(window, title, title.Capacity);
            var hash = "";
            if (includeHash) { using var input = File.OpenRead(path); hash = Convert.ToHexString(SHA256.HashData(input)); }
            return new(window, target.Id, target.StartTime.ToUniversalTime().ToFileTimeUtc(), path, hash, title.ToString(), reason == 100, material, modern ? NativePrototypeMethods.VeliModernKind(window) : 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { return null; }
    }
    internal static IEnumerable<Candidate> Enumerate()
    {
        var windows = new List<nint>();
        NativeMethods.EnumWindows((hwnd, _) => { windows.Add(hwnd); return true; }, 0);
        return windows.Select(window => Inspect(window)).OfType<Candidate>().OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    internal static bool HasOtherAdapterVersion(Candidate candidate)
    {
        using var target=Process.GetProcessById(candidate.Pid);
        string expected=Path.Combine(AppContext.BaseDirectory,candidate.Is32Bit?"VeliShell.NativeFrame.x86.dll":"VeliShell.NativeFrame.dll");
        foreach(ProcessModule module in target.Modules) {
            if(module.ModuleName is not { } name || !name.StartsWith("VeliShell.NativeFrame",StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))continue;
            // A pinned DLL from a different package must not silently receive a
            // newer bootstrap protocol. The user closes/restarts the app, never us.
            if(!string.Equals(module.FileName,expected,StringComparison.OrdinalIgnoreCase))return true;
        }
        return false;
    }
    internal static async Task<Guid> BeginAsync(Candidate candidate, bool dark, int gloss, int transparency)
    {
        if (!BuildIdentity.IsTest || PackageIdentityService.HasIdentity) throw new InvalidOperationException("Test channel only.");
        var fresh = Inspect(candidate.Window);
        if (fresh != candidate) throw new InvalidOperationException(LocalizationService.Current.Get("Classic.Changed"));
        var id = Guid.NewGuid();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Event(id, "ready"));
        using var controller = Process.GetCurrentProcess();
        using var guardian = NativePrototypeSession.Start([Guard, id.ToString("N"), controller.Id.ToString(CultureInfo.InvariantCulture), controller.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            candidate.Window.ToInt64().ToString(CultureInfo.InvariantCulture), candidate.Pid.ToString(CultureInfo.InvariantCulture), candidate.Started.ToString(CultureInfo.InvariantCulture),
            candidate.Hash, dark ? "1" : "0", gloss.ToString(CultureInfo.InvariantCulture), transparency.ToString(CultureInfo.InvariantCulture)]);
        for (var attempt = 0; attempt < 80 && !ready.WaitOne(0) && !guardian.HasExited; attempt++) await Task.Delay(100);
        if (!ready.WaitOne(0))
        { Signal(id); throw new InvalidOperationException(LocalizationService.Current.Get("Classic.Failed")); }
        return id;
    }
    internal static bool Signal(Guid id)
    {
        try { using var signal = EventWaitHandle.OpenExisting(Event(id, "restore")); return signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    internal static int RecoverAll()
    {
        try
        {
            if (!Directory.Exists(Folder)) return 0;
            return Directory.EnumerateFiles(Folder, "*.session").Take(32).Select(Path.GetFileNameWithoutExtension)
                .Count(value => Guid.TryParseExact(value, "N", out var id) && Signal(id));
        }
        catch (IOException error) { App.Log("Classic recovery discovery failed", error); return 0; }
        catch (UnauthorizedAccessException error) { App.Log("Classic recovery access failed", error); return 0; }
    }
    internal static void Launch(App app)
    {
        using var process = NativePrototypeSession.Start([Manager, app.Themes.IsDark ? "dark" : "light", app.Preferences.GlassGloss.ToString(CultureInfo.InvariantCulture), app.Preferences.GlassTransparency.ToString(CultureInfo.InvariantCulture)]);
    }
    internal static void Run(string[] args, App app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!BuildIdentity.IsTest || PackageIdentityService.HasIdentity) { app.Shutdown(2); return; }
        if (args[0] == Guard) { RunGuard(args, app); return; }
        using var single = new Mutex(true, @"Local\VeliShell.ClassicFrame.Manager", out var created);
        if (!created) { app.Shutdown(3); return; }
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Light.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VeliShell;component/Themes/Controls.xaml") });
        using var theme = new ThemeService();
        static double Number(string? value) => double.TryParse(value, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? Math.Clamp(n, 0, 100) : 50;
        theme.Apply(args.ElementAtOrDefault(1) == "dark" ? Appearance.Dark : Appearance.Light, Number(args.ElementAtOrDefault(2)), Number(args.ElementAtOrDefault(3)));
        var window = new ClassicFrameWindow(theme);
        app.MainWindow = window; window.Closed += (_, _) => app.Shutdown(); window.Show();
        var loop = new DispatcherFrame(); app.Exit += (_, _) => loop.Continue = false; Dispatcher.PushFrame(loop);
    }
    private static void RunGuard(string[] args, App app)
    {
        if (args.Length != 11 || !Guid.TryParseExact(args[1], "N", out var id) || !int.TryParse(args[2], out var ownerPid) ||
            !long.TryParse(args[3], out var ownerStarted) || !long.TryParse(args[4], out var hwndValue) || !int.TryParse(args[5], out var pid) || !long.TryParse(args[6], out var started) ||
            !int.TryParse(args[9], out var gloss) || !int.TryParse(args[10], out var transparency)) { app.Shutdown(2); return; }
        using var controller = Process.GetProcessById(ownerPid);
        if (controller.StartTime.ToUniversalTime().Ticks != ownerStarted || !string.Equals(controller.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) { app.Shutdown(2); return; }
        var target = Inspect((nint)hwndValue);
        if (target is null || target.Pid != pid || target.Started != started || target.Hash != args[7]) { app.Shutdown(2); return; }
        using var process = Process.GetProcessById(pid);
        using var single = new Mutex(true, @"Local\VeliShell.ClassicFrame.Guard", out var created);
        if (!created) { app.Shutdown(3); return; } // v1 deliberately supports one foreign window at a time.
        using var recovery = new EventWaitHandle(false, EventResetMode.ManualReset, Event(id, "restore"));
        using var ready = EventWaitHandle.OpenExisting(Event(id, "ready"));
        using var source = new HwndSource(new HwndSourceParameters("VeliShell Classic Recovery " + id.ToString("N")) { ParentWindow = (nint)(-3), Width = 0, Height = 0 });
        const int hotkey = 0x5650;
        if (!NativeMethods.RegisterHotKey(source.Handle, hotkey, 0x4000 | 1 | 2 | 4, 0x7B)) { app.Shutdown(3); return; }
        source.AddHook((nint h, int message, nint wp, nint lp, ref bool handled) =>
        { if (message == 0x312 && wp == hotkey) { recovery.Set(); handled = true; } return 0; });
        Directory.CreateDirectory(Folder);
        var journal = Path.Combine(Folder, id.ToString("N") + ".session");
        // Durable admission record exists BEFORE the DLL may alter the chosen window.
        File.WriteAllText(journal, $"{pid}\n{started}\n{hwndValue}\n{target.Path}\n{target.Hash}\nPrepared");
        nint native = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        bool stopping = false; var begun = Stopwatch.StartNew();
        try
        {
            native = NativePrototypeMethods.VeliClassicBegin((nint)hwndValue, id.ToString("N"), (uint)target.Pid, (ulong)target.Started, args[8] == "1" ? 1 : 0, gloss, transparency, NativeFramesController.CaptionTint(args[8] == "1"));
            if (native == 0) { App.Log("Classic broker rejected admission before hook."); app.Shutdown(4); return; }
            timer.Tick += (_, _) =>
            {
                stopping |= recovery.WaitOne(0) || controller.HasExited || begun.Elapsed > TimeSpan.FromSeconds(7) && NativePrototypeMethods.GetProp((nint)hwndValue, NativePrototypeMethods.ClassicActive) == 0;
                if (process.HasExited || !NativeMethods.IsWindow((nint)hwndValue)) { stopping = true; app.Shutdown(); return; }
                var state = NativePrototypeMethods.VeliClassicPoll(native, stopping ? 1 : 0);
                if (state == 1 && !stopping) ready.Set();
                if (state is 2 or 3 or -1) { App.Log($"Classic broker completed: state={state}, detail={NativePrototypeMethods.VeliClassicError(native)}"); app.Shutdown(); return; }
                if (stopping && state == 0) { App.Log($"Classic broker pending attach: detail={NativePrototypeMethods.VeliClassicError(native)}"); if (NativePrototypeMethods.VeliClassicEnd(native) == 1) { native = 0; app.Shutdown(); return; } }
                // Pending rollback is never labelled successful; guardian remains alive for a hung UI.
            };
            timer.Start();
            var loop = new DispatcherFrame(); app.Exit += (_, _) => loop.Continue = false; Dispatcher.PushFrame(loop);
        }
        finally
        {
            timer.Stop(); NativeMethods.UnregisterHotKey(source.Handle, hotkey);
            if (native != 0) { NativePrototypeMethods.VeliClassicPoll(native, 1); NativePrototypeMethods.VeliClassicEnd(native); }
            File.Delete(journal);
        }
    }
}
