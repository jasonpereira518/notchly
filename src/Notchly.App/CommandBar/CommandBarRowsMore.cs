// SPDX-License-Identifier: GPL-3.0-or-later
// The rest of what the command bar offers (upstream CommandBarCatalog.swift): answers to what was typed, links the person saved,
// emoji, what is selected, what was copied, files, and menu items of the app the bar was opened over. The ones that read another app
// (selection, menus, files) are fed from the service off the UI thread; these builders only turn what they found into rows.
using System.Globalization;
using System.IO;
using Notchly.App.Pasteboard;
using Notchly.App.Platform;
using Notchly.App.Platform.Apps;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Windowing;
using Notchly.App.Tools;
using Notchly.Core.Clipboard;
using Notchly.Core.CommandBar;
using Notchly.Core.Features;
using Notchly.Core.Input;
using Notchly.Core.MenuBar;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.CommandBar;

internal sealed partial class CommandBarRows
{
    // MARK: Answers and typed addresses

    public CommandBarEntry? Answer(string query, CultureInfo culture)
    {
        if (CommandBarAnswers.TryAnswer(query, culture, DateTimeOffset.Now, TimeZoneInfo.Local) is not { } answer) return null;
        var (id, glyph) = answer.Kind switch
        {
            CommandBarAnswerKind.Math => ("math.result", ""),
            CommandBarAnswerKind.Units => ("units.result", ""),
            CommandBarAnswerKind.Date => ("date.result", ""),
            _ => (ColorValue.Parse(query) is not null ? "color.preview" : "color.result", ""),
        };
        var icon = answer.Color is { } color ? CommandBarIcon.Swatch(color) : CommandBarIcon.Glyph(glyph);
        var subtitle = answer.Detail.Length > 0 ? answer.Detail : CommandBarStrings.CopyHint;
        return new CommandBarEntry(id, id, answer.Title, subtitle, icon, _ =>
        {
            if (!ClipboardWrite.TrySetText(answer.CopyText)) QuickToolHud.Show(CommandBarStrings.CopyFailed);
            else QuickToolHud.Show(answer.CopyText);
        }) { IsAnswer = true, CountsUsage = false };
    }

    /// <summary>A web address typed into the bar is opened, not searched.</summary>
    public CommandBarEntry? OpenUrl(string query)
    {
        if (CommandBarLinks.TypedUrl(query) is not { } url) return null;
        var target = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
        return new CommandBarEntry("action.openURL", "action.openURL", "Open " + url, "Web address", CommandBarIcon.Glyph(""), _ => ShellActions.Open(target))
            { CountsUsage = false, IsAnswer = true };
    }

    // MARK: Links

    public IReadOnlyList<CommandBarEntry> Links()
    {
        var links = CommandBarLinks.Decode(Settings.GetString(DefaultsKey.CommandBarLinks));
        return links.Select(link =>
        {
            var id = "link." + link.Id.ToString("D");
            var glyph = link.Kind == CommandBarLinkKind.Place ? "" : "";
            return new CommandBarEntry(id, id, link.Name, CommandBarStrings.KindLink, CommandBarIcon.Glyph(glyph), _ => OpenLink(link))
            {
                TakesArgument = link.TakesArgument,
                RevealPath = CommandBarLinks.RevealPath(link),
                Shortcut = null,
            };
        }).ToList();
    }

