// SPDX-License-Identifier: GPL-3.0-or-later
// What each quick toggle does on Windows (upstream drives Finder, SkyLight and pmset): the theme, hidden files and
// desktop icons through the settings Explorer itself keeps, the Recycle Bin, lock, display off and the screen saver
// through their system calls, ejecting through the shell's own Eject, the refresh rate through the display mode,
// and the radios through Windows.Devices.Radios. Each returns whether it worked, so a failure can show.
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Notchly.Core.Tools;
using Serilog;
using Windows.Devices.Radios;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Platform.Tools;

public static class QuickToggleActions
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const uint WmSettingChange = 0x001A;
    private static readonly IntPtr Broadcast = new(0xFFFF);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr window, string? root, uint flags);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int events, uint flags, IntPtr item1, IntPtr item2);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr BroadcastText(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    // MARK: Theme

    public static bool? DarkModeOn() =>
        Registry.CurrentUser.OpenSubKey(Personalize)?.GetValue("AppsUseLightTheme") is int value ? QuickToggleSupport.DarkModeOn(value) : null;

    /// <summary>Apps and the shell switch together, then Windows is told so open apps repaint.</summary>
    public static bool SetDarkMode(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Personalize);
            var value = dark ? 0 : 1;
            key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
            BroadcastText(Broadcast, WmSettingChange, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 200, out _);
            return true;
        }
        catch (Exception e) { Log.Warning(e, "Theme could not be changed"); return false; }
    }

    // MARK: Explorer's settings

    public static bool? HiddenFilesShown() =>
        Registry.CurrentUser.OpenSubKey(Advanced)?.GetValue("Hidden") is int value ? QuickToggleSupport.HiddenFilesShown(value) : false;

    public static bool SetHiddenFiles(bool shown)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Advanced);
            key.SetValue("Hidden", shown ? 1 : 2, RegistryValueKind.DWord);
            BroadcastText(Broadcast, WmSettingChange, IntPtr.Zero, "ShellState", SMTO_ABORTIFHUNG, 200, out _);
            // Open Explorer windows redraw their lists.
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception e) { Log.Warning(e, "Hidden files could not be changed"); return false; }
    }

    public static bool DesktopIconsShown() =>
        QuickToggleSupport.DesktopIconsShown(Registry.CurrentUser.OpenSubKey(Advanced)?.GetValue("HideIcons") as int?);

    /// <summary>The desktop's own "Show desktop icons" command, sent to its list view.</summary>
    public static bool SetDesktopIcons(bool shown)
    {
        if (DesktopIconsShown() == shown) return true;
        var view = DesktopView();
        if (view == IntPtr.Zero) return false;
        PostMessage(view, WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero);
        return true;
    }

    private static IntPtr DesktopView()
    {
        var program = FindWindow("Progman", null);
        var view = FindWindowEx(program, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;
        // With a slideshow wallpaper the view lives in one of the WorkerW windows.
        for (var worker = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null); worker != IntPtr.Zero;
             worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null))
        {
            view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) return view;
        }
        return IntPtr.Zero;
    }

    // MARK: Commands

    /// <summary>Empties the Recycle Bin without asking again: the panel has already asked.</summary>
    public static bool EmptyRecycleBin() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) >= 0;

    public static bool Lock() => LockWorkStation();

    public static bool DisplayOff() =>
        SendMessageTimeout(Broadcast, WM_SYSCOMMAND, new IntPtr(0xF170), new IntPtr(2), SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero;

    public static bool ScreenSaver() => PostMessage(GetDesktopWindow(), WM_SYSCOMMAND, new IntPtr(0xF140), IntPtr.Zero);

    // MARK: Ejecting

    /// <summary>A drive the eject toggle may offer, with what Windows says about it.</summary>
    public sealed record Volume(string Root, string Name, string? Id, QuickToggleSupport.DriveFacts Facts);

    /// <summary>External and removable volumes, minus the ones the user excluded.</summary>
    public static IReadOnlyList<Volume> EjectableVolumes(IEnumerable<string> exclusions)
    {
        var usbLetters = UsbDriveLetters();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var list = new List<Volume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var local = drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom;
                var facts = new QuickToggleSupport.DriveFacts(local, string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                    usbLetters.Contains(drive.Name[..2]), drive.DriveType == DriveType.Removable, drive.DriveType == DriveType.CDRom);
                var name = drive.IsReady ? drive.VolumeLabel : "";
                var label = name.Length > 0 ? name : drive.Name;
                if (QuickToggleSupport.ShouldOfferEject(facts) && !QuickToggleSupport.IsExcluded(exclusions, label, null, drive.Name))
                    list.Add(new Volume(drive.Name, label, null, facts));
            }
            catch (IOException) { /* a drive that went away mid-listing */ }
        }
        return list;
    }

    /// <summary>The letters of volumes on a USB disk, which Windows reports as fixed media.</summary>
    private static HashSet<string> UsbDriveLetters()
    {
        var letters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var disks = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive WHERE InterfaceType = 'USB'");
            foreach (ManagementObject disk in disks.Get())
            {
                using (disk)
                {
                    foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
                        using (partition)
                            foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                                using (logical) letters.Add(Convert.ToString(logical["DeviceID"]) ?? "");
                }
            }
        }
        catch (Exception e) { Log.Debug(e, "USB disks could not be listed"); }
        return letters;
    }

    /// <summary>The shell's own Eject for a drive, as the context menu does it.</summary>
    public static bool Eject(string root)
    {
        try
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic drives = ((dynamic)shell).NameSpace(17);
            dynamic item = drives.ParseName(root);
            item.InvokeVerb("Eject");
            return true;
        }
        catch (Exception e) { Log.Warning(e, "Eject failed for {Root}", root); return false; }
    }

    // MARK: Refresh rate

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DevMode devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? device, ref DevMode devMode, IntPtr window, uint flags, IntPtr param);

    private const int CurrentSettings = -1;
    private const uint DmDisplayFrequency = 0x400000, CdsUpdateRegistry = 1;

    /// <summary>The refresh rate now and the rates this display offers at its current size, or null when unreadable.</summary>
    public static (int Current, IReadOnlyList<int> Available)? RefreshRates(string deviceName)
    {
        var now = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(deviceName, CurrentSettings, ref now)) return null;
        var rates = new SortedSet<int>();
        for (var i = 0; ; i++)
        {
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(deviceName, i, ref mode)) break;
            if (mode.PelsWidth == now.PelsWidth && mode.PelsHeight == now.PelsHeight && mode.BitsPerPel == now.BitsPerPel && mode.DisplayFrequency > 1)
                rates.Add((int)mode.DisplayFrequency);
        }
        return ((int)now.DisplayFrequency, rates.ToList());
    }

    public static bool SetRefreshRate(string deviceName, int hertz)
    {
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(deviceName, CurrentSettings, ref mode)) return false;
        mode.DisplayFrequency = (uint)hertz;
        mode.Fields = DmDisplayFrequency;
        return ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == 0;
    }

    // MARK: Radios

    public static async Task<bool?> RadioOnAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
            return radio is null ? null : radio.State == RadioState.On;
        }
        catch (Exception e) { Log.Debug(e, "Radio unreadable"); return null; }
    }

    public static async Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return false;
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
            return radio is not null && await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch (Exception e) { Log.Warning(e, "Radio could not be switched"); return false; }
    }
}
