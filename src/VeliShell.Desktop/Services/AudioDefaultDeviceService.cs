using System.Runtime.InteropServices;

namespace VeliShell.Desktop.Services;

internal static class AudioDefaultDeviceService
{
    internal static bool IsSwitchInterfaceAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        object? instance = null;
        try
        {
            instance = new PolicyConfigClient();
            return instance is IPolicyConfig;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
                                         InvalidComObjectException or TypeLoadException)
        { return false; }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }
    }

    /// <summary>
    /// Windows has no documented setter counterpart to IMMDeviceEnumerator's
    /// GetDefaultAudioEndpoint. Keep the policy-interface call isolated and
    /// verify the outcome through the documented enumerator after each click.
    /// </summary>
    internal static bool TrySetDefault(string id, bool input)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(id)) return false;
        var known = input ? SystemControlStatusService.ReadInputDevices() :
            SystemControlStatusService.ReadOutputDevices();
        if (!known.Any(device => string.Equals(device.Id, id, StringComparison.Ordinal))) return false;
        if (known.Any(device => device.IsDefault && string.Equals(device.Id, id, StringComparison.Ordinal)))
            return true;
        object? instance = null;
        try
        {
            instance = new PolicyConfigClient();
            if (instance is not IPolicyConfig policy) return false;
            // Apply all three Windows endpoint roles; eMultimedia is the role
            // shown as the main default in VeliShell.
            var allRolesSet = true;
            foreach (var role in new[] { 1, 0, 2 })
                allRolesSet &= policy.SetDefaultEndpoint(id, role) == 0;
            var refreshed = input ? SystemControlStatusService.ReadInputDevices() :
                SystemControlStatusService.ReadOutputDevices();
            return allRolesSet && refreshed.Any(device => device.IsDefault &&
                string.Equals(device.Id, id, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
                                         InvalidComObjectException or TypeLoadException)
        {
            App.Log("Windows rejected default audio endpoint change", exception);
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(nint deviceName, nint format);
        [PreserveSig] int GetDeviceFormat(nint deviceName, int defaultFormat, nint format);
        [PreserveSig] int ResetDeviceFormat(nint deviceName);
        [PreserveSig] int SetDeviceFormat(nint deviceName, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(nint deviceName, int defaultPeriod, nint period, nint minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(nint deviceName, nint period);
        [PreserveSig] int GetShareMode(nint deviceName, nint mode);
        [PreserveSig] int SetShareMode(nint deviceName, nint mode);
        [PreserveSig] int GetPropertyValue(nint deviceName, nint key, nint value);
        [PreserveSig] int SetPropertyValue(nint deviceName, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, int role);
        [PreserveSig] int SetEndpointVisibility(nint deviceName, int visible);
    }
}
