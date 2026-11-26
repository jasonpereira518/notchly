// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the file naming half of upstream Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift (fileName, expandSaveSubfolder,
// expandFileNamePattern, uniqueFileName) and of ScreenshotService.saveDestination / fileName / rewindNumberSequence. Pure: the folder,
// the file system and the persisted number sequence come in through small injected seams so every rule is testable.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Notchly.Core.Capture;

/// <summary>The persisted "%#" counter (settings key <c>screenshotFileNumberNext</c>).</summary>
public interface IScreenshotNumberStore
{
    int Next { get; set; }
}

/// <summary>A plain in-memory number store; also what the tests use.</summary>
public sealed class MemoryNumberStore(int next = 1) : IScreenshotNumberStore
{
    public int Next { get; set; } = next;
}

/// <summary>A resolved save target.</summary>
/// <param name="Folder">The folder the file goes in (the dated subfolder when it could be made).</param>
/// <param name="FileName">The unique file name, extension included.</param>
/// <param name="ConsumedNumber">The "%#" number this save used; hand it to <see cref="ScreenshotNaming.RewindNumber"/> if the write fails.</param>
public readonly record struct ScreenshotSaveDestination(string Folder, string FileName, int? ConsumedNumber)
{
    public string FullPath => Path.Combine(Folder, FileName);
}

public static class ScreenshotNaming
{
    /// <summary>Upstream's default prefix before localisation.</summary>
    public const string DefaultPrefix = "Screenshot";

    /// <summary>The "start at" stepper in Settings runs over this range.</summary>
    public const int NumberStartMinimum = 0, NumberStartMaximum = 999_999;

    /// <summary>The unique-name walk gives up after this many variants.</summary>
    public const int UniqueNameLimit = 9999;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Stable local file name with a localisable prefix and colon-free time: "Screenshot 2025-07-14 at 09.41.05.png".</summary>
    public static string FileName(string prefix, DateTime date, string fileExtension = "png") =>
        $"{prefix} {date.ToString("yyyy-MM-dd 'at' HH.mm.ss", Invariant)}.{fileExtension}";

