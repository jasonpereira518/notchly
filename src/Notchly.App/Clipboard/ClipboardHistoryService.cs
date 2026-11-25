// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Clipboard/ClipboardHistoryService.swift and ClipboardHistoryWrite.swift: records what is
// copied (text, an image, files), keeps it within a limit and an age, keeps it on disk, and puts an entry back on the clipboard. Windows
// differences: changes arrive from the shared ClipboardMonitor instead of a poll, content that asks not to be recorded is found by its
// clipboard formats, the source is a program name, and the file and the images live in %APPDATA%\Notchly.
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using Notchly.App.Shell;
using Notchly.Core;
using Notchly.Core.Clipboard;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Pasteboard;

public sealed class ClipboardHistoryService : IFeatureService, IClipboardConsumer
{
    public const string FileName = "ClipboardHistory.json", ImageFolder = "ClipboardImages";

    private readonly SettingsStore _settings;
    private readonly ClipboardMonitor _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly string _folder;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ClipboardAutoClear _autoClear;
    private IDisposable? _registration;
    private DispatcherTimer? _trimTimer, _saveTimer;
    private IReadOnlyList<ClipboardEntry> _entries = [];
    private bool _loaded, _loadFailed, _dirty;
    private Task _saveChain = Task.CompletedTask;
    private readonly HashSet<Guid> _exempt = [];

    public ClipboardHistoryService(SettingsStore settings, ClipboardMonitor monitor, Dispatcher dispatcher, string? folder = null, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings;
        _monitor = monitor;
        _dispatcher = dispatcher;
        _folder = folder ?? AppIdentity.DataDirectory;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _autoClear = new ClipboardAutoClear(settings, monitor, dispatcher, _clock);
    }

    public int Order => 10;
    public bool WantsImage => _settings.GetBool(DefaultsKey.ClipboardHistoryIncludeImagesFiles);

    /// <summary>The history: pinned entries first, then the newest. Changes only on the UI thread.</summary>
    public IReadOnlyList<ClipboardEntry> Entries { get { EnsureLoaded(); return _entries; } }

    public event Action? Changed;

    private string HistoryFile => Path.Combine(_folder, FileName);
    private string ImageDirectory => Path.Combine(_folder, ImageFolder);
    public string ImagePath(ClipboardEntry entry) => entry.ImageFile is { } file ? Path.Combine(ImageDirectory, file) : "";

    // MARK: Lifecycle

