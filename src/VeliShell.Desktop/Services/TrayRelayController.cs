using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

internal sealed record TrayCallbackTarget(ulong Window, ulong Started, uint Pid, uint Id, uint Message, uint Version, Guid Guid)
{
    internal string Executable { get; init; } = "";
    internal NativeMethods.TrayCallback Native => new() { Window = Window, Started = Started, Pid = Pid,
        Id = Id, Message = Message, Version = Version, Active = 1, Guid = Guid };
}

internal sealed class TrayRelayController : IDisposable
{
    internal const string Guard = "--tray-relay-guard", Recover = "--restore-tray-bridge";
    internal static bool DiagnosticMode { get; set; }
    internal static string Folder => Path.Combine(App.DataDirectory, DiagnosticMode ? "tray-bridge-qa" : "tray-bridge");
    internal static string Latch => Path.Combine(Folder, "recovery.lock");
    internal static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 && !PackageIdentityService.HasIdentity;
    internal static TrayRelayController? Current { get; private set; }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<TrayCallbackTarget> _icons = [];
    private Process? _guard;
    private Guid _session;
    private bool _refreshing, _disposed, _stopping, _preparingUpdate;
    internal bool Failed { get; private set; }
    internal bool Active => !_disposed && !_stopping && _session != Guid.Empty && _guard is { HasExited: false };
    internal int IconCount => Volatile.Read(ref _icons).Count;
    internal Guid DiagnosticSession => DiagnosticMode ? _session : Guid.Empty;
    internal void FailDiagnosticGuard()
    {
        if (!DiagnosticMode || !Active || _guard is null ||
            !string.Equals(_guard.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No owned diagnostic guard.");
        _guard.Kill(); // Retained child from this diagnostic session only.
    }
    internal event Action? Changed;
    internal string Status => LocalizationService.Current.Get(!Supported ? "TrayBridge.Unsupported" :
        Failed ? "TrayBridge.Failed" : Active ? "TrayBridge.Active" : "TrayBridge.Disabled") +
        (Active ? " · " + IconCount.ToString(CultureInfo.InvariantCulture) : "");
    internal TrayRelayController()
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
    }
    internal async Task EnableAsync(bool explicitConsent)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || !Supported || _preparingUpdate) throw new InvalidOperationException(LocalizationService.Current.Get("TrayBridge.Unsupported"));
            if (Active) return;
            if (_guard is { HasExited: false }) throw new InvalidOperationException(LocalizationService.Current.Get("TrayBridge.Failed"));
            Directory.CreateDirectory(Folder);
            if (!explicitConsent && (File.Exists(Latch) || Directory.EnumerateFiles(Folder, "*.session").Any()))
                throw new InvalidOperationException(LocalizationService.Current.Get("TrayBridge.Failed"));
            if (explicitConsent) File.Delete(Latch);
            _session = Guid.NewGuid(); _stopping = false; Failed = false;
            _guard?.Dispose();
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent(_session));
            using var owner = Process.GetCurrentProcess();
            _guard = NativePrototypeSession.Start([Guard, owner.Id.ToString(CultureInfo.InvariantCulture),
                owner.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), _session.ToString("N"), DiagnosticMode ? "qa" : "normal"]);
            for (var n = 0; n < 80 && !ready.WaitOne(0) && !_guard.HasExited; n++) await Task.Delay(100);
            if (!ready.WaitOne(0) || _guard.HasExited)
            {
                await StopCoreAsync(); Failed = true;
                throw new InvalidOperationException(LocalizationService.Current.Get("TrayBridge.Failed"));
            }
            if (explicitConsent)
                foreach (var stale in Directory.EnumerateFiles(Folder, "*.session").Take(64))
                    if (!Path.GetFileNameWithoutExtension(stale).Equals(_session.ToString("N"), StringComparison.OrdinalIgnoreCase)) File.Delete(stale);
            Current = this; _timer.Start();
            await RefreshAsync(); Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }
    internal async Task<bool> StopAsync()
    {
        await _gate.WaitAsync();
        try { return await StopCoreAsync(); }
        finally { _gate.Release(); }
    }
    private async Task<bool> StopCoreAsync()
    {
        _stopping = true; _timer.Stop(); Volatile.Write(ref _icons, []);
        if (ReferenceEquals(Current, this)) Current = null;
        if (_session != Guid.Empty) NativeMethods.VeliShellRelayStopSession(in _session, false);
        if (_guard is { HasExited: false })
        {
            var wait = _guard.WaitForExitAsync();
            if (await Task.WhenAny(wait, Task.Delay(4000)) != wait) { Failed = true; Changed?.Invoke(); return false; }
        }
        _session = Guid.Empty; Changed?.Invoke(); return true;
    }
    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed || _stopping) return;
        if (_guard is { HasExited: true })
        {
            Failed = true; _stopping = true; _timer.Stop();
            Volatile.Write(ref _icons, []); if (ReferenceEquals(Current, this)) Current = null;
            MarkRecovery(); Changed?.Invoke(); return;
        }
        if (!Active) return;
        _refreshing = true; var session = _session;
        try
        {
            var icons = await Task.Run(() => ReadCallbacks(session, requireActive: true));
            if (Active && _session == session) Volatile.Write(ref _icons, icons);
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            App.Log("Tray callback snapshot failed", error); Volatile.Write(ref _icons, []);
            Failed = true; _stopping = true; _timer.Stop(); if (ReferenceEquals(Current, this)) Current = null;
            try { NativeMethods.VeliShellRelayStopSession(in _session, true); }
            catch (Exception stopError) { App.Log("Tray snapshot recovery signal failed", stopError); }
            MarkRecovery(); Changed?.Invoke();
        }
        finally { _refreshing = false; }
    }
    internal IDisposable SuspendForUpdate()
    {
        if (_preparingUpdate || _disposed) throw new InvalidOperationException("Tray bridge is preparing an update.");
        _preparingUpdate = true; return new UpdateScope(this);
    }
    private sealed class UpdateScope(TrayRelayController owner) : IDisposable
    {
        public void Dispose() => owner._preparingUpdate = false;
    }
    internal static IReadOnlyList<TrayCallbackTarget> ReadCallbacks(Guid session, bool requireActive = false)
    {
        var json = new StringBuilder(262144);
        if (NativeMethods.VeliShellReadTrayCallbacks(in session, json, json.Capacity) != 0)
        {
            if (requireActive) throw new IOException("The tray receiver is no longer active.");
            return [];
        }
        return (JsonSerializer.Deserialize<TrayCallbackTarget[]>(json.ToString()) ?? []).Take(128)
            .Select(icon => icon with { Executable = WindowCatalog.GetProcessPath(icon.Pid) })
            .Where(icon => !string.IsNullOrWhiteSpace(icon.Executable)).ToArray();
    }
    internal TrayCallbackTarget? FindTarget(string executable, Guid? guid = null, uint? id = null) =>
        Active ? SelectTarget(Volatile.Read(ref _icons), executable, guid, id) : null;
    internal static TrayCallbackTarget? SelectTarget(IEnumerable<TrayCallbackTarget> icons, string executable, Guid? guid = null, uint? id = null)
    {
        var matches = icons.Where(icon => AppIdentityService.SameExecutable(icon.Executable, executable) &&
                (guid is null || guid == Guid.Empty || icon.Guid == guid) &&
                (guid is not null && guid != Guid.Empty || id is null || icon.Id == id))
            .DistinctBy(icon => (icon.Window, icon.Id, icon.Guid)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    internal bool Show(TrayCallbackTarget target, int x, int y, bool userRequested)
    {
        if (!userRequested || !Active || !Volatile.Read(ref _icons).Contains(target)) return false;
        var native = target.Native; return NativeMethods.VeliShellShowTrayCallback(in _session, in native, x, y) == 0;
    }
    internal static string ReadyEvent(Guid id) => @"Local\VeliShell.TrayRelay.Ready." + id.ToString("N");
    internal static int RecoverAll()
    {
        var result = NativeMethods.VeliShellRelayRequestStop(0, true) == 0 ? 1 : 0;
        MarkRecovery(); return result;
    }
    internal static void MarkRecovery()
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(Latch, "Emergency recovery. Explicit activation required."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { App.Log("Tray recovery journal could not be written", error); }
    }
    internal static string RuntimeModule()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "VeliShell.TrayBridge.dll");
        var bytes = File.ReadAllBytes(source); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeliShell", "tray-runtime", hash); Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "VeliShell.TrayBridge." + hash[..16] + ".dll");
        if (!File.Exists(target))
        {
            var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, target); }
            catch (IOException) when (File.Exists(target)) { File.Delete(temporary); }
        }
        if (!SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(SHA256.HashData(bytes))) throw new IOException("Tray module integrity mismatch.");
        return target;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _stopping = true; _timer.Stop();
        if (ReferenceEquals(Current, this)) Current = null;
        if (_session != Guid.Empty) NativeMethods.VeliShellRelayStopSession(in _session, false);
        _disposed = true; _guard?.Dispose();
    }
}