    private void OpenLink(CommandBarLink link)
    {
        var argument = CommandBarLinks.ArgumentFor(link, host.Query.Trim()) ?? "";
        var clipboard = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
        var expanded = CommandBarLinks.Expand(link.Destination, link.Kind, argument, clipboard, host.SelectedText, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (link.Kind == CommandBarLinkKind.Place)
        {
            if (CommandBarLinks.PlacePath(link, expanded) is { } path) ShellActions.Open(path);
        }
        else if (CommandBarLinks.Url(link, expanded) is { } url) ShellActions.Open(url.AbsoluteUri);
    }

    // MARK: Emoji

    private IReadOnlyList<CommandBarEntry>? _emoji;
    private string _emojiTone = "?";

    public IReadOnlyList<CommandBarEntry> Emoji()
    {
        var toneRaw = Settings.GetString(DefaultsKey.CommandBarEmojiSkinTone);
        if (_emoji is not null && toneRaw == _emojiTone) return _emoji;
        var tone = CommandBarEmoji.TryParseTone(toneRaw) ?? CommandBarEmoji.SkinTone.None;
        _emojiTone = toneRaw;
        _emoji = CommandBarEmoji.All.Select(item =>
        {
            var glyph = CommandBarEmoji.AcceptsSkinTone(item.Glyph) ? CommandBarEmoji.Applying(tone, item.Glyph) : item.Glyph;
            var id = CommandBarPreferences.EmojiRowId(item.Identity);
            return new CommandBarEntry(id, id, glyph + "  " + item.Name, CommandBarStrings.KindEmoji, CommandBarIcon.Glyph(""), n => _ = host.TypeAtCaretAsync(glyph))
                { MatchTitle = item.Name, Keywords = item.Keywords + " emoji", KeepsBarOpen = false };
        }).ToList();
        return _emoji;
    }

    // MARK: What is selected

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public IReadOnlyList<CommandBarEntry> Selection(string? text, out string preview)
    {
        preview = "";
        if (string.IsNullOrWhiteSpace(text)) return [];
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        preview = flat.Length > 36 ? flat[..36] + "…" : flat;
        var rows = new List<CommandBarEntry>();
        CommandBarEntry Row(string id, string title, string glyph, Action<int?> run) =>
            new("selection." + id, "selection." + id, title, CommandBarStrings.KindSelection, CommandBarIcon.Glyph(glyph), run) { CountsUsage = false };
        rows.Add(Row("copy", CommandBarStrings.SelectionCopy, "", _ => ClipboardWrite.TrySetText(text)));
        rows.Add(Row("search", CommandBarStrings.SelectionSearch, "", _ => host.PutInSearch(flat)) with { KeepsBarOpen = true });
        if (UrlCleaning.Clean(text.Trim()) is { } cleaned && cleaned.Url != text.Trim())
            rows.Add(Row("cleanLink", "Clean the link", "", n => _ = host.TypeAtCaretAsync(cleaned.Url)));
        if (text.Length <= 5000)
        {
            void Case(string id, string title, string changed)
            {
                if (changed != text) rows.Add(Row(id, title, "", n => _ = host.TypeAtCaretAsync(changed)));
            }
            Case("upper", CommandBarStrings.SelectionUpper, text.ToUpperInvariant());
            Case("lower", CommandBarStrings.SelectionLower, text.ToLowerInvariant());
            Case("titleCase", CommandBarStrings.SelectionTitleCase, CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()));
        }
        if (Settings.IsAvailable(AppFeature.Shelf))
            rows.Add(Row("shelf", CommandBarStrings.SelectionShelf, "", _ => AddToShelf(text)));
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        rows.Add(Row("count", CommandBarStrings.Format(CommandBarStrings.SelectionCountFormat, words, text.Length), "", _ => { }) with { KeepsBarOpen = true });
        return rows;
    }

