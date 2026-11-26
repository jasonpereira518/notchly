// SPDX-License-Identifier: GPL-3.0-or-later
// Ported from upstream Tests/CommandBarFeatureTests.swift: command bar search and ranking, highlighting,
// emoji scope, typed numbers, text jobs, compact mode, completion and the presentation lifecycle.
// Skipped: pinyin keyword checks, checks that read Swift source text, and those that need the service or the UI.
using Notchly.Core.CommandBar;

namespace Notchly.Core.Tests;

public class CommandBarSearchTests
{
    private static List<int> Rank(IReadOnlyList<CommandBarCandidate> candidates, string query) =>
        CommandBarSearch.RankedIndexes(candidates, query);

    private static CommandBarCandidate Row(int index, string title, string keywords = "", int priority = 0, int boost = 0) =>
        CommandBarCandidate.Create(index, title, keywords, priority, boost);

    // ---- Folding and matching ------------------------------------------------------------------------------------

    [Fact]
    public void NormalizedFoldsCaseAndCollapsesWhitespace() =>
        Assert.Equal("brilho da tela", CommandBarSearch.Normalized("  Brilho   da\tTela "));

    [Fact]
    public void FullWidthSpaceFoldsLikeTheHalfWidthOne() =>
        Assert.Equal("bd 123", CommandBarSearch.Normalized("bd　123"));

    [Fact]
    public void CharactersThatTakeUpNoSpaceNeverReachTheMatching()
    {
        Assert.Equal("whatsapp", CommandBarSearch.Normalized("‎WhatsApp"));
        Assert.Equal("softhyphen", CommandBarSearch.Normalized("Soft­hyphen"));
    }

    [Fact]
    public void WidthFoldingTurnsFullWidthLettersIntoPlainOnes() =>
        Assert.Equal("abc 12", CommandBarSearch.Normalized("ＡＢＣ １２"));

    [Theory]
    [InlineData("Reunião com João", "reuniao joao", true)]            // accents and case
    [InlineData("Brilho da tela", "brilho", true)]                               // a plain word
    [InlineData("Brilho da tela", "Brilho", true)]                               // capitalized
    [InlineData("Brilho da tela", "brlho", true)]                                // a dropped letter
    [InlineData("Brilho da tela", "birlho", true)]                               // two swapped letters
    [InlineData("Zen", "zne", true)]                                             // a swapped pair in a three-letter name
    [InlineData("Brilho da tela", "volume", false)]                              // an unrelated word
    [InlineData("Zen", "zip", false)]                                            // short substitutions stay out
    [InlineData("Silenciar microfone", "silenciar micro", true)]                 // tokens as prefixes
    [InlineData("Silenciar microfone", "silenciar tela", false)]                 // every token must land
    public void TitleMatching(string title, string query, bool expected) =>
        Assert.Equal(expected, CommandBarSearch.Matches(title, query));

    [Theory]
    [InlineData("Capturar tela", "screenshot print", "print")]
    [InlineData("Capturas recentes", "Recent captures screenshot recording", "recent captures")]
    public void KeywordsMatchLikeTheTitleDoes(string title, string keywords, string query) =>
        Assert.True(CommandBarSearch.Matches(title, query, keywords));

    [Fact]
    public void TypoHelpers()
    {
        Assert.True(CommandBarSearch.IsSubsequence("brlho", "brilho"));
        Assert.False(CommandBarSearch.IsSubsequence("brilhoo", "brilho"));
        Assert.True(CommandBarSearch.WithinOneEdit("birlho", "brilho"));
        Assert.True(CommandBarSearch.WithinOneEdit("brilo", "brilho"));
        Assert.True(CommandBarSearch.WithinOneEdit("brilyo", "brilho"));
        Assert.False(CommandBarSearch.WithinOneEdit("brolyo", "brilho"));
        Assert.True(CommandBarSearch.IsAdjacentTransposition("zne", "zen"));
        Assert.False(CommandBarSearch.IsAdjacentTransposition("zne", "zone"));
        Assert.False(CommandBarSearch.IsAdjacentTransposition("zip", "zen"));
    }

