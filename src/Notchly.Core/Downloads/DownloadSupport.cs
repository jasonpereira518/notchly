// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchDownloadSupport.swift: partial names, the
// folder snapshot, merging, completion evidence and the compact strip's wing; plus the transitions
// NotchDownloadService.scan() draws between two snapshots.
using System.Globalization;

namespace Notchly.Core.Downloads;

public static class DownloadSupport
{
    /// <summary>Partials the folder reader follows at once.</summary>
    public const int MaximumObservedFiles = 32;
    /// <summary>Folder entries the page lists, newest first.</summary>
    public const int MaximumListedFiles = 200;
    /// <summary>Recent arrivals kept beside the folder's own listing.</summary>
    public const int MaximumFinished = 5;
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromSeconds(15);
    /// <summary>A transfer that stopped growing this long is shown as paused.</summary>
    public static readonly TimeSpan InactiveAfter = TimeSpan.FromSeconds(120);
    /// <summary>How long a vanished partial waits for its arrival before it counts as failed.</summary>
    public static readonly TimeSpan FailureGrace = TimeSpan.FromSeconds(2);

    public const double PercentSize = 10;
    public const double CompactNameWingThreshold = 94;
    public const double CompactNameMinimumWing = 64;
    public const double CompactNameMaximumWing = 160;
    public const double CompactBareWing = 56;

