// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Wallpaper/WallpaperService.swift: the pictures to choose from (Windows' own, and the
// images and folders the person added), applying one to every display or the one with the pointer, and hiding a picture of an added
// folder without deleting it. Windows' own pictures come from its Web folder where upstream reads Apple's catalog.
using System.IO;
using Notchly.App.Platform;
using Notchly.App.Platform.Display;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Tools;

public sealed class WallpaperService(SettingsStore settings) : IFeatureService
{
    public const string Title = "Wallpaper", ApplyFailed = "Could not set the wallpaper";

    private WallpaperWindow? _window;

    public WallpaperGalleryLifecycle Gallery { get; } = new();

    public void Sync() { }

    /// <summary>Windows' own picture folders.</summary>
    public static IReadOnlyList<string> SystemFolders
    {
        get
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return [Path.Combine(windows, "Web", "Wallpaper"), Path.Combine(windows, "Web", "Screen")];
        }
    }

    public IReadOnlyList<string> OwnPaths => WallpaperSupport.SplitPaths(settings.GetString(DefaultsKey.WallpaperOwnPaths));
    private HashSet<string> Excluded => WallpaperSupport.SplitPaths(settings.GetString(DefaultsKey.WallpaperExcludedOwnPaths)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public int HiddenCount => Excluded.Count;

    public void Open()
    {
        if (_window is { IsVisible: true } open) { open.Activate(); return; }
        _window = new WallpaperWindow(this, settings);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }

    /// <summary>
    /// Reads the pictures on a worker thread. Null when the gallery closed or a newer scan began meanwhile
    /// (<paramref name="token"/> from <see cref="WallpaperGalleryLifecycle.Invalidate"/>), so a late result is never shown.
    /// </summary>
    public Task<IReadOnlyList<WallpaperEntry>?> LoadAsync(Guid token) => Task.Run<IReadOnlyList<WallpaperEntry>?>(() =>
    {
        bool Continue() => Gallery.Accepts(token);
        var system = WallpaperSupport.SystemEntries(SystemFolders.Where(Directory.Exists), Continue);
        var own = new List<WallpaperEntry>();
        foreach (var path in OwnPaths)
        {
            if (!Continue()) return null;
            if (Directory.Exists(path)) own.AddRange(WallpaperSupport.OwnEntries(WallpaperSupport.ImagesInFolder(path, Continue)));
            else if (File.Exists(path) && WallpaperSupport.IsStillImage(path)) own.AddRange(WallpaperSupport.OwnEntries([path]));
        }
        if (!Continue()) return null;
        var merged = WallpaperSupport.Merge(system, WallpaperSupport.WithoutExcluded(WallpaperSupport.OwnEntries(own.Select(e => e.ImagePath)), Excluded));
        return Continue() ? merged : null;
    });

    public void AddPaths(IEnumerable<string> paths)
    {
        var list = OwnPaths.ToList();
        foreach (var path in paths)
            if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        settings.Set(DefaultsKey.WallpaperOwnPaths, WallpaperSupport.JoinPaths(list));
    }

    /// <summary>A picture added by itself is forgotten; one found in an added folder is hidden, and the file stays.</summary>
    public void Remove(WallpaperEntry entry)
    {
        var direct = OwnPaths.FirstOrDefault(p => string.Equals(Path.GetFullPath(p), entry.Id, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) { RemovePath(direct); return; }
        var hidden = Excluded;
        hidden.Add(entry.Id);
        settings.Set(DefaultsKey.WallpaperExcludedOwnPaths, WallpaperSupport.JoinPaths(hidden.Order(StringComparer.OrdinalIgnoreCase)));
    }

    public void RemovePath(string path) =>
        settings.Set(DefaultsKey.WallpaperOwnPaths, WallpaperSupport.JoinPaths(OwnPaths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))));

    public void ShowHidden() => settings.Set(DefaultsKey.WallpaperExcludedOwnPaths, "");

    /// <summary>The picture each display shows now, so the gallery can mark it.</summary>
    public IReadOnlySet<string> CurrentPictures()
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in DesktopWallpaper.Monitors())
            if (DesktopWallpaper.Current(monitor.Id) is { Length: > 0 } path) current.Add(Path.GetFullPath(path));
        return current;
    }

    /// <summary>Sets the picture on every display, or on the display with the pointer. False when Windows refused.</summary>
    public bool Apply(WallpaperEntry entry)
    {
        var monitors = DesktopWallpaper.Monitors();
        var all = settings.GetBool(DefaultsKey.WallpaperApplyAllDisplays);
        if (all || monitors.Count <= 1) return DesktopWallpaper.Set(null, entry.ImagePath);
        Native.GetCursorPos(out var p);
        var pointer = monitors.FirstOrDefault(m => m.Contains(p.X, p.Y))?.Id;
        var target = WallpaperSupport.TargetDisplays(false, monitors.Select(m => m.Id).ToList(), pointer);
        return target.Count > 0 && target.All(id => DesktopWallpaper.Set(id, entry.ImagePath));
    }

    public void Dispose()
    {
        Gallery.EndAll();
        _window?.Close();
    }
}
