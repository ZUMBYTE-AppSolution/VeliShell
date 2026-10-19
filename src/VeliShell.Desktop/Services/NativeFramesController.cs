using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using VeliShell.Core;

namespace VeliShell.Desktop.Services;

internal sealed record NativeFrameTarget(long Window, int Pid, long Started);
internal sealed record NativeFramesConfig(bool Enabled, bool Dark, int Gloss, int Transparency, string[] Exclusions, bool Preview, uint Tint, bool ModernMaterials = false, bool ModernFrames = false, NativeFrameTarget[]? Targets = null);
internal sealed record NativeFrameStatus(string Title, string Path, string State, string? Reason = null, long Window = 0, int Pid = 0, long Started = 0, int Adapter = 0, int Error = 0)
{
    public override string ToString() {
        string L(string key) => LocalizationService.Current.Get("NativeFrames." + key);
        var text = Title + " · " + L("State." + State);
        if (Adapter != 0) text += " · " + L("Adapter." + Adapter);
        if (Reason is not null) text += " · " + L("Reason." + Reason);
        if (Error != 0) text += " · " + L("ErrorCode") + " " + Error + " (0x" + unchecked((uint)Error).ToString("X8", CultureInfo.InvariantCulture) + ")";
        if (Pid != 0) text += "\n" + Path + " · PID " + Pid + " · HWND 0x" + Window.ToString("X", CultureInfo.InvariantCulture);
        return text;
    }
}
internal sealed record NativeFramesStatus(bool Ready, bool Stopping, NativeFrameStatus[] Windows, string? Error = null);
internal sealed record NativeFrameLoaded(int Pid, long Started, string Path);