    /// <summary>
    /// Chromium browsers (Chrome, Edge, Brave, Opera, Vivaldi) write <c>.crdownload</c>, Firefox
    /// <c>.part</c>, Safari a <c>.download</c> package; <c>.partial</c> is legacy Edge and download managers.
    /// </summary>
    public static readonly IReadOnlyList<string> PartialExtensions = ["crdownload", "part", "download", "partial"];

    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    /// <summary>The file a partial becomes, or null when the name is not a partial transfer.</summary>
    public static string? ExpectedPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (extension.Length < 2 || !PartialExtensions.Contains(extension[1..], Paths)) return null;
        var output = path[..^extension.Length];
        return System.IO.Path.GetFileName(output).Length == 0 ? null : output;
    }

    public static bool IsPartial(string path) => ExpectedPath(path) is not null;

    public static string Normalize(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    public static bool IsDirectChild(string path, string folder)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !System.IO.Path.IsPathFullyQualified(folder)) return false;
        var parent = System.IO.Path.GetDirectoryName(Normalize(path));
        return parent is not null && Paths.Equals(Normalize(parent), Normalize(folder));
    }

    /// <summary>A share of the transfer, only when its total is known.</summary>
    public static double? Fraction(long completed, long total, double reportedFraction, bool indeterminate)
    {
        if (indeterminate || total <= 0 || completed < 0 || !double.IsFinite(reportedFraction)) return null;
        return Math.Clamp(reportedFraction, 0, 1);
    }

    /// <summary>A partial's identity reappearing at the name it was to become is a finished download.</summary>
    public static bool DidFinish(PartialDownload partial, string finalPath, string? identity) =>
        Paths.Equals(Normalize(partial.ExpectedPath), Normalize(finalPath))
        && partial.Identity is { } previous && identity is not null && previous == identity;

    /// <summary>
    /// Active transfers own their destination until they finish, so a browser's placeholder (Firefox
    /// creates the final name empty beside its .part) never shows as a finished file. Folder entries stay
    /// after the short completion notice ends. Newest first, then by path.
    /// </summary>
    public static IReadOnlyList<DownloadItem> MergedItems(IEnumerable<DownloadItem> active, IEnumerable<DownloadItem> files,
                                                          IEnumerable<DownloadItem> finished)
    {
        var activeList = active.ToList();
        var represented = new HashSet<string>(Paths);
        foreach (var item in activeList)
        {
            represented.Add(Normalize(item.Path));
            represented.Add(Normalize(ExpectedPath(item.Path) ?? item.Path));
        }
        var seen = new HashSet<string>(Paths);
        return activeList
            .Concat(files.Where(f => !represented.Contains(Normalize(f.Path))))
            .Concat(finished.Where(f => !represented.Contains(Normalize(f.Path))))
            .Where(item => seen.Add(Normalize(item.Path)))
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToList();
    }

    // MARK: Folder snapshot

    /// <summary>
    /// The folder's top level: partials (at most 32, with identity and live size from <paramref name="facts"/>)
    /// and visible entries (newest 200). Hidden and system entries such as desktop.ini, links and junctions
    /// are skipped and nothing nested is read. Null when the folder itself cannot be read.
    /// </summary>
    public static DownloadFolderSnapshot? ScanFolder(string folder, Func<string, FileFacts?> facts)
    {
        var partials = new Dictionary<string, PartialDownload>(Paths);
        var files = new List<DownloadItem>();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                ReturnSpecialDirectories = false,
            };
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                // Symbolic links and junctions; cloud placeholders (OneDrive) are reparse points with no target.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null) continue;
                var path = Normalize(entry.FullName);
                var isDirectory = entry is DirectoryInfo;
                var created = Valid(entry.CreationTimeUtc) ?? Valid(entry.LastWriteTimeUtc) ?? DateTime.MinValue;
                if (ExpectedPath(path) is not { } expected)
                {
                    files.Add(new DownloadItem(path, entry.Name, isDirectory ? null : ((FileInfo)entry).Length, 1,
                        Completed: true, Active: false, created));
                    continue;
                }
                if (partials.Count >= MaximumObservedFiles) continue;
                string? content = null;
                if (isDirectory)
                {
                    // A partial package can hold the real file. Inspect only its expected payload.
                    var payload = System.IO.Path.Combine(path, System.IO.Path.GetFileName(expected));
                    if (File.Exists(payload) && !new FileInfo(payload).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        content = payload;
                }
                var read = facts(content ?? path);
                var bytes = read is { IsDirectory: false } ? read.Bytes : content is null && !isDirectory ? ((FileInfo)entry).Length : 0;
                partials[path] = new PartialDownload(path, expected, bytes, read?.Identity, created,
                    Valid(entry.LastWriteTimeUtc) ?? created, content);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        // The UI merges and compares this list on every tick, so a crowded folder hands it only its newest entries.
        if (files.Count > MaximumListedFiles)
            files = files.OrderByDescending(f => f.Date).ThenBy(f => f.Path, StringComparer.Ordinal).Take(MaximumListedFiles).ToList();
        return new DownloadFolderSnapshot(partials, files);
    }

    private static DateTime? Valid(DateTime value) => value.Year > 1601 ? value : null;

    // MARK: Transitions

    /// <summary>
    /// Between two scans: a partial that went away whose identity now sits at its expected name finished.
    /// One that went away with nothing at its name, no other partial taking over the same name or the same
    /// identity (Chromium's "Unconfirmed 123.crdownload" becoming "name.crdownload"), and no rename into a
    /// final name seen by the watcher, did not finish. A failed read of the folder decides nothing.
    /// </summary>
    public static DownloadScanOutcome Compare(IReadOnlyDictionary<string, PartialDownload> previous,
                                              DownloadFolderSnapshot? current, Func<string, FileFacts?> facts,
                                              Func<string, bool> exists, Func<PartialDownload, bool> finishedElsewhere,
                                              DateTime now)
    {
        var completed = new List<DownloadItem>();
        var failed = new List<PartialDownload>();
        foreach (var old in previous.Values)
        {
            if (current?.Partials.ContainsKey(old.Path) == true || exists(old.Path)) continue;
            if (facts(old.ExpectedPath) is { IsDirectory: false } arrived && DidFinish(old, old.ExpectedPath, arrived.Identity))
            {
                completed.Add(new DownloadItem(old.ExpectedPath, System.IO.Path.GetFileName(old.ExpectedPath), arrived.Bytes, 1,
                    Completed: true, Active: false, now));
                continue;
            }
            if (current is null) continue;
            var takenOver = current.Partials.Values.Any(p =>
                Paths.Equals(p.ExpectedPath, old.ExpectedPath) || (old.Identity is not null && p.Identity == old.Identity));
            if (!takenOver && !exists(old.ExpectedPath) && !finishedElsewhere(old)) failed.Add(old);
        }
        return new DownloadScanOutcome(completed, failed);
    }

    /// <summary>
    /// The watcher saw a partial renamed to a final name: Windows' counterpart of the browsers' "download
    /// finished" broadcast on macOS. Only a visible entry directly in the folder counts; links, partials,
    /// missing, nested, outside and relative paths do not.
    /// </summary>
    public static DownloadItem? RenamedItem(string oldPath, string newPath, string folder, DateTime now)
    {
        if (!IsPartial(oldPath) || IsPartial(newPath) || !IsDirectChild(newPath, folder)) return null;
        var path = Normalize(newPath);
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null) return null;
        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return null;
        return new DownloadItem(path, info.Name, info is FileInfo file ? file.Length : null, 1, Completed: true, Active: false, now);
    }

    // MARK: Compact strip

    /// <summary>
    /// The wing beside the camera: wide enough for the arrow and the file's name, between 64 and 160 DIP;
    /// the bare 56 without a name or without room for one.
    /// </summary>
    public static double CompactWing(string? name, double contentWidth, double inset, double room = double.PositiveInfinity)
    {
        if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(contentWidth) || room < CompactNameWingThreshold) return CompactBareWing;
        return Math.Min(CompactNameMaximumWing, Math.Max(CompactNameMinimumWing, Math.Ceiling(inset + contentWidth + 4)));
    }

    // MARK: Text

    /// <summary>Sizes as Explorer writes them: 1024-based, one decimal below 100.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{Math.Max(0, bytes)} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var format = value < 100 ? "0.#" : "0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string FormatRate(double bytesPerSecond) => FormatBytes((long)Math.Round(bytesPerSecond)) + "/s";

    /// <summary>"Downloading · 48.2 MB · 3.1 MB/s", "Paused · 48.2 MB", or the unknown total.</summary>
    public static string ProgressLine(DownloadItem item)
    {
        var parts = new List<string> { item.Active ? "Downloading" : "Paused" };
        if (item.ReceivedBytes is > 0 and var bytes) parts.Add(FormatBytes(bytes));
        else if (item.Fraction is null) parts.Add("Total size unavailable");
        if (item.Active && item.BytesPerSecond is > 0 and var rate) parts.Add(FormatRate(rate));
        return string.Join(" · ", parts);
    }
}
