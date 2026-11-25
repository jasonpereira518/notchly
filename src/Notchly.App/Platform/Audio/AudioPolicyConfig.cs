// SPDX-License-Identifier: GPL-3.0-or-later
// Windows stand-in for upstream's per-app output device (the mixer's process taps and aggregate device): the
// per-app output Settings → Volume mixer offers. Windows has no published call for it; EarTrumpet and SoundSwitch use
// the undocumented AudioPolicyConfig factory below. It is wrapped defensively: where the interface is missing
// (an older or changed Windows) Available is false and the mixer hides its per-app output menu.
using System.Runtime.InteropServices;
using Serilog;

namespace Notchly.App.Platform.Audio;

public static class AudioPolicyConfig
{
    private const string RuntimeClass = "Windows.Media.Internal.AudioPolicyConfig";
    // The interface changed its id in Windows 10 21H2; the newer one is tried first.
    private static readonly Guid CurrentIid = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid LegacyIid = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");
    private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";

    private static object? _factory;
    private static bool _legacy, _probed;

    /// <summary>Whether this Windows has the interface; probed once.</summary>
    public static bool Available
    {
        get { Probe(); return _factory is not null; }
    }

    /// <summary>The endpoint id `pid`'s audio is persisted to, or null for the system default.</summary>
    public static string? GetRoute(uint pid)
    {
        if (!Available) return null;
        var handle = IntPtr.Zero;
        try
        {
            var result = _legacy
                ? ((ILegacy)_factory!).GetPersistedDefaultAudioEndpoint(pid, 0, 1, out handle)
                : ((ICurrent)_factory!).GetPersistedDefaultAudioEndpoint(pid, 0, 1, out handle);
            if (result < 0 || handle == IntPtr.Zero) return null;
            var raw = ReadString(handle);
            return string.IsNullOrEmpty(raw) ? null : Unpack(raw);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.Debug(e, "Per-app output could not be read");
            return null;
        }
        finally { if (handle != IntPtr.Zero) WindowsDeleteString(handle); }
    }

    /// <summary>Makes `pid`'s audio play to `deviceId`, or to the system default when null. False when Windows refused.</summary>
    public static bool SetRoute(uint pid, string? deviceId)
    {
        if (!Available) return false;
        var handle = IntPtr.Zero;
        try
        {
            if (deviceId is not null && WindowsCreateString(Pack(deviceId), Pack(deviceId).Length, out handle) < 0) return false;
            var ok = true;
            // Console (0) and multimedia (1) cover every role the mixer routes; communications follows the system.
            foreach (var role in new[] { 0, 1 })
            {
                var result = _legacy
                    ? ((ILegacy)_factory!).SetPersistedDefaultAudioEndpoint(pid, 0, role, handle)
                    : ((ICurrent)_factory!).SetPersistedDefaultAudioEndpoint(pid, 0, role, handle);
                ok &= result >= 0;
            }
            return ok;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.Debug(e, "Per-app output could not be set");
            return false;
        }
        finally { if (handle != IntPtr.Zero) WindowsDeleteString(handle); }
    }

    /// <summary>Puts every app back on the system default, as choosing the output for all apps does.</summary>
    public static bool ClearAll()
    {
        if (!Available) return false;
        try
        {
            return (_legacy ? ((ILegacy)_factory!).ClearAllPersistedApplicationDefaultEndpoints() : ((ICurrent)_factory!).ClearAllPersistedApplicationDefaultEndpoints()) >= 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return false; }
    }

    private static string Pack(string id) => DevicePrefix + id + RenderSuffix;

    private static string Unpack(string raw)
    {
        var text = raw.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase) ? raw[DevicePrefix.Length..] : raw;
        var suffix = text.IndexOf("#{", StringComparison.Ordinal);
        return suffix >= 0 ? text[..suffix] : text;
    }

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        foreach (var (iid, legacy) in new[] { (CurrentIid, false), (LegacyIid, true) })
        {
            IntPtr className = IntPtr.Zero, factory = IntPtr.Zero;
            try
            {
                if (WindowsCreateString(RuntimeClass, RuntimeClass.Length, out className) < 0) continue;
                var id = iid;
                if (RoGetActivationFactory(className, ref id, out factory) < 0 || factory == IntPtr.Zero) continue;
                _factory = legacy ? Marshal.GetTypedObjectForIUnknown(factory, typeof(ILegacy)) : Marshal.GetTypedObjectForIUnknown(factory, typeof(ICurrent));
                _legacy = legacy;
                Log.Information("Per-app audio output is available ({Kind})", legacy ? "legacy interface" : "current interface");
                return;
            }
            catch (Exception e) when (e is COMException or InvalidCastException or EntryPointNotFoundException or DllNotFoundException)
            {
                Log.Debug(e, "Audio policy interface {Iid} is not available", iid);
            }
            finally
            {
                if (className != IntPtr.Zero) WindowsDeleteString(className);
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
        }
        Log.Information("Per-app audio output is not available on this Windows");
    }

    private static string? ReadString(IntPtr hstring)
    {
        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        return buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer, (int)length);
    }

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    // IInspectable's three methods lead the vtable; the unused factory methods hold their places.
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICurrent
    {
        [PreserveSig] int GetIids(out uint count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);
        [PreserveSig] int Unused1(); [PreserveSig] int Unused2(); [PreserveSig] int Unused3(); [PreserveSig] int Unused4();
        [PreserveSig] int Unused5(); [PreserveSig] int Unused6(); [PreserveSig] int Unused7(); [PreserveSig] int Unused8();
        [PreserveSig] int Unused9(); [PreserveSig] int Unused10(); [PreserveSig] int Unused11(); [PreserveSig] int Unused12();
        [PreserveSig] int Unused13(); [PreserveSig] int Unused14(); [PreserveSig] int Unused15(); [PreserveSig] int Unused16();
        [PreserveSig] int Unused17(); [PreserveSig] int Unused18(); [PreserveSig] int Unused19();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [ComImport, Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ILegacy
    {
        [PreserveSig] int GetIids(out uint count, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr name);
        [PreserveSig] int GetTrustLevel(out int level);
        [PreserveSig] int Unused1(); [PreserveSig] int Unused2(); [PreserveSig] int Unused3(); [PreserveSig] int Unused4();
        [PreserveSig] int Unused5(); [PreserveSig] int Unused6(); [PreserveSig] int Unused7(); [PreserveSig] int Unused8();
        [PreserveSig] int Unused9(); [PreserveSig] int Unused10(); [PreserveSig] int Unused11(); [PreserveSig] int Unused12();
        [PreserveSig] int Unused13(); [PreserveSig] int Unused14(); [PreserveSig] int Unused15(); [PreserveSig] int Unused16();
        [PreserveSig] int Unused17(); [PreserveSig] int Unused18(); [PreserveSig] int Unused19();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }
}
