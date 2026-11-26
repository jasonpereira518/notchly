// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: upstream's RecentCaptureService over its RecentCaptureStore. The folder is %APPDATA%\Notchly\Captures: history.json,
// the screenshots (as PNG) and a thumbnail each. Core decides what is kept; this class only reads and writes the folder, refusing
// links and writing the index through a temporary file, and tells the windows when the list changed.
using System.IO;
using Notchly.App.Platform.Capture;
using Notchly.Core;
using Notchly.Core.Capture;
using Serilog;

namespace Notchly.App.Screenshots;

public sealed class CaptureHistory : IRecentCaptureStorage
{
    private readonly object _gate = new();
    private readonly RecentCaptureStore _store;

    public CaptureHistory(string? folder = null)
    {
        Folder = folder ?? Path.Combine(AppIdentity.DataDirectory, "Captures");
        _store = new RecentCaptureStore(this);
    }

    public string Folder { get; }

    /// <summary>The list changed (a capture added or removed, or the history cleared).</summary>
    public event Action? Changed;

    public IReadOnlyList<RecentCaptureEntry> Entries()
    {
        lock (_gate)
        {
            if (!_store.LoadIfNeeded()) return [];
            var before = _store.Entries.Count;
            _store.PruneMissing(name => File.Exists(Path.Combine(Folder, name)),
                recordingExists: File.Exists, screenshotBytes: SizeOf);
            if (_store.Entries.Count != before) _store.Persist();
            return _store.Entries.ToList();
        }
    }

    public string? PathOf(RecentCaptureEntry entry) =>
        entry.ScreenshotName is { } name && RecentCapturePolicy.IsSafeName(name) ? Path.Combine(Folder, name) : entry.RecordingPath;

    public string? ThumbnailPathOf(RecentCaptureEntry entry) =>
        entry.ThumbnailName is { } name && RecentCapturePolicy.IsSafeName(name) ? Path.Combine(Folder, name) : null;

    /// <summary>Keeps a screenshot (and a thumbnail) in the history; null when the folder cannot be written.</summary>
    public RecentCaptureEntry? Add(CapturedImage image, double scale, Platform.Native.RECT area)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var id = Guid.NewGuid();
            var shotName = RecentCapturePolicy.ScreenshotFileName(id);
            var thumbName = RecentCapturePolicy.ThumbnailFileName(id);
            File.WriteAllBytes(Path.Combine(Folder, shotName), ImageCodec.EncodePng(image, scale));
            File.WriteAllBytes(Path.Combine(Folder, thumbName), ImageCodec.EncodePng(ImageCodec.Thumbnail(image)));
            var entry = new RecentCaptureEntry(id, RecentCaptureKind.Screenshot, DateTimeOffset.Now, shotName, null, thumbName, scale,
                area.Left, area.Top, area.Width, area.Height);
            lock (_gate)
            {
                if (!_store.LoadIfNeeded()) { TryDelete(shotName); TryDelete(thumbName); return null; }
                _store.Prepend(entry, SizeOf);
                _store.Persist();
            }
            Changed?.Invoke();
            return entry;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "A capture could not be added to the history");
            return null;
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_store.LoadIfNeeded() || !_store.Remove(id)) return;
            _store.Persist();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (!_store.LoadIfNeeded()) return;
            _store.Clear();
            _store.Persist();
        }
        Changed?.Invoke();
    }

    private long? SizeOf(RecentCaptureEntry entry)
    {
        try { return entry.ScreenshotName is { } name ? new FileInfo(Path.Combine(Folder, name)).Length : null; }
        catch (IOException) { return null; }
    }

    // MARK: IRecentCaptureStorage

    private string IndexPath => Path.Combine(Folder, RecentCapturePolicy.IndexFileName);

    IndexRead IRecentCaptureStorage.ReadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new IndexRead(IndexReadStatus.Missing);
            return new IndexRead(IndexReadStatus.Ok, File.ReadAllText(IndexPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new IndexRead(IndexReadStatus.Unreadable); }
    }

    bool IRecentCaptureStorage.WriteIndex(string json)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = IndexPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, IndexPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "The capture history could not be written");
            return false;
        }
    }

    IReadOnlyList<string>? IRecentCaptureStorage.ListFiles()
    {
        try { return Directory.Exists(Folder) ? Directory.GetFiles(Folder).Select(Path.GetFileName).OfType<string>().ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    void IRecentCaptureStorage.DeleteFile(string name) => TryDelete(name);

    private void TryDelete(string name)
    {
        if (!RecentCapturePolicy.IsSafeName(name)) return;
        try
        {
            var path = Path.Combine(Folder, name);
            // Never follow a link out of the folder.
            if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
