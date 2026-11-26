// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream ScratchpadMarkTests, the pure and file contracts of ScratchpadStoreContractTests, and the scratchpad block of
// ScreenshotFeatureTests (about lines 3931-4120).
using System.Text;
using System.Text.Json;
using Notchly.Core.Tools;

namespace Notchly.Core.Tests;

public class ScratchpadTests
{
    private static ScratchpadRange R(int start, int length) => new(start, length);

    private static (string Text, ScratchpadRange Selection) Applying(ScratchpadMark mark, string text, ScratchpadRange selection)
    {
        var edit = ScratchpadSupport.Edit(mark, text, selection);
        return (edit.ApplyTo(text), edit.Selection);
    }

    /// <summary>
    /// Clicks a mark repeatedly, each time from the selection the click before it left behind, which is what a person actually does.
    /// </summary>
    private static string Clicking(ScratchpadMark mark, int times, string text, ScratchpadRange selection)
    {
        string current = text;
        var range = selection;
        for (int i = 0; i < times; i++)
        {
            var edit = ScratchpadSupport.Edit(mark, current, range);
            current = edit.ApplyTo(current);
            range = edit.Selection;
        }
        return current;
    }

    // ---- ScratchpadMarkTests ------------------------------------------------------------------------------------------

    // Two clicks put the text back where it started. Heading is the one exception by design, since it walks its levels on the way out.
    [Theory]
    [InlineData(ScratchpadMark.Bold)]
    [InlineData(ScratchpadMark.Italic)]
    [InlineData(ScratchpadMark.Strikethrough)]
    [InlineData(ScratchpadMark.Code)]
    [InlineData(ScratchpadMark.Link)]
    [InlineData(ScratchpadMark.Bullet)]
    [InlineData(ScratchpadMark.Quote)]
    [InlineData(ScratchpadMark.Numbered)]
    public void TwiceLeavesTheTextAlone(ScratchpadMark mark) =>
        Assert.Equal("note", Clicking(mark, 2, "note", R(0, 4)));

    [Fact]
    public void HeadingComesBackOffAfterItsLevels() =>
        Assert.Equal("note", Clicking(ScratchpadMark.Heading, 4, "note", R(0, 4)));

    [Fact]
    public void ItalicInsideBoldTogglesWithoutTouchingTheBold()
    {
        Assert.Equal("**note**", Clicking(ScratchpadMark.Italic, 2, "**note**", R(2, 4)));
        Assert.Equal("**note**", Clicking(ScratchpadMark.Italic, 4, "**note**", R(2, 4)));
        Assert.Equal("*note*", Clicking(ScratchpadMark.Bold, 2, "*note*", R(1, 4)));
    }

    [Fact]
    public void BoldWrapsTheSelectionAndLeavesTheWordsSelected()
    {
        var wrapped = Applying(ScratchpadMark.Bold, "note", R(0, 4));
        Assert.Equal("**note**", wrapped.Text);
        Assert.Equal(R(2, 4), wrapped.Selection);
    }

    [Fact]
    public void ASecondClickTakesBoldOff() =>
        Assert.Equal("note", Applying(ScratchpadMark.Bold, "**note**", R(2, 4)).Text);

    [Theory]
    [InlineData(0, 19)]
    [InlineData(2, 15)]
    public void AMixedSelectionJoinsBoldSpansWithoutBreakingDelimiters(int start, int length)
    {
        var joined = Applying(ScratchpadMark.Bold, "**one** and **two**", R(start, length));
        Assert.Equal("**one and two**", joined.Text);
        Assert.Equal(R(2, 11), joined.Selection);
        Assert.Equal("one and two", Applying(ScratchpadMark.Bold, joined.Text, joined.Selection).Text);
    }