    [Fact]
    public void ScoreIsNullWithoutAMatchAndFavoursTheWholeTitle()
    {
        Assert.Null(CommandBarSearch.Score("Brilho", ""));
        Assert.Null(CommandBarSearch.Score("Brilho", "volume"));
        var exact = CommandBarSearch.Score("Brilho", "brilho")!.Value;
        var prefix = CommandBarSearch.Score("Brilho da tela", "brilho")!.Value;
        Assert.Equal(140 + 1200, exact);
        Assert.Equal(140 + 900, prefix);
    }

    [Fact]
    public void AMemoryBoostCannotBeatABetterKeywordMatch()
    {
        // The query memory tops out at three points; a keyword match that is exact outranks a keyword prefix by far more.
        var keywordPrefix = CommandBarSearch.Score("Other", "pri", "primary") ?? 0;
        var keywordExact = CommandBarSearch.Score("Other", "pri", "pri") ?? 0;
        Assert.True(keywordPrefix + CommandBarQueryMemory.MaximumBoost < keywordExact);
    }

    [Fact]
    public void ASavedSearchStaysInTheListWhileWhatToLookForIsTyped()
    {
        // Scored against its own name the search left the list on the first word of the argument.
        Assert.Null(CommandBarSearch.Score("gh", "gh vorssaint utils", "Link"));
        Assert.NotNull(CommandBarSearch.Score("gh vorssaint utils", "gh vorssaint utils", "Link"));
    }

    // ---- Ranking -------------------------------------------------------------------------------------------------

    private static readonly CommandBarCandidate[] Screen =
    [
        Row(0, "Capturar tela"), Row(1, "Copiar texto da tela"), Row(2, "Bloquear a tela"), Row(3, "Manter acordado"),
    ];

    [Fact]
    public void MatchingRowsKeepCatalogOrderOnEqualScores() => Assert.Equal([0, 1, 2], Rank(Screen, "tela"));

    [Fact]
    public void TheFullTitleWinsTheTopRow() => Assert.Equal(0, Rank(Screen, "capturar tela").First());

    [Fact]
    public void UsageBoostReordersEquallyGoodMatchesButNeverResurrectsANonMatch()
    {
        CommandBarCandidate[] boosted = [Row(0, "Capturar tela"), Row(1, "Copiar texto da tela", boost: 300)];
        Assert.Equal([1, 0], Rank(boosted, "tela"));
        Assert.Equal([0], Rank(boosted, "capturar"));
    }

    [Fact]
    public void ABlankQueryRanksNothing() => Assert.Empty(Rank(Screen, " "));

    [Fact]
    public void LiteralShortMatchesRankAboveATranspositionAndUnrelatedNamesStayOut()
    {
        CommandBarCandidate[] typo = [Row(0, "Zebra"), Row(1, "Zen"), Row(2, "Zne Tools")];
        Assert.Equal([2, 1], Rank(typo, "zne"));
    }

    [Fact]
    public void TheAppNamedExactlyWhatWasTypedLeadsInvisibleMarkAndAll()
    {
        CommandBarCandidate[] invisible =
        [
            Row(0, "WhatsApp Business (and 1 more tab)", "Menu Safari History Recently Closed"),
            Row(1, "‎WhatsApp"),
        ];
        Assert.Equal([1, 0], Rank(invisible, "whatsapp"));
    }

    [Fact]
    public void RankBiasOrdersApps_OwnedActionsAndBorrowedMenuRows()
    {
        Assert.True(CommandBarPreferences.RankBias(CommandBarSource.Menus) < 0);
        Assert.True(CommandBarPreferences.RankBias(CommandBarSource.Apps) > CommandBarPreferences.RankBias(CommandBarSource.Actions));
        Assert.Equal(0, CommandBarPreferences.RankBias(CommandBarSource.Actions));
    }

    [Fact]
    public void AtEqualQualityTheAppsOwnRowBeatsTheMenuOfTheAppInFront()
    {
        CommandBarCandidate[] borrowed =
        [
            Row(0, "Tela cheia", boost: CommandBarPreferences.RankBias(CommandBarSource.Menus)),
            Row(1, "Tela cheia"),
        ];
        Assert.Equal([1, 0], Rank(borrowed, "tela cheia"));
    }

