// SPDX-License-Identifier: GPL-3.0-or-later
// Tests of the engine that turns a query into rows (ported from the behaviour of upstream CommandBarService.searchRows and
// suggestionRows, which upstream pins through its service; here the pure part is exercised directly).
using Notchly.Core.CommandBar;

namespace Notchly.Core.Tests;

public class CommandBarEngineTests
{
    private const double Now = 1_785_240_000;

    private static CommandBarEntry Row(string id, string title, string subtitle = "", string keywords = "", string? stableKey = null) =>
        new(id, stableKey ?? id, title, subtitle, CommandBarIcon.Glyph("x"), _ => { }) { Keywords = keywords };

    private static CommandBarLearningContext Learning(Dictionary<string, CommandBarUse>? usage = null) =>
        new() { Usage = usage ?? [], HabitKey = new byte[32] };

    private static List<CommandBarEntry> Search(string query, IReadOnlyList<CommandBarEntry> pool, CommandBarPreferenceSnapshot? prefs = null,
                                                CommandBarEntry? answer = null, CommandBarLearningContext? learning = null) =>
        CommandBarEngine.Search(query, pool, answer, null, prefs ?? CommandBarPreferenceSnapshot.Empty, learning ?? Learning(), Now);

    [Fact]
    public void ATitlePrefixLeadsAContainsMatch()
    {
        var rows = Search("scr", [Row("action.a", "Transcribe"), Row("action.b", "Scratchpad"), Row("action.c", "Camera screen")]);
        Assert.Equal("action.b", rows[0].Id);
    }

    [Fact]
    public void EachKindIsCapped()
    {
        var pool = Enumerable.Range(1, 9).Select(i => Row($"app.{i}", $"Notes {i}")).ToList();
        var rows = Search("notes", pool);
        Assert.Equal(5, rows.Count(r => r.Id.StartsWith("app.")));
    }

    [Fact]
    public void TheListStopsAtTwelve()
    {
        var pool = Enumerable.Range(1, 30).Select(i => Row($"action.{i}", $"Toggle {i}")).ToList();
        Assert.Equal(12, Search("toggle", pool).Count);
    }

    [Fact]
    public void AnAnswerLeadsTheRankedRows()
    {
        var answer = Row("math.result", "14");
        var rows = Search("2+3*4", [Row("action.x", "Nothing like it")], answer: answer);
        Assert.Equal("math.result", rows[0].Id);
    }

    [Fact]
    public void AColorPreviewIsPlacedAfterTheRowsThatMatch()
    {
        var preview = Row("color.preview", "#00BC7D");
        var rows = Search("#00bc7d", [Row("action.a", "Color picker")], answer: preview);
        Assert.Contains(rows, r => r.Id == "color.preview");
    }

    [Fact]
    public void ASourceSwitchedOffContributesNothingButActionsAlwaysStay()
    {
        var disabled = new HashSet<CommandBarSource> { CommandBarSource.Apps, CommandBarSource.Actions };
        var prefs = CommandBarPreferenceSnapshot.Empty with { Disabled = disabled };
        var rows = Search("notes", [Row("app.1", "Notes"), Row("action.notes", "Open notes")], prefs);
        Assert.DoesNotContain(rows, r => r.Id.StartsWith("app."));
        Assert.Contains(rows, r => r.Id == "action.notes");
    }

    [Fact]
    public void AHiddenRowNeverShows()
    {
        var prefs = CommandBarPreferenceSnapshot.Empty with { Hidden = new HashSet<string> { "app.1" } };
        Assert.DoesNotContain(Search("notes", [Row("app.1", "Notes")], prefs), r => r.Id == "app.1");
    }

    [Fact]
    public void AGivenNameOutranksEveryTitle()
    {
        var prefs = CommandBarPreferenceSnapshot.Empty with { Aliases = new Dictionary<string, string> { ["app.codex"] = "chat" } };
        var rows = Search("chat", [Row("action.chat", "Chat window"), Row("app.codex", "Codex", stableKey: "app.codex")], prefs);
        Assert.Equal("app.codex", rows[0].Id);
    }

