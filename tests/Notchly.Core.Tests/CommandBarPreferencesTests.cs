// SPDX-License-Identifier: GPL-3.0-or-later
// Ported from upstream Tests/CommandBarFeatureTests.swift: what the person controls (sources, aliases, pins, hidden
// rows, position) and the row shortcuts and app table sort (CommandBarAppSortContract).
// Skipped: checks that read Swift source text, and the macOS take-over decision.
using Notchly.Core.CommandBar;
using Notchly.Core.Notch;
using Notchly.Core.Shortcuts;

namespace Notchly.Core.Tests;

public class CommandBarPreferencesTests
{
    private static HashSet<CommandBarSource> None => [];

    // ---- Sources -------------------------------------------------------------------------------------------------

    [Fact]
    public void SourceIdsAreStableBecauseTheyPersistInsideTheDisabledList() =>
        Assert.Equal(
            ["actions", "apps", "menus", "windows", "quitApps", "uninstallApps", "settingsPages", "macSettings", "snippets",
             "clipboard", "emoji", "folders", "answers", "calculator", "selection", "links", "files", "killProcess"],
            CommandBarSources.All.Select(s => s.StorageId()));

    [Fact]
    public void OnlyTheAppsOwnActionsCannotBeSwitchedOff()
    {
        Assert.True(CommandBarSource.Actions.IsAlwaysOn());
        Assert.Single(CommandBarSources.All, s => s.IsAlwaysOn());
    }

    [Theory]
    [InlineData("app.x", CommandBarSource.Apps)]
    [InlineData("menu.1.Bold", CommandBarSource.Menus)]
    [InlineData("folder./tmp", CommandBarSource.Folders)]
    [InlineData("macsettings.com.apple.Sound-Settings.extension", CommandBarSource.MacSettings)]
    [InlineData("settings.general", CommandBarSource.SettingsPages)]
    [InlineData("action.screenshot", CommandBarSource.Actions)]
    [InlineData("action.recentCaptures", CommandBarSource.Actions)]
    [InlineData("emoji.browse", CommandBarSource.Emoji)]
    public void EveryRowKnowsWhichSourceItCameFrom(string rowId, CommandBarSource expected) =>
        Assert.Equal(expected, CommandBarPreferences.SourceOfRowId(rowId));

    [Fact]
    public void ASwitchedOffSourceStaysOffAndActionsNeverCanBe()
    {
        Assert.False(CommandBarPreferences.IsEnabled(CommandBarSource.Folders, "folders,emoji"));
        Assert.True(CommandBarPreferences.IsEnabled(CommandBarSource.Apps, "folders,emoji"));
        Assert.True(CommandBarPreferences.IsEnabled(CommandBarSource.Actions, "actions"));
    }

    [Fact]
    public void TheDisabledListWritesTheSameWayEveryTime() =>
        Assert.Equal("emoji,folders",
            CommandBarPreferences.StorageValue([CommandBarSource.Emoji, CommandBarSource.Folders, CommandBarSource.Actions]));

    [Fact]
    public void AnUnknownSourceIdIsDroppedInsteadOfCorruptingTheSet() =>
        Assert.Equal(new HashSet<CommandBarSource> { CommandBarSource.Folders, CommandBarSource.Emoji },
            CommandBarPreferences.DisabledSources("folders, nonsense ,emoji"));

    [Fact]
    public void TheActionsSourceIsDroppedFromAStoredDisabledList() =>
        Assert.Empty(CommandBarPreferences.DisabledSources("actions"));

    [Fact]
    public void ActionBrowseRowsAreTheFourThatOpenAnotherCategory()
    {
        Assert.Equal(new HashSet<string> { "emoji.browse", "kill.browse", "uninstall.browse", "uninstall.finder" },
            CommandBarPreferences.ActionBrowseRowIds);
        Assert.Equal("emoji.browse", CommandBarPreferences.EmojiBrowserRowId);
        Assert.Equal("kill.browse", CommandBarPreferences.KillProcessBrowserRowId);
        // A row is filed under the category it opens, so the actions list admits a navigation row by name, not by prefix.
        Assert.Equal(new HashSet<CommandBarSource> { CommandBarSource.UninstallApps, CommandBarSource.Emoji, CommandBarSource.KillProcess },
            CommandBarPreferences.ActionBrowseRowIds.Select(CommandBarPreferences.SourceOfRowId).ToHashSet());
        Assert.All(CommandBarPreferences.ActionBrowseRowIds, id => Assert.True(CommandBarPreferences.IsActionRow(id)));
    }

