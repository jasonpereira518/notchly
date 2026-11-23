// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: makes an output the default for every app, as upstream's setUniversalOutputDeviceUID.
// Windows has no published call for this; the Sound settings page, SoundSwitch and EarTrumpet all
// use the policy-config interface below, unchanged since Windows 7 and needing no elevation.
using System.Runtime.InteropServices;
using Serilog;

namespace Notchly.App.Platform.Audio;

public static class AudioOutputSwitcher
{
    /// <summary>Sets `deviceId` as the default output for all three roles; false when any was refused.</summary>
    public static bool SetDefault(string deviceId)
    {
        object? client = null;
        try
        {
            client = new PolicyConfigClient();
            var config = (IPolicyConfig)client;
            var ok = true;
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                ok &= config.SetDefaultEndpoint(deviceId, role) >= 0;
            return ok;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.Warning(e, "Default output could not be set");
            return false;
        }
        finally
        {
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    /// <summary>The methods ahead of SetDefaultEndpoint hold its place in the vtable; they are never called.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
        [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int defaultPeriod, IntPtr period, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