    [Fact]
    public void APinBreaksATieAndNeverJumpsABetterMatch()
    {
        var tied = new[] { Row("action.a", "Open files"), Row("action.b", "Open filer") };
        var prefs = CommandBarPreferenceSnapshot.Empty with { PinOrder = ["action.b"] };
        Assert.Equal("action.b", Search("open file", tied, prefs)[0].Id);
        var better = new[] { Row("action.a", "File"), Row("action.b", "Open the file now") };
        Assert.Equal("action.a", Search("file", better, CommandBarPreferenceSnapshot.Empty with { PinOrder = ["action.b"] })[0].Id);
    }

    [Fact]
    public void WhatIsUsedOftenRisesAmongEqualMatches()
    {
        var usage = new Dictionary<string, CommandBarUse> { ["action.b"] = new(12, Now - 60) };
        var rows = Search("tool", [Row("action.a", "Toolbox"), Row("action.b", "Tooling")], learning: Learning(usage));
        Assert.Equal("action.b", rows[0].Id);
    }

    [Fact]
    public void ASystemAnswerNeedsItsNameNotAShortPrefix()
    {
        var battery = Row("answer.battery", "Battery");
        Assert.DoesNotContain(Search("ba", [battery]), r => r.Id == "answer.battery");
        Assert.Contains(Search("batt", [battery]), r => r.Id == "answer.battery");
    }

    [Fact]
    public void ARowThatTakesAnArgumentStaysWhileTheArgumentIsTyped()
    {
        var link = Row("link.1", "Bing") with { TakesArgument = true };
        Assert.Contains(Search("bing weather in oslo", [link]), r => r.Id == "link.1");
    }

    [Fact]
    public void ATrailingNumberIsSplitOffOnlyForACommandThatTakesOne()
    {
        var volume = Row("action.volume", "Volume") with { NumericRange = (0, 100) };
        var rows = CommandBarEngine.Search("volume 40", [volume], null, null, CommandBarPreferenceSnapshot.Empty, Learning(), Now,
                                           text => text == "volume");
        Assert.Equal("action.volume", rows[0].Id);
        Assert.Empty(CommandBarEngine.Search("volume 40", [volume], null, null, CommandBarPreferenceSnapshot.Empty, Learning(), Now, _ => false));
    }

    [Fact]
    public void InsideACategoryThereIsNoCapAndFortyRowsAtMost()
    {
        var pool = Enumerable.Range(1, 80).Select(i => Row($"app.{i}", $"Tool {i}")).ToList();
        Assert.Equal(40, CommandBarEngine.SearchCategory("tool", pool, CommandBarPreferenceSnapshot.Empty, Learning(), Now).Count);
    }

    [Fact]
    public void EmojiColonShowsTheFirstFortyAndASearchRanksTheRest()
    {
        var pool = Enumerable.Range(1, 60).Select(i => Row($"emoji.{i}", $"Grin {i}") with { MatchTitle = $"grin {i}" }).ToList();
        Assert.Equal(40, CommandBarEngine.SearchEmoji("", pool, Learning(), Now).Count);
        Assert.Equal("emoji.7", CommandBarEngine.SearchEmoji("grin 7", pool, Learning(), Now)[0].Id);
    }

    // MARK: The empty bar