    [Fact]
    public void NamingTheNavigationRowsWidensTheActionsListToThemAlone()
    {
        Assert.True(CommandBarPreferences.IsActionRow("action.cleaner"));
        Assert.False(CommandBarPreferences.IsActionRow("app.Safari"));
        Assert.False(CommandBarPreferences.IsActionRow("settings.appearance"));
        Assert.False(CommandBarPreferences.IsActionRow("emoji.grin"));
    }

    [Fact]
    public void ANavigationRowLeavesTheActionsListWhileItsCategoryIsSwitchedOff()
    {
        var emojiOff = CommandBarPreferences.DisabledSources("emoji");
        Assert.False(CommandBarPreferences.IsActionRow(CommandBarPreferences.EmojiBrowserRowId, emojiOff));
        Assert.True(CommandBarPreferences.IsActionRow(CommandBarPreferences.EmojiBrowserRowId, None));

        var killOff = CommandBarPreferences.DisabledSources("killProcess");
        Assert.False(CommandBarPreferences.IsActionRow(CommandBarPreferences.KillProcessBrowserRowId, killOff));
        Assert.True(CommandBarPreferences.IsActionRow("action.cleaner", killOff));

        // A row whose destination is still on stays; a category with every navigation row off is left with nothing.
        Assert.All(CommandBarPreferences.ActionBrowseRowIds, id =>
            Assert.True(CommandBarPreferences.IsActionRow(id, emojiOff) || CommandBarPreferences.IsActionRow(id, killOff)));
        var allOff = CommandBarPreferences.DisabledSources("uninstallApps,emoji,killProcess");
        Assert.DoesNotContain(CommandBarPreferences.ActionBrowseRowIds, id => CommandBarPreferences.IsActionRow(id, allOff));
    }

    [Fact]
    public void TheEmojiComesBackOutOfTheIdItsRowIsStoredUnder()
    {
        Assert.Equal("\U0001F44D", CommandBarPreferences.EmojiIdentity(CommandBarPreferences.EmojiRowId("\U0001F44D")));
        Assert.Null(CommandBarPreferences.EmojiIdentity("app.finder"));
        Assert.Null(CommandBarPreferences.EmojiIdentity("emoji."));
    }

    [Fact]
    public void AppsLeadCommandsWhileAFileNeedsAPlainlyBetterMatch()
    {
        Assert.True(CommandBarPreferences.RankBias(CommandBarSource.Files) < CommandBarPreferences.RankBias(CommandBarSource.Actions));
        Assert.True(CommandBarPreferences.RankBias(CommandBarSource.Apps) > CommandBarPreferences.RankBias(CommandBarSource.Actions));
        Assert.Equal(0, CommandBarPreferences.RankBias(CommandBarSource.UninstallApps));
    }

    [Fact]
    public void RankBiasValues()
    {
        Assert.Equal(-80, CommandBarPreferences.RankBias(CommandBarSource.Menus));
        Assert.Equal(-40, CommandBarPreferences.RankBias(CommandBarSource.Files));
        Assert.Equal(-40, CommandBarPreferences.RankBias(CommandBarSource.SettingsPages));
        Assert.Equal(80, CommandBarPreferences.RankBias(CommandBarSource.Apps));
        Assert.All([CommandBarSource.Actions, CommandBarSource.Windows, CommandBarSource.Clipboard, CommandBarSource.Emoji, CommandBarSource.Links],
            s => Assert.Equal(0, CommandBarPreferences.RankBias(s)));
    }

    // ---- Aliases -------------------------------------------------------------------------------------------------