    [Fact]
    public void SeparateStrikethroughAndCodeSpansJoin()
    {
        Assert.Equal("~~one and two~~", Applying(ScratchpadMark.Strikethrough, "~~one~~ and ~~two~~", R(0, 19)).Text);
        Assert.Equal("`one and two`", Applying(ScratchpadMark.Code, "`one` and `two`", R(0, 15)).Text);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 13)]
    public void SeparateItalicSpansJoin(int start, int length) =>
        Assert.Equal("*one and two*", Applying(ScratchpadMark.Italic, "*one* and *two*", R(start, length)).Text);

    [Fact]
    public void AmbiguousOrEscapedDelimitersAreLeftIntact()
    {
        const string nestedItalicSpans = "***one*** and ***two***";
        Assert.Equal(nestedItalicSpans, Applying(ScratchpadMark.Italic, nestedItalicSpans, R(0, nestedItalicSpans.Length)).Text);
        const string escapedBold = "**a \\** b**";
        Assert.Equal(escapedBold, Applying(ScratchpadMark.Bold, escapedBold, R(0, escapedBold.Length)).Text);
        const string boldWithCode = "**one `**` and `**` two**";
        Assert.Equal(boldWithCode, Applying(ScratchpadMark.Bold, boldWithCode, R(0, boldWithCode.Length)).Text);
        const string longCode = "``a`b``";
        Assert.Equal(longCode, Applying(ScratchpadMark.Code, longCode, R(0, longCode.Length)).Text);
    }

    [Fact]
    public void MarkersInsideTheSelectionComeOff() =>
        Assert.Equal("gone", Applying(ScratchpadMark.Strikethrough, "~~gone~~", R(0, 8)).Text);

    [Fact]
    public void WithNoSelectionTheCaretLandsBetweenTheMarkers()
    {
        var caret = Applying(ScratchpadMark.Bold, "", R(0, 0));
        Assert.Equal("****", caret.Text);
        Assert.Equal(2, caret.Selection.Start);
    }

    // Italic's marker is also half of bold's, and a single character cannot tell the two apart: what does is whether the run of stars is odd.
    [Fact]
    public void ItalicNestsInsideBoldAndComesBackOff()
    {
        Assert.Equal("***note***", Applying(ScratchpadMark.Italic, "**note**", R(2, 4)).Text);
        Assert.Equal("**note**", Applying(ScratchpadMark.Italic, "***note***", R(3, 4)).Text);
        Assert.Equal("**note**", Applying(ScratchpadMark.Italic, "***note***", R(0, 10)).Text);
        Assert.Equal("*note*", Applying(ScratchpadMark.Italic, "note", R(0, 4)).Text);
        Assert.Equal("note", Applying(ScratchpadMark.Italic, "*note*", R(1, 4)).Text);
        // Bold over an italic word keeps the italic star it is wrapped around.
        Assert.Equal("***note***", Applying(ScratchpadMark.Bold, "*note*", R(0, 6)).Text);
    }

    [Fact]
    public void HeadingPrefixesTheLineAndStepsDownItsLevels()
    {
        Assert.Equal("# ", Applying(ScratchpadMark.Heading, "", R(0, 0)).Text);

        var heading = Applying(ScratchpadMark.Heading, "Title", R(2, 0));
        Assert.Equal("# Title", heading.Text);
        Assert.Equal(4, heading.Selection.Start);

        // Every size the preview draws has to be reachable from the one button.
        Assert.Equal("## Title", Applying(ScratchpadMark.Heading, "# Title", R(4, 0)).Text);
        Assert.Equal("### Title", Applying(ScratchpadMark.Heading, "## Title", R(4, 0)).Text);
        Assert.Equal("Title", Applying(ScratchpadMark.Heading, "### Title", R(4, 0)).Text);

        // Swapping one line mark for another replaces it rather than stacking.
        Assert.Equal("# item", Applying(ScratchpadMark.Heading, "- item", R(3, 0)).Text);
    }

    [Fact]
    public void BulletsAndBlankLines()
    {
        Assert.Equal("- one\n- two", Applying(ScratchpadMark.Bullet, "one\ntwo", R(1, 5)).Text);
        Assert.Equal("one\ntwo", Applying(ScratchpadMark.Bullet, "- one\n- two", R(2, 7)).Text);
        Assert.Equal("- one\n\n- two", Applying(ScratchpadMark.Bullet, "one\n\ntwo", R(0, 8)).Text);
    }

    // Selections arrive in UTF-16 units from the text view. Reading them as characters would put the markers inside the emoji.
    [Fact]
    public void MarksLandPastAnEmoji() =>
        Assert.Equal("\U0001F642**ab**", Applying(ScratchpadMark.Bold, "\U0001F642ab", R(2, 2)).Text);

    [Fact]
    public void QuoteAndCode()
    {
        Assert.Equal("> said", Applying(ScratchpadMark.Quote, "said", R(0, 0)).Text);
        Assert.Equal("`ls -la`", Applying(ScratchpadMark.Code, "ls -la", R(0, 6)).Text);
    }

    // Numbering is written out, so the list reads correctly as plain text and not only once the preview has renumbered it.
    [Fact]
    public void NumberedLinesTakeTheirOwnNumber()
    {
        Assert.Equal("1. one\n2. two\n3. three", Applying(ScratchpadMark.Numbered, "one\ntwo\nthree", R(0, 13)).Text);
        Assert.Equal("one\ntwo", Applying(ScratchpadMark.Numbered, "1. one\n2. two", R(0, 13)).Text);
        Assert.Equal("one\ntwo", Applying(ScratchpadMark.Numbered, "9. one\n10. two", R(0, 14)).Text);
        // A list that is already a bullet list becomes a numbered one rather than collecting both marks.
        Assert.Equal("1. one\n2. two", Applying(ScratchpadMark.Numbered, "- one\n- two", R(0, 11)).Text);
    }

    [Fact]
    public void LinkWrapsTheWordsAndLeavesTheAddressSelected()
    {
        var link = Applying(ScratchpadMark.Link, "docs", R(0, 4));
        Assert.Equal("[docs](url)", link.Text);
        Assert.Equal(R(7, 3), link.Selection);
    }

    // The address is what the first click leaves selected, so that is the state the second click has to recognise and undo.
    [Fact]
    public void ASecondClickTakesTheLinkOff()
    {
        var unlinked = Applying(ScratchpadMark.Link, "[docs](url)", R(7, 3));
        Assert.Equal("docs", unlinked.Text);
        Assert.Equal(R(0, 4), unlinked.Selection);
        Assert.Equal("see docs now", Applying(ScratchpadMark.Link, "see [docs](https://x.dev) now", R(5, 4)).Text);
        Assert.Equal("docs", Applying(ScratchpadMark.Link, "[docs](url)", R(0, 11)).Text);
    }

    [Fact]
    public void LinkAddressesWithParenthesesAndImages()
    {
        Assert.Equal("docs", Applying(ScratchpadMark.Link, "[docs](https://example.test/a_(b_(c)))", R(1, 4)).Text);
        Assert.Equal("docs", Applying(ScratchpadMark.Link, "[docs](https://example.test/a\\)b)", R(1, 4)).Text);
        const string image = "![alt](url)";
        Assert.Equal(image, Applying(ScratchpadMark.Link, image, R(2, 3)).Text);
    }

    // A caret resting against a link's edge is about to write a new one.
    [Fact]
    public void ACaretBesideALinkWritesANewOne() =>
        Assert.Equal("[docs](url)[](url)", Applying(ScratchpadMark.Link, "[docs](url)", R(11, 0)).Text);

    [Fact]
    public void FocusedShortcutsFindAndStepThroughMatches()
    {
        Assert.Equal(ScratchpadShortcutAction.Find, ScratchpadFocusedShortcut.Action("f", ctrl: true));
        Assert.Equal(ScratchpadShortcutAction.Find, ScratchpadFocusedShortcut.Action("F", ctrl: true));
        Assert.Null(ScratchpadFocusedShortcut.Action("f", ctrl: false));
        Assert.Equal(ScratchpadShortcutAction.FindNext, ScratchpadFocusedShortcut.Action("g", ctrl: true));
        Assert.Equal(ScratchpadShortcutAction.FindPrevious, ScratchpadFocusedShortcut.Action("G", ctrl: true, shift: true));
        // Shift only belongs to G, so Shift-Ctrl-T and W stay the text's.
        Assert.Null(ScratchpadFocusedShortcut.Action("t", ctrl: true, shift: true));
        Assert.Null(ScratchpadFocusedShortcut.Action("w", ctrl: true, shift: true));
        Assert.Null(ScratchpadFocusedShortcut.Action("f", ctrl: true, shift: true));
    }

    [Fact]
    public void TheTextSizeIsClampedAndDefaultsWhenCorrupt()
    {
        Assert.Equal(12, ScratchpadSupport.SanitizedTextSize(12));
        Assert.Equal(ScratchpadSupport.TextSizeMin, ScratchpadSupport.SanitizedTextSize(2));
        Assert.Equal(ScratchpadSupport.TextSizeMax, ScratchpadSupport.SanitizedTextSize(400));
        Assert.Equal(ScratchpadSupport.DefaultTextSize, ScratchpadSupport.SanitizedTextSize(double.NaN));
        Assert.InRange(ScratchpadSupport.DefaultTextSize, ScratchpadSupport.TextSizeMin, ScratchpadSupport.TextSizeMax);
        Assert.Equal(13, ScratchpadSupport.DefaultTextSize);
        Assert.Equal(13, ScratchpadSupport.SanitizedTextSize(12.5));
    }

    // A range that outlived the text it was measured against is clamped rather than trusted.
    [Fact]
    public void AnOutOfDateSelectionCannotCrash() =>
        Assert.Equal("hi****", Applying(ScratchpadMark.Bold, "hi", R(40, 9)).Text);

    // Select All over a heading, then take it off: the selection shrinks with the text rather than running on into the line below.
    [Fact]
    public void ARemovedPrefixDoesNotStretchTheSelection()
    {
        var unheaded = Applying(ScratchpadMark.Heading, "### a\nnext", R(0, 5));
        Assert.Equal("a\nnext", unheaded.Text);
        Assert.Equal(R(0, 1), unheaded.Selection);
    }

    // Indentation nests a list, so a mark sits after it and comes back off.
    [Fact]
    public void ANestedBulletComesOffAndGoesBackOn()
    {
        Assert.Equal("- a\n  b", Clicking(ScratchpadMark.Bullet, 1, "- a\n  - b", R(6, 0)));
        Assert.Equal("- a\n  - b", Clicking(ScratchpadMark.Bullet, 2, "- a\n  - b", R(6, 0)));
    }

    // Markdown does not close a mark against a space, so the space a drag picks up after a word stays outside the markers.
    [Fact]
    public void ATrailingSpaceStaysOutsideTheMarkers() =>
        Assert.Equal("**hello** world", Applying(ScratchpadMark.Bold, "hello world", R(0, 6)).Text);

    [Fact]
    public void LineMarksKeepWindowsLineEndings()
    {
        Assert.Equal("- one\r\n- two", Applying(ScratchpadMark.Bullet, "one\r\ntwo", R(0, 8)).Text);
        Assert.Equal("one\r\ntwo\r\n", Applying(ScratchpadMark.Bullet, "- one\r\n- two\r\n", R(0, 14)).Text);
        Assert.Equal("- two", Applying(ScratchpadMark.Bullet, "one\r\ntwo", R(5, 0)).Text.Split("\r\n")[1]);
    }

    // ---- Task lists ---------------------------------------------------------------------------------------------------

    private static List<(bool Checked, int Box, string Text)> ShownTasks(string text) =>
        ScratchpadSupport.MarkdownPreview(text)
            .Where(b => b.Kind.Type == ScratchpadBlockType.TaskListItem)
            .Select(b => (b.Kind.Checked, b.Kind.Box, b.Text))
            .ToList();

    /// <summary>Clicks the nth checkbox the reading shows.</summary>
    private static string? ClickingTask(int n, string text)
    {
        var shown = ShownTasks(text);
        if (n >= shown.Count) return null;
        return ScratchpadSupport.TaskToggleEdit(shown[n].Box, text)?.ApplyTo(text);
    }

    private const string TaskSource =
        "- [ ] first\n- [x] done\n- plain\n  - [] nested\n\n```\n- [ ] in code\n```\n> - [ ] quoted\n1. [ ] ordered";

    [Fact]
    public void TaskItemsReadAsCheckboxesAtTheirBoxInTheText()
    {
        var tasks = ShownTasks(TaskSource);
        Assert.Equal([false, true, false, false], tasks.Select(t => t.Checked));
        Assert.Equal([2, 14, 35, 72], tasks.Select(t => t.Box));
        Assert.Equal(["first", "done", "nested", "quoted"], tasks.Select(t => t.Text));
    }

    [Fact]
    public void AListItemWithoutACheckboxStaysABullet() =>
        Assert.Contains(ScratchpadSupport.MarkdownPreview(TaskSource),
            b => b.Kind == ScratchpadBlockKind.UnorderedListItem(1) && b.Text == "plain");

    // A list inside a quote is still the quote's, and its tasks are still tasks.
    [Fact]
    public void ATaskOrBulletInsideAQuoteKeepsTheQuotesDepth()
    {
        var inQuote = ScratchpadSupport.MarkdownPreview("> Reminder\n> - [ ] Bring the key\n> - spare\n> > - [x] deep\n\n- [ ] home");
        Assert.Equal([false, true, false, true, true], inQuote.Select(b => b.Kind.Type == ScratchpadBlockType.TaskListItem));
        Assert.Equal([0, 1, 1, 2, 0], inQuote.Select(b => b.QuoteDepth));
        Assert.Equal(ScratchpadBlockKind.Quote(1), inQuote[0].Kind);
        Assert.Equal(ScratchpadBlockKind.UnorderedListItem(1), inQuote[2].Kind);
    }

    [Fact]
    public void AQuoteInsideAListItemDoesNotPutTheItemInAQuote() =>
        Assert.Equal([0, 0], ScratchpadSupport.MarkdownPreview("- note\n\n  > aside").Select(b => b.QuoteDepth));

    [Fact]
    public void AClickChecksOrUnchecksOnlyItsOwnBox()
    {
        Assert.StartsWith("- [x] first\n- [x] done", ClickingTask(0, TaskSource));
        Assert.Contains("- [ ] done", ClickingTask(1, TaskSource));
        Assert.Contains("  - [x] nested", ClickingTask(2, TaskSource));
        Assert.Contains("> - [x] quoted", ClickingTask(3, TaskSource));
        Assert.Contains("- [ ] in code", ClickingTask(3, TaskSource));
    }

    // The parser, not a count of lines, decides what is a task: a tab after a heading makes code, and a line indented under a paragraph
    // or under a task joins it. A click still edits its own line.
    [Fact]
    public void AClickEditsTheBoxItWasDrawnFromWhenAnIndentedTaskReadsAsCodeOrAParagraph()
    {
        Assert.Equal("# Today\n\t- [ ] call bob\n- [x] email ann", ClickingTask(0, "# Today\n\t- [ ] call bob\n- [ ] email ann"));
        Assert.Equal("Shopping\n    - [ ] eggs\n- [x] milk", ClickingTask(0, "Shopping\n    - [ ] eggs\n- [ ] milk"));
        Assert.Equal("- [x] milk\n        - [ ] organic\n- [ ] bread", ClickingTask(0, "- [ ] milk\n        - [ ] organic\n- [ ] bread"));
        Assert.Equal("- [ ] milk\n        - [ ] organic\n- [x] bread", ClickingTask(1, "- [ ] milk\n        - [ ] organic\n- [ ] bread"));
    }

    [Fact]
    public void ABoxOpeningALaterParagraphOfAListItemIsATaskOfItsOwn()
    {
        Assert.Equal(["follow up"], ShownTasks("- note\n\n  [ ] follow up").Select(t => t.Text));
        Assert.Equal("- note\n\n  [x] follow up", ClickingTask(0, "- note\n\n  [ ] follow up"));
    }

    [Fact]
    public void BoxOffsetsCountTheTextAsTheEditorDoesPastAccentsAndEmoji()
    {
        const string wide = "Café ☕️ \U0001F469‍\U0001F469‍\U0001F467 é\n- [ ] a\n- [x] b";
        Assert.Equal(wide.Replace("- [ ] a", "- [x] a"), ClickingTask(0, wide));
        Assert.Equal(wide.Replace("- [x] b", "- [ ] b"), ClickingTask(1, wide));
    }

    [Fact]
    public void ABoxAfterAWindowsLineEndingIsFoundWhereItIs() =>
        Assert.Equal("- [ ] a\r\n- [x] b", ClickingTask(1, "- [ ] a\r\n- [ ] b"));

    // Lines are counted here, so a lazy continuation line or a NUL cannot slip a task onto its twin.
    [Fact]
    public void ABoxAfterANulOrALazyContinuationLineIsFoundOnItsOwnLine()
    {
        const string nul = "- [ ] one\nx\u0000\n- [ ] two\n- [ ] three";
        const string twins = "pasted\u0000bytes\n- [ ] buy milk\n- [ ] buy milk\n- [x] call mom";
        const string lazy = "- [ ] buy milk\nnote\n- [ ] buy milk\n- [ ] buy milk";
        Assert.Equal([2, 15, 25], ShownTasks(nul).Select(t => t.Box));
        Assert.Equal([15, 30, 45], ShownTasks(twins).Select(t => t.Box));
        Assert.Equal([2, 22, 37], ShownTasks(lazy).Select(t => t.Box));
        Assert.Equal("pasted\u0000bytes\n- [ ] buy milk\n- [x] buy milk\n- [x] call mom", ClickingTask(1, twins));
        Assert.Equal("- [ ] buy milk\nnote\n- [x] buy milk\n- [ ] buy milk", ClickingTask(1, lazy));
    }

    [Fact]
    public void EveryTaskAfterAContinuationLineKeepsItsBox()
    {
        Assert.Equal([2, 15, 23, 31, 39], ShownTasks("- [ ] a\nlazy\n- [ ] b\n- [ ] c\n- [ ] d\n- [ ] e").Select(t => t.Box));
        Assert.Equal([2, 33, 43], ShownTasks("- [ ] groceries\n milk and eggs\n- [ ] gym\n- [ ] call mom").Select(t => t.Box));
        Assert.Equal([4, 19, 29], ShownTasks("> - [ ] a\nlazy\n> - [ ] b\n> - [ ] b").Select(t => t.Box));
        Assert.Equal([2, 12], ShownTasks("- [ ] a\rb\r- [ ] c").Select(t => t.Box));
    }

    [Fact]
    public void ATaskWithALineBreakAnEntityAnEscapeOrABareLinkKeepsItsBox() =>
        Assert.Equal([2, 16, 37],
            ShownTasks("- [ ] a  \n  b\n- [ ] c &amp; d \\* e\n- [ ] f https://example.com").Select(t => t.Box));

    [Fact]
    public void AnEscapedEncodedCodeOrEmphasizedBracketIsTextNotABox() =>
        Assert.Empty(ShownTasks("- \\[ ] escaped\n- &#91; ] encoded\n- `[ ]` code\n- *[ ]* emphasis"));

    [Fact]
    public void ABoxOffsetThatNoLongerHoldsABoxChangesNothing()
    {
        Assert.Null(ScratchpadSupport.TaskToggleEdit(0, "- [ ] a"));
        Assert.Null(ScratchpadSupport.TaskToggleEdit(2, "- [ a"));
        Assert.Null(ScratchpadSupport.TaskToggleEdit(2, "- ( ] a"));
        Assert.Null(ScratchpadSupport.TaskToggleEdit(2, "- ["));
        Assert.Null(ScratchpadSupport.TaskToggleEdit(40, "- [ ] a"));
    }

    [Fact]
    public void TogglingTwiceRestoresTheText()
    {
        string toggled = ClickingTask(0, "- [ ] a\n- [ ] b") ?? "";
        Assert.Equal("- [ ] a\n- [ ] b", ClickingTask(0, toggled));
    }

    [Fact]
    public void ABareBoxChecksThenUnchecksToAnEmptyBox()
    {
        string bare = ClickingTask(0, "- [] a") ?? "";
        Assert.Equal("- [x] a", bare);
        Assert.Equal("- [ ] a", ClickingTask(0, bare));
    }

    [Fact]
    public void AClosingBracketTypedAfterAListItemsOpenerClosesAnEmptyBox()
    {
        Assert.True(ScratchpadSupport.CompletesEmptyTaskBox("- ["));
        Assert.True(ScratchpadSupport.CompletesEmptyTaskBox("  * ["));
        Assert.True(ScratchpadSupport.CompletesEmptyTaskBox("> - ["));
        Assert.True(ScratchpadSupport.CompletesEmptyTaskBox("+  ["));
    }

    [Fact]
    public void AnOpeningBracketAnywhereElseIsLeftAlone()
    {
        Assert.False(ScratchpadSupport.CompletesEmptyTaskBox("["));
        Assert.False(ScratchpadSupport.CompletesEmptyTaskBox("see ["));
        Assert.False(ScratchpadSupport.CompletesEmptyTaskBox("- text ["));
        Assert.False(ScratchpadSupport.CompletesEmptyTaskBox("- [ "));
        Assert.False(ScratchpadSupport.CompletesEmptyTaskBox("- "));
    }

    // ---- Shift-Return ------------------------------------------------------------------------------------------------------

    private static string LineBreak(string text, int? caretAt = null) =>
        ScratchpadSupport.LineBreakEdit(text, R(caretAt ?? text.Length, 0)).ApplyTo(text);

    [Fact]
    public void ShiftReturnAfterATaskOpensAFreshUncheckedTaskAtTheSameDepth()
    {
        Assert.Equal("- [ ] buy milk\n- [ ] ", LineBreak("- [ ] buy milk"));
        Assert.Equal("- [x] done\n- [ ] ", LineBreak("- [x] done"));
        Assert.Equal("  * [] nested\n  * [ ] ", LineBreak("  * [] nested"));
    }

    [Fact]
    public void ShiftReturnAfterABulletOpensTheSameBullet()
    {
        Assert.Equal("- item\n- ", LineBreak("- item"));
        Assert.Equal("+ item\n+ ", LineBreak("+ item"));
        Assert.Equal("> - quoted\n> - ", LineBreak("> - quoted"));
    }

    [Fact]
    public void ShiftReturnAfterANumberedItemOpensTheNextNumber()
    {
        Assert.Equal("1. one\n2. ", LineBreak("1. one"));
        Assert.Equal("9) nine\n10) ", LineBreak("9) nine"));
    }

    // Upstream's largest number is Int.max (Swift's 64-bit Int); here a long, the same bound.
    [Fact]
    public void ShiftReturnAfterTheLargestNumberRepeatsItInsteadOfOverflowing() =>
        Assert.Equal("9223372036854775807. x\n9223372036854775807. ", LineBreak("9223372036854775807. x"));

    [Fact]
    public void ShiftReturnOnAnEmptyItemEndsTheListAndTakesTheMarkerAway()
    {
        Assert.Equal("", LineBreak("- [ ] "));
        Assert.Equal("a\n\nb", LineBreak("a\n- \nb", 4));
        Assert.Equal("", LineBreak("1. "));
    }

    [Fact]
    public void ShiftReturnRightAfterABoxWithNothingPastItEndsTheListToo()
    {
        Assert.Equal("", LineBreak("- [ ]"));
        Assert.Equal("", LineBreak("- [x]"));
        Assert.Equal("a\n", LineBreak("a\n> - [ ]"));
    }

    [Fact]
    public void ShiftReturnOutsideAListIsAPlainLineAndInsideOneSplitsTheItem()
    {
        Assert.Equal("plain text\n", LineBreak("plain text"));
        Assert.Equal("- [ ] \n- [ ] split here", LineBreak("- [ ] split here", 6));
        Assert.Equal("-\n item", LineBreak("- item", 1));
    }

    [Fact]
    public void ShiftReturnFollowsTheLineEndingsTheTextAlreadyUses()
    {
        Assert.Equal("a\r\n- b\r\n- ", LineBreak("a\r\n- b"));
        Assert.Equal("- b\n- ", ScratchpadSupport.LineBreakEdit("- b", R(3, 0), "\n").ApplyTo("- b"));
    }

    // ---- The preview's blocks -------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePreviewRendersSemanticBlocksAndInlineFormatting()
    {
        var preview = ScratchpadSupport.MarkdownPreview(
            "# Heading\n\n**Bold** and *italic* with [link](https://example.com)\n\n- First\n- Second\n\n1. Third\n\n```\ncode\n```");
        Assert.Equal(
            [
                ScratchpadBlockKind.Heading(1), ScratchpadBlockKind.Paragraph,
                ScratchpadBlockKind.UnorderedListItem(1), ScratchpadBlockKind.UnorderedListItem(1),
                ScratchpadBlockKind.OrderedListItem(1, 1), ScratchpadBlockKind.Code,
            ],
            preview.Select(b => b.Kind));
        Assert.Equal("Heading", preview[0].Text);
        Assert.Equal("First", preview[2].Text);
        Assert.Equal("code", preview[5].Text);
        Assert.Equal(preview[2].ContainerId, preview[3].ContainerId);
        Assert.NotNull(preview[2].ContainerId);
        Assert.NotEqual(preview[3].ContainerId, preview[4].ContainerId);
        Assert.Contains(preview[1].Runs, r => r.Bold);
        Assert.Contains(preview[1].Runs, r => r.Italic);
        Assert.Contains(preview[1].Runs, r => r.Link == "https://example.com");
    }

    [Fact]
    public void AnEmptyPadHasNoBlocksAndBlankTextOneParagraph()
    {
        Assert.Empty(ScratchpadSupport.MarkdownPreview(""));
        var blank = ScratchpadSupport.MarkdownPreview("\n\n");
        Assert.Single(blank);
        Assert.Equal(ScratchpadBlockKind.Paragraph, blank[0].Kind);
    }

    [Fact]
    public void SetextHeadingsRulesAndOrderedListNumbers()
    {
        var blocks = ScratchpadSupport.MarkdownPreview("Title\n=====\n\nSub\n---\n\n***\n\n3. c\n4. d");
        Assert.Equal(
            [
                ScratchpadBlockKind.Heading(1), ScratchpadBlockKind.Heading(2), ScratchpadBlockKind.ThematicBreak,
                ScratchpadBlockKind.OrderedListItem(1, 1), ScratchpadBlockKind.OrderedListItem(2, 1),
            ],
            blocks.Select(b => b.Kind));
    }

    // ---- Support: retention, sizes, names, document ------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_784_000_000);

    [Fact]
    public void RetentionSanitizesToTheAllowedPeriodsAndFallsBackToNever()
    {
        Assert.Equal(ScratchpadRetention.Day, ScratchpadRetentions.Sanitized("day"));
        Assert.Equal(ScratchpadRetention.Week, ScratchpadRetentions.Sanitized("week"));
        Assert.Equal(ScratchpadRetention.Month, ScratchpadRetentions.Sanitized("month"));
        Assert.Equal(ScratchpadRetention.Never, ScratchpadRetentions.Sanitized(null));
        Assert.Equal(ScratchpadRetention.Never, ScratchpadRetentions.Sanitized("yesterday"));
        Assert.Equal(ScratchpadRetention.Never, ScratchpadRetentions.Sanitized("never"));
    }

    [Fact]
    public void RetentionPeriodsAreADayAWeekAndThirtyDays()
    {
        Assert.Null(ScratchpadRetentions.MaxIdleInterval(ScratchpadRetention.Never));
        Assert.Equal(TimeSpan.FromSeconds(86_400), ScratchpadRetentions.MaxIdleInterval(ScratchpadRetention.Day));
        Assert.Equal(TimeSpan.FromSeconds(7 * 86_400), ScratchpadRetentions.MaxIdleInterval(ScratchpadRetention.Week));
        Assert.Equal(TimeSpan.FromSeconds(30 * 86_400), ScratchpadRetentions.MaxIdleInterval(ScratchpadRetention.Month));
        Assert.Equal(["never", "day", "week", "month"],
            Enum.GetValues<ScratchpadRetention>().Select(ScratchpadRetentions.RawValue));
    }

    [Fact]
    public void ThePinAndTheExportDialogBothBlockOutsideClickDismissal()
    {
        Assert.True(ScratchpadSupport.DismissesOnOutsideClick(isPinned: false, exportModalActive: false));
        Assert.False(ScratchpadSupport.DismissesOnOutsideClick(isPinned: true, exportModalActive: false));
        Assert.False(ScratchpadSupport.DismissesOnOutsideClick(isPinned: false, exportModalActive: true));
    }

    [Fact]
    public void TheBackgroundOpacityIsClampedAndABrokenOneIsFullyOpaque()
    {
        Assert.Equal(0.7, ScratchpadSupport.SanitizedBackgroundOpacity(0.7));
        Assert.Equal(ScratchpadSupport.BackgroundOpacityMin, ScratchpadSupport.SanitizedBackgroundOpacity(0));
        Assert.Equal(ScratchpadSupport.BackgroundOpacityMin, ScratchpadSupport.SanitizedBackgroundOpacity(-3));
        Assert.Equal(1.0, ScratchpadSupport.SanitizedBackgroundOpacity(4));
        Assert.Equal(1.0, ScratchpadSupport.SanitizedBackgroundOpacity(double.NaN));
        Assert.Equal(1.0, ScratchpadSupport.SanitizedBackgroundOpacity(double.PositiveInfinity));
    }

    [Fact]
    public void TheScratchpadOnlyClearsWhenAPeriodIsChosenAndTheLastEditIsOlderThanIt()
    {
        Assert.False(ScratchpadSupport.ShouldClear(null, Now, ScratchpadRetention.Day));
        Assert.False(ScratchpadSupport.ShouldClear(Now.AddSeconds(-90_000), Now, ScratchpadRetention.Never));
        Assert.False(ScratchpadSupport.ShouldClear(Now.AddSeconds(-3_600), Now, ScratchpadRetention.Day));
        Assert.True(ScratchpadSupport.ShouldClear(Now.AddSeconds(-90_000), Now, ScratchpadRetention.Day));
        Assert.False(ScratchpadSupport.ShouldClear(Now.AddSeconds(-90_000), Now, ScratchpadRetention.Week));
        Assert.True(ScratchpadSupport.ShouldClear(Now.AddSeconds(-8 * 86_400), Now, ScratchpadRetention.Week));
        // A clock that moved backwards never clears.
        Assert.False(ScratchpadSupport.ShouldClear(Now.AddSeconds(60), Now, ScratchpadRetention.Day));
    }

    [Fact]
    public void TheExportFileNameIsTheTitlePlusTheLocalDate()
    {
        string name = ScratchpadSupport.ExportFileName("Scratchpad", Now);
        Assert.StartsWith("Scratchpad 20", name);
        Assert.EndsWith(".txt", name);
        Assert.Equal("Scratchpad ".Length + 14, name.Length);
        Assert.Equal($"Scratchpad {Now.ToLocalTime():yyyy-MM-dd}.txt", name);
    }

    [Fact]
    public void ExportNamesCannotTurnTabNamesIntoPathComponents()
    {
        string name = ScratchpadSupport.ExportFileName("Work/Ideas: 1", Now);
        Assert.StartsWith("Work-Ideas- 1 ", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
    }

    private static readonly Guid FirstPadId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondPadId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ThirdPadId = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static ScratchpadDocument Migrated() =>
        ScratchpadDocument.Initial("Scratchpad", FirstPadId, "existing text", Now.AddSeconds(-120));

    private static ScratchpadDocument ThreePads() =>
        Migrated().AddingPad("Scratchpad", SecondPadId)!.AddingPad("Scratchpad", ThirdPadId)!;

    [Fact]
    public void NewScratchpadsAppendInOrderReceiveClearNamesAndBecomeSelected()
    {
        var threePads = ThreePads();
        Assert.Equal(["Scratchpad 1", "Scratchpad 2", "Scratchpad 3"], threePads.Pads.Select(p => p.Name));
        Assert.Equal([FirstPadId, SecondPadId, ThirdPadId], threePads.Pads.Select(p => p.Id));
        Assert.Equal(ThirdPadId, threePads.SelectedId);
    }

    [Fact]
    public void AnExistingUnnumberedScratchpadStillOccupiesTheFirstNumberedSlot() =>
        Assert.Equal("Scratchpad 2", ScratchpadSupport.NextPadName("Scratchpad", ["Scratchpad"]));

    [Fact]
    public void ScratchpadNamesStaySingleLineBoundedAndNeverEmpty()
    {
        var renamed = ThreePads().Renaming(SecondPadId, "  Work\nideas  ");
        Assert.Equal("Work ideas", renamed!.Pads[1].Name);
        Assert.Equal(ScratchpadDocument.MaximumNameLength, ScratchpadSupport.SanitizedPadName(new string('x', 50)).Length);
        Assert.Null(ThreePads().Renaming(SecondPadId, "   "));
        Assert.Equal("Scratchpad 1", ScratchpadSupport.NextPadName("   ", []));
    }

    [Fact]
    public void ClosingTheSelectedScratchpadKeepsOrderAndSelectsItsNearestNeighbor()
    {
        var selectedFirst = ThreePads().Selecting(FirstPadId);
        var removedFirst = selectedFirst!.Removing(FirstPadId);
        Assert.Equal([SecondPadId, ThirdPadId], removedFirst!.Pads.Select(p => p.Id));
        Assert.Equal(SecondPadId, removedFirst.SelectedId);

        // The last tab selects the new last one; a pad that is not selected leaves the selection alone.
        Assert.Equal(SecondPadId, ThreePads().Removing(ThirdPadId)!.SelectedId);
        Assert.Equal(ThirdPadId, ThreePads().Removing(FirstPadId)!.SelectedId);
        Assert.Null(ThreePads().Removing(Guid.NewGuid()));
        Assert.Null(ThreePads().Selecting(Guid.NewGuid()));
    }

    [Fact]
    public void TheLastScratchpadCannotBeClosed() =>
        Assert.Null(Migrated().Removing(FirstPadId));

    [Fact]
    public void OnlyClosingAScratchpadWithContentNeedsConfirmation()
    {
        Assert.True(ScratchpadSupport.RequiresCloseConfirmation(Migrated().Pads[0]));
        Assert.False(ScratchpadSupport.RequiresCloseConfirmation(ScratchpadDocument.Initial("Scratchpad").Pads[0]));
    }

    [Fact]
    public void TheTabShortcutsNeedCtrlAloneOnTOrW()
    {
        Assert.Equal(ScratchpadShortcutAction.CreatePad,
            ScratchpadFocusedShortcut.Action("t", ctrl: true, canCreatePad: true, canClosePad: true));
        Assert.Null(ScratchpadFocusedShortcut.Action("t", ctrl: true, canCreatePad: false, canClosePad: true));
        Assert.Equal(ScratchpadShortcutAction.CloseSelectedPad,
            ScratchpadFocusedShortcut.Action("w", ctrl: true, canCreatePad: true, canClosePad: true));
        Assert.Equal(ScratchpadShortcutAction.HidePad,
            ScratchpadFocusedShortcut.Action("w", ctrl: true, canCreatePad: true, canClosePad: false));
        Assert.Null(ScratchpadFocusedShortcut.Action("t", ctrl: false, canCreatePad: true, canClosePad: true));
        Assert.Null(ScratchpadFocusedShortcut.Action("w", ctrl: false, canCreatePad: true, canClosePad: true));
        Assert.Null(ScratchpadFocusedShortcut.Action("a", ctrl: true, canCreatePad: true, canClosePad: true));
        // Caps Lock keeps the close shortcut.
        Assert.Equal(ScratchpadShortcutAction.CloseSelectedPad,
            ScratchpadFocusedShortcut.Action("W", ctrl: true, canCreatePad: true, canClosePad: true));
        // The AZERTY Z at the US W position must not close a scratchpad.
        Assert.Null(ScratchpadFocusedShortcut.Action("z", ctrl: true, canCreatePad: true, canClosePad: true));
        // Events without a character do not trigger the shortcuts.
        Assert.Null(ScratchpadFocusedShortcut.Action(null, ctrl: true, canCreatePad: true, canClosePad: true));
    }

    [Fact]
    public void ScratchpadsKeepASmallFixedUpperBound()
    {
        var limited = Migrated();
        for (int i = 2; i <= ScratchpadDocument.MaximumPadCount; i++)
            limited = limited.AddingPad("Scratchpad")!;
        Assert.Equal(ScratchpadDocument.MaximumPadCount, limited.Pads.Count);
        Assert.Equal(12, limited.Pads.Count);
        Assert.Null(limited.AddingPad("Scratchpad"));
        Assert.Equal("Scratchpad 12", limited.Pads[^1].Name);
    }

    [Fact]
    public void RetentionClearsOnlyScratchpadsWhoseOwnTextExpired()
    {
        var retained = ScratchpadDocument.Initial("Scratchpad", FirstPadId, "expired text", Now.AddSeconds(-90_000));
        retained = retained.AddingPad("Scratchpad", SecondPadId)!;
        retained = retained.UpdatingSelectedText("recent text", Now.AddSeconds(-300));
        retained = retained.ApplyingRetention(ScratchpadRetention.Day, Now);
        Assert.Equal("", retained.Pads[0].Text);
        Assert.Null(retained.Pads[0].ModifiedAt);
        Assert.Equal("recent text", retained.Pads[1].Text);
    }

    [Fact]
    public void TextNamesOrderAndSelectionRoundTripTogether()
    {
        var renamed = ThreePads().Renaming(SecondPadId, "  Work\nideas  ")!;
        var decoded = ScratchpadDocument.Decoded(renamed.Encoded(), "Scratchpad");
        Assert.Equal(renamed, decoded);
        Assert.Null(ScratchpadDocument.Decoded(null, "Scratchpad"));
        Assert.Null(ScratchpadDocument.Decoded(Encoding.UTF8.GetBytes("{broken"), "Scratchpad"));
    }

    [Fact]
    public void TheEncodingMatchesUpstreamsShape()
    {
        var document = ScratchpadDocument.Initial("Scratchpad", FirstPadId, "café", DateTimeOffset.UnixEpoch.AddSeconds(978_307_200 + 10));
        document = document.AddingPad("Scratchpad", SecondPadId)!;
        using var json = JsonDocument.Parse(document.Encoded());
        var root = json.RootElement;
        Assert.Equal("00000000-0000-0000-0000-000000000002", root.GetProperty("selectedID").GetString());
        var pads = root.GetProperty("pads");
        Assert.Equal("00000000-0000-0000-0000-000000000001", pads[0].GetProperty("id").GetString());
        Assert.Equal("Scratchpad 1", pads[0].GetProperty("name").GetString());
        Assert.Equal("café", pads[0].GetProperty("text").GetString());
        // Swift's default Date coding: seconds since 2001-01-01.
        Assert.Equal(10.0, pads[0].GetProperty("modifiedAt").GetDouble());
        Assert.False(pads[1].TryGetProperty("modifiedAt", out _));
    }

    [Fact]
    public void DecodingIsLenientAboutDatesAndSanitisesTheResult()
    {
        const string json = """
            {"pads":[
              {"id":"AAAAAAAA-0000-0000-0000-000000000001","name":"  one\ntwo ","text":"hi","modifiedAt":"2026-07-17T10:00:00Z"},
              {"id":"AAAAAAAA-0000-0000-0000-000000000001","name":"dup","text":"dup"},
              {"id":"AAAAAAAA-0000-0000-0000-000000000002","name":"","text":"","modifiedAt":5},
              {"id":"AAAAAAAA-0000-0000-0000-000000000003","name":"x","text":"y","modifiedAt":null}
            ],"selectedID":"AAAAAAAA-0000-0000-0000-0000000000FF"}
            """;
        var document = ScratchpadDocument.Decoded(Encoding.UTF8.GetBytes(json), "Scratchpad")!;
        Assert.Equal(3, document.Pads.Count);
        Assert.Equal("one two", document.Pads[0].Name);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero), document.Pads[0].ModifiedAt);
        Assert.Equal("Scratchpad 1", document.Pads[1].Name);   // an empty name takes the next free one
        Assert.Null(document.Pads[1].ModifiedAt);             // no text, no date
        Assert.Null(document.Pads[2].ModifiedAt);
        Assert.Equal(document.Pads[0].Id, document.SelectedId);   // an unknown selection falls back to the first pad
        Assert.Null(ScratchpadDocument.Decoded(Encoding.UTF8.GetBytes("{}"), "Scratchpad"));
        Assert.Null(ScratchpadDocument.Decoded([], "Scratchpad"));
    }

    [Fact]
    public void ADocumentWithNoPadsBecomesOneEmptyPad()
    {
        var document = ScratchpadDocument.Decoded(Encoding.UTF8.GetBytes("""{"pads":[],"selectedID":"AAAAAAAA-0000-0000-0000-000000000001"}"""), "Scratchpad")!;
        Assert.Single(document.Pads);
        Assert.Equal("", document.Pads[0].Text);
        Assert.Equal("Scratchpad 1", document.Pads[0].Name);
    }

    [Fact]
    public void UpdatingTheSelectedTextDatesItAndAnEmptyTextClearsTheDate()
    {
        var document = ScratchpadDocument.Initial("Scratchpad", FirstPadId);
        var typed = document.UpdatingSelectedText("note", Now);
        Assert.Equal("note", typed.SelectedPad!.Text);
        Assert.Equal(Now, typed.SelectedPad.ModifiedAt);
        Assert.Same(typed, typed.UpdatingSelectedText("note", Now.AddHours(1)));
        Assert.Null(typed.UpdatingSelectedText("", Now).SelectedPad!.ModifiedAt);
    }

    // ---- ScratchpadStoreContractTests: the pure and file contracts ----------------------------------------------------------

    private static ScratchpadDocument Original() =>
        ScratchpadDocument.Initial("Scratchpad", text: "Keep these notes", modifiedAt: Now.AddSeconds(-90_000));

    private static void WithDirectory(Action<string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "notchly-scratchpad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { check(directory); }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void ACannotSaveBeforeItsFirstSuccessfulReadAndANewPadStartsEmpty() =>
        WithDirectory(directory =>
        {
            var store = new ScratchpadStore(directory);
            var empty = ScratchpadDocument.Initial("Scratchpad");
            Assert.False(store.Save(empty));
            var loaded = store.Load("Scratchpad", ScratchpadRetention.Never, Now);
            Assert.Single(loaded.Pads);
            Assert.Equal("", loaded.Pads[0].Text);
            var original = Original();
            Assert.True(store.Save(original));
            Assert.Equal(original, store.Load("Scratchpad", ScratchpadRetention.Never, Now));
            Assert.True(File.Exists(Path.Combine(directory, "Scratchpad.json")));
            Assert.Equal(original, ScratchpadDocument.Decoded(File.ReadAllBytes(Path.Combine(directory, "Scratchpad.json")), "Scratchpad"));
        });

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("{}")]
    public void ADamagedStoredFileFailsWithoutApplyingRetentionAndBlocksSaving(string damagedText) =>
        WithDirectory(directory =>
        {
            var store = new ScratchpadStore(directory);
            var original = Original();
            var empty = ScratchpadDocument.Initial("Scratchpad");
            string path = Path.Combine(directory, "Scratchpad.json");
            string legacyPath = Path.Combine(directory, "Scratchpad.txt");
            byte[] damaged = Encoding.UTF8.GetBytes(damagedText);
            File.WriteAllBytes(path, damaged);
            File.WriteAllText(legacyPath, "Older notes");
            Assert.ThrowsAny<Exception>(() => store.Load("Scratchpad", ScratchpadRetention.Day, Now));
            Assert.False(store.CanSave);
            Assert.False(store.Save(empty));
            Assert.False(store.Save(original));
            Assert.Equal(damaged, File.ReadAllBytes(path));
            Assert.Equal("Older notes", File.ReadAllText(legacyPath));

            File.WriteAllBytes(path, original.Encoded());
            var retried = store.Load("Scratchpad", ScratchpadRetention.Never, Now);
            Assert.Equal(original, retried);
            Assert.True(store.Save(original));
        });

    [Fact]
    public void AReadFailureAfterASuccessfulOpeningIsNotTreatedAsAMissingFileAndRevokesSaving() =>
        WithDirectory(directory =>
        {
            var store = new ScratchpadStore(directory);
            var original = Original();
            string path = Path.Combine(directory, "Scratchpad.json");
            byte[] originalData = original.Encoded();
            File.WriteAllBytes(path, originalData);
            store.Load("Scratchpad", ScratchpadRetention.Never, Now);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => store.Load("Scratchpad", ScratchpadRetention.Day, Now));
                Assert.False(store.Save(ScratchpadDocument.Initial("Scratchpad")));
                Assert.False(store.Save(original));
            }
            Assert.Equal(originalData, File.ReadAllBytes(path));
        });

    [Fact]
    public void AnUnreadableOrInvalidLegacyTextBlocksSavingAndPreservesItsBytes() =>
        WithDirectory(directory =>
        {
            string legacyPath = Path.Combine(directory, "Scratchpad.txt");
            // Invalid UTF-8.
            byte[] invalid = [0xff, 0xfe, 0xff];
            File.WriteAllBytes(legacyPath, invalid);
            var store = new ScratchpadStore(directory);
            Assert.ThrowsAny<Exception>(() => store.Load("Scratchpad", ScratchpadRetention.Day, Now));
            Assert.False(store.Save(ScratchpadDocument.Initial("Scratchpad")));
            Assert.Equal(invalid, File.ReadAllBytes(legacyPath));
            Assert.False(File.Exists(Path.Combine(directory, "Scratchpad.json")));

            // Unreadable (locked).
            byte[] valid = Encoding.UTF8.GetBytes("Keep these notes");
            File.WriteAllBytes(legacyPath, valid);
            using (new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => store.Load("Scratchpad", ScratchpadRetention.Day, Now));
                Assert.False(store.Save(ScratchpadDocument.Initial("Scratchpad")));
            }
            Assert.Equal(valid, File.ReadAllBytes(legacyPath));
            Assert.False(File.Exists(Path.Combine(directory, "Scratchpad.json")));
        });

    [Fact]
    public void ValidLegacyTextIsRemovedOnlyAfterItsReplacementIsVerified() =>
        WithDirectory(directory =>
        {
            string legacyPath = Path.Combine(directory, "Scratchpad.txt");
            File.WriteAllText(legacyPath, "Older notes");
            var store = new ScratchpadStore(directory);
            var migrated = store.Load("Scratchpad", ScratchpadRetention.Never, Now);
            var saved = File.ReadAllBytes(Path.Combine(directory, "Scratchpad.json"));
            Assert.Equal("Older notes", migrated.Pads[0].Text);
            Assert.NotNull(migrated.Pads[0].ModifiedAt);
            Assert.Equal(migrated, ScratchpadDocument.Decoded(saved, "Scratchpad"));
            Assert.False(File.Exists(legacyPath));
        });

    [Fact]
    public void ABlockedWriteNeverCountsAsASavedDocumentAndKeepsTheLegacyCopy() =>
        WithDirectory(directory =>
        {
            // The folder cannot be created: a file sits where it should be.
            string blocker = Path.Combine(directory, "blocker");
            File.WriteAllText(blocker, "x");
            var store = new ScratchpadStore(Path.Combine(blocker, "pad"));
            var loaded = store.Load("Scratchpad", ScratchpadRetention.Never, Now);
            Assert.Null(store.LastSavedDocument);
            Assert.True(store.CanSave);
            Assert.False(store.Save(loaded));
            Assert.Equal([Path.Combine(directory, "blocker")], Directory.GetFiles(directory));
        });

    [Fact]
    public void RetentionStillClearsExpiredNotesAfterASuccessfulRead() =>
        WithDirectory(directory =>
        {
            string path = Path.Combine(directory, "Scratchpad.json");
            File.WriteAllBytes(path, Original().Encoded());
            var store = new ScratchpadStore(directory);
            var loaded = store.Load("Scratchpad", ScratchpadRetention.Day, Now);
            Assert.Equal("", loaded.Pads[0].Text);
            Assert.Equal(loaded, ScratchpadDocument.Decoded(File.ReadAllBytes(path), "Scratchpad"));
        });

    [Fact]
    public void AnUnavailableFolderNeverDiscardsStoredNotes()
    {
        var unavailable = new ScratchpadStore(null);
        Assert.ThrowsAny<IOException>(() => unavailable.Load("Scratchpad", ScratchpadRetention.Never, Now));
        Assert.False(unavailable.Save(ScratchpadDocument.Initial("Scratchpad")));
    }

    [Fact]
    public void SavingAnIdenticalDocumentSkipsTheWriteAndSavesAreReadBackAndReplaceTheFile() =>
        WithDirectory(directory =>
        {
            var store = new ScratchpadStore(directory);
            var loaded = store.Load("Scratchpad", ScratchpadRetention.Never, Now);
            string path = Path.Combine(directory, "Scratchpad.json");
            Assert.True(File.Exists(path));
            var typed = loaded.UpdatingSelectedText("first", Now);
            Assert.True(store.Save(typed));
            DateTime written = File.GetLastWriteTimeUtc(path);
            File.SetLastWriteTimeUtc(path, written.AddHours(-1));
            Assert.True(store.Save(typed));   // identical: nothing is written
            Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(path));
            Assert.True(store.Save(typed.UpdatingSelectedText("second", Now)));
            Assert.Equal("second", ScratchpadDocument.Decoded(File.ReadAllBytes(path), "Scratchpad")!.Pads[0].Text);
            Assert.Equal([path], Directory.GetFiles(directory));   // no temp files left behind
        });
}
