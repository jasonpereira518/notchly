// SPDX-License-Identifier: GPL-3.0-or-later
// The island's shelf: the live counterpart of upstream ShelfService.swift for the Files page.
// It owns the item tree (Islet.Core.Shelf.ShelfStore), loads and saves %APPDATA%\Islet\shelf.json,
// reads drops, builds drag-out data, and retires files it wrote itself ten minutes after their
// item leaves. Files the user dropped are never touched. Runs on the UI thread.
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Threading;
using Islet.App.Platform.Shell;
using Islet.Core;
using Islet.Core.Shelf;
using Serilog;
using IDataObject = System.Windows.IDataObject;

namespace Islet.App.Island.Files;

public sealed class ShelfService : IDisposable
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff"];

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveTimer;
    private readonly List<(string Path, DateTime Due)> _retiring = [];
    private readonly DispatcherTimer _retireTimer;

    public ShelfStore Store { get; } = new();
    public string StoreFile { get; }
    public string PayloadDirectory { get; }

    /// <summary>Raised on the UI thread after any change to the items, pins, piles or selection.</summary>
    public event Action? Changed;

    /// <param name="storeFile">Defaults to %APPDATA%\Islet\shelf.json; tests pass their own.</param>
    /// <param name="payloadDirectory">Defaults to %LOCALAPPDATA%\Islet\Shelf.</param>
    public ShelfService(Dispatcher dispatcher, string? storeFile = null, string? payloadDirectory = null)
    {
        _dispatcher = dispatcher;
        StoreFile = storeFile ?? Path.Combine(AppIdentity.DataDirectory, "shelf.json");
        PayloadDirectory = payloadDirectory ?? Path.Combine(AppIdentity.LocalDataDirectory, "Shelf");
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _retireTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _retireTimer.Tick += (_, _) => RetireDue();
        Load();
    }

    public static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private bool IsOwned(string? path) =>
        path is not null && ShelfStore.NormalizePath(path).StartsWith(ShelfStore.NormalizePath(PayloadDirectory) + Path.DirectorySeparatorChar,
                                                                    StringComparison.OrdinalIgnoreCase);

    /// <summary>A missing file survives when its drive or share is simply not connected right now.</summary>
    private static bool FileStillThere(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return true;
        return ShelfPersistence.VolumeRoot(path) is { } root && !Directory.Exists(root);
    }

    // MARK: Persistence

    private void Load()
    {
        string? json = null;
        try { if (File.Exists(StoreFile)) json = File.ReadAllText(StoreFile); }
        catch (IOException e) { Log.Warning(e, "Could not read the shelf"); }
        switch (ShelfPersistence.Load(json))
        {
            case ShelfStoreLoad.Items items:
                Store.Restore(ShelfPersistence.Sanitized(items.Value, FileStillThere), IsImagePath);
                SweepOrphanPayloads();
                break;
            case ShelfStoreLoad.Partial partial:
                // Entries this build cannot read still own files: keep a copy and sweep nothing.
                Backup("partial");
                Store.Restore(ShelfPersistence.Sanitized(partial.Value, FileStillThere), IsImagePath);
                break;
            default:
                Backup("unreadable");
                break;
        }
    }

    private void Backup(string reason)
    {
        try { if (File.Exists(StoreFile)) File.Copy(StoreFile, $"{StoreFile}.{reason}", overwrite: true); }
        catch (IOException e) { Log.Warning(e, "Could not keep a copy of the shelf"); }
        Log.Warning("Shelf store was {Reason}; a copy was kept", reason);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
            var temp = StoreFile + ".tmp";
            File.WriteAllText(temp, ShelfPersistence.Encode(Store.ToPersisted()));
            File.Move(temp, StoreFile, overwrite: true);
        }
        catch (IOException e) { Log.Warning(e, "Could not save the shelf"); }
    }

    private void Changes(bool persist = true)
    {
        if (persist) { _saveTimer.Stop(); _saveTimer.Start(); }
        Changed?.Invoke();
    }

    /// <summary>Startup: folders in the store no saved item points into any more.</summary>
    private void SweepOrphanPayloads()
    {
        if (!Directory.Exists(PayloadDirectory)) return;
        var kept = Store.Items.SelectMany(i => i.Leaves()).Select(l => l.Path).OfType<string>()
                        .Select(ShelfStore.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(PayloadDirectory))
        {
            var normalized = ShelfStore.NormalizePath(folder);
            if (kept.Any(p => p.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // MARK: Changes

    public bool CanAccept(IDataObject data) => DropReader.CanRead(data);

    /// <summary>Adds what was dropped. Files the shelf wrote for a refused drop are deleted at once.</summary>
    public ShelfItem? AddDrop(IDataObject data)
    {
        var written = new List<string>();
        ShelfItem? added = null;
        try
        {
            var items = ShelfDrop.Items(DropReader.Read(data, PayloadDirectory, written));
            added = Store.Add(items);
            if (added is not null) Changes();
            return added;
        }
        finally
        {
            if (added is null) foreach (var path in written) DeleteOwned(path);
        }
    }

    public void AddFiles(IReadOnlyList<string> paths)
    {
        if (Store.Add(paths.Select(p => ShelfItem.File(p, IsImagePath(p))).ToList()) is not null) Changes();
    }

    public void Remove(IEnumerable<Guid> ids) => Retire(Store.Remove(ids));
    public void ClearAll() => Retire(Store.ClearAll());
    public void TogglePin(Guid id) { Store.TogglePin(id); Changes(); }
    public void ToggleExpanded(Guid id) { Store.ToggleExpanded(id); Changes(persist: false); }
    public void ToggleSelection(Guid id) { Store.ToggleSelection(id); Changes(persist: false); }
    public void ExtendSelection(Guid id) { Store.ExtendSelection(id); Changes(persist: false); }
    public void ClearSelection() { if (Store.Selection.Count == 0) return; Store.ClearSelection(); Changes(persist: false); }

    private void Retire(IReadOnlyList<ShelfItem> removed)
    {
        var due = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        foreach (var leaf in ShelfStore.LeavesOf(removed))
            if (IsOwned(leaf.Path)) _retiring.Add((leaf.Path!, due));
        if (_retiring.Count > 0) _retireTimer.Start();
        Changes();
    }

    private void RetireDue()
    {
        var referenced = Store.Items.SelectMany(i => i.Leaves()).Select(l => l.Path).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var entry in _retiring.Where(e => e.Due <= now).ToList())
        {
            _retiring.Remove(entry);
            if (!referenced.Contains(entry.Path)) DeleteOwned(entry.Path);
        }
        if (_retiring.Count == 0) _retireTimer.Stop();
    }

    private void DeleteOwned(string path)
    {
        if (!IsOwned(path)) return;
        try
        {
            File.Delete(path);
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // MARK: Drag out

    /// <summary>What dragging these items out offers: their files, plus text and a single link.</summary>
    public DataObject DragData(IReadOnlyList<ShelfItem> items)
    {
        var leaves = ShelfStore.LeavesOf(items);
        var data = new DataObject();
        var files = leaves.Where(l => l.Kind == ShelfKind.File && l.Path is not null && FileStillThere(l.Path)).Select(l => l.Path!).ToArray();
        if (files.Length > 0)
        {
            var collection = new StringCollection();
            collection.AddRange(files);
            data.SetFileDropList(collection);
        }
        var texts = leaves.Where(l => l.Kind is ShelfKind.Text or ShelfKind.Link).Select(l => l.Text ?? l.Url!).ToList();
        if (texts.Count > 0) data.SetText(string.Join(Environment.NewLine, texts));
        if (files.Length == 0 && leaves.Count == 1 && leaves[0].Kind == ShelfKind.Link)
            data.SetData("UniformResourceLocatorW", new MemoryStream(System.Text.Encoding.Unicode.GetBytes(leaves[0].Url + "\0")));
        return data;
    }

    /// <summary>Upstream drag completion: removal and closing follow the drop, pins and preferences.</summary>
    public void CompleteDrag(IReadOnlyList<ShelfItem> dragged, bool accepted, bool removeAfterDrop)
    {
        if (!ShelfInteraction.ShouldRemoveAfterDrag(accepted, dragged.Count, removeAfterDrop)) return;
        var removable = ShelfInteraction.RemovableAfterDrag(dragged.Select(d => d.Id), Store.ProtectedIds);
        if (removable.Count > 0) Remove(removable);
    }

    // MARK: Create ZIP (upstream NotchFileToolsSupport, with System.IO.Compression)

    /// <summary>Each selected file or folder becomes its own ZIP; outputs join the shelf.</summary>
    public async Task<string?> CreateZipsAsync(IReadOnlyList<ShelfItem> items, Window owner)
    {
        var inputs = ShelfStore.LeavesOf(items).Where(l => l.Kind == ShelfKind.File && l.Path is not null
                                                          && ArchiveSupport.IsArchivableInput(l.Path) && FileStillThere(l.Path))
                                         .Select(l => l.Path!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (inputs.Count == 0) return "Nothing to zip: only files and folders can be archived.";
        List<(string Input, string Output)> jobs;
        if (inputs.Count == 1)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileName(ArchiveSupport.UniqueArchiveName(inputs[0], File.Exists, Path.GetDirectoryName(inputs[0]) ?? "")),
                InitialDirectory = Path.GetDirectoryName(inputs[0]),
                Filter = "ZIP archive|*.zip",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(owner) != true) return null;
            jobs = [(inputs[0], dialog.FileName)];
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to save the ZIP files" };
            if (dialog.ShowDialog(owner) != true) return null;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            jobs = inputs.Select(input =>
            {
                var output = ArchiveSupport.UniqueArchiveName(input, p => File.Exists(p) || taken.Contains(p), dialog.FolderName);
                taken.Add(output);
                return (input, output);
            }).ToList();
        }
        if (jobs.Any(j => !ArchiveSupport.DestinationIsOutsideInputs(j.Output, inputs)))
            return "A ZIP cannot be saved inside a folder it archives.";

        var outputs = await Task.Run(() => jobs.Select(j => Zip(j.Input, j.Output)).OfType<string>().ToList());
        if (outputs.Count > 0) AddFiles(outputs);
        return outputs.Count == jobs.Count ? null : "Some items could not be archived.";
    }

    /// <summary>Written to a staging file first, so a failure never leaves half an archive behind.</summary>
    private static string? Zip(string input, string output)
    {
        var staging = output + ".islet-partial";
        try
        {
            if (File.Exists(staging)) File.Delete(staging);
            if (Directory.Exists(input)) ZipFile.CreateFromDirectory(input, staging, CompressionLevel.Optimal, includeBaseDirectory: true);
            else
            {
                using var archive = ZipFile.Open(staging, ZipArchiveMode.Create);
                archive.CreateEntryFromFile(input, Path.GetFileName(input), CompressionLevel.Optimal);
            }
            File.Move(staging, output, overwrite: true);
            return output;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning(e, "Could not archive {Input}", input);
            try { File.Delete(staging); } catch (IOException) { }
            return null;
        }
    }

    /// <summary>Writes pending changes now.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        Save();
    }

    public void Dispose()
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); Save(); }
        _retireTimer.Stop();
    }
}