    [Fact]
    public void TheStepDownNeverBuriesAMenuCommandThatIsWhatWasTyped()
    {
        CommandBarCandidate[] sharper =
        [
            Row(0, "Fechar aba", boost: CommandBarPreferences.RankBias(CommandBarSource.Menus)),
            Row(1, "Fechar todas as abas do navegador"),
        ];
        Assert.Equal(0, Rank(sharper, "fechar aba").First());
    }

    [Fact]
    public void AnEquallyGoodAppMatchLeadsALowPriorityDiscoveryPage()
    {
        CommandBarCandidate[] rows =
        [
            Row(0, "What's New", boost: CommandBarPreferences.RankBias(CommandBarSource.SettingsPages)),
            Row(1, "Whatever", boost: CommandBarPreferences.RankBias(CommandBarSource.Apps)),
        ];
        Assert.Equal(1, Rank(rows, "what").First());
    }

    [Fact]
    public void ALearnedQueryChoiceOutranksAnUnselectedStrongerTextMatch()
    {
        CommandBarCandidate[] rows = [Row(0, "Passwords"), Row(1, "Secure Pass", priority: 1)];
        Assert.Equal(1, Rank(rows, "pass").First());
    }

    [Fact]
    public void ANameGivenToAnAppStillLeadsItsOrdinaryTitleMatch()
    {
        CommandBarCandidate[] rows = [Row(0, "Editor"), Row(1, "Source Studio", "editor", priority: 2_400)];
        Assert.Equal(1, Rank(rows, "editor").First());
    }

    [Fact]
    public void AnExplicitAliasRemainsStrongerThanLearnedQueryBehaviour()
    {
        CommandBarCandidate[] rows = [Row(0, "Passwords", priority: 1_100), Row(1, "Secure Pass", priority: 720)];
        Assert.Equal(0, Rank(rows, "pass").First());
    }

    [Fact]
    public void AnEnglishCommandOutranksAFuzzyAlternative()
    {
        var exact = Row(0, "Eject All Disks", "eject");
        var fuzzy = Row(1, "Ejection helper");
        Assert.Equal(0, Rank([fuzzy, exact], "Eject All Disks").First());
    }

    [Fact]
    public void PersonalAliasesUseTheExistingRankingPath()
    {
        var exact = Row(0, "Eject All Disks", "eject");
        var alias = Row(2, "Other", "mydisk", priority: 2_400);
        Assert.Equal([2], Rank([exact, alias], "mydisk"));
    }

    [Fact]
    public void CandidatesKeepTheCallersIndex()
    {
        CommandBarCandidate[] rows = [Row(10, "Alpha"), Row(20, "Beta")];
        Assert.Equal([20], Rank(rows, "beta"));
    }

    // ---- Feature ordering ----------------------------------------------------------------------------------------

    [Fact]
    public void AFeaturesSwitchLeadsItsPresetsAndItsSettingsPageFollowsThem()
    {
        (string Id, string Title)[] awakeRows =
        [
            ("settings.feature.keepAwake", "Keep Awake"),
            ("action.keepAwake.15", "Keep awake for 15 minutes"),
            ("action.keepAwake.30", "Keep awake for 30 minutes"),
            ("action.keepAwake", "Enable keep awake"),
        ];
        var candidates = awakeRows.Select((r, i) => Row(i, r.Title)).ToArray();
        var ids = awakeRows.Select(r => r.Id).ToArray();
        // By title alone the page named like the feature leads and the switch comes last.
        Assert.Equal([0, 1, 2, 3], Rank(candidates, "keep awake"));
        Assert.Equal([3, 1, 2, 0], CommandBarSearch.FeatureOrdered(Rank(candidates, "keep awake"), i => ids[i], _ => 0));
    }

    [Fact]
    public void AFeaturesRowsTradePlacesAmongTheirOwnSlotsAndNothingElseMoves()
    {
        string[] ids = ["settings.feature.micMute", "app.safari", "action.micMute", "action.keepAwake"];
        Assert.Equal([2, 1, 0, 3], CommandBarSearch.FeatureOrdered([0, 1, 2, 3], i => ids[i], _ => 0));
    }

    [Fact]
    public void ARowChosenByNameOrHabitKeepsThePlaceItRanked()
    {
        string[] ids = ["settings.feature.micMute", "app.safari", "action.micMute", "action.keepAwake"];
        Assert.Equal([0, 1, 2, 3], CommandBarSearch.FeatureOrdered([0, 1, 2, 3], i => ids[i], i => i == 0 ? 400 : 0));
    }