internal static class TrayRelayGuard
{
    internal static void Run(string[] args, App app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (args[0] == TrayRelayController.Recover)
        {
            TrayRelayController.DiagnosticMode = args.ElementAtOrDefault(1) == "qa";
            TrayRelayController.RecoverAll(); app.Shutdown(); return;
        }
        if (!TrayRelayController.Supported || args.Length != 5 || args[4] is not ("qa" or "normal") || !int.TryParse(args[1], out var pid) ||
            !ulong.TryParse(args[2], out var started) || !Guid.TryParseExact(args[3], "N", out var id)) { app.Shutdown(2); return; }
        TrayRelayController.DiagnosticMode = args[4] == "qa";
        using var owner = Process.GetProcessById(pid);
        if ((ulong)owner.StartTime.ToUniversalTime().ToFileTimeUtc() != started ||
            !string.Equals(owner.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) { app.Shutdown(2); return; }
        using var source = new HwndSource(new HwndSourceParameters("VeliShell Tray Recovery") { ParentWindow = (nint)(-3), Width = 0, Height = 0 });
        const int hotkey = 0x5654;
        if (!NativeMethods.RegisterHotKey(source.Handle, hotkey, NativeMethods.ModAlt | NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModNoRepeat, 0x79)) { app.Shutdown(3); return; }
        var module = NativeLibrary.Load(TrayRelayController.RuntimeModule(), typeof(App).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        var start = Marshal.GetDelegateForFunctionPointer<NativeMethods.RelayStart>(NativeLibrary.GetExport(module, "VeliShellRelayStart"));
        var pulse = Marshal.GetDelegateForFunctionPointer<NativeMethods.RelayOperation>(NativeLibrary.GetExport(module, "VeliShellRelayPulse"));
        var reason = Marshal.GetDelegateForFunctionPointer<NativeMethods.RelayOperation>(NativeLibrary.GetExport(module, "VeliShellRelayStopReason"));
        var stop = Marshal.GetDelegateForFunctionPointer<NativeMethods.RelayOperation>(NativeLibrary.GetExport(module, "VeliShellRelayUninstall"));
        var journal = Path.Combine(TrayRelayController.Folder, id.ToString("N") + ".session");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var bootstrap = new NativeMethods.NotifyIconData
        {
            Size = checked((uint)Marshal.SizeOf<NativeMethods.NotifyIconData>()), Window = source.Handle,
            Id = 0x5654, Flags = 2 | 8, Icon = NativeMethods.LoadSharedIcon(0, (nint)32512),
            Tip = "", Info = "", InfoTitle = "", State = 1, StateMask = 1
        };
        var stopped = false;
        void Finish(bool emergency)
        {
            if (stopped) return; stopped = true; timer.Stop();
            stop();
            if (emergency) TrayRelayController.MarkRecovery();
            app.Shutdown();
        }
        source.AddHook((nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
        { if (message == 0x312 && wp == hotkey) { Finish(true); handled = true; } return 0; });
        try
        {
            var result = start(checked((uint)pid), started, in id);
            if (result != 0) { App.Log("Tray bridge activation failed: HRESULT 0x" + unchecked((uint)result).ToString("X8", CultureInfo.InvariantCulture)); app.Shutdown(4); return; }
            // Exercise the actual Shell_NotifyIcon transport with our own
            // NIS_HIDDEN bootstrap icon. It is never presented to the user.
            if (!NativeMethods.ShellNotifyIcon(0, ref bootstrap)) throw new IOException("The hidden tray receiver probe failed.");
            File.WriteAllText(journal, pid.ToString(CultureInfo.InvariantCulture));
            using var ready = EventWaitHandle.OpenExisting(TrayRelayController.ReadyEvent(id));
            var announced = false; var startup = Stopwatch.StartNew();
            timer.Tick += (_, _) =>
            {
                try
                {
                    var state = pulse();
                    if (owner.HasExited || state == 2 || state < 0) { Finish(reason() != 1); return; }
                    if (state == 0 && startup.Elapsed > TimeSpan.FromSeconds(5))
                    { App.Log("Explorer did not acknowledge the tray bridge."); Finish(true); return; }
                    if (state == 1 && !announced)
                    {
                        announced = true; NativeMethods.ShellNotifyIcon(2, ref bootstrap); ready.Set();
                        // Ask once per session, only after Explorer acknowledged
                        // the receiver. Never broadcast during the isolated check.
                        if (!TrayRelayController.DiagnosticMode)
                        {
                            var refresh = NativeMethods.RegisterWindowMessage("TaskbarCreated");
                            if (refresh != 0) NativeMethods.PostMessage((nint)0xffff, refresh, 0, 0);
                        }
                    }
                }
                catch (Exception error) { App.Log("Tray guard failed closed", error); Finish(true); }
            };
            timer.Start();
            var loop = new DispatcherFrame(); app.Exit += (_, _) => loop.Continue = false;
            Dispatcher.PushFrame(loop);
        }
        finally
        {
            timer.Stop();
            NativeMethods.ShellNotifyIcon(2, ref bootstrap);
            if (stop() == 0) File.Delete(journal);
            else TrayRelayController.MarkRecovery();
            NativeMethods.UnregisterHotKey(source.Handle, hotkey); NativeLibrary.Free(module);
        }
    }
}
