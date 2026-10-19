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
    private sealed record Binding(ClassicFrameSession.Candidate App, Guid Id, NativeFrameLease Lease, Stopwatch Age)
    { internal bool Stopping, Loaded, ExclusionRestore, MaterialOptOut, TimedOut; internal int State; }
    internal static void Run(string[] args, App app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!NativeFramesController.Supported || args.Length != 4 || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var ticks) || !Guid.TryParseExact(args[3], "N", out var id)) { app.Shutdown(2); return; }
        using var owner = Process.GetProcessById(pid);
        if (owner.StartTime.ToUniversalTime().Ticks != ticks || !string.Equals(owner.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) { app.Shutdown(2); return; }
        var configPath = NativeFramesController.FileFor(id, "json");
        var config = NativeFramesController.Read<NativeFramesConfig>(configPath);
        if (config is null || !config.Enabled) { app.Shutdown(2); return; }
        var targets = config.Targets ?? [];
        if (targets.Length > 8 || targets.Length > 0 && (!config.Preview || !BuildIdentity.IsTest)) { app.Shutdown(2); return; }
        using var single = new Mutex(true, @"Local\VeliShell.NativeWindows.Guard" + (config.Preview ? ".Preview" : ""), out var created);
        if (!created) { app.Shutdown(3); return; }
        Directory.CreateDirectory(NativeFramesController.Folder);
        foreach (var old in Directory.EnumerateFiles(NativeFramesController.Folder, "*.session").Take(32))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(old), "N", out var oldId) &&
                NativeFramesController.Read<NativeFramesConfig>(NativeFramesController.FileFor(oldId,"json"))?.Preview == config.Preview) File.Delete(old);
        using var source = new HwndSource(new HwndSourceParameters("VeliShell Native Windows Recovery " + id.ToString("N")) { ParentWindow = (nint)(-3), Width = 0, Height = 0 });
        using var ready = EventWaitHandle.OpenExisting(NativeFramesController.Event(id, "ready"));
        using var recovery = new EventWaitHandle(false, EventResetMode.ManualReset, NativeFramesController.Event(id, "restore"));
        const int hotkey = 0x5650;
        // Isolated QA posts a recovery message to this HWND; never steal the
        // real user's emergency shortcut or contend with their live module.
        if (!config.Preview && !NativeMethods.RegisterHotKey(source.Handle, hotkey, 0x4000 | 1 | 2 | 4, 0x7B)) { app.Shutdown(3); return; }
        source.AddHook((nint h, int message, nint wp, nint lp, ref bool handled) => { if (message == 0x312 && wp == hotkey) { recovery.Set(); handled = true; } return 0; });
        var session = NativeFramesController.FileFor(id, "session");
        Directory.CreateDirectory(NativeFramesController.Folder); File.WriteAllText(session, pid.ToString());
        var bindings = new Dictionary<nint, Binding>();
        var attempted = new HashSet<(nint Window, int Pid, long Started)>();
        var failures = new Dictionary<nint, NativeFrameStatus>();
        var skipped = new Dictionary<nint, NativeFrameStatus>();
        bool stopping = false, ticking = false; string? lastError = null;
        var scan = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var fixtureHashes = new[] { "VeliShell.ClassicSample.exe", "VeliShell.ClassicSample.x86.exe" }.Select(name => Path.Combine(AppContext.BaseDirectory, name)).Where(File.Exists)
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToHashSet(StringComparer.Ordinal);
        var ownHash = config.Preview ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath!))) : "";
        bool Excluded(string path) => config.Exclusions.Take(128).Any(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase) || string.Equals(entry, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        bool PreviewAdmits(ClassicFrameSession.Candidate candidate) => !config.Preview ||
            (targets.Length > 0 ? candidate.Adapter > 0 && targets.Contains(new(candidate.Window.ToInt64(),candidate.Pid,candidate.Started)) :
            string.Equals(Path.GetDirectoryName(candidate.Path), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            (Path.GetFileName(candidate.Path).StartsWith("VeliShell.ClassicSample", StringComparison.OrdinalIgnoreCase) && fixtureHashes.Contains(candidate.Hash) ||
                string.Equals(candidate.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && candidate.Hash == ownHash && NativePrototypeMethods.GetProp(candidate.Window,"VeliShell.WpfSample") == 1));
        NativeFrameStatus Row(ClassicFrameSession.Candidate candidate, string state, string? reason = null, int error = 0) {
            var title=new System.Text.StringBuilder(512); NativeMethods.GetWindowText(candidate.Window,title,title.Capacity);
            return new(title.Length>0?title.ToString():candidate.Title,candidate.Path,state,reason,candidate.Window.ToInt64(),candidate.Pid,candidate.Started,candidate.Adapter,error);
        }
        void SaveStatus()
        {
            // One current row per HWND/identity; old failures never override a
            // live binding. Closed/reused handles disappear rather than linger.
            var rows = bindings.Values.Select(b => Row(b.App,b.Stopping ? "Restoring" : b.State != 1 ? "Pending" : b.App.Material ? "Material" : NativePrototypeMethods.GetProp(b.App.Window,"VeliShell.ClassicFrame.Glass")==1 ? "Active" : "Pending"));
            var current=NativeFrameStatusSnapshot.Merge(rows,failures.Values,skipped.Values.Take(32),SameWindow);
            NativeFramesController.Write(NativeFramesController.FileFor(id,"status"),new NativeFramesStatus(!stopping,stopping,current,lastError));
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
                if (next is null || next.Preview != config.Preview || next.Exclusions is null || next.Exclusions.Length > 128 || !(next.Targets ?? []).SequenceEqual(targets)) Stop();
                else config = next;
                if (!config.Enabled) Stop(emergency: false);
                else if (owner.HasExited || recovery.WaitOne(0)) Stop();
                // Refresh every live lease before any bounded admission may wait on another app.
                foreach (var pair in bindings.ToArray())
                {
                    var binding = pair.Value;
                    binding.ExclusionRestore |= Excluded(binding.App.Path);
                    binding.MaterialOptOut |= binding.App.Material && (!config.ModernMaterials || config.ModernFrames && NativePrototypeMethods.VeliModernReason(binding.App.Window) is 0 or 100) || binding.App.Adapter != 0 && !config.ModernFrames;
                    binding.Stopping |= stopping || Excluded(binding.App.Path) || binding.MaterialOptOut ||
                        !binding.App.Material && binding.Age.Elapsed > TimeSpan.FromSeconds(5) && NativePrototypeMethods.GetProp(binding.App.Window, NativePrototypeMethods.ClassicActive) == 0;
                    binding.Lease.Configure(config);
                    var state = binding.Lease.Poll(binding.Stopping);
                    binding.State = state;
                    if (binding.Lease.Faulted) { lastError = "Compatibility helper exited; recovery requested."; Stop(); }
                    if (state is 0 or 4 && binding.Age.Elapsed > TimeSpan.FromSeconds(5) && !binding.Stopping) { binding.TimedOut=true; binding.Stopping=true; }
                    if (state == 1 && !binding.Loaded && !binding.App.Material) {
                        binding.Loaded = true;
                        NativeFramesController.Write(NativeFramesController.FileFor(binding.Id, "loaded"), new NativeFrameLoaded(binding.App.Pid, binding.App.Started, binding.App.Path));
                    }
                    if (state is 2 or 3 or -1 || !NativeMethods.IsWindow(binding.App.Window)) {
                        // This lease used the immutable runtime, not MSI files.
                        // Drop only its own record after End confirms restoration;
                        // legacy records from older packages remain for update checks.
                        if (state == 2 && !binding.Stopping && binding.Lease.Error != 0) lastError = "Native adapter restored itself: " + binding.Lease.Error;
                        if (state == -1 || state == 2 && !stopping && !binding.ExclusionRestore && !binding.MaterialOptOut)
                            failures[pair.Key]=Row(binding.App,"Failed",binding.TimedOut?"Timeout":"Activation",binding.Lease.Error);
                        if (binding.Lease.End()) { bindings.Remove(pair.Key); if (state == 3 || binding.ExclusionRestore || binding.MaterialOptOut) attempted.Remove((pair.Key, binding.App.Pid, binding.App.Started)); File.Delete(NativeFramesController.FileFor(binding.Id, "admission")); File.Delete(NativeFramesController.FileFor(binding.Id, "loaded")); }
                    } else if (state == 0 && binding.Stopping && binding.Lease.End()) {
                        if(binding.TimedOut)failures[pair.Key]=Row(binding.App,"Failed","Timeout",binding.Lease.Error);
                        bindings.Remove(pair.Key); File.Delete(NativeFramesController.FileFor(binding.Id,"admission")); File.Delete(NativeFramesController.FileFor(binding.Id,"loaded"));
                    }
                }
                if (stopping) { SaveStatus(); if (bindings.Count == 0) app.Shutdown(); return; }
                if (scan.Elapsed < TimeSpan.FromMilliseconds(500)) return;
                scan.Restart(); SaveStatus();
                foreach (var key in failures.Keys.Where(key=>!SameWindow(failures[key])).ToArray()) failures.Remove(key);
                if (bindings.Count >= 64) { lastError = "WindowLimit"; return; }
                var handles = new List<nint>(); NativeMethods.EnumWindows((window, _) => { handles.Add(window); return true; }, 0);
                skipped.Clear();
                foreach (var window in handles.Take(512))
                {
                    if (targets.Length > 0 && !targets.Any(t=>t.Window==window.ToInt64())) continue;
                    if (bindings.ContainsKey(window) || NativePrototypeMethods.GetProp(window, NativePrototypeMethods.ClassicActive) != 0) continue;
                    var reason = NativePrototypeMethods.VeliClassicReason(window, 1);
                    int modernReason = config.ModernFrames ? NativePrototypeMethods.VeliModernReason(window) : 11;
                    bool modern = config.ModernFrames && modernReason is 0 or 100;
                    bool material = !modern && reason is not (0 or 100) && config.ModernMaterials && NativePrototypeMethods.VeliMaterialEligible(window) == 1;
                    var candidate = ClassicFrameSession.Inspect(window, includeHash: false, crossBitness: true, material: material, modern: modern);
                    if (candidate is null) {
                        if (!config.Preview || targets.Length>0) RememberSkipped(window,config.ModernFrames && NativePrototypeMethods.VeliModernKind(window)>0?modernReason:reason,skipped);
                        continue;
                    }
                    if (string.Equals(Path.GetFileName(candidate.Path),"VeliShell.exe",StringComparison.OrdinalIgnoreCase) &&
                        (NativePrototypeMethods.GetProp(window,"VeliShell.WpfSample")!=1 || !string.Equals(candidate.Path,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))) continue;
                    if (!config.Preview && Path.GetFileName(candidate.Path).StartsWith("VeliShell.ClassicSample",StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(Path.GetDirectoryName(candidate.Path),AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)) continue;
                    if (Excluded(candidate.Path)) { skipped[window] = Row(candidate,"Skipped","Excluded"); continue; }
                    if (attempted.Contains((window, candidate.Pid, candidate.Started))) continue;
                    candidate = ClassicFrameSession.Inspect(window, crossBitness: true, material: material, modern: modern);
                    if (candidate is null || !PreviewAdmits(candidate) || Excluded(candidate.Path)) continue;
                    try {
                        if(!candidate.Material && ClassicFrameSession.HasOtherAdapterVersion(candidate)) { skipped[window]=Row(candidate,"Skipped","RestartApp");continue; }
                    } catch(Exception error) when(error is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) {
                        skipped[window]=Row(candidate,"Skipped","Permission");continue;
                    }
                    var sessionId = Guid.NewGuid();
                    // Identity record precedes target changes. No HWND/process pointer is loaded from a file.
                    NativeFramesController.Write(NativeFramesController.FileFor(sessionId, "admission"), new { candidate.Pid, candidate.Started, candidate.Path, candidate.Hash, Hwnd = window.ToInt64() });
                    if (!material) NativeFramesController.Write(NativeFramesController.FileFor(sessionId, "loaded"), new NativeFrameLoaded(candidate.Pid, candidate.Started, candidate.Path));
                    attempted.Add((window, candidate.Pid, candidate.Started));
                    var lease = NativeFrameLease.Begin(candidate, sessionId, config, out var beginError);
                    if (lease is null) { File.Delete(NativeFramesController.FileFor(sessionId, "loaded")); File.Delete(NativeFramesController.FileFor(sessionId,"admission")); failures[window] = Row(candidate,"Failed","Activation",beginError); }
                    else { failures.Remove(window); bindings.Add(window, new(candidate, sessionId, lease, Stopwatch.StartNew()) { Loaded = !material }); }
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
            timer.Stop(); if (!config.Preview) NativeMethods.UnregisterHotKey(source.Handle, hotkey);
            foreach (var binding in bindings.Values) { binding.Lease.Poll(true); if (!binding.Lease.End()) binding.Lease.Dispose(); }
            File.Delete(session);
        }
    }
    private static bool SameWindow(NativeFrameStatus row)
    {
        if (!NativeMethods.IsWindow((nint)row.Window)) return false;
        NativeMethods.GetWindowThreadProcessId((nint)row.Window,out var pid);
        if (pid != row.Pid) return false;
        try { using var process=Process.GetProcessById(row.Pid); return process.StartTime.ToUniversalTime().ToFileTimeUtc()==row.Started; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static void RememberSkipped(nint window, int reason, Dictionary<nint, NativeFrameStatus> skipped)
    {
        if (reason == 2 || skipped.Count >= 32) return;
        try {
            var text = new System.Text.StringBuilder(512); NativeMethods.GetWindowText(window, text, text.Capacity);
            if (text.Length == 0) return;
            NativeMethods.GetWindowThreadProcessId(window, out var pid); using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            if (path is null || string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
            var key = reason switch { 1 => "HighContrast", 3 => "Style", 4 => "Region", 5 => "Protected", 6 or 10 => "Custom", 7 => "Permission", 8 => "Architecture", 9 => "System", _ => "Unknown" };
            skipped[window] = new(text.ToString(), path, "Skipped", key,window.ToInt64(),(int)pid,process.StartTime.ToUniversalTime().ToFileTimeUtc());
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
