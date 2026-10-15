using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Services;

internal sealed record AudioAppSession(string Id, string Name, int ProcessId,
    double VolumePercent, bool IsMuted, bool IsSystemSound);

internal static class AudioSessionService
{
    internal static IReadOnlyList<AudioAppSession> Read(string endpointId)
    {
        var sessions = new List<AudioAppSession>();
        WithSessions(endpointId, (control, info, volume) =>
        {
            if (info.GetState(out var state) != 0 || state == 2 ||
                info.GetSessionInstanceIdentifier(out var id) != 0 ||
                string.IsNullOrWhiteSpace(id) || id.Length > 1024 ||
                volume.GetMasterVolume(out var level) != 0 ||
                volume.GetMute(out var muted) != 0) return false;
            info.GetProcessId(out var pid);
            control.GetDisplayName(out var displayName);
            var systemSound = info.IsSystemSoundsSession() == 0;
            string name;
            if (systemSound) name = LocalizationService.Current.Get("AudioDevices.SystemSounds");
            else if (!string.IsNullOrWhiteSpace(displayName)) name = displayName.Trim();
            else
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    var processName = process.ProcessName;
                    name = processName.Length > 0
                        ? char.ToUpper(processName[0], LocalizationService.Current.ActiveCulture) + processName[1..]
                        : LocalizationService.Current.Get("ShellLink.Application");
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                { name = LocalizationService.Current.Get("ShellLink.Application"); }
            }
            sessions.Add(new AudioAppSession(id, name, (int)pid,
                Math.Clamp(level * 100d, 0, 100), muted, systemSound));
            return sessions.Count >= 64;
        });
        return sessions.OrderBy(session => session.IsSystemSound ? 0 : 1)
            .ThenBy(session => session.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static bool TrySetVolume(string endpointId, string sessionId, double percent)
        => TrySetVolumes(endpointId, [sessionId], percent);

    internal static bool TrySetVolumes(string endpointId, IReadOnlyCollection<string> sessionIds, double percent)
    {
        if (!double.IsFinite(percent) || sessionIds.Count == 0) return false;
        var wanted = sessionIds.ToHashSet(StringComparer.Ordinal);
        var applied = 0;
        WithSessions(endpointId, (_, info, volume) =>
        {
            if (info.GetSessionInstanceIdentifier(out var id) != 0 ||
                !wanted.Contains(id)) return false;
            var context = Guid.Empty;
            if (volume.SetMasterVolume((float)(Math.Clamp(percent, 0, 100) / 100), ref context) == 0)
                applied++;
            return applied >= wanted.Count;
        });
        return applied == wanted.Count;
    }

    internal static bool TrySetMuted(string endpointId, string sessionId, bool muted)
        => TrySetMuted(endpointId, [sessionId], muted);

    internal static bool TrySetMuted(string endpointId, IReadOnlyCollection<string> sessionIds, bool muted)
    {
        if (sessionIds.Count == 0) return false;
        var wanted = sessionIds.ToHashSet(StringComparer.Ordinal);
        var applied = 0;
        WithSessions(endpointId, (_, info, volume) =>
        {
            if (info.GetSessionInstanceIdentifier(out var id) != 0 ||
                !wanted.Contains(id)) return false;
            var context = Guid.Empty;
            if (volume.SetMute(muted, ref context) == 0) applied++;
            return applied >= wanted.Count;
        });
        return applied == wanted.Count;
    }

    private static void WithSessions(string endpointId,
        Func<IAudioSessionControl, IAudioSessionControl2, ISimpleAudioVolume, bool> visit)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(endpointId)) return;
        IMMDeviceEnumerator? devices = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            devices = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
            if (devices.GetDevice(endpointId, out device) != 0 || device is null) return;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23, 0, out var instance) != 0 ||
                instance is not IAudioSessionManager2 audioManager) return;
            manager = audioManager;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null ||
                sessions.GetCount(out var count) != 0) return;
            for (var index = 0; index < Math.Min(count, 64); index++)
            {
                IAudioSessionControl? control = null;
                IAudioSessionControl2? info = null;
                ISimpleAudioVolume? volume = null;
                try
                {
                    if (sessions.GetSession(index, out control) != 0 || control is null) continue;
                    info = control as IAudioSessionControl2;
                    volume = control as ISimpleAudioVolume;
                    if (info is null || volume is null) continue;
                    if (visit(control, info, volume)) break;
                }
                finally
                {
                    Release(volume);
                    Release(info);
                    Release(control);
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
                                         InvalidComObjectException or TypeLoadException or
                                         System.Reflection.TargetInvocationException)
        { App.Log("Windows audio session access failed", exception); }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
            Release(devices);
        }
    }

    private static void Release(object? value)
    {
        try { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        catch (InvalidComObjectException) { }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out nint device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, nint parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out nint store);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(nint notification);
        [PreserveSig] int UnregisterSessionNotification(nint notification);
        [PreserveSig] int RegisterDuckNotification(nint session, nint notification);
        [PreserveSig] int UnregisterDuckNotification(nint notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl control);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName(nint name, ref Guid context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath(nint path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint notification);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notification);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName(nint name, ref Guid context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath(nint path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint notification);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