    [Fact]
    public void ASwitchRunOftenKeepsItsPlaceAndItsPresetsStillComeBeforeItsSettingsPage()
    {
        string[] ids = ["action.keepAwake", "settings.feature.keepAwake", "action.keepAwake.15", "action.keepAwake.30"];
        Assert.Equal([0, 2, 3, 1], CommandBarSearch.FeatureOrdered([0, 1, 2, 3], i => ids[i], i => i == 0 ? 300 : 0));
    }

    [Fact]
    public void AFeaturesSwitchLeadsThePageOfItsOwnNamedLikeIt()
    {
        string[] ids = ["settings.notchMascot", "app.companion", "toggle.notchMascot", "settings.setting.panelConfiguration"];
        Assert.Equal([2, 1, 0, 3], CommandBarSearch.FeatureOrdered([0, 1, 2, 3], i => ids[i], _ => 0));
    }

    [Fact]
    public void TwoSwitchesOfOneFeatureKeepTheOrderTheyRankedIn()
    {
        string[] ids = ["toggle.scrollInverter.horizontal", "settings.mouse", "toggle.scrollInverter.vertical"];
        Assert.Equal([0, 1, 2], CommandBarSearch.FeatureOrdered([0, 1, 2], i => ids[i], _ => 0));
    }

    // ---- Repeated ids --------------------------------------------------------------------------------------------

    [Fact]
    public void ARepeatedIdKeepsTheBetterRankedRowAndDropsTheOther() =>
        Assert.Equal([0, 1, 3], CommandBarSearch.FirstOccurrences(["a", "b", "a", "c", "b"]));

    [Fact]
    public void AnEmptyListStaysEmpty() => Assert.Empty(CommandBarSearch.FirstOccurrences([]));

    [Fact]
    public void AListWithNothingRepeatedIsLeftAlone() => Assert.Equal([0, 1], CommandBarSearch.FirstOccurrences(["a", "b"]));

    // ---- Highlighting --------------------------------------------------------------------------------------------

    [Fact]
    public void TheMatchedWordIsMarkedWhereItReallyIs() =>
        Assert.Equal(Enumerable.Range(7, 6).ToHashSet(), CommandBarSearch.HighlightOffsets("Screen brightness", "bright"));

    [Fact]
    public void EveryTokenGetsItsOwnMark() =>
        Assert.Equal(Enumerable.Range(0, 6).Concat(Enumerable.Range(10, 4)).ToHashSet(),
            CommandBarSearch.HighlightOffsets("Brilho da tela", "brilho tela"));

    [Fact]
    public void AccentsDoNotShiftTheMarks() =>
        Assert.Equal(Enumerable.Range(0, 7).ToHashSet(), CommandBarSearch.HighlightOffsets("Reunião com João", "reuniao"));

    [Fact]
    public void ATypoRescueMarksNothingRatherThanGuessing() => Assert.Empty(CommandBarSearch.HighlightOffsets("Settings", "brlho"));

    [Fact]
    public void TheMarkLandsOnTheWordNotOnTheFirstLettersThatRepeat() =>
        Assert.Equal(Enumerable.Range(10, 5).ToHashSet(), CommandBarSearch.HighlightOffsets("Empty the Trash", "trash"));

    [Fact]
    public void NothingTypedNothingMarked() => Assert.Empty(CommandBarSearch.HighlightOffsets("Anything", ""));

    // ---- Emoji scope and colour preview --------------------------------------------------------------------------

    [Fact]
    public void AnOrdinarySearchNeverOpensTheEmojiIndex() => Assert.Null(CommandBarSearch.EmojiQuery("fire"));

    [Fact]
    public void ALeadingColonScopesTheSearchAndStaysOutOfTheEmojiQuery()
    {
        Assert.Equal("fire", CommandBarSearch.EmojiQuery(":fire"));
        Assert.Equal("heart", CommandBarSearch.EmojiQuery("  : heart  "));
    }

    [Fact]
    public void AColonByItselfOpensTheEmojiIndexForBrowsing() => Assert.Equal("", CommandBarSearch.EmojiQuery(":"));