    [Fact]
    public void ARowTakesTheNameItWasGivenAndClearingTheFieldRemovesIt()
    {
        var aliases = CommandBarPreferences.SettingAlias("codex", "app./Applications/Chat.app", new Dictionary<string, string>());
        Assert.Equal("codex", aliases["app./Applications/Chat.app"]);
        aliases = CommandBarPreferences.SettingAlias("  ", "app./Applications/Chat.app", aliases);
        Assert.Empty(aliases);
    }

    [Fact]
    public void AnAliasIsCutToSixtyCharacters()
    {
        var aliases = CommandBarPreferences.SettingAlias(new string('x', 90), "app.a", new Dictionary<string, string>());
        Assert.Equal(60, aliases["app.a"].Length);
    }

    [Theory]
    [InlineData("codex", "codex", true)]       // whole
    [InlineData("codex", "cod", true)]         // as it is being typed
    [InlineData("codex", "codexx", false)]     // never beyond it
    [InlineData("meu chat codex", "codex", true)] // several words all find the same row
    [InlineData("Códex", "codex", true)]  // accents never break a name
    public void AliasMatching(string alias, string query, bool expected) =>
        Assert.Equal(expected, CommandBarPreferences.AliasMatches(alias, query));

    [Fact]
    public void AFinishedNameOutranksOneStillBeingTyped()
    {
        Assert.Equal(CommandBarPreferences.AliasMatch.Exact, CommandBarPreferences.AliasHit("codex", "codex"));
        Assert.Equal(CommandBarPreferences.AliasMatch.Prefix, CommandBarPreferences.AliasHit("codex", "cod"));
        Assert.Null(CommandBarPreferences.AliasHit("codex", "zzz"));
    }

    [Fact]
    public void TheNameThePersonGaveBeatsTheAppsOwnTitle()
    {
        Assert.Equal(2400, (int)CommandBarPreferences.AliasMatch.Exact);
        Assert.Equal(1100, (int)CommandBarPreferences.AliasMatch.Prefix);
        Assert.True((int)CommandBarPreferences.AliasMatch.Exact > 1200);
        Assert.True((int)CommandBarPreferences.AliasMatch.Prefix > 900);
    }

    [Fact]
    public void FoldedLettersAskAnAliasTheSameQuestionTheRawOnesDo()
    {
        var folded = CommandBarPreferences.AliasHitNormalized("Códex", CommandBarSearch.Normalized("CÓD"));
        Assert.Equal(CommandBarPreferences.AliasMatch.Prefix, folded);
        Assert.Equal(CommandBarPreferences.AliasHit("Códex", "CÓD"), folded);
    }

    [Fact]
    public void ANameAlreadyTakenIsReportedInsteadOfBeingStolen()
    {
        var aliases = new Dictionary<string, string> { ["app.a"] = "codex" };
        Assert.Equal("app.a", CommandBarPreferences.RowUsingAlias("codex", aliases, "app.b"));
        // Renaming a row never conflicts with itself.
        Assert.Null(CommandBarPreferences.RowUsingAlias("codex", aliases, "app.a"));
    }

    [Fact]
    public void NamesSurviveTheRoundTripAndACorruptListDecodesAsNone()
    {
        var aliases = new Dictionary<string, string> { ["a"] = "one", ["b"] = "two" };
        Assert.Equal(aliases, CommandBarPreferences.DecodeAliases(CommandBarPreferences.EncodeAliases(aliases)));
        Assert.Empty(CommandBarPreferences.DecodeAliases("not json"));
        Assert.Empty(CommandBarPreferences.DecodeAliases(null));
    }

    [Fact]
    public void ABlankStoredAliasIsDropped() =>
        Assert.Equal(new Dictionary<string, string> { ["a"] = "one" },
            CommandBarPreferences.DecodeAliases("{\"a\":\" one \",\"b\":\"  \"}"));

    [Fact]
    public void OnlyRowsThatAreTheSameThingTomorrowCanBeNamed()
    {
        Assert.True(CommandBarPreferences.AcceptsAlias("app.x"));
        Assert.False(CommandBarPreferences.AcceptsAlias("menu.1.Bold"));
        Assert.False(CommandBarPreferences.AcceptsAlias("window.4"));
        Assert.False(CommandBarPreferences.AcceptsAlias("clipboard.abc"));
        Assert.False(CommandBarPreferences.AcceptsAlias("uninstall.x"));
    }