    private void AddToShelf(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "Notchly shelf text " + DateTime.Now.ToString("yyyyMMdd-HHmmss", Invariant) + ".txt");
        File.WriteAllText(path, text);
        App.Shelf.AddFiles([path]);
    }

    // MARK: What was copied

    private ClipboardHistoryService? History => Settings.IsAvailable(AppFeature.ClipboardHistory) ? App.Features.Get<ClipboardHistoryService>(AppFeature.ClipboardHistory) : null;

    private CommandBarEntry ClipboardRow(ClipboardEntry entry, ClipboardHistoryService history)
    {
        var id = "clipboard." + entry.Id.ToString("D");
        var title = entry.Kind switch
        {
            ClipboardKind.Image => "Image" + (entry.ImageWidth is { } w && entry.ImageHeight is { } h ? $" {w} × {h}" : ""),
            ClipboardKind.Files => string.Join(", ", entry.FilePaths.Select(Path.GetFileName)),
            _ => ClipboardHistorySupport.RowPreview(entry.Text),
        };
        return new CommandBarEntry(id, id, title, CommandBarStrings.KindClipboard, CommandBarIcon.Glyph(""), n => _ = host.PasteAsync(history, entry))
            { CountsUsage = false, Keywords = entry.Kind == ClipboardKind.Text ? "" : entry.Kind.ToString() };
    }

    public IReadOnlyList<CommandBarEntry> ClipboardBrowse(int limit) =>
        History is { } history ? history.Entries.Take(limit).Select(e => ClipboardRow(e, history)).ToList() : [];

    public IReadOnlyList<CommandBarEntry> ClipboardMatching(string query) =>
        History is { } history ? ClipboardHistorySupport.Search(history.Entries, query).Take(4).Select(e => ClipboardRow(e, history)).ToList() : [];

    // MARK: Files

    private string _fileQuery = "";
    private IReadOnlyList<CommandBarEntry> _fileRows = [];
    private CancellationTokenSource? _fileSearch;

    /// <summary>
    /// Files found by name in the folders the person named. Asked for rather than waited on: the answer lands a moment later and asks
    /// the list to be worked out again. Until then the rows of an earlier answer for the same letters, if any, stay.
    /// </summary>
    public IReadOnlyList<CommandBarEntry> FilesFor(string query, Action refresh)
    {
        var scopes = FileScopes();
        if (!CommandBarPreferences.IsEnabled(CommandBarSource.Files, CommandBarPreferences.DisabledSources(Settings.GetString(DefaultsKey.CommandBarDisabledSources)))
            || scopes.Count == 0)
        {
            _fileSearch?.Cancel();
            return [];
        }
        if (query == _fileQuery) return _fileRows;
        if (CommandBarFileSearchSupport.Sql(query, scopes) is not { } sql) { _fileSearch?.Cancel(); return []; }
        _fileSearch?.Cancel();
        var source = _fileSearch = new CancellationTokenSource();
        var patterns = CommandBarFileSearchSupport.AllIgnores(Settings.GetString(DefaultsKey.CommandBarFileIgnores));
        _ = Task.Run(async () =>
        {
            await Task.Delay(120, source.Token).ConfigureAwait(false);
            var result = await WindowsSearchClient.QueryAsync(sql, source.Token).ConfigureAwait(false);
            if (source.IsCancellationRequested) return;
            var paths = CommandBarFileSearchSupport.Offerable(result.Paths.Where(p => CommandBarFileSearchSupport.MatchesAllWords(p, query)), patterns, IsHidden);
            var rows = CommandBarFileSearchSupport.Sorted(paths).Take(CommandBarFileSearchSupport.RowsPerSearch).Select(FileRow).ToList();
            _fileQuery = query;
            _fileRows = rows;
            if (CommandBarFileSearchSupport.ShouldPublishResult(query, host.Query.Trim())) refresh();
        }, source.Token).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled);
        return [];
    }

    private IReadOnlyList<string> FileScopes()
    {
        var saved = CommandBarFileSearchSupport.DecodeList(Settings.GetString(DefaultsKey.CommandBarFileScopes));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] children = [];
        try { children = Directory.Exists(home) ? Directory.EnumerateDirectories(home).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return CommandBarFileSearchSupport.ResolvedScopes(saved, home, children, Directory.Exists);
    }

    private static bool IsHidden(string path)
    {
        try { return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    private static CommandBarEntry FileRow(string path)
    {
        var id = "file." + path;
        var folder = Path.GetDirectoryName(path) ?? "";
        return new CommandBarEntry(id, id, Path.GetFileName(path), CommandBarFileSearchSupport.Abbreviating(folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                CommandBarIcon.File(path), _ => ShellActions.Open(path))
            { RevealPath = path, CountsUsage = false };
    }

    public void ForgetFiles()
    {
        _fileSearch?.Cancel();
        _fileQuery = "";
        _fileRows = [];
    }

    // MARK: Menu commands of the app the bar was opened over

    /// <summary>Enabled leaf items of a classic menu, to a depth of four and 400 items; the Window and Help menus are skipped.</summary>
    public IReadOnlyList<CommandBarEntry> MenuRows(IntPtr window, ForegroundApp app)
    {
        var rows = new List<CommandBarEntry>();
        try
        {
            if (AppMenus.NativeTitles(window) is not { } titles) return rows;
            foreach (var title in titles)
            {
                if (title.Title.Replace("&", "").Trim() is "Window" or "Help") continue;
                var crumbs = new List<string> { title.Title.Replace("&", "") };
                Walk(AppMenus.NativeItems(window, title.Index), crumbs, 1, rows, app, title.Index);
            }
        }
        catch (Exception e) { Log.Debug("Menu items could not be read: {Message}", e.Message); }
        return rows;
    }

    private static void Walk(IReadOnlyList<MenuNode> nodes, List<string> crumbs, int depth, List<CommandBarEntry> rows, ForegroundApp app, int index)
    {
        foreach (var node in nodes)
        {
            if (rows.Count >= 400) return;
            if (node.Kind == MenuNodeKind.Separator || !node.Enabled) continue;
            var name = node.Title.Replace("&", "");
            if (node.Kind == MenuNodeKind.Submenu)
            {
                if (depth < 4) { crumbs.Add(name); Walk(node.Children, crumbs, depth + 1, rows, app, index); crumbs.RemoveAt(crumbs.Count - 1); }
                continue;
            }
            if (node.Command is not { } command || string.IsNullOrWhiteSpace(name)) continue;
            var id = $"menu.{index}.{string.Join('>', crumbs)}>{name}";
            rows.Add(new CommandBarEntry(id, id, name, CommandBarStrings.KindMenu + " · " + string.Join(" › ", crumbs), CommandBarIcon.Glyph(""),
                n => _ = MenuCommands.RunAsync(command, app)) { CountsUsage = false, MenuShortcut = node.Shortcut });
        }
    }
}
