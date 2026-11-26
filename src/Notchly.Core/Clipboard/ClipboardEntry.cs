// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Clipboard/ClipboardHistoryService.swift (ClipboardHistoryEntry): one thing that was copied.
namespace Notchly.Core.Clipboard;

public enum ClipboardKind { Text, Image, Files }

/// <summary>
/// One history entry. Text entries hold the trimmed text; image entries hold the name of a PNG in the history's image folder and
/// its SHA-256; file entries hold the copied paths in order.
/// </summary>
public sealed record ClipboardEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; init; } = "";
    public DateTimeOffset CopiedAt { get; init; }
    public DateTimeOffset? PinnedAt { get; init; }
    public ClipboardKind Kind { get; init; } = ClipboardKind.Text;
    public IReadOnlyList<string> FilePaths { get; init; } = [];
    public string? ImageFile { get; init; }
    public string? ImageHash { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    /// <summary>The program that put it on the clipboard (its executable name), when known.</summary>
    public string? SourceApp { get; init; }
    /// <summary>The full path of that program, so its icon can be shown; may be missing.</summary>
    public string? SourcePath { get; init; }

    public bool IsPinned => PinnedAt is not null;

    public static ClipboardEntry ForText(string text, DateTimeOffset at, string? source = null) =>
        new() { Text = text, CopiedAt = at, Kind = ClipboardKind.Text, SourceApp = source };

    public static ClipboardEntry ForImage(string file, string hash, int width, int height, DateTimeOffset at, string? source = null) =>
        new() { Kind = ClipboardKind.Image, ImageFile = file, ImageHash = hash, ImageWidth = width, ImageHeight = height, CopiedAt = at, SourceApp = source };

    public static ClipboardEntry ForFiles(IEnumerable<string> paths, DateTimeOffset at, string? source = null) =>
        new() { Kind = ClipboardKind.Files, FilePaths = paths.ToList(), CopiedAt = at, SourceApp = source };
}