    // ---- Pins ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AnUninstallRowIsOfferedFreshEachTimeSoItCannotBePinned()
    {
        Assert.False(CommandBarPreferences.AcceptsPin("uninstall.x"));
        Assert.False(CommandBarPreferences.AcceptsPin("menu.1.Bold"));
        Assert.True(CommandBarPreferences.AcceptsPin("app.x"));
    }

    [Fact]
    public void PinsKeepTheOrderTheyWereMadeInAndTheSameGestureUnpins()
    {
        var pins = CommandBarPreferences.TogglingPin("action.screenshot", []);
        pins = CommandBarPreferences.TogglingPin("app.chat", pins);
        Assert.Equal(["action.screenshot", "app.chat"], pins);
        Assert.Equal(["app.chat"], CommandBarPreferences.TogglingPin("action.screenshot", pins));
    }

    [Fact]
    public void PinsAreCappedAtThirtyDroppingTheOldest()
    {
        var pins = new List<string>();
        for (var i = 0; i < 32; i++) pins = CommandBarPreferences.TogglingPin($"app.{i}", pins);
        Assert.Equal(30, pins.Count);
        Assert.Equal("app.2", pins[0]);
        Assert.Equal("app.31", pins[^1]);
    }

    [Fact]
    public void TheEmptyBarLeadsWithThePinsThatStillExist() =>
        Assert.Equal(["app.chat"], CommandBarPreferences.LeadingPins(["app.chat", "app.gone"], new HashSet<string> { "app.chat", "action.a" }));

    [Fact]
    public void AnUninstalledFeatureIsNotListedAsAPin() =>
        Assert.Equal(["app.chat"],
            CommandBarPreferences.ListedPins(["action.cleaningMode", "app.chat", "settings.cleaningMode"], new HashSet<string> { "app.chat" }));

    [Fact]
    public void AnAppThatLeftThisMachineStillAppearsSoItsPinCanBeRemoved() =>
        Assert.Equal(["action.cleaningMode", "app.gone"],
            CommandBarPreferences.ListedPins(["action.cleaningMode", "app.gone"], new HashSet<string> { "action.cleaningMode" }));

    [Fact]
    public void APinBreaksATieAndNeverJumpsOverABetterMatch()
    {
        Assert.Equal(60, CommandBarPreferences.PinTieBreak);
        Assert.True(CommandBarPreferences.PinTieBreak < 700);
    }

    [Fact]
    public void PinsSurviveTheRoundTripAndNeverRepeat()
    {
        Assert.Equal(["a", "b"], CommandBarPreferences.DecodePins(CommandBarPreferences.EncodePins(["a", "b"])));
        Assert.Equal(["a", "b"], CommandBarPreferences.DecodePins("a\na\n\nb"));
    }

    // ---- Hidden rows ---------------------------------------------------------------------------------------------

    [Fact]
    public void ARowCanBeToldNeverToShowAndToComeBack()
    {
        var hidden = CommandBarPreferences.TogglingHidden("app.x", new HashSet<string>());
        Assert.Equal(new HashSet<string> { "app.x" }, hidden);
        hidden = CommandBarPreferences.TogglingHidden("app.x", hidden);
        Assert.Empty(hidden);
    }

    [Fact]
    public void HiddenRowsSurviveTheRoundTrip() =>
        Assert.Equal(new HashSet<string> { "a", "b" }, CommandBarPreferences.DecodeHidden(CommandBarPreferences.EncodeHidden(["b", "a"])));

    [Fact]
    public void HiddenRowsWriteSortedSoTheSameSetAlwaysWritesTheSameString() =>
        Assert.Equal("a\nb", CommandBarPreferences.EncodeHidden(["b", "a"]));