    [Fact]
    public void TheEmptyBarOrdersSelectionPinsSuggestionsGroupsThenCopies()
    {
        var catalog = new[]
        {
            Row("action.screenOCR", "Copy text from screen", "Tools"), Row("action.keepAwake", "Keep awake", "Energy"),
            Row("action.scratchpad", "Scratchpad", "Tools"), Row("link.1", "Bing", "Your shortcut"), Row("action.mic", "Mute microphone", "Sound"),
        };
        var selection = new[] { Row("selection.copy", "Copy it") };
        var copied = new[] { Row("clipboard.1", "hello") };
        var prefs = CommandBarPreferenceSnapshot.Empty with { PinOrder = ["action.scratchpad"] };
        var browse = CommandBarEngine.Browse(selection, "hello", catalog, catalog, copied, prefs, Learning(), Now);
        Assert.Equal("selection.copy", browse.Rows[0].Id);
        Assert.Equal("Selected · hello", browse.Titles[0]);
        Assert.Equal("action.scratchpad", browse.Rows[1].Id);
        Assert.Equal("Pinned", browse.Titles[1]);
        Assert.Equal("Suggestions", browse.Titles[2]);
        Assert.Equal("clipboard.1", browse.Rows[^1].Id);
        Assert.Equal(CommandBarStrings.KindClipboard, browse.Titles[browse.Rows.Count - 1]);
        Assert.Contains("Sound", browse.Titles.Values);
        Assert.Contains(CommandBarStrings.KindLink, browse.Titles.Values);
    }

    [Fact]
    public void SuggestionsAreAtMostSevenMinusThePins()
    {
        var catalog = Enumerable.Range(1, 20).Select(i => Row($"action.{i}", $"Thing {i}", "Tools")).ToList();
        var usage = catalog.ToDictionary(r => r.Id, _ => new CommandBarUse(5, Now - 10));
        var prefs = CommandBarPreferenceSnapshot.Empty with { PinOrder = ["action.1", "action.2"] };
        var browse = CommandBarEngine.Browse([], "", catalog, catalog, [], prefs, Learning(usage), Now);
        var start = browse.Titles.First(t => t.Value == "Suggestions").Key;
        var end = browse.Titles.Keys.Where(k => k > start).Min();
        Assert.Equal(5, end - start);
    }

    [Fact]
    public void AGroupShowsAtMostTwelveRowsAndAHiddenOrDisabledRowIsLeftOut()
    {
        var catalog = Enumerable.Range(1, 30).Select(i => Row($"action.{i}", $"Thing {i}", "Tools")).ToList();
        catalog.Add(Row("app.1", "An app", "Apps"));
        var prefs = CommandBarPreferenceSnapshot.Empty with { Hidden = new HashSet<string> { "action.30" }, Disabled = new HashSet<CommandBarSource> { CommandBarSource.Apps } };
        var browse = CommandBarEngine.Browse([], "", catalog, catalog, [], prefs, Learning(), Now);
        var tools = browse.Titles.First(t => t.Value == "Tools").Key;
        var next = browse.Titles.Keys.Where(k => k > tools).DefaultIfEmpty(browse.Rows.Count).Min();
        Assert.True(next - tools <= CommandBarEngine.BrowseGroupLimit);
        Assert.DoesNotContain(browse.Rows, r => r.Id is "action.30" or "app.1");
    }

    [Fact]
    public void ARowRepeatingItsHeadingSaysNothingUnderIt()
    {
        var catalog = new[] { Row("action.a", "One", "Tools") };
        var browse = CommandBarEngine.Browse([], "", catalog, catalog, [], CommandBarPreferenceSnapshot.Empty, Learning(), Now);
        Assert.All(browse.Rows.Where(r => r.Id == "action.a"), r => Assert.True(r.Subtitle is "" or "Tools"));
    }

    [Fact]
    public void TheDefaultShortcutIsAltSpaceAndItParses()
    {
        var stored = (string)Notchly.Core.Settings.Defaults.Registered.First(p => p.Key == Notchly.Core.Settings.DefaultsKey.CommandBarShortcut).Value!;
        var shortcut = Notchly.Core.Shortcuts.Shortcut.Parse(stored);
        Assert.NotNull(shortcut);
        Assert.True(shortcut!.Value.IsValid);
        Assert.Equal(0x20, shortcut.Value.VirtualKey);
        Assert.Equal(Notchly.Core.Shortcuts.ShortcutModifiers.Alt, shortcut.Value.Modifiers);
    }
}