    [Fact]
    public void AColorTypedOnItsOwnLeadsWhenNothingOnTheListSpellsIt()
    {
        Assert.Equal(0, CommandBarSearch.ColorPreviewIndex([], "#2139"));
        Assert.Equal(0, CommandBarSearch.ColorPreviewIndex(["Safari", "Notes"], "#cafe"));
    }

    [Fact]
    public void ARowThatSpellsTheTypedColorKeepsReturnAndTheSwatchSitsUnderIt()
    {
        Assert.Equal(1, CommandBarSearch.ColorPreviewIndex(["Fix #2139 crash"], "#2139"));
        Assert.Equal(1, CommandBarSearch.ColorPreviewIndex(["#CAFE"], "#cafe"));
    }

    // ---- Verbs ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheVerbIsRecognizedWithTheNameTypedAfterItAndWithoutSpacesToo()
    {
        Assert.True(CommandBarSearch.MatchesVerb("quit saf", "Quit %@"));
        Assert.True(CommandBarSearch.MatchesVerb("encerrar", "Encerrar %@"));
        Assert.True(CommandBarSearch.MatchesVerb("終了", "%@を終了"));
        Assert.True(CommandBarSearch.MatchesVerb("quit saf", "Quit {0}"));
    }

    [Fact]
    public void AnAppNameAloneNeverDragsTheQuitRowsIn()
    {
        Assert.False(CommandBarSearch.MatchesVerb("safari", "Quit %@"));
        Assert.False(CommandBarSearch.MatchesVerb("", "Quit %@"));
        string[] formats = ["Quit %@", "終了%@"];
        Assert.False(CommandBarSearch.MatchesVerb("Safari", formats));
        Assert.False(CommandBarSearch.MatchesVerb("SafariQuitHelper", formats));
    }

    [Theory]
    [InlineData("quit safari", true)]
    [InlineData("Quit Safari", true)]
    [InlineData("Safari", false)]
    public void ALocalizedAndEnglishFormatBothPassThePoolGate(string query, bool expected) =>
        Assert.Equal(expected, CommandBarSearch.MatchesVerb(query, ["%@を終了", "Quit %@"]));

    [Fact]
    public void JapaneseCanPlaceTheVerbDirectlyAfterTheAppName()
    {
        string[] formats = ["%@を終了", "Quit %@"];
        Assert.True(CommandBarSearch.MatchesVerb("Safariを終了", formats));
    }

    // ---- Typed numbers -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("brilho 40", "brilho", 40)]
    [InlineData("volume 20%", "volume", 20)]
    [InlineData("manter acordado 30", "manter acordado", 30)]
    public void ATrailingNumberSplitsOffTheVerb(string query, string text, int number) =>
        Assert.Equal(new CommandBarNumberSplit(text, number), CommandBarSearch.SplitTrailingNumber(query));

    [Theory]
    [InlineData("brilho")]
    [InlineData("40")]
    public void ABareVerbOrANumberAloneCarriesNoNumber(string query) =>
        Assert.Equal(new CommandBarNumberSplit(query, null), CommandBarSearch.SplitTrailingNumber(query));

    [Theory]
    [InlineData("40", 40)]
    [InlineData("140", 100)]
    [InlineData("35%", 35)]
    public void InlineArgumentsAcceptDigitsAndClamp(string text, int expected) =>
        Assert.Equal(expected, CommandBarSearch.ArgumentValue(text, 0, 100));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12345")]
    public void InlineArgumentsRejectTheRest(string text) => Assert.Null(CommandBarSearch.ArgumentValue(text, 0, 100));

    // ---- App names -----------------------------------------------------------------------------------------------

    [Fact]
    public void ApplicationKeywordsKeepTheDiskAndAlternateNamesSearchable()
    {
        var keywords = CommandBarSearch.ApplicationKeywords("Cloud Notes", "CloudNotes", ["Former Notes"]);
        Assert.True(CommandBarSearch.Matches("Cloud Notes", "cloudnotes", keywords));
        Assert.True(CommandBarSearch.Matches("Cloud Notes", "former", keywords));
        // A disk name that only differs by case is not repeated.
        Assert.Equal("", CommandBarSearch.ApplicationKeywords("Reader", "reader", []));
        Assert.Equal("yunbiji", CommandBarSearch.ApplicationKeywords("T", "", [], _ => "yunbiji"));
    }