    [Fact]
    public void RemovingAnUninstalledAppCleansUpItsAliasPinAndHiddenEntries()
    {
        var uninstalled = CommandBarRowShortcuts.ApplicationStableKeys(["com.rows.Rows"], ["/Applications/Rows.app"]);
        var aliases = new Dictionary<string, string> { ["app.bundle.com.rows.Rows"] = "Rows Spreadsheets", ["app.bundle.other"] = "Other App" };
        Assert.Equal(new Dictionary<string, string> { ["app.bundle.other"] = "Other App" },
            CommandBarPreferences.RemovingAliases(uninstalled, aliases));
        Assert.Equal(["app.bundle.other"], CommandBarPreferences.RemovingPins(uninstalled, ["app.bundle.com.rows.Rows", "app.bundle.other"]));
        Assert.Equal(new HashSet<string> { "app.bundle.other" },
            CommandBarPreferences.RemovingHidden(uninstalled, new HashSet<string> { "app.bundle.com.rows.Rows", "app.bundle.other" }));
    }

    // ---- Position ------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePositionOffsetSurvivesARoundedRoundTrip() =>
        Assert.Equal(new SizeD(-24, 81),
            CommandBarPreferences.DecodePositionOffset(CommandBarPreferences.EncodePositionOffset(new SizeD(-24.4, 80.6))));

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("12,")]
    [InlineData("12,nope")]
    [InlineData("12,nope,20")]
    [InlineData("nan,1")]
    [InlineData("inf,1")]
    public void AnInvalidPositionOffsetIsIgnored(string raw) =>
        Assert.Equal(SizeD.Zero, CommandBarPreferences.DecodePositionOffset(raw));

    [Fact]
    public void AnEmptyOrNonFinitePositionOffsetIsNotStored()
    {
        Assert.Equal("", CommandBarPreferences.EncodePositionOffset(SizeD.Zero));
        Assert.Equal("", CommandBarPreferences.EncodePositionOffset(new SizeD(double.PositiveInfinity, 1)));
        Assert.Equal("", CommandBarPreferences.EncodePositionOffset(new SizeD(0.4, -0.4)));
        Assert.Equal("-24,81", CommandBarPreferences.EncodePositionOffset(new SizeD(-24.4, 80.6)));
    }

    // Upstream's screen is x -1440, y 0, 1440x900 with y growing upward, and a 560x380 panel. Its upper-right clamp is
    // (-576, 504) and its lower-left (-1424, 16), both as the panel's bottom-left. With y growing downward and the
    // top-left as the origin the same corners are (-576, 16) and (-1424, 504): 900 - 504 - 380 and 900 - 16 - 380.
    private static readonly RectD Screen = new(-1440, 0, 1440, 900);
    private static readonly SizeD PanelSize = new(560, 380);

    [Fact]
    public void TheCommandBarStaysFullyInsideAScreenOnBothAxes()
    {
        Assert.Equal(new PointD(-576, 16), CommandBarPreferences.ClampedPanelOrigin(PanelSize, Screen, new SizeD(10_000, -10_000)));
        Assert.Equal(new PointD(-1424, 504), CommandBarPreferences.ClampedPanelOrigin(PanelSize, Screen, new SizeD(-10_000, 10_000)));
    }

    [Fact]
    public void ThePanelTopOpensAtTwentyEightPercentOfTheWorkAreaAndIsCentred()
    {
        var origin = CommandBarPreferences.ClampedPanelOrigin(PanelSize, Screen, SizeD.Zero);
        Assert.Equal(-720 - 280, origin.X);
        Assert.Equal(900 * 0.28, origin.Y, 6);
    }

    [Fact]
    public void ThePanelStaysInsideAWorkAreaSmallerThanItself()
    {
        var origin = CommandBarPreferences.ClampedPanelOrigin(new SizeD(560, 380), new RectD(0, 40, 400, 300), new SizeD(500, 500));
        Assert.Equal(new PointD(16, 56), origin);
    }

    // ---- Row shortcuts -------------------------------------------------------------------------------------------

    private static readonly Shortcut OptionB = new('B', ShortcutModifiers.Alt | ShortcutModifiers.Win);
    private static readonly Shortcut OptionN = new('N', ShortcutModifiers.Alt | ShortcutModifiers.Win);
    private static readonly Shortcut CommandPeriod = new(0xBE, ShortcutModifiers.Win);

    private static Dictionary<string, Shortcut> NoBindings => new();

    [Fact]
    public void ARowAnswersToTheKeysItWasGivenAndTwoRowsNeverShareOne()
    {
        var bound = CommandBarRowShortcuts.Setting(OptionB, "app.bundle.a", NoBindings);
        Assert.Equal(OptionB, bound["app.bundle.a"]);
        Assert.Null(CommandBarRowShortcuts.AssignmentIssue(OptionB, "app.bundle.a", bound));
        Assert.Equal(CommandBarAssignmentIssue.Occupied("app.bundle.a"),
            CommandBarRowShortcuts.AssignmentIssue(OptionB, "app.bundle.b", bound));
        Assert.Equal(CommandBarAssignmentIssue.Invalid,
            CommandBarRowShortcuts.AssignmentIssue(new Shortcut('B', ShortcutModifiers.None), "app.bundle.a", bound));

        bound = CommandBarRowShortcuts.Setting(OptionB, "app.bundle.b", bound);
        Assert.Equal(OptionB, bound["app.bundle.b"]);
        Assert.False(bound.ContainsKey("app.bundle.a"));
        Assert.Empty(CommandBarRowShortcuts.Setting(null, "app.bundle.b", bound));
        Assert.Equal("app.bundle.b", CommandBarRowShortcuts.KeyFor(OptionB, bound));
        Assert.Null(CommandBarRowShortcuts.KeyFor(OptionN, bound));
    }

    [Fact]
    public void ABareLetterIsNeverTakenFromEveryApp()
    {
        Assert.True(CommandBarRowShortcuts.IsUsable(OptionB));
        Assert.False(CommandBarRowShortcuts.IsUsable(new Shortcut('B', ShortcutModifiers.None)));
        Assert.True(CommandBarRowShortcuts.IsUsable(new Shortcut('Q', ShortcutModifiers.Control)));
    }

    [Fact]
    public void TheBindingsSurviveARoundTripThroughStorage()
    {
        var bound = CommandBarRowShortcuts.Setting(OptionB, "app.bundle.b", NoBindings);
        Assert.Equal(bound, CommandBarRowShortcuts.Decode(CommandBarRowShortcuts.Encode(bound)));
        Assert.Contains("\"app.bundle.b\":\"alt+win:66\"", CommandBarRowShortcuts.Encode(bound));
        Assert.Empty(CommandBarRowShortcuts.Decode("not json"));
        Assert.Empty(CommandBarRowShortcuts.Decode(null));
        // A damaged entry is skipped, its neighbours are kept.
        Assert.Equal(["app.ok"], CommandBarRowShortcuts.Decode("{\"app.ok\":\"alt+win:66\",\"app.bad\":\"garbage\",\"app.none\":\"none\"}").Keys);
    }

    [Fact]
    public void TheEmojiBrowserRowCanOwnAGlobalShortcutLikeAnyOtherRow()
    {
        var binding = CommandBarRowShortcuts.Setting(CommandPeriod, CommandBarPreferences.EmojiBrowserRowId, NoBindings);
        Assert.Equal(CommandBarPreferences.EmojiBrowserRowId, CommandBarRowShortcuts.KeyFor(CommandPeriod, binding));
    }

    [Fact]
    public void TwentySixAppShortcutsFitWithRoomLeftForOtherCommands()
    {
        var alphabet = NoBindings;
        for (var index = 0; index < 26; index++)
            alphabet = CommandBarRowShortcuts.Setting(new Shortcut('A' + index, ShortcutModifiers.Control), $"app.bundle.{index}", alphabet);
        Assert.Equal(26, alphabet.Count);
        Assert.True(CommandBarRowShortcuts.HasRoom("row.extra", alphabet));
        Assert.Equal(alphabet, CommandBarRowShortcuts.Decode(CommandBarRowShortcuts.Encode(alphabet)));
    }

    [Fact]
    public void AFullListSaysSoBeforeTheKeysAreTakenAndReboundingIsAlwaysAllowed()
    {
        var full = NoBindings;
        for (var index = 0; index < CommandBarRowShortcuts.Limit; index++)
            full[$"row.{index}"] = new Shortcut(index + 1, ShortcutModifiers.Control);
        Assert.Equal(64, CommandBarRowShortcuts.Limit);
        Assert.False(CommandBarRowShortcuts.HasRoom("row.new", full));
        Assert.True(CommandBarRowShortcuts.HasRoom("row.0", full));
        Assert.True(CommandBarRowShortcuts.HasRoom("row.new", NoBindings));
        Assert.Equal(CommandBarAssignmentIssue.Full, CommandBarRowShortcuts.AssignmentIssue(OptionN, "row.new", full));
        Assert.Null(CommandBarRowShortcuts.AssignmentIssue(OptionN, "row.0", full));
        // A new row is refused by Setting too.
        Assert.False(CommandBarRowShortcuts.Setting(OptionN, "row.new", full).ContainsKey("row.new"));
    }

    [Fact]
    public void AnAppInFrontWithItsWindowInFrontHidesOnItsOwnShortcut()
    {
        Assert.True(CommandBarRowShortcuts.HidesAppInFront(true, false, () => true));
        Assert.False(CommandBarRowShortcuts.HidesAppInFront(true, false, () => false));   // comes forward instead
        Assert.False(CommandBarRowShortcuts.HidesAppInFront(false, false, () => true));   // behind another app
        Assert.False(CommandBarRowShortcuts.HidesAppInFront(true, true, () => true));     // a hidden app comes back
    }

    [Fact]
    public void BringingAnAppForwardNeverReadsTheWindowList()
    {
        var read = false;
        Assert.False(CommandBarRowShortcuts.HidesAppInFront(false, false, () => { read = true; return true; }));
        Assert.False(read);
    }

    [Fact]
    public void AnAppWithAnIdIsListedByItNotByWhereItLives()
    {
        Assert.Equal("app.bundle.com.apple.mail", CommandBarRowShortcuts.AppKey("com.apple.mail", "/Applications/Mail.app"));
        Assert.Equal("app./Applications/Mail.app", CommandBarRowShortcuts.AppKey(null, "/Applications/Mail.app"));
    }

    [Fact]
    public void RemovingUninstalledAppKeysFreesTheirShortcutsAndKeepsOtherApps()
    {
        const string rowsKey = "app.bundle.com.rows.Rows";
        const string ghosttyKey = "app.bundle.com.ghostty.Ghostty";
        var bindings = CommandBarRowShortcuts.Setting(OptionB, rowsKey, NoBindings);
        bindings = CommandBarRowShortcuts.Setting(OptionN, "app.bundle.other", bindings);
        // Before cleanup, a new app assigning an occupied shortcut meets a conflict.
        Assert.Equal(CommandBarAssignmentIssue.Occupied(rowsKey), CommandBarRowShortcuts.AssignmentIssue(OptionB, ghosttyKey, bindings));
        var uninstalled = CommandBarRowShortcuts.ApplicationStableKeys(["com.rows.Rows"], ["/Applications/Rows.app"]);
        Assert.Contains(rowsKey, uninstalled);
        Assert.Contains("app./Applications/Rows.app", uninstalled);
        Assert.Contains("uninstall.bundle.com.rows.Rows", uninstalled);
        var cleaned = CommandBarRowShortcuts.Removing(uninstalled, bindings);
        Assert.False(cleaned.ContainsKey(rowsKey));
        Assert.Equal(OptionN, cleaned["app.bundle.other"]);
        Assert.Null(CommandBarRowShortcuts.AssignmentIssue(OptionB, ghosttyKey, cleaned));
    }

    [Fact]
    public void PendingAppLaunchRunsOnceAndOnlyWithItsOriginalBinding()
    {
        var bindings = new Dictionary<string, Shortcut> { ["app.bundle.a"] = OptionB, ["app.bundle.b"] = OptionN };
        var pending = new CommandBarRowShortcuts.PendingAppLaunch();

        pending.Schedule("app.bundle.a", bindings);
        Assert.Equal("app.bundle.a", pending.Take(bindings, isAvailable: true));
        Assert.Null(pending.Take(bindings, isAvailable: true));

        // The latest request replaces an earlier one while the catalog loads.
        pending.Schedule("app.bundle.a", bindings);
        pending.Schedule("app.bundle.b", bindings);
        Assert.Equal("app.bundle.b", pending.Take(bindings, isAvailable: true));

        // Removing or changing the shortcut while apps load cancels the launch.
        pending.Schedule("app.bundle.a", bindings);
        Assert.Null(pending.Take(NoBindings, isAvailable: true));
        pending.Schedule("app.bundle.a", bindings);
        Assert.Null(pending.Take(new Dictionary<string, Shortcut> { ["app.bundle.a"] = OptionN }, isAvailable: true));

        // Disabling the feature discards the deferred launch rather than postponing it.
        pending.Schedule("app.bundle.a", bindings);
        Assert.Null(pending.Take(bindings, isAvailable: false));
        Assert.Null(pending.Take(bindings, isAvailable: true));

        // Suspending shortcuts or running another command cancels a queued launch.
        pending.Schedule("app.bundle.a", bindings);
        pending.Cancel();
        Assert.Null(pending.Take(bindings, isAvailable: true));
    }

    // ---- App table sort ------------------------------------------------------------------------------------------

    private static readonly (string Key, string Title)[] AppRows =
        [("mail", "Mail"), ("app10", "App 10"), ("app2", "App 2"), ("safari", "Safari"), ("notes", "Notes")];

    private static List<string> Order(CommandBarAppSort.Column column, bool ascending = true)
    {
        var aliases = new Dictionary<string, string> { ["safari"] = "web", ["mail"] = "inbox", ["notes"] = "" };
        var shortcuts = new Dictionary<string, Shortcut> { ["notes"] = OptionN, ["mail"] = OptionB };
        var pins = new HashSet<string> { "safari", "app2" };
        return CommandBarAppSort.Sorted(AppRows, column, ascending, r => r.Title, r => r.Key, aliases, shortcuts, pins)
            .Select(r => r.Key).ToList();
    }

    [Fact]
    public void TheNameColumnKeepsTheNumericAwareOrderAndCanBeReversed()
    {
        Assert.Equal(["app2", "app10", "mail", "notes", "safari"], Order(CommandBarAppSort.Column.Name));
        Assert.Equal(["safari", "notes", "mail", "app10", "app2"], Order(CommandBarAppSort.Column.Name, ascending: false));
    }

    [Fact]
    public void AssignedShortcutsComeFirstAndUnassignedRowsFollowByName()
    {
        var byShortcut = Order(CommandBarAppSort.Column.Shortcut);
        Assert.Equal(new HashSet<string> { "mail", "notes" }, byShortcut.Take(2).ToHashSet());
        Assert.Equal(["app2", "app10", "safari"], byShortcut.TakeLast(3));
        var reversed = Order(CommandBarAppSort.Column.Shortcut, ascending: false);
        Assert.Equal(byShortcut.Take(2), reversed.Take(2).AsEnumerable().Reverse());
        Assert.Equal(["app2", "app10", "safari"], reversed.TakeLast(3));
    }

    [Fact]
    public void AliasesSortByTextAndAnEmptyAliasCountsAsNone()
    {
        Assert.Equal(["mail", "safari", "app2", "app10", "notes"], Order(CommandBarAppSort.Column.Alias));
        Assert.Equal(["safari", "mail", "app2", "app10", "notes"], Order(CommandBarAppSort.Column.Alias, ascending: false));
    }

    [Fact]
    public void PinnedRowsComeFirstEachGroupOrderedByName()
    {
        Assert.Equal(["app2", "safari", "app10", "mail", "notes"], Order(CommandBarAppSort.Column.Pinned));
        Assert.Equal(["app10", "mail", "notes", "app2", "safari"], Order(CommandBarAppSort.Column.Pinned, ascending: false));
    }

    [Fact]
    public void EqualShortcutsFallBackToTheNameInEitherDirection()
    {
        var same = new Shortcut('B', ShortcutModifiers.Win);
        var tied = CommandBarAppSort.Sorted(AppRows, CommandBarAppSort.Column.Shortcut, ascending: false, r => r.Title, r => r.Key,
            new Dictionary<string, string>(), new Dictionary<string, Shortcut> { ["safari"] = same, ["mail"] = same }, new HashSet<string>());
        Assert.Equal(["mail", "safari"], tied.Take(2).Select(r => r.Key));
    }
}