    /// <summary>
    /// Expands a date-token pattern into a relative subfolder path, "%y-%mo" becoming "24-03" and "%year/%month" becoming
    /// "2024/March". Slashes become nested folders (a Windows back slash counts as one too). The result never escapes the base
    /// folder: empty, "." and ".." components are dropped. An empty pattern means no subfolder. Characters Windows cannot keep
    /// in a folder name become dashes (a deviation: macOS only objects to "/" and ":").
    /// </summary>
    public static string ExpandSaveSubfolder(string pattern, DateTime date)
    {
        if (pattern.Length == 0) return "";
        return string.Join('/', ApplyDateTokens(pattern, date)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeComponent)
            .Where(part => part.Length > 0 && part != "." && part != ".."));
    }

    /// <summary>
    /// Expands a non-empty file name pattern with the date tokens of <see cref="ExpandSaveSubfolder"/> plus "%#" (and "%##", "%###", ...)
    /// for an auto-incrementing, zero-padded number. Slashes and colons would nest folders or fail the write, so they become dashes,
    /// and so does anything else Windows refuses in a file name. The caller falls back to the default name for a blank pattern
    /// and appends the extension.
    /// </summary>
    public static string ExpandFileNamePattern(string pattern, DateTime date, int number) =>
        SanitizeComponent(ApplyNumberTokens(ApplyDateTokens(pattern, date), number));

    /// <summary>Whether a file name pattern uses the number sequence, so callers know whether to advance and persist it.</summary>
    public static bool FileNamePatternUsesNumber(string pattern) => pattern.Contains("%#", StringComparison.Ordinal);

    /// <summary>Slash, colon and the rest of what a Windows file name cannot hold, each as a dash.</summary>
    private static string SanitizeComponent(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            builder.Append(ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || ch < ' ' ? '-' : ch);
        return builder.ToString();
    }

    private static string ApplyDateTokens(string pattern, DateTime date)
    {
        // Longer tokens go first so "%year"/"%month" are not clobbered by their short prefixes "%y"/"%mo".
        (string Token, string Value)[] tokens =
        [
            ("%year", date.Year.ToString("0000", Invariant)),
            ("%month", MonthName(date.Month)),
            ("%y", (date.Year % 100).ToString("00", Invariant)),
            ("%mo", date.Month.ToString("00", Invariant)),
            ("%d", date.Day.ToString("00", Invariant)),
            ("%h", date.Hour.ToString("00", Invariant)),
            ("%mi", date.Minute.ToString("00", Invariant)),
            ("%s", date.Second.ToString("00", Invariant)),
        ];
        var result = pattern;
        foreach (var (token, value) in tokens) result = result.Replace(token, value, StringComparison.Ordinal);
        return result;
    }

    /// <summary>"%#", "%##", "%###" become the number, zero-padded to the run's length minus the "%".</summary>
    private static string ApplyNumberTokens(string pattern, int number) =>
        Regex.Replace(pattern, "%#+", match =>
            number.ToString("D" + Math.Max(match.Length - 1, 1), Invariant));

    private static string MonthName(int month) =>
        new DateTime(2000, Math.Clamp(month, 1, 12), 1).ToString("MMMM", Invariant);

    /// <summary>First free variant of a file name: "name.png", "name 2.png", ... up to 9999.</summary>
    public static string UniqueFileName(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var dot = name.LastIndexOf('.');
        var hasExtension = dot > 0 && dot < name.Length - 1;
        var stem = hasExtension ? name[..dot] : name;
        var extension = hasExtension ? name[(dot + 1)..] : "";
        for (var index = 2; index <= UniqueNameLimit; index++)
        {
            var candidate = extension.Length == 0 ? $"{stem} {index}" : $"{stem} {index}.{extension}";
            if (!exists(candidate)) return candidate;
        }
        return name;
    }

    // MARK: - Number sequence

    /// <summary>
    /// The default dated name when no pattern is set, otherwise the pattern with its tokens expanded. A pattern that uses "%#"
    /// advances and persists the sequence, and the number it used comes back so a failed save can give it back.
    /// </summary>
    public static (string Name, int? ConsumedNumber) ResolveFileName(
        string prefix, string? pattern, DateTime date, string fileExtension, IScreenshotNumberStore store)
    {
        var trimmed = (pattern ?? "").Trim();
        if (trimmed.Length == 0) return (FileName(prefix, date, fileExtension), null);
        if (FileNamePatternUsesNumber(trimmed))
        {
            var number = store.Next;
            var expanded = ExpandFileNamePattern(trimmed, date, number);
            store.Next = number + 1;
            return ($"{expanded}.{fileExtension}", number);
        }
        return ($"{ExpandFileNamePattern(trimmed, date, 0)}.{fileExtension}", null);
    }

    /// <summary>
    /// Gives a consumed "%#" number back after its save failed or was deleted, but only while nothing else advanced the
    /// sequence since, so a rewind can never undo another capture's number.
    /// </summary>
    public static bool RewindNumber(IScreenshotNumberStore store, int consumed)
    {
        if (store.Next != consumed + 1) return false;
        store.Next = consumed;
        return true;
    }

    /// <summary>The Settings "reset" button and the start stepper: the next number becomes the chosen start.</summary>
    public static void ResetSequence(IScreenshotNumberStore store, int start) =>
        store.Next = Math.Clamp(start, NumberStartMinimum, NumberStartMaximum);

    // MARK: - Save location

    /// <summary>"~" and "~/x" read as the home folder; environment variables such as %USERPROFILE% expand too.</summary>
    public static string ExpandStoredFolder(string stored, string homeFolder)
    {
        var text = Environment.ExpandEnvironmentVariables(stored.Trim());
        if (text == "~") return homeFolder;
        if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(homeFolder, text[2..]);
        return text;
    }

    /// <summary>
    /// The configured folder when it still exists, otherwise the fallback (the Desktop, or the home folder when even that is
    /// unknown). An empty setting means the fallback.
    /// </summary>
    public static string ResolveBaseFolder(string? stored, Func<string, bool> directoryExists,
        string fallbackFolder, string homeFolder)
    {
        if (!string.IsNullOrWhiteSpace(stored))
        {
            var expanded = ExpandStoredFolder(stored, homeFolder);
            if (expanded.Length > 0 && directoryExists(expanded)) return expanded;
        }
        return string.IsNullOrEmpty(fallbackFolder) ? homeFolder : fallbackFolder;
    }

    /// <summary>
    /// The whole save decision: base folder, the dated subfolder when it can be created (otherwise the base folder, rather than
    /// losing the screenshot), and a unique dated or patterned file name.
    /// </summary>
    /// <param name="createDirectory">Makes a folder (and its parents); true when it now exists.</param>
    /// <param name="fileExists">Whether a full path is already taken.</param>
    public static ScreenshotSaveDestination ResolveDestination(
        string? storedFolder, string? subfolderPattern, string? fileNamePattern,
        string prefix, DateTime date, string fileExtension, IScreenshotNumberStore store,
        string fallbackFolder, string homeFolder,
        Func<string, bool> directoryExists, Func<string, bool> createDirectory, Func<string, bool> fileExists)
    {
        var folder = ResolveBaseFolder(storedFolder, directoryExists, fallbackFolder, homeFolder);
        var subfolder = ExpandSaveSubfolder(subfolderPattern ?? "", date);
        if (subfolder.Length > 0)
        {
            var dated = Path.Combine(folder, subfolder.Replace('/', Path.DirectorySeparatorChar));
            if (createDirectory(dated)) folder = dated;
        }
        var (name, consumed) = ResolveFileName(prefix, fileNamePattern, date, fileExtension, store);
        var unique = UniqueFileName(name, candidate => fileExists(Path.Combine(folder, candidate)));
        return new ScreenshotSaveDestination(folder, unique, consumed);
    }
}
