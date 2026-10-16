using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using VeliShell.Desktop.Native;

namespace VeliShell.Desktop.Services;

// Owns either an x64 native lease or a pointer-free x86 helper connection.
// Helpers are never killed: on lost heartbeat they independently roll back.
internal sealed class NativeFrameLease : IDisposable
{
    private readonly ClassicFrameSession.Candidate _app;
    private nint _native;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _wire;
    private Process? _bridge;
    private bool _stopped, _ended;
    internal bool Material => _app.Material;
    internal int Error => _native != 0 ? NativePrototypeMethods.VeliClassicError(_native) : _wire?.ReadInt32(24) ?? 0;
    internal bool Faulted => !_stopped && _bridge is { HasExited: true } && _wire is not null && _wire.ReadInt32(16) is not (2 or 3);
    private NativeFrameLease(ClassicFrameSession.Candidate app) => _app = app;
    internal static NativeFrameLease? Begin(ClassicFrameSession.Candidate app, Guid id, NativeFramesConfig config)
        => Begin(app, id, config, out _);
    internal static NativeFrameLease? Begin(ClassicFrameSession.Candidate app, Guid id, NativeFramesConfig config, out int error)
    {
        error = 0;
        var lease = new NativeFrameLease(app);
        try {
            if (!app.Is32Bit && !app.Material) {
                lease._native = app.Adapter != 0
                    ? NativePrototypeMethods.VeliModernBegin(app.Window, id.ToString("N"), (uint)app.Pid, (ulong)app.Started, config.Dark ? 1 : 0, config.Gloss, config.Transparency, config.Tint)
                    : NativePrototypeMethods.VeliClassicBegin(app.Window, id.ToString("N"), (uint)app.Pid, (ulong)app.Started, config.Dark ? 1 : 0, config.Gloss, config.Transparency, config.Tint);
                if (lease._native == 0) error = NativePrototypeMethods.VeliClassicBeginError();
                return lease._native == 0 ? null : lease;
            }
            // Default kernel-object DACL is inherited from the current user token;
            // random per-lease name, non-inheritable handles, no cross-session object.
            lease._mapping = MemoryMappedFile.CreateNew(@"Local\VeliShell.FrameBridge." + id.ToString("N"), 256, MemoryMappedFileAccess.ReadWrite, MemoryMappedFileOptions.None, HandleInheritability.None);
            lease._wire = lease._mapping.CreateViewAccessor(0, 256, MemoryMappedFileAccess.ReadWrite);
            var wire = lease._wire;
            wire.Write(0, 0x56464233); wire.Write(4, app.Material ? 1 : 0);
            using var owner = Process.GetCurrentProcess();
            wire.Write(44, owner.Id); wire.Write(48, owner.StartTime.ToUniversalTime().ToFileTimeUtc());
            wire.Write(56, app.Pid); wire.Write(64, app.Started); wire.Write(72, (ulong)unchecked((uint)app.Window.ToInt64()));
            lease.Configure(config); wire.Write(8, Environment.TickCount64);
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "VeliShell.FrameBridge.x86.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(id.ToString("N"));
            lease._bridge = Process.Start(start) ?? throw new IOException("Compatibility bridge did not start.");
            return lease;
        } catch { lease.Dispose(); throw; }
    }
    internal void Configure(NativeFramesConfig config)
    {
        if (_native != 0) NativePrototypeMethods.VeliClassicConfigure(_native, config.Dark ? 1 : 0, config.Gloss, config.Transparency, config.Tint);
        if (_wire is { } wire) { wire.Write(28, config.Dark ? 1 : 0); wire.Write(32, config.Gloss); wire.Write(36, config.Transparency); wire.Write(40, config.Tint); }
    }
    internal int Poll(bool stop)
    {
        _stopped |= stop;
        if (_native != 0) return NativePrototypeMethods.VeliClassicPoll(_native, _stopped ? 1 : 0);
        if (_wire is null) return -1;
        _wire.Write(8, Environment.TickCount64); if (_stopped) _wire.Write(20, 1);
        if (_bridge?.HasExited == true) {
            int terminal=_wire.ReadInt32(16);
            if(terminal is 2 or 3) return terminal;
            if (Material) return RecoverMaterial() ? -1 : 4;
            // A dead bridge does not imply the target UI thread finished restoring.
            return NativePrototypeMethods.GetProp(_app.Window, NativePrototypeMethods.ClassicActive) == 0 ? -1 : 4;
        }
        return _wire.ReadInt32(16);
    }
    private bool RecoverMaterial()
    {
        if (_wire is null) return true;
        NativeMethods.GetWindowThreadProcessId(_app.Window, out var pid);
        try { using var process = Process.GetProcessById((int)pid); if (pid != _app.Pid || process.StartTime.ToUniversalTime().ToFileTimeUtc() != _app.Started) return true; }
        catch (ArgumentException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        int[] attributes = [20, 33, 34, 35, 36, 38]; bool restored = true;
        int mask = _wire.ReadInt32(96);
        for (var i = 0; i < attributes.Length; i++) if ((mask & (1 << i)) != 0) {
            if (NativeMethods.DwmGetWindowAttribute(_app.Window, attributes[i], out var current, 4) < 0) { restored = false; continue; }
            if (current != _wire.ReadInt32(104 + i * 8)) continue;
            int original = _wire.ReadInt32(100 + i * 8);
            restored &= NativeMethods.DwmSetWindowAttribute(_app.Window, attributes[i], ref original, 4) >= 0;
        }
        if (restored && NativePrototypeMethods.GetProp(_app.Window, "VeliShell.Material.Active") == (_bridge?.Id ?? 0)) NativePrototypeMethods.RemoveProp(_app.Window, "VeliShell.Material.Active");
        return restored;
    }
    internal bool End()
    {
        if (_ended) return true;
        if (_native != 0) { if (NativePrototypeMethods.VeliClassicEnd(_native) != 1) return false; _native = 0; }
        if (_wire is not null && _bridge is { HasExited: false }) { _wire.Write(20, 1); return false; }
        _ended = true; Dispose(); return true;
    }
    public void Dispose()
    {
        if (_wire is not null) _wire.Write(20, 1);
        _wire?.Dispose(); _mapping?.Dispose(); _bridge?.Dispose(); _wire = null; _mapping = null; _bridge = null;
    }
}
