// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Wallpaper/WallpaperSupport.swift (the gallery lifecycle, folder scan, merge, filter,
// paging and display targets). Windows differences: the built-in pictures are Windows' own (Web\Wallpaper, Web\Screen), a hidden or
// system file is skipped where macOS skips dot files, a reparse point is skipped where macOS skips a symbolic link, and there are no
// bundles to avoid. The Apple catalog, .madesktop thumbnails and the Store.plist patch have no counterpart.
namespace Notchly.Core.Tools;

/// <summary>
/// Lets a folder scan publish only while its gallery is open and it is the latest scan. The scan reads this from a worker thread while
/// the UI changes visibility on the UI thread.
/// </summary>
public sealed class WallpaperGalleryLifecycle
{
    private readonly object _lock = new();
    private readonly HashSet<Guid> _viewers = [];
    private Guid _generation = Guid.NewGuid();

    public bool IsVisible { get { lock (_lock) return _viewers.Count > 0; } }

    public void Begin(Guid viewer) { lock (_lock) _viewers.Add(viewer); }

    /// <summary>A closing gallery cancels work only when no other gallery is still open.</summary>
    public bool End(Guid viewer)
    {
        lock (_lock)
        {
            if (!_viewers.Remove(viewer) || _viewers.Count > 0) return false;
            _generation = Guid.NewGuid();
            return true;
        }
    }

    public void EndAll()
    {
        lock (_lock)
        {
            _viewers.Clear();
            _generation = Guid.NewGuid();
        }
    }

    public Guid Invalidate()
    {
        lock (_lock)
        {
            _generation = Guid.NewGuid();
            return _generation;
        }
    }

    public bool Accepts(Guid token)
    {
        lock (_lock) return _viewers.Count > 0 && _generation == token;
    }
}

public enum WallpaperSource { System, Own }

public enum WallpaperFilter { All, Own, System }

public sealed record WallpaperEntry(string Id, string ImagePath, string PreviewPath, string Title, WallpaperSource Source);

public static class WallpaperSupport
{
    /// <summary>The pictures Windows can set as a background without a codec.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jfif", "png", "bmp", "gif", "tif", "tiff",
    };

    public const int PageSize = 24;

    public static bool IsStillImage(string path) =>
        Path.GetExtension(path) is { Length: > 1 } extension && ImageExtensions.Contains(extension[1..]);

    public static string Title(string path) => Path.GetFileNameWithoutExtension(path);

    private static string Key(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static int ByTitle(WallpaperEntry a, WallpaperEntry b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// The pictures in a folder and its subfolders, by file name. Hidden and system files and links are skipped (a link is never
    /// followed, so a loop cannot trap the scan). Returns nothing when <paramref name="shouldContinue"/> turns false.
    /// </summary>
    public static IReadOnlyList<string> ImagesInFolder(string folder, Func<bool>? shouldContinue = null)
    {
        shouldContinue ??= () => true;
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (!shouldContinue()) return [];
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (IsStillImage(entry)) found.Add(entry);
            }
        }
        return found.OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static IReadOnlyList<WallpaperEntry> OwnEntries(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<WallpaperEntry>();
        foreach (var path in paths)
        {
            var key = Key(path);
            if (seen.Add(key)) entries.Add(new WallpaperEntry(key, path, path, Title(path), WallpaperSource.Own));
        }
        return entries;
    }

    /// <summary>The pictures in the given folders (Windows' own), as system entries, by title.</summary>
    public static IReadOnlyList<WallpaperEntry> SystemEntries(IEnumerable<string> folders, Func<bool>? shouldContinue = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<WallpaperEntry>();
        foreach (var folder in folders)
            foreach (var path in ImagesInFolder(folder, shouldContinue))
            {
                var key = Key(path);
                if (seen.Add(key)) entries.Add(new WallpaperEntry(key, path, path, Title(path), WallpaperSource.System));
            }
        entries.Sort(ByTitle);
        return entries;
    }

    public static IReadOnlyList<WallpaperEntry> Merge(IEnumerable<WallpaperEntry> system, IEnumerable<WallpaperEntry> own)
    {
        var merged = system.ToList();
        var seen = merged.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        merged.AddRange(own.Where(e => seen.Add(e.Id)));
        merged.Sort(ByTitle);
        return merged;
    }

    public static IReadOnlyList<WallpaperEntry> Filtered(IEnumerable<WallpaperEntry> entries, WallpaperFilter filter) => filter switch
    {
        WallpaperFilter.Own => entries.Where(e => e.Source == WallpaperSource.Own).ToList(),
        WallpaperFilter.System => entries.Where(e => e.Source == WallpaperSource.System).ToList(),
        _ => entries.ToList(),
    };

    public static string FilterRaw(WallpaperFilter filter) => filter switch
    {
        WallpaperFilter.Own => "own", WallpaperFilter.System => "system", _ => "all",
    };

    public static WallpaperFilter FilterFromRaw(string? raw) => raw switch
    {
        "own" => WallpaperFilter.Own, "system" => WallpaperFilter.System, _ => WallpaperFilter.All,
    };

    /// <summary>Entries the person hid from a folder they added: dropped without deleting the file.</summary>
    public static IReadOnlyList<WallpaperEntry> WithoutExcluded(IEnumerable<WallpaperEntry> entries, IReadOnlySet<string> excludedIds) =>
        entries.Where(e => e.Source != WallpaperSource.Own || !excludedIds.Contains(e.Id)).ToList();

    // MARK: Paging (1-based)

    public static int PageCount(int itemCount, int pageSize = PageSize) =>
        pageSize <= 0 || itemCount <= 0 ? (itemCount == 0 ? 0 : 1) : (itemCount + pageSize - 1) / pageSize;

    public static int ClampedPage(int page, int itemCount, int pageSize = PageSize)
    {
        var pages = PageCount(itemCount, pageSize);
        return pages <= 0 ? 1 : Math.Min(Math.Max(page, 1), pages);
    }

    public static IReadOnlyList<T> PageSlice<T>(IReadOnlyList<T> items, int page, int pageSize = PageSize)
    {
        if (items.Count == 0 || pageSize <= 0) return [];
        var start = (ClampedPage(page, items.Count, pageSize) - 1) * pageSize;
        return start >= items.Count ? [] : items.Skip(start).Take(Math.Min(pageSize, items.Count - start)).ToList();
    }

    /// <summary>The displays a picture is set on: all of them, or the one with the pointer (else the first).</summary>
    public static IReadOnlyList<string> TargetDisplays(bool allDisplays, IReadOnlyList<string> displayIds, string? pointerDisplayId)
    {
        if (allDisplays) return displayIds;
        if (pointerDisplayId is not null && displayIds.Contains(pointerDisplayId)) return [pointerDisplayId];
        return displayIds.Count > 0 ? [displayIds[0]] : [];
    }

    /// <summary>The folders and pictures a person added, as the one-per-line text the settings keep.</summary>
    public static IReadOnlyList<string> SplitPaths(string? raw) =>
        (raw ?? "").Split(['\n'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim('\r', ' ')).Where(p => p.Length > 0).ToList();

    public static string JoinPaths(IEnumerable<string> paths) => string.Join("\n", paths);
}
