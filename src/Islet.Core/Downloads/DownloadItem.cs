// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchDownloadSupport.swift (NotchDownloadItem,
// NotchPartialDownload): a file in the watched folder, finished or still arriving.
namespace Islet.Core.Downloads;

/// <summary>A download in progress, a file in the folder, or a recent arrival.</summary>
public sealed record DownloadItem(string Path, string Name, long? ReceivedBytes, double? Fraction, bool Completed,
                                  bool Active, DateTime Date)
{
    public string Id => Path;
    /// <summary>Bytes a second over the last few seconds; Windows browsers publish no total, so this stands in.</summary>
    public double? BytesPerSecond { get; init; }
}

/// <summary>A partial transfer (<c>name.crdownload</c>, <c>name.part</c>) and the file it becomes.</summary>
public sealed record PartialDownload(string Path, string ExpectedPath, long Bytes, string? Identity,
                                     DateTime Created, DateTime Modified, string? ContentPath = null);

/// <summary>
/// What one short-lived handle reads about an entry: its identity (volume serial and file index,
/// Windows' dev:ino), its current size and kind. A rename keeps the identity.
/// </summary>
public sealed record FileFacts(string Identity, long Bytes, bool IsDirectory);

/// <summary>The folder's partial transfers and its newest visible entries.</summary>
public sealed record DownloadFolderSnapshot(IReadOnlyDictionary<string, PartialDownload> Partials,
                                            IReadOnlyList<DownloadItem> Files);

/// <summary>What changed between two scans: transfers that finished and those that went away unfinished.</summary>
public sealed record DownloadScanOutcome(IReadOnlyList<DownloadItem> Completed, IReadOnlyList<PartialDownload> Failed);