    [Fact]
    public void ARealAliasIsKeptAndTheBundlesOwnFileNameIsNot() =>
        Assert.Equal(["Legacy Planner"], CommandBarSearch.UsableAlternateNames(["Legacy Planner", "Planner.app"], "Planner", "Planner.app"));

    [Fact]
    public void AnAliasRepeatingTheNameUnderTheIconTeachesTheSearchNothing() =>
        Assert.Equal(["Preferences", "Settings", "Previous Settings"],
            CommandBarSearch.UsableAlternateNames(
                ["Preferences", "Settings", "Configuration.app", "Previous Settings", "Configuration"],
                "Configuration", "Configuration.app"));

    [Fact]
    public void AnUntranslatedPlaceholderIsNotANameAnybodyTypes() =>
        Assert.Equal(["browser"], CommandBarSearch.UsableAlternateNames(["ALTERNATE_NAME_1", "  ", "browser"], "Navigator", "Navigator.app"));

    [Fact]
    public void TwoAliasesThatDifferOnlyByAccentOrCaseAreOneAlias() =>
        Assert.Equal(["Café"], CommandBarSearch.UsableAlternateNames(["Café", "cafe"], "Reader", "Reader.app"));

    // ---- Text jobs -----------------------------------------------------------------------------------------------

    [Fact]
    public void WordsAreCountedTheWayAPersonCountsThem()
    {
        Assert.Equal(5, CommandBarText.WordCount("uma frase com cinco palavras"));
        Assert.Equal(2, CommandBarText.WordCount("  espaços   demais  "));
        Assert.Equal(0, CommandBarText.WordCount(""));
    }

    [Fact]
    public void AnAccentedLetterIsOneCharacterNotTwo()
    {
        Assert.Equal(4, CommandBarText.CharacterCount("café"));
        Assert.Equal(4, CommandBarText.CharacterCount("café"));
    }

    [Fact]
    public void APreviewOfAParagraphStaysOnOneLine() =>
        Assert.Equal("uma linha com quebra", CommandBarText.Preview("uma linha\ncom quebra"));

    [Fact]
    public void ALongSelectionIsCutWithAnEllipsis() =>
        Assert.Equal(new string('a', 10) + "…", CommandBarText.Preview(new string('a', 60), 10));

    [Fact]
    public void ACaseRowIsOnlyOfferedWhenTheCaseWouldChange()
    {
        Assert.True(CommandBarText.ChangesCase("abc", s => s.ToUpperInvariant()));
        Assert.False(CommandBarText.ChangesCase("ABC", s => s.ToUpperInvariant()));
    }

    [Fact]
    public void AMenuTrailNeverRepeatsANameRightAfterItself()
    {
        Assert.Equal("Notes › Format › Bold", CommandBarMenuPath.Crumb("Notes", ["Notes", "Format", "Bold"]));
        Assert.Equal("Notes › Edit", CommandBarMenuPath.Crumb("Notes", ["", "Edit"]));
    }

    // ---- Clipboard access ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void ClipboardHistoryIsAvailableWhenCapturingOrWhenItemsAreSaved(bool capture, bool saved, bool expected) =>
        Assert.Equal(expected, CommandBarClipboardAccess.CanUseHistory(capture, saved));

    // ---- Compact mode --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, false, true)]   // the browse list is what an ordinary empty bar shows
    [InlineData(true, false, false, false)]   // a compact bar draws no list until something is typed
    [InlineData(true, true, false, true)]     // a category is an explicit drill-in
    [InlineData(true, false, true, true)]     // a peek is the person asking for the list anyway
    public void ShowsBrowseList(bool compact, bool hasCategory, bool isPeeking, bool expected) =>
        Assert.Equal(expected, CommandBarHome.ShowsBrowseList(compact, hasCategory, isPeeking));

