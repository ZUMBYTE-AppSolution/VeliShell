using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VeliShell.Core;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal static class NativeFramesGuard
{
    private sealed record Binding(ClassicFrameSession.Candidate App, Guid Id, nint Handle, Stopwatch Age)
    { internal bool Stopping, Loaded, ExclusionRestore; }
    internal static void Run(string[] args, App app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!NativeFramesController.Supported || args.Length != 4 || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var ticks) || !Guid.TryParseExact(args[3], "N", out var id)) { app.Shutdown(2); return; }
        using var owner = Process.GetProcessById(pid);
        if (owner.StartTime.ToUniversalTime().Ticks != ticks || !string.Equals(owner.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) { app.Shutdown(2); return; }
        using var single = new Mutex(true, @"Local\VeliShell.NativeWindows.Guard", out var created);
        if (!created) { app.Shutdown(3); return; }
        Directory.CreateDirectory(NativeFramesController.Folder);
        foreach (var old in Directory.EnumerateFiles(NativeFramesController.Folder, "*.session").Take(32))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(old), "N", out _)) File.Delete(old); // Singleton acquired: no automatic guardian owns these stale markers.
        var configPath = NativeFramesController.FileFor(id, "json");
        var config = NativeFramesController.Read<NativeFramesConfig>(configPath);
        if (config is null || !config.Enabled) { app.Shutdown(2); return; }
        using var source = new HwndSource(new HwndSourceParameters("VeliShell Native Windows Recovery " + id.ToString("N")) { ParentWindow = (nint)(-3), Width = 0, Height = 0 });
        using var ready = EventWaitHandle.OpenExisting(NativeFramesController.Event(id, "ready"));
        using var recovery = new EventWaitHandle(false, EventResetMode.ManualReset, NativeFramesController.Event(id, "restore"));
        const int hotkey = 0x5650;
        if (!NativeMethods.RegisterHotKey(source.Handle, hotkey, 0x4000 | 1 | 2 | 4, 0x7B)) { app.Shutdown(3); return; }
        source.AddHook((nint h, int message, nint wp, nint lp, ref bool handled) => { if (message == 0x312 && wp == hotkey) { recovery.Set(); handled = true; } return 0; });
        var session = NativeFramesController.FileFor(id, "session");
        Directory.CreateDirectory(NativeFramesController.Folder); File.WriteAllText(session, pid.ToString());
        var bindings = new Dictionary<nint, Binding>();
        var attempted = new HashSet<(nint Window, int Pid, long Started)>();
        var failures = new List<NativeFrameStatus>();
        bool stopping = false, ticking = false; string? lastError = null;
        var scan = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "VeliShell.ClassicSample.exe");
        var fixtureHash = config.Preview && File.Exists(fixturePath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))) : "";
        bool Excluded(string path) => config.Exclusions.Take(128).Any(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase) || string.Equals(entry, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        bool PreviewAdmits(ClassicFrameSession.Candidate candidate) => !config.Preview ||
            string.Equals(Path.GetDirectoryName(candidate.Path), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(candidate.Path).StartsWith("VeliShell.ClassicSample", StringComparison.OrdinalIgnoreCase) && candidate.Hash == fixtureHash;
        void SaveStatus()
        {
            var rows = bindings.Values.Select(b => new NativeFrameStatus(b.App.Title, b.App.Path, b.Stopping ? "Restoring" : NativePrototypeMethods.GetProp(b.App.Window, "VeliShell.ClassicFrame.Glass") == 1 ? "Active" : "Pending"));
            NativeFramesController.Write(NativeFramesController.FileFor(id, "status"), new NativeFramesStatus(!stopping, stopping, rows.Concat(failures.TakeLast(16)).Take(80).ToArray(), lastError));
        }
        void Stop(bool emergency = true)
        {
            if (stopping) return; stopping = true;
            if (emergency && !config.Preview) File.WriteAllText(NativeFramesController.Latch, "Native styling stopped. Explicit activation is required.");
            foreach (var binding in bindings.Values) binding.Stopping = true;
        }
        void Tick()
        {
            if (ticking) return; ticking = true;
            try
            {
                var next = NativeFramesController.Read<NativeFramesConfig>(configPath);
                if (next is null || next.Preview != config.Preview || next.Exclusions is null || next.Exclusions.Length > 128) Stop();
                else config = next;
                if (!config.Enabled) Stop(emergency: false);
                else if (owner.HasExited || recovery.WaitOne(0)) Stop();
                // Refresh every live lease before any bounded admission may wait on another app.
                foreach (var pair in bindings.ToArray())
                {
                    var binding = pair.Value;
                    binding.ExclusionRestore |= Excluded(binding.App.Path);
                    binding.Stopping |= stopping || Excluded(binding.App.Path) || binding.Age.Elapsed > TimeSpan.FromSeconds(5) && NativePrototypeMethods.GetProp(binding.App.Window, NativePrototypeMethods.ClassicActive) == 0;
                    NativePrototypeMethods.VeliClassicConfigure(binding.Handle, config.Dark ? 1 : 0, config.Gloss, config.Transparency, config.Tint);
                    var state = NativePrototypeMethods.VeliClassicPoll(binding.Handle, binding.Stopping ? 1 : 0);
                    if (state is 0 or 4 && binding.Age.Elapsed > TimeSpan.FromSeconds(5)) binding.Stopping = true;
                    if (state == 1 && !binding.Loaded) {
                        binding.Loaded = true;
                        NativeFramesController.Write(NativeFramesController.FileFor(binding.Id, "loaded"), new NativeFrameLoaded(binding.App.Pid, binding.App.Started, binding.App.Path));
                    }
                    if (state is 2 or 3 or -1 || !NativeMethods.IsWindow(binding.App.Window)) {
                        if (state == -1) failures.Add(new(binding.App.Title, binding.App.Path, "Failed"));
                        if (NativePrototypeMethods.VeliClassicEnd(binding.Handle) == 1) { bindings.Remove(pair.Key); if (state == 3 || binding.ExclusionRestore) attempted.Remove((pair.Key, binding.App.Pid, binding.App.Started)); File.Delete(NativeFramesController.FileFor(binding.Id, "admission")); }
                    } else if (state == 0 && binding.Stopping && NativePrototypeMethods.VeliClassicEnd(binding.Handle) == 1) bindings.Remove(pair.Key);
                }
                if (stopping) { SaveStatus(); if (bindings.Count == 0) app.Shutdown(); return; }
                if (scan.Elapsed < TimeSpan.FromMilliseconds(500)) return;
                scan.Restart(); SaveStatus();
                if (failures.Count > 64) failures.RemoveRange(0, failures.Count - 64);
                if (bindings.Count >= 64) { lastError = "WindowLimit"; return; }
                var handles = new List<nint>(); NativeMethods.EnumWindows((window, _) => { handles.Add(window); return true; }, 0);
                foreach (var window in handles.Take(512))
                {
                    if (bindings.ContainsKey(window) || NativePrototypeMethods.GetProp(window, NativePrototypeMethods.ClassicActive) != 0) continue;
                    var candidate = ClassicFrameSession.Inspect(window, includeHash: false);
                    if (candidate is null || Excluded(candidate.Path) || attempted.Contains((window, candidate.Pid, candidate.Started))) continue;
                    candidate = ClassicFrameSession.Inspect(window);
                    if (candidate is null || !PreviewAdmits(candidate) || Excluded(candidate.Path)) continue;
                    var sessionId = Guid.NewGuid();
                    // Identity record precedes target changes. No HWND/process pointer is loaded from a file.
                    NativeFramesController.Write(NativeFramesController.FileFor(sessionId, "admission"), new { candidate.Pid, candidate.Started, candidate.Path, candidate.Hash, Hwnd = window.ToInt64() });
                    NativeFramesController.Write(NativeFramesController.FileFor(sessionId, "loaded"), new NativeFrameLoaded(candidate.Pid, candidate.Started, candidate.Path));
                    attempted.Add((window, candidate.Pid, candidate.Started));
                    var native = NativePrototypeMethods.VeliClassicBegin(window, sessionId.ToString("N"), (uint)candidate.Pid, (ulong)candidate.Started, config.Dark ? 1 : 0, config.Gloss, config.Transparency, config.Tint);
                    if (native == 0) { File.Delete(NativeFramesController.FileFor(sessionId, "loaded")); failures.Add(new(candidate.Title, candidate.Path, "Failed")); }
                    else bindings.Add(window, new(candidate, sessionId, native, Stopwatch.StartNew()) { Loaded = true });
                    break; // At most one bounded, thread-specific admission per scan.
                }
                if (attempted.Count > 512) {
                    var alive = handles.ToHashSet(); attempted.RemoveWhere(item => !alive.Contains(item.Window));
                }
            }
            catch (Exception error) { lastError = error.GetType().Name; App.Log("Native window supervisor failed closed", error); Stop(); }
            finally { ticking = false; }
        }
        timer.Tick += (_, _) => Tick();
        try {
            SaveStatus(); timer.Start(); ready.Set();
            var loop = new DispatcherFrame(); app.Exit += (_, _) => loop.Continue = false; Dispatcher.PushFrame(loop);
        }
        finally {
            timer.Stop(); NativeMethods.UnregisterHotKey(source.Handle, hotkey);
            foreach (var binding in bindings.Values) { NativePrototypeMethods.VeliClassicPoll(binding.Handle, 1); NativePrototypeMethods.VeliClassicEnd(binding.Handle); }
            File.Delete(session);
        }
    }
}