    public void Sync()
    {
        var on = _settings.GetBool(DefaultsKey.ClipboardHistoryEnabled);
        if (on && _registration is null)
        {
            EnsureLoaded();
            _registration = _monitor.Add(this);
            _trimTimer = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => Trim(), _dispatcher);
        }
        else if (!on && _registration is not null)
        {
            _registration.Dispose();
            _registration = null;
            _trimTimer?.Stop();
            _trimTimer = null;
        }
        if (on) Trim();
        _autoClear.Sync();
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _registration = null;
        _trimTimer?.Stop();
        _autoClear.Dispose();
        Flush();
    }

    /// <summary>An entry open in an editor is not aged out while it is open.</summary>
    public void Exempt(Guid id, bool exempt) { if (exempt) _exempt.Add(id); else _exempt.Remove(id); }

    // MARK: Capture

    public void OnChange(ClipboardChange change)
    {
        if (!_settings.GetBool(DefaultsKey.ClipboardHistoryEnabled)) return;
        var s = change.Snapshot;
        if (s.IsExcluded) return;
        if (ClipboardHistorySupport.IsIgnored(s.SourceProgram, _settings.GetList(DefaultsKey.ClipboardHistoryIgnoredApps))) return;
        var entry = EntryFor(s);
        if (entry is null) return;
        Record(entry);
    }

    private ClipboardEntry? EntryFor(ClipboardSnapshot s)
    {
        var now = _clock();
        var includeMedia = _settings.GetBool(DefaultsKey.ClipboardHistoryIncludeImagesFiles);
        if (includeMedia && ClipboardHistorySupport.StorableFiles(s.Files) is { } files)
            return ClipboardEntry.ForFiles(files, now, s.SourceProgram);
        if (includeMedia && s.Png is { Length: > 0 } png && png.Length <= ClipboardHistorySupport.MaxImageBytes)
        {
            var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            var existing = _entries.FirstOrDefault(e => e.Kind == ClipboardKind.Image && e.ImageHash == hash);
            var file = existing?.ImageFile;
            if (file is null)
            {
                file = Guid.NewGuid().ToString("N") + ".png";
                try
                {
                    Directory.CreateDirectory(ImageDirectory);
                    File.WriteAllBytes(Path.Combine(ImageDirectory, file), png);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(e, "A clipboard image could not be saved");
                    return null;
                }
            }
            return ClipboardEntry.ForImage(file, hash, s.PixelWidth, s.PixelHeight, now, s.SourceProgram);
        }
        if (s.Text is null) return null;
        var text = ClipboardHistorySupport.StorableText(ClipboardHistorySupport.PreferredText(s.Text, s.WebUrl));
        if (text is null) return null;
        if (_settings.GetBool(DefaultsKey.ClipboardHistoryFilterSensitiveText) && ClipboardHistorySupport.LooksSensitive(text)) return null;
        return ClipboardEntry.ForText(text, now, s.SourceProgram);
    }

    private void Record(ClipboardEntry entry)
    {
        EnsureLoaded();
        _entries = Retained(ClipboardHistorySupport.Recording(_entries, entry));
        Commit();
    }

    private IReadOnlyList<ClipboardEntry> Retained(IReadOnlyList<ClipboardEntry> entries) =>
        ClipboardHistorySupport.Retained(entries, _settings.GetInt(DefaultsKey.ClipboardHistoryLimit),
            _settings.GetInt(DefaultsKey.ClipboardHistoryRetentionDays), _clock(), exempt: _exempt);

    /// <summary>Applies the limit and the age (hourly, and when the settings change).</summary>
    public void Trim()
    {
        EnsureLoaded();
        var kept = Retained(_entries);
        if (kept.Count == _entries.Count) return;
        _entries = kept;
        Commit();
    }

    // MARK: Changes from the UI

    public void TogglePin(Guid id)
    {
        var next = ClipboardHistorySupport.TogglePin(_entries, id, _clock());
        // A pin that would push a pinned entry out of the file is refused.
        if (!ClipboardPersistence.PinnedEntriesFit(next)) return;
        _entries = next;
        Commit();
    }

    public void Move(Guid id, int direction)
    {
        _entries = ClipboardHistorySupport.Move(_entries, id, direction);
        Commit();
    }

    public void Delete(IEnumerable<Guid> ids)
    {
        var drop = ids.ToHashSet();
        _entries = _entries.Where(e => !drop.Contains(e.Id)).ToList();
        Commit();
    }

    /// <summary>Clears the recent entries; pinned ones stay.</summary>
    public void ClearRecent()
    {
        _entries = _entries.Where(e => e.IsPinned).ToList();
        Commit();
    }

    // MARK: Putting an entry back

    /// <summary>Writes the entries to the clipboard, as files when all are files, otherwise as text (a single image as an image).</summary>
    public bool CopyToClipboard(IReadOnlyList<ClipboardEntry> entries)
    {
        if (entries.Count == 0) return false;
        ClipboardWriteResult result;
        if (entries.Count == 1 && entries[0].Kind == ClipboardKind.Image)
        {
            try { result = _monitor.Access.WriteImage(File.ReadAllBytes(ImagePath(entries[0])), excludeFromMonitoring: false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "A history image could not be read"); return false; }
        }
        else if (ClipboardHistorySupport.ModeFor(entries) == PasteMode.Files)
            result = _monitor.Access.WriteFiles(ClipboardHistorySupport.BatchFiles(entries), excludeFromMonitoring: false);
        else
        {
            var text = ClipboardHistorySupport.BatchText(entries);
            if (text.Length == 0) return false;
            result = _monitor.Access.WriteText(text, excludeFromMonitoring: false);
        }
        _monitor.RememberOwnWrite(result);
        if (result.Ok) Touch(entries);
        return result.Ok;
    }

    /// <summary>A copy out of the history moves the entries to the top of their block without recording anything new.</summary>
    private void Touch(IReadOnlyList<ClipboardEntry> entries)
    {
        var now = _clock();
        foreach (var entry in entries.Reverse())
        {
            if (_entries.FirstOrDefault(e => e.Id == entry.Id) is not { } live) continue;
            _entries = ClipboardHistorySupport.Recording(_entries, live with { CopiedAt = now });
        }
        Commit();
    }

    // MARK: Disk

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(HistoryFile)) return;
            var entries = ClipboardPersistence.Decode(File.ReadAllText(HistoryFile));
            if (entries is null)
            {
                // Not readable: leave the file as it is, and do not write over it.
                _loadFailed = true;
                Log.Warning("The clipboard history file could not be read and is left alone");
                return;
            }
            _entries = entries;
            SweepImages(entries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            Log.Warning(e, "The clipboard history could not be loaded");
        }
    }

    private void Commit()
    {
        Changed?.Invoke();
        _dirty = true;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Flush(), _dispatcher);
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes the history now (on a worker, in order) if anything changed.</summary>
    public void Flush()
    {
        _saveTimer?.Stop();
        if (!_dirty || _loadFailed || !_loaded) return;
        _dirty = false;
        var entries = _entries;
        var file = HistoryFile;
        _saveChain = _saveChain.ContinueWith(_ =>
        {
            try
            {
                Directory.CreateDirectory(_folder);
                var temp = file + ".tmp";
                File.WriteAllText(temp, ClipboardPersistence.Encode(entries));
                File.Move(temp, file, overwrite: true);
                SweepImages(entries);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "The clipboard history could not be saved"); }
        }, TaskScheduler.Default);
    }

    /// <summary>Waits for queued writes: only a harness or shutdown needs this.</summary>
    public void WaitForSaves() => _saveChain.Wait(TimeSpan.FromSeconds(10));

    private void SweepImages(IReadOnlyList<ClipboardEntry> entries)
    {
        try
        {
            if (!Directory.Exists(ImageDirectory)) return;
            // A file just written may not be in this list yet: only files a couple of minutes old can be orphans.
            var old = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            var names = Directory.EnumerateFiles(ImageDirectory).Where(f => File.GetCreationTimeUtc(f) < old).Select(Path.GetFileName).OfType<string>();
            foreach (var orphan in ClipboardPersistence.OrphanImages(names, entries))
                File.Delete(Path.Combine(ImageDirectory, orphan));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Debug(e, "Clipboard images could not be swept"); }
    }
}