    [Theory]
    [InlineData(true, "", false, false, true)]       // an empty compact field is the whole panel
    [InlineData(true, "   ", false, false, true)]    // whitespace is not something typed
    [InlineData(true, "fire", false, false, false)]  // one letter brings the list and the footer back
    [InlineData(true, "", true, false, false)]       // a category keeps the panel open
    [InlineData(true, "", false, true, false)]       // a peeked bar is not collapsed either
    [InlineData(false, "", false, false, false)]     // the ordinary bar is never collapsed
    public void IsCollapsed(bool compact, string query, bool hasCategory, bool isPeeking, bool expected) =>
        Assert.Equal(expected, CommandBarHome.IsCollapsed(compact, query, hasCategory, isPeeking));

    // ---- Presentation lifecycle ----------------------------------------------------------------------------------

    [Fact]
    public void HomePresentsWhileItsCatalogHydratesAndClosingCancelsTheDeferredWork()
    {
        var first = Guid.NewGuid();
        var lifecycle = new CommandBarPresentationLifecycle();
        lifecycle.BeginHome(first);
        Assert.True(lifecycle.IsLoadingHome);
        lifecycle.Hide();
        Assert.False(lifecycle.CompleteHomeHydration(first, isVisible: false));
    }

    [Fact]
    public void OnlyTheLatestVisibleHomePresentationMayReceiveDeferredWork()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var lifecycle = new CommandBarPresentationLifecycle();
        lifecycle.BeginHome(first);
        lifecycle.BeginHome(second);
        Assert.False(lifecycle.CompleteHomeHydration(first, isVisible: true));
        Assert.True(lifecycle.CompleteHomeHydration(second, isVisible: true));
        Assert.True(lifecycle.AcceptsHomeUpdates(second, isVisible: true));
        Assert.False(lifecycle.AcceptsHomeUpdates(first, isVisible: true));
        Assert.False(lifecycle.AcceptsHomeUpdates(second, isVisible: false));
        Assert.True(lifecycle.AcceptsSharedCacheCompletion(first, second, isVisible: true));
        lifecycle.Hide();
        Assert.False(lifecycle.AcceptsSharedCacheCompletion(first, second, isVisible: true));
    }

    [Fact]
    public void AnAsyncRowShortcutWaitsWithoutBeingConsumedThenRunsOnceOnItsPresentation()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var deferred = new CommandBarDeferredRowShortcut();
        deferred.Schedule("action.trash", first);
        Assert.Null(deferred.Key(second));
        Assert.Equal("action.trash", deferred.Key(first));
        Assert.Null(deferred.Take(second));
        Assert.Equal("action.trash", deferred.Take(first));
        Assert.Null(deferred.Take(first));
    }

    [Fact]
    public void ClosingOrSupersedingAPresentationCancelsItsPromptShortcut()
    {
        var first = Guid.NewGuid();
        var deferred = new CommandBarDeferredRowShortcut();
        deferred.Schedule("action.trash", first);
        deferred.Cancel();
        Assert.Null(deferred.Take(first));
    }

    // ---- Tab completion ------------------------------------------------------------------------------------------

    [Fact]
    public void TabCompletionRetainsEmojiScopeAndTheSearchableName()
    {
        var completed = CommandBarCompletion.CompletedQuery(":fire", "\U0001F525  fire", "fire");
        Assert.Equal(":fire", completed);
        Assert.Equal("fire", CommandBarSearch.EmojiQuery(completed));
    }

    [Theory]
    [InlineData("fir")]
    [InlineData("")]
    public void TabKeepsASelectedEmojiCategoryResultSearchable(string categoryQuery)
    {
        var completed = CommandBarCompletion.CompletedQuery(categoryQuery, "\U0001F525  fire", "fire");
        Assert.Equal("fire", completed);
        Assert.Null(CommandBarSearch.EmojiQuery(completed));
        Assert.Equal([0], Rank([Row(0, "fire")], completed));
    }

    [Fact]
    public void OrdinaryTabCompletionStillUsesTheSelectedTitle() =>
        Assert.Equal("Whatever", CommandBarCompletion.CompletedQuery("whts", "Whatever", null));

    [Fact]
    public void TabRemembersTheFuzzySearchUnlessTheCompletedFieldIsEdited()
    {
        Assert.Equal("whts", CommandBarCompletion.QueryForLearning("Whatever", "whts"));
        Assert.Equal("whts", CommandBarCompletion.RetainedOriginal("whts", "Whatever", "Whatever"));
        Assert.Null(CommandBarCompletion.RetainedOriginal("whts", "Whatever", "Whatever b"));
    }
}