internal sealed class NativeFramesController : IDisposable
{
    internal const string Guard = "--native-windows-guard";
    internal const string Preview = "--native-windows-preview";
    internal static string Folder => Path.Combine(App.DataDirectory, "native-windows");
    internal static string Latch => Path.Combine(Folder, "recovery.lock");
    internal static string Event(Guid id, string kind) => @"Local\VeliShell.NativeWindows." + id.ToString("N") + "." + kind;
    internal static string FileFor(Guid id, string kind) => Path.Combine(Folder, id.ToString("N") + "." + kind);
    internal static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 && !PackageIdentityService.HasIdentity;
    private Process? _guard;
    private Guid _id;
    private readonly bool _preview;
    private readonly NativeFrameTarget[]? _targets;
    private bool _disposed, _stopped;
    private NativeFramesConfig? _lastConfig;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal NativeFramesStatus Status { get; private set; } = new(false, false, []);
    internal event Action? Changed;
    internal bool Active => _guard is { HasExited: false } && !_stopped;
    internal bool Restoring => _guard is { HasExited: false } && _stopped;
    internal bool RequestIsolatedRecovery() => _preview && _targets is { Length: > 0 } && Signal(_id);
    internal void TestIsolatedHelperFailure()
    {
        if (!BuildIdentity.IsTest || !_preview || _targets is not { Length: 1 } || _guard is not { HasExited: false } ||
            !string.Equals(_guard.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No exact-window test helper is available.");
        _guard.Kill(); // Only our retained child process, never the target app.
    }
    internal NativeFramesController(bool preview = false, NativeFrameTarget[]? targets = null)
    {
        if (targets is not null && (!preview || !BuildIdentity.IsTest || targets.Length is < 1 or > 8)) throw new ArgumentException("Test-only exact-window scope required.");
        _targets = targets; _preview = preview; _timer.Tick += (_, _) => {
            try { RefreshStatus(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _stopped = true; Signal(_id); Status = new(false, true, [], error.GetType().Name); App.Log("Native styling status failed closed", error); Changed?.Invoke(); }
        }; _timer.Start();
    }
    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Folder);
        var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true);
    }
    internal static T? Read<T>(string path)
    {
        try { using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); if (input.Length > 128 * 1024) return default; return JsonSerializer.Deserialize<T>(input); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    internal static uint CaptionTint(bool dark)
    {
        var dictionary = new System.Windows.ResourceDictionary { Source = new Uri($"pack://application:,,,/VeliShell;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        var color = (System.Windows.Media.Color)dictionary["NativeCaptionTint"];
        return (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
    }
    private NativeFramesConfig Config(Settings settings, bool dark) => new(settings.NativeFramesEnabled, dark, (int)settings.GlassGloss, (int)settings.GlassTransparency, settings.NativeFrameExclusions.ToArray(), _preview, CaptionTint(dark), settings.NativeModernMaterialsEnabled && settings.NativeModernMaterialsConsentVersion == 1, settings.NativeModernFramesEnabled && settings.NativeModernFramesConsentVersion == 1, _targets);
    internal async Task EnableAsync(Settings settings, bool dark, bool explicitConsent)
    {
        if (_disposed || !Supported || settings.NativeFramesConsentVersion != 1) throw new InvalidOperationException(LocalizationService.Current.Get("NativeFrames.Unsupported"));
        if (Active) { Update(settings, dark); return; }
        if (_guard is { HasExited: false }) throw new InvalidOperationException(LocalizationService.Current.Get("NativeFrames.Restoring"));
        Directory.CreateDirectory(Folder);
        if (!explicitConsent && (File.Exists(Latch) || Directory.EnumerateFiles(Folder, "*.session").Any())) throw new InvalidOperationException(LocalizationService.Current.Get("NativeFrames.Locked"));
        if (explicitConsent) File.Delete(Latch);
        _guard?.Dispose(); _guard = null; _id = Guid.NewGuid(); _stopped = false;
        _lastConfig = Config(settings, dark) with { Enabled = true }; Write(FileFor(_id, "json"), _lastConfig);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Event(_id, "ready"));
        using var owner = Process.GetCurrentProcess();
        _guard = NativePrototypeSession.Start([Guard, owner.Id.ToString(CultureInfo.InvariantCulture), owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), _id.ToString("N")]);
        for (var n = 0; n < 80 && !ready.WaitOne(0) && !_guard.HasExited; n++) await Task.Delay(100);
        if (!ready.WaitOne(0)) { Stop(); throw new InvalidOperationException(LocalizationService.Current.Get("NativeFrames.GuardFailed")); }
        RefreshStatus();
    }
    internal void Update(Settings settings, bool dark)
    {
        if (_disposed || _guard is null || _guard.HasExited) return;
        _lastConfig = Config(settings, dark); Write(FileFor(_id, "json"), _lastConfig);
        if (!settings.NativeFramesEnabled) Stop();
    }
    internal void Stop()
    {
        _stopped = true;
        if (_guard is { HasExited: false } && _lastConfig is not null) { _lastConfig = _lastConfig with { Enabled = false }; Write(FileFor(_id, "json"), _lastConfig); }
        Signal(_id); RefreshStatus();
    }
    internal static bool Signal(Guid id)
    {
        try { using var signal = EventWaitHandle.OpenExisting(Event(id, "restore")); return signal.Set(); }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { return false; }
    }
    internal static int RecoverAll()
    {
        try {
            Directory.CreateDirectory(Folder); File.WriteAllText(Latch, "Recovery requested. Explicit activation is required.");
            return Directory.EnumerateFiles(Folder, "*.session").Take(16).Count(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) && Signal(id));
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { App.Log("Native frame recovery failed", error); return 0; }
    }
    private void RefreshStatus()
    {
        if (_guard is null) return;
        var crashed = _guard.HasExited && !_stopped;
        if (crashed) { _stopped = true; if (!_preview) { Directory.CreateDirectory(Folder); File.WriteAllText(Latch, "Recovery helper exited. Explicit activation is required."); } }
        Status = Read<NativeFramesStatus>(FileFor(_id, "status")) ?? new(false, _stopped, []);
        if (crashed) Status = Status with { Ready = false, Stopping = true, Error = "Recovery helper exited" };
        _stopped |= Status.Stopping;
        if (_guard.HasExited) Status = Status with { Ready = false };
        Changed?.Invoke();
    }
    internal static bool HasLoadedApps()
    {
        if (!Directory.Exists(Folder)) return false;
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.loaded")) {
            if (++scanned > 512) return true; // Unscanned records must never allow an unsafe update.
            var record = Read<NativeFrameLoaded>(file); if (record is null) return true;
            try {
                using var process = Process.GetProcessById(record.Pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == record.Started) return true;
            } catch (ArgumentException) { } catch (System.ComponentModel.Win32Exception) { return true; }
            File.Delete(file);
        }
        return false;
    }
    public void Dispose()
    {
        if (_disposed) return;
        try { Stop(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Signal(_id); App.Log("Native styling shutdown signal", error); }
        _disposed = true; _timer.Stop(); _guard?.Dispose(); _guard = null;
    }
}
