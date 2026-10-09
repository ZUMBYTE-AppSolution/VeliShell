using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Services;

internal readonly record struct SystemControlStatus(
    bool IsNetworkAvailable,
    byte? BatteryPercent,
    bool IsPluggedIn,
    double? MasterVolume,
    bool IsMuted);

internal sealed record AudioOutputEndpoint(string Id, string Name, bool IsDefault,
    double? VolumePercent, bool IsMuted);

internal static class SystemControlStatusService
{
    internal static SystemControlStatus Read()
    {
        byte? batteryPercent = null;
        var pluggedIn = false;
        if (OperatingSystem.IsWindows() && GetSystemPowerStatus(out var powerStatus))
        {
            pluggedIn = powerStatus.AcLineStatus == 1;
            if (powerStatus.BatteryLifePercent <= 100)
                batteryPercent = powerStatus.BatteryLifePercent;
        }

        double? volume = null;
        var muted = false;
        if (OperatingSystem.IsWindows() && TryWithAudioEndpoint(endpoint =>
            {
                if (endpoint.GetMasterVolumeLevelScalar(out var scalar) != 0 ||
                    endpoint.GetMute(out muted) != 0)
                    return false;
                volume = Math.Clamp(scalar * 100d, 0d, 100d);
                return true;
            }) == false)
        {
            volume = null;
            muted = false;
        }

        return new SystemControlStatus(
            NetworkInterface.GetIsNetworkAvailable(), batteryPercent, pluggedIn, volume, muted);
    }

    internal static bool TrySetMasterVolume(double percent)
    {
        if (!OperatingSystem.IsWindows() || !double.IsFinite(percent)) return false;
        var scalar = (float)(Math.Clamp(percent, 0d, 100d) / 100d);
        return TryWithAudioEndpoint(endpoint =>
        {
            var context = Guid.Empty;
            return endpoint.SetMasterVolumeLevelScalar(scalar, ref context) == 0;
        });
    }

    internal static bool TrySetMuted(bool muted)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return TryWithAudioEndpoint(endpoint =>
        {
            var context = Guid.Empty;
            return endpoint.SetMute(muted, ref context) == 0;
        });
    }

    internal static bool TrySetEndpointVolume(string deviceId, double percent)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceId) ||
            !double.IsFinite(percent)) return false;
        return TryWithAudioEndpoint(endpoint =>
        {
            var context = Guid.Empty;
            return endpoint.SetMasterVolumeLevelScalar((float)(Math.Clamp(percent, 0d, 100d) / 100d),
                ref context) == 0;
        }, deviceId);
    }

    internal static bool TrySetEndpointMuted(string deviceId, bool muted)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceId)) return false;
        return TryWithAudioEndpoint(endpoint =>
        {
            var context = Guid.Empty;
            return endpoint.SetMute(muted, ref context) == 0;
        }, deviceId);
    }

    internal static IReadOnlyList<AudioOutputEndpoint> ReadOutputDevices()
    {
        if (!OperatingSystem.IsWindows()) return [];
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        IMMDevice? defaultDevice = null;
        try
        {
            var type = Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            var defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) == 0 &&
                defaultDevice is not null)
                defaultDevice.GetId(out defaultId);
            if (enumerator.EnumAudioEndpoints(0, 1, out collection) != 0 || collection is null ||
                collection.GetCount(out var count) != 0) return [];
            var results = new List<AudioOutputEndpoint>();
            for (uint index = 0; index < Math.Min(count, 32); index++)
            {
                IMMDevice? device = null;
                IPropertyStore? properties = null;
                IAudioEndpointVolume? endpoint = null;
                try
                {
                    if (collection.Item(index, out device) != 0 || device is null ||
                        device.GetId(out var id) != 0 || string.IsNullOrWhiteSpace(id)) continue;
                    var name = "";
                    if (device.OpenPropertyStore(0, out properties) == 0 && properties is not null)
                    {
                        var key = new PropertyKey
                        {
                            FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14
                        };
                        if (properties.GetValue(ref key, out var value) == 0)
                        {
                            try
                            {
                                if (value.VariantType == 31 && value.PointerValue != 0)
                                    name = Marshal.PtrToStringUni(value.PointerValue) ?? "";
                            }
                            finally { PropVariantClear(ref value); }
                        }
                    }
                    if (string.IsNullOrWhiteSpace(name)) name = $"Audio {index + 1}";
                    double? volume = null;
                    var muted = false;
                    var interfaceId = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref interfaceId, 23, nint.Zero, out var instance) == 0 &&
                        instance is IAudioEndpointVolume audio)
                    {
                        endpoint = audio;
                        if (endpoint.GetMasterVolumeLevelScalar(out var scalar) == 0)
                            volume = Math.Clamp(scalar * 100d, 0d, 100d);
                        endpoint.GetMute(out muted);
                    }
                    results.Add(new AudioOutputEndpoint(id, name,
                        string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), volume, muted));
                }
                finally
                {
                    ReleaseComObject(endpoint);
                    ReleaseComObject(properties);
                    ReleaseComObject(device);
                }
            }
            return results.OrderByDescending(device => device.IsDefault)
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
               InvalidComObjectException or TypeLoadException or MissingMethodException or
               System.Reflection.TargetInvocationException)
        {
            App.Log("Windows audio-device enumeration failed", exception);
            return [];
        }
        finally
        {
            ReleaseComObject(defaultDevice);
            ReleaseComObject(collection);
            ReleaseComObject(enumerator);
        }
    }

    private static bool TryWithAudioEndpoint(Func<IAudioEndpointVolume, bool> action, string? deviceId = null)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            var result = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(0, 1, out device)
                : enumerator.GetDevice(deviceId, out device);
            if (result != 0 || device is null)
                return false;

            var interfaceId = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref interfaceId, 23, nint.Zero, out var instance) != 0 ||
                instance is not IAudioEndpointVolume audioEndpoint)
                return false;

            endpoint = audioEndpoint;
            return action(endpoint);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
               InvalidComObjectException or TypeLoadException or MissingMethodException or
               System.Reflection.TargetInvocationException)
        {
            App.Log("Windows audio endpoint access failed", exception);
            return false;
        }
        finally
        {
            ReleaseComObject(endpoint);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
        catch (InvalidComObjectException)
        {
            // The RCW was already released while tearing down the short-lived query.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public nint PointerValue;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            int classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int storageAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyValue value);
        [PreserveSig] int Commit();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minimumDb, out float maximumDb, out float incrementDb);
    }
}
