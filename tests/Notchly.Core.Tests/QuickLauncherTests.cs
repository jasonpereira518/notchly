// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream Tests/QuickLauncherActionTests.swift (activation table and delays, presentation, composition,
// rail), the rail and grid cases of Tests/NotchTests.swift (railCapacity ... gridIndex, the Tools page sweeps that
// need only the pure functions), and the sanitizedPanelItemOrder cases of AppManagementFeatureTests and
// MenuPanelSectionGateTests.
using Notchly.Core.Features;
using Notchly.Core.Launcher;
using Notchly.Core.Notch;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class QuickLauncherTests
{
    /// <summary>The generated members are the real tile model; only the environment is replaced: no windows, taps or capture.</summary>
    private sealed class Host : IQuickLauncherHost
    {
        public List<string> Events { get; } = [];
        public List<(double Delay, Action Run)> Jobs { get; } = [];
        public HashSet<QuickLauncherItem> Unavailable { get; } = [];
        public bool CameraInNotch { get; set; }

        public void Hide() => Events.Add("hide");
        public void Schedule(double delaySeconds, Action action) => Jobs.Add((delaySeconds, action));
        public bool IsAvailable(QuickLauncherItem item) => !Unavailable.Contains(item);

        public bool Perform(QuickLauncherAction action)
        {
            if (action == QuickLauncherAction.CameraShowInIsland)
            {
                if (!CameraInNotch) return false;
                Events.Add(action.ContractName());
                return true;
            }
            Events.Add(action.ContractName());
            return true;
        }

        public void Drain()
        {
            var pending = Jobs.ToArray();
            Jobs.Clear();
            foreach (var job in pending) job.Run();
        }
    }

    private static (QuickLauncherModel Launcher, Host Host, SettingsStore Store) Make()
    {
        var host = new Host();
        var store = SettingsStore.InMemory();
        return (new QuickLauncherModel(host, store), host, store);
    }

    private static QuickLauncherKeyEvent Key(QuickLauncherKey key, bool composing = false) => new(key, Composing: composing);

    // MARK: Activation table

    public static TheoryData<QuickLauncherItem, AppFeature, string?, double?> Cases => new()
    {
        { QuickLauncherItem.KeepAwake, AppFeature.KeepAwake, "keepAwake.toggle", null },
        { QuickLauncherItem.MicMute, AppFeature.MicMute, "micMute.toggle", null },
        { QuickLauncherItem.ScreenOcr, AppFeature.ScreenOCR, "screenOCR.capture", 0.15 },
        { QuickLauncherItem.Screenshot, AppFeature.Screenshot, "screenshot.capture", 0.15 },
        { QuickLauncherItem.ScreenRecorder, AppFeature.ScreenRecorder, "recorder.toggle", 0.15 },
        { QuickLauncherItem.ColorPicker, AppFeature.ColorPicker, "colorPicker.pick", 0.15 },
        { QuickLauncherItem.CameraPreview, AppFeature.CameraPreview, "camera.show", 0.15 },
        { QuickLauncherItem.Scratchpad, AppFeature.Scratchpad, "scratchpad.show", 0.15 },
        { QuickLauncherItem.Clipboard, AppFeature.ClipboardHistory, "clipboard.showHistoryWindow", 0.1 },
        { QuickLauncherItem.Cleaning, AppFeature.CleaningMode, "cleaning.activate", 0.1 },
        { QuickLauncherItem.WindowLayout, AppFeature.WindowLayout, null, null },
        { QuickLauncherItem.Homebrew, AppFeature.Homebrew, null, null },
        { QuickLauncherItem.Media, AppFeature.MediaTools, null, null },
        { QuickLauncherItem.UrlCleaner, AppFeature.UrlCleaner, null, null },
        { QuickLauncherItem.Uninstaller, AppFeature.Uninstaller, null, null },
        { QuickLauncherItem.Cleaner, AppFeature.Cleaner, null, null },
        { QuickLauncherItem.Toggles, AppFeature.QuickToggles, null, null },
    };

    [Fact]
    public void EveryLauncherTileHasAnActivationContract() =>
        Assert.Equal(Cases.Select(c => (QuickLauncherItem)c[0]).ToHashSet(), QuickLauncherItems.All.ToHashSet());

    [Theory]
    [MemberData(nameof(Cases))]
    public void ATileActivatesAsItsContractSays(QuickLauncherItem item, AppFeature feature, string? action, double? delay)
    {
        var (launcher, host, _) = Make();
        launcher.Run(item);
        Assert.Equal(feature, item.Feature());
        if (delay is not null && action is not null)
        {
            Assert.Equal(["hide"], host.Events);
            Assert.Null(launcher.ActiveUtility);
            Assert.Single(host.Jobs);
            Assert.Equal(delay.Value, host.Jobs[0].Delay);
            host.Drain();
            Assert.Equal(["hide", action], host.Events);
        }
        else if (action is not null)
        {
            Assert.Equal([action], host.Events);
            Assert.Empty(host.Jobs);
            Assert.Null(launcher.ActiveUtility);
        }
        else
        {
            Assert.Empty(host.Events);
            Assert.Empty(host.Jobs);
            Assert.Equal(item, launcher.ActiveUtility);
        }
        host.Events.Clear();
        launcher.IsEditing = true;
        launcher.ActiveUtility = null;
        launcher.Run(item);
        host.Drain();
        Assert.Empty(host.Events);
        Assert.Null(launcher.ActiveUtility);
    }

    [Fact]
    public void TheTableAgreesWithTheContract()
    {
        foreach (var c in Cases)
        {
            var item = (QuickLauncherItem)c[0];
            var activation = item.Activation();
            Assert.Equal((string?)c[2], activation.Action?.ContractName());
            Assert.Equal((double?)c[3] ?? 0, activation.DelaySeconds);
            Assert.Equal(c[3] is not null, activation.HidesFirst);
            Assert.Equal(c[2] is null, activation.OpensUtility);
        }
    }

    [Fact]
    public void AnEmbeddedCameraSwitchesTheIslandBeforeHidingWithoutAReopenDelay()
    {
        var (launcher, host, _) = Make();
        host.CameraInNotch = true;
        launcher.Run(QuickLauncherItem.CameraPreview);
        Assert.Equal(["camera.notch", "hide"], host.Events);
        Assert.Empty(host.Jobs);
    }

    [Fact]
    public void StorageIdsAreUpstreamsRawValues()
    {
        Assert.Equal(["keepAwake", "cleaner", "toggles", "micMute", "screenOCR", "colorPicker", "clipboard", "windowLayout",
                      "cleaning", "homebrew", "media", "urlCleaner", "uninstaller", "screenshot", "screenRecorder",
                      "cameraPreview", "scratchpad"],
                     QuickLauncherItems.All.Select(i => i.Id()));
        Assert.All(QuickLauncherItems.All, i => Assert.Equal(i, QuickLauncherItems.FromId(i.Id())));
        Assert.Null(QuickLauncherItems.FromId("bad"));
        Assert.Equal(17, QuickLauncherItems.All.Count);
    }

    // MARK: Presentation

    [Fact]
    public void ANewPresentationResetsEditControlsAndSelectsTheFirstTile()
    {
        var (launcher, host, _) = Make();
        var old = launcher.PresentationId;
        launcher.IsEditing = true;
        launcher.EditingOptionsItem = QuickLauncherItem.Clipboard;
        launcher.PrepareForPresentation();
        Assert.NotEqual(old, launcher.PresentationId);
        Assert.False(launcher.IsEditing);
        Assert.Null(launcher.EditingOptionsItem);
        Assert.Equal(0, launcher.SelectedIndex);
        host.Events.Clear();
        Assert.True(launcher.HandlePanelKey(Key(QuickLauncherKey.Return)));
        Assert.Equal(["keepAwake.toggle"], host.Events);
        host.Events.Clear();
        Assert.True(launcher.HandlePanelKey(new QuickLauncherKeyEvent(QuickLauncherKeys.FromVirtualKey(0x61))));
        Assert.Equal(["keepAwake.toggle"], host.Events);
    }

    [Fact]
    public void KeypadAndTopRowDigitsAndEnterMapToTheSameKeys()
    {
        for (var digit = 1; digit <= 9; digit++)
        {
            Assert.Equal(QuickLauncherKeys.FromVirtualKey(0x30 + digit), QuickLauncherKeys.FromVirtualKey(0x60 + digit));
            Assert.Equal(digit - 1, QuickLauncherKeys.DigitIndex(QuickLauncherKeys.FromVirtualKey(0x60 + digit)));
        }
        Assert.Equal(QuickLauncherKey.Other, QuickLauncherKeys.FromVirtualKey(0x30));
        Assert.Equal(QuickLauncherKey.Other, QuickLauncherKeys.FromVirtualKey(0x60));
        Assert.Equal(QuickLauncherKey.Return, QuickLauncherKeys.FromVirtualKey(0x0D));
        Assert.Equal(QuickLauncherKey.Escape, QuickLauncherKeys.FromVirtualKey(0x1B));
        Assert.Equal([QuickLauncherKey.Left, QuickLauncherKey.Up, QuickLauncherKey.Right, QuickLauncherKey.Down],
                     new[] { 0x25, 0x26, 0x27, 0x28 }.Select(QuickLauncherKeys.FromVirtualKey));
    }

    [Theory]
    [InlineData(QuickLauncherItem.UrlCleaner)]
    [InlineData(QuickLauncherItem.Homebrew)]
    [InlineData(QuickLauncherItem.Uninstaller)]
    [InlineData(QuickLauncherItem.Media)]
    public void ARemovedFeatureTakesItsUtilityAndOptionsWithIt(QuickLauncherItem item)
    {
        var (launcher, host, _) = Make();
        launcher.ActiveUtility = item;
        launcher.PrepareForPresentation();
        Assert.Equal(item, launcher.ActiveUtility);
        host.Unavailable.Add(item);
        launcher.EditingOptionsItem = item;
        launcher.RefreshAvailability();
        Assert.Null(launcher.ActiveUtility);
        Assert.Null(launcher.EditingOptionsItem);
        launcher.ActiveUtility = item;
        launcher.PrepareForPresentation();
        Assert.Null(launcher.ActiveUtility);
        host.Events.Clear();
        launcher.Run(item);
        Assert.Null(launcher.ActiveUtility);
        Assert.Empty(host.Events);
    }

    [Fact]
    public void AnEmptyLauncherHasNoImaginaryInitialAction()
    {
        var (launcher, host, _) = Make();
        foreach (var item in QuickLauncherItems.All) host.Unavailable.Add(item);
        launcher.PrepareForPresentation();
        host.Events.Clear();
        Assert.Null(launcher.SelectedIndex);
        Assert.True(launcher.HandlePanelKey(Key(QuickLauncherKey.Return)));
        Assert.Empty(host.Events);
    }

    // MARK: Composition

    public static TheoryData<string> EscSteps => ["utility", "options", "edit", "launcher"];

    /// <summary>Each Esc step, reached while a utility's field holds an input method that is still composing: none may take the key from it.</summary>
    [Theory]
    [MemberData(nameof(EscSteps))]
    public void AComposingInputMethodKeepsEscFromEveryStep(string step)
    {
        var (launcher, host, _) = Make();
        switch (step)
        {
            case "utility": launcher.ActiveUtility = QuickLauncherItem.Homebrew; break;
            case "options": launcher.IsEditing = true; launcher.EditingOptionsItem = QuickLauncherItem.Clipboard; break;
            case "edit": launcher.IsEditing = true; break;
        }
        bool Closed() => step switch
        {
            "utility" => launcher.ActiveUtility is null,
            "options" => launcher.EditingOptionsItem is null && launcher.IsEditing,
            "edit" => !launcher.IsEditing,
            _ => host.Events.SequenceEqual(["hide"]),
        };
        host.Events.Clear();
        Assert.False(launcher.HandlePanelKey(Key(QuickLauncherKey.Escape, composing: true)));
        Assert.False(Closed());
        Assert.Empty(host.Events);
        Assert.True(launcher.HandlePanelKey(Key(QuickLauncherKey.Escape)));
        Assert.True(Closed());
    }

    [Fact]
    public void EscStepsOutOneLevelAtATime()
    {
        var (launcher, host, _) = Make();
        launcher.IsEditing = true;
        launcher.EditingOptionsItem = QuickLauncherItem.Clipboard;
        launcher.HandlePanelKey(Key(QuickLauncherKey.Escape));
        Assert.True(launcher.IsEditing && launcher.EditingOptionsItem is null);
        launcher.HandlePanelKey(Key(QuickLauncherKey.Escape));
        Assert.False(launcher.IsEditing);
        Assert.Empty(host.Events);
        launcher.HandlePanelKey(Key(QuickLauncherKey.Escape));
        Assert.Equal(["hide"], host.Events);
    }

    [Fact]
    public void KeysPassThroughWithModifiersEditModeOrAHostedUtility()
    {
        var (launcher, _, _) = Make();
        launcher.PrepareForPresentation();
        Assert.False(launcher.HandlePanelKey(new QuickLauncherKeyEvent(QuickLauncherKey.Return, QuickLauncherModifiers.Control)));
        Assert.False(launcher.HandlePanelKey(new QuickLauncherKeyEvent(QuickLauncherKey.Digit1, QuickLauncherModifiers.Alt)));
        Assert.False(launcher.HandlePanelKey(new QuickLauncherKeyEvent(QuickLauncherKey.Down, QuickLauncherModifiers.Windows)));
        Assert.True(launcher.HandlePanelKey(new QuickLauncherKeyEvent(QuickLauncherKey.Digit2, QuickLauncherModifiers.Shift)));
        Assert.Equal(QuickLauncherItem.Cleaner, launcher.ActiveUtility);
        launcher.ActiveUtility = null;
        Assert.False(launcher.HandlePanelKey(Key(QuickLauncherKey.Other)));
        launcher.IsEditing = true;
        Assert.False(launcher.HandlePanelKey(Key(QuickLauncherKey.Return)));
        launcher.IsEditing = false;
        launcher.ActiveUtility = QuickLauncherItem.Homebrew;
        Assert.False(launcher.HandlePanelKey(Key(QuickLauncherKey.Down)));
        Assert.False(launcher.DismissesOnOutsideInteraction);
    }

    // MARK: Rail selection

    /// <summary>
    /// Hover and the arrows both select, but only the arrows move the island's rail: scrolling to a hovered tile
    /// slid the next one under the pointer, and the rail kept going.
    /// </summary>
    [Fact]
    public void OnlyTheArrowsMoveTheRail()
    {
        var (launcher, _, _) = Make();
        launcher.PrepareForPresentation();
        Assert.Equal(0, launcher.KeyboardIndex);
        launcher.Select(launcher.VisibleItems[3]);
        Assert.Equal(3, launcher.SelectedIndex);
        Assert.Null(launcher.KeyboardIndex);
        Assert.True(launcher.HandlePanelKey(Key(QuickLauncherKey.Right), GridFlow.ByColumns(2)));
        Assert.Equal(5, launcher.SelectedIndex);
        Assert.Equal(5, launcher.KeyboardIndex);
    }

    // MARK: Items, order and hiding

    [Fact]
    public void HiddenTilesLeaveTheGridButStayInTheHiddenList()
    {
        var (launcher, host, store) = Make();
        launcher.SetHidden(QuickLauncherItem.Cleaner, true);
        Assert.Equal("cleaner", store.GetString(DefaultsKey.QuickLauncherHiddenItems));
        Assert.DoesNotContain(QuickLauncherItem.Cleaner, launcher.VisibleItems);
        Assert.Equal([QuickLauncherItem.Cleaner], launcher.HiddenItems);
        host.Unavailable.Add(QuickLauncherItem.Cleaner);
        Assert.Empty(launcher.HiddenItems);
        host.Unavailable.Clear();
        launcher.SetHidden(QuickLauncherItem.Cleaner, false);
        Assert.Equal(QuickLauncherItems.All, launcher.VisibleItems);
        launcher.SetHidden(QuickLauncherItem.Toggles, true);
        launcher.SetHidden(QuickLauncherItem.Cleaning, true);
        Assert.Equal("cleaning,toggles", launcher.HiddenItemsRaw);
    }

    [Fact]
    public void HidingTheTileWhoseOptionsAreOpenClosesThem()
    {
        var (launcher, _, _) = Make();
        launcher.EditingOptionsItem = QuickLauncherItem.Clipboard;
        launcher.SetHidden(QuickLauncherItem.KeepAwake, true);
        Assert.Equal(QuickLauncherItem.Clipboard, launcher.EditingOptionsItem);
        launcher.SetHidden(QuickLauncherItem.Clipboard, true);
        Assert.Null(launcher.EditingOptionsItem);
    }

    [Fact]
    public void HidingTilesClampsTheSelection()
    {
        var (launcher, _, _) = Make();
        launcher.PrepareForPresentation();
        launcher.Select(QuickLauncherItems.All[16]);
        Assert.Equal(16, launcher.SelectedIndex);
        launcher.SetHidden(QuickLauncherItem.Scratchpad, true);
        Assert.Equal(15, launcher.SelectedIndex);
    }

    [Fact]
    public void ASavedOrderIsKeptAndANewTileJoinsAtTheEnd()
    {
        var (launcher, _, store) = Make();
        launcher.SetItemOrder([QuickLauncherItem.Scratchpad, QuickLauncherItem.KeepAwake]);
        Assert.Equal("scratchpad,keepAwake", store.GetString(DefaultsKey.QuickLauncherItemOrder));
        Assert.Equal([QuickLauncherItem.Scratchpad, QuickLauncherItem.KeepAwake], launcher.VisibleItems.Take(2));
        Assert.Equal(17, launcher.VisibleItems.Count);
        store.Set(DefaultsKey.QuickLauncherItemOrder, "media,nope,media,cleaner");
        Assert.Equal([QuickLauncherItem.Media, QuickLauncherItem.Cleaner, QuickLauncherItem.KeepAwake],
                     launcher.VisibleItems.Take(3));
        Assert.Equal(17, launcher.VisibleItems.Distinct().Count());
    }

    // MARK: Item order sanitiser

    [Fact]
    public void PanelItemOrderKeepsSavedValidItemsFirstAndAppendsDefaults() =>
        Assert.Equal(["uninstaller", "homebrew", "media", "cleanURL", "cleaning"],
                     QuickToolsSupport.SanitizedPanelItemOrder("uninstaller,homebrew,homebrew,bad",
                                                               ["homebrew", "media", "uninstaller", "cleanURL", "cleaning"]));

    [Fact]
    public void ExistingCustomOrdersGainANewItemWithoutLosingTheirOrder()
    {
        string[] defaults = ["a", "b", "c", "smoothScroll", "d"];
        string[] saved = [.. defaults.Where(d => d != "smoothScroll")];
        Assert.Equal([.. saved, "smoothScroll"], QuickToolsSupport.SanitizedPanelItemOrder(string.Join(",", saved), defaults));
        Assert.Equal(defaults, QuickToolsSupport.SanitizedPanelItemOrder("", defaults));
        Assert.Equal(defaults, QuickToolsSupport.SanitizedPanelItemOrder(null, defaults));
    }

    [Fact]
    public void HiddenIdsRoundTripSortedAndDropEmptyEntries()
    {
        Assert.Equal(new HashSet<string> { "b", "a" }, QuickToolsSupport.HiddenIds("b,,a,"));
        Assert.Empty(QuickToolsSupport.HiddenIds(""));
        Assert.Equal("a,b", QuickToolsSupport.SerializeHiddenIds(new HashSet<string> { "b", "a" }));
        Assert.Equal("", QuickToolsSupport.SerializeHiddenIds([]));
    }

    // MARK: Grid and rail (NotchTests)

    [Fact]
    public void ARailFitsWholeColumnsOfItsWidthAndNeverFewerThanOne()
    {
        Assert.Equal(5, RailLayout.Capacity(424, 76, 8));
        Assert.Equal(3, RailLayout.Capacity(304, 76, 8));
        Assert.Equal(1, RailLayout.Capacity(20, 76, 8));
        Assert.Equal(1, RailLayout.Capacity(double.NaN, 76, 8));
    }

    [Fact]
    public void RowsFollowTheItemsThatNeedThemAndStopWhereTheHeightEnds()
    {
        Assert.Equal(1, RailLayout.Rows(4, 5, 74, 8, 180));
        Assert.Equal(1, RailLayout.Rows(7, 5, 74, 8, 106));
        Assert.Equal(2, RailLayout.Rows(7, 5, 74, 8, 190));
        Assert.Equal(1, RailLayout.Rows(0, 0, 74, 8, 0));
        Assert.Equal(8, RailLayout.Rows(40, 5, 74, 8, double.PositiveInfinity));
    }

    [Fact]
    public void ARailsHeightIsItsRowsAndTheGapsBetweenThem()
    {
        Assert.Equal(74, RailLayout.Height(1, 74, 8));
        Assert.Equal(238, RailLayout.Height(3, 74, 8));
        Assert.Equal(74, RailLayout.Height(0, 74, 8));
    }

    [Fact]
    public void ARailSpreadsItsItemsOverTheFewestColumnsItsRowsAllowAndFitsOnceTheyAllDo()
    {
        Assert.Equal(6, RailLayout.Columns(11, 2));
        Assert.Equal(4, RailLayout.Columns(8, 2));
        Assert.Equal(0, RailLayout.Columns(0, 0));
        Assert.True(RailLayout.Fits(6, 76, 8, 496));
        Assert.False(RailLayout.Fits(6, 76, 8, 495));
    }

    [Fact]
    public void TheLaunchersArrowsReadAcrossTheRowsThatFitAndFollowTheColumnsOnceTheRailScrolls()
    {
        var compact = new NotchGeometry(new RectD(0, 0, 1440, 900), layout: NotchSize.Compact);
        Assert.Equal(424, compact.ContentWidth);
        Assert.Equal(GridFlow.ByRows(4), compact.ToolFlow(8));
        Assert.Equal(GridFlow.ByColumns(2), compact.ToolFlow(12));
        Assert.Equal(2, compact.ToolRows(8));
    }

    [Fact]
    public void InOneRowTheSideArrowsWalkTheTilesAndTheVerticalPairStaysPut()
    {
        GridDirection[] directions = [GridDirection.Left, GridDirection.Right, GridDirection.Up, GridDirection.Down, GridDirection.Left];
        Assert.Equal([1, 3, 2, 2, 1],
                     directions.Select(d => QuickToolsSupport.GridIndex(2, 5, GridFlow.ByColumns(1), d)));
    }

    [Fact]
    public void TheRowFlowKeepsThePanelGridsOwnWalk()
    {
        Assert.Equal(5, QuickToolsSupport.GridIndex(2, 7, GridFlow.ByRows(3), GridDirection.Down));
        Assert.Equal(2, QuickToolsSupport.GridIndex(2, 7, GridFlow.ByRows(3), GridDirection.Right));
        Assert.Equal(3, QuickToolsSupport.GridIndex(3, 7, QuickLauncherLayout.Columns, GridDirection.Left));
        Assert.Equal(0, QuickToolsSupport.GridIndex(0, 0, 3, GridDirection.Down));
    }

    [Fact]
    public void WithTwoRowsTheArrowsFollowTheColumnsTheTilesFillClampedToTheLastTile()
    {
        static int Go(int from, GridDirection d) => QuickToolsSupport.GridIndex(from, 7, GridFlow.ByColumns(2), d);
        Assert.Equal(4, Go(2, GridDirection.Right));
        Assert.Equal(0, Go(2, GridDirection.Left));
        Assert.Equal(3, Go(2, GridDirection.Down));
        Assert.Equal(3, Go(3, GridDirection.Down));
        Assert.Equal(2, Go(3, GridDirection.Up));
        Assert.Equal(6, Go(6, GridDirection.Down));
        Assert.Equal(5, Go(5, GridDirection.Right));
        Assert.Equal(6, Go(9, GridDirection.Up));
    }

    [Fact]
    public void TheLayoutConstantsAreUpstreams()
    {
        Assert.Equal(3, QuickLauncherLayout.Columns);
        Assert.Equal(420, QuickLauncherLayout.PanelWidth);
        Assert.Equal(16, QuickLauncherLayout.Padding);
        Assert.Equal(10, QuickLauncherLayout.Spacing);
        Assert.Equal((76, 72, 6), (QuickLauncherLayout.RailTileWidth, QuickLauncherLayout.RailTileHeight, QuickLauncherLayout.RailSpacing));
        Assert.Equal(5, QuickLauncherLayout.EditColumns);
    }

    // The Tools page sweeps, over every allowed island size.

    [Fact]
    public void TheToolsRailClosesItsGapsBeforeItScrollsAndTheArrowsWalkWhatItDraws()
    {
        const double tile = QuickLauncherLayout.RailTileWidth, spacing = QuickLauncherLayout.RailSpacing;
        var screen = new RectD(0, 0, 1440, 900);
        var sizes = new List<(NotchSize Size, double Width, double Height)>
        {
            (NotchSize.Compact, NotchSizes.DefaultWidth, NotchSizes.DefaultHeight),
            (NotchSize.Spacious, NotchSizes.DefaultWidth, NotchSizes.DefaultHeight),
        };
        double[] heights = [NotchSizes.MinHeight, 280, 300, 340, 400, NotchSizes.DefaultHeight, NotchSizes.MaxHeight];
        for (var width = NotchSizes.MinWidth; width <= NotchSizes.MaxWidth; width += 5)
            foreach (var height in heights) sizes.Add((NotchSize.Custom, width, height));

        var tools = QuickLauncherItems.All.Count;
        var closed = 0;
        foreach (var silhouette in new[] { NotchSilhouette.Capsule, NotchSilhouette.Notch })
        foreach (var (size, width, height) in sizes)
        {
            var geometry = new NotchGeometry(screen, layout: size, customWidth: width, customHeight: height, silhouette: silhouette);
            var content = geometry.ContentWidth;
            for (var count = 1; count <= tools; count++)
            {
                var rows = geometry.ToolRows(count);
                var columns = RailLayout.Columns(count, rows);
                var gap = RailLayout.Gap(columns, tile, spacing, QuickLauncherLayout.RailLeastSpacing, content);
                var inset = RailLayout.ToolTitleInset(count, rows, content);
                var label = $"{silhouette} {size} {width}x{height}, {count} tools, {columns} across {content}";
                // Tiles never scroll while they fit side by side.
                if (gap is null) Assert.True(columns * tile > content, label);
                // A rail that fits with its gaps, or scrolls, keeps its gaps and its titles' full width.
                if (RailLayout.Fits(columns, tile, spacing, content)) { Assert.Equal((double?)spacing, gap); Assert.Equal(0d, inset); }
                if (gap is null) Assert.Equal(0d, inset);
                if (gap is { } g)
                {
                    if (g < spacing) closed++;
                    Assert.True(g >= QuickLauncherLayout.RailLeastSpacing && columns * tile + (columns - 1) * g <= content + 0.001, label);
                    // Titles in a rail that closed its gaps stay as far apart as its spacing keeps them.
                    Assert.Equal(spacing, g + inset * 2, 3);
                }
                // The arrows walk rows exactly when the rail draws rows.
                GridFlow drawn = gap is null ? GridFlow.ByColumns(rows) : GridFlow.ByRows(columns);
                Assert.Equal(drawn, geometry.ToolFlow(count));
            }
        }
        Assert.True(closed > 0);
    }

    [Fact]
    public void AtTheNarrowestWidthEightToolsSitInTwoRowsOfFourWithGapsClosedAndTitlesInset()
    {
        const double content = 304, tile = QuickLauncherLayout.RailTileWidth, spacing = QuickLauncherLayout.RailSpacing;
        var rows = RailLayout.ToolRows(8, content, 160);
        Assert.Equal(2, rows);
        Assert.Equal((double?)0, RailLayout.Gap(4, tile, spacing, QuickLauncherLayout.RailLeastSpacing, content));
        Assert.Equal(spacing / 2, RailLayout.ToolTitleInset(8, rows, content));
        Assert.Equal(GridFlow.ByRows(4), RailLayout.ToolFlow(8, content, 160));
    }

    [Fact]
    public void ARailThatWouldJustMissTheWidthClosesItsGapsEvenly()
    {
        // 6 x 76 + 5 x 6 = 486 does not fit 480, but 456 does: the gap shrinks to what is left.
        Assert.Equal((double?)(24d / 5), RailLayout.Gap(6, 76, 6, 0, 480));
        Assert.Null(RailLayout.Gap(6, 76, 6, 0, 455));
        Assert.Null(RailLayout.Gap(1, 76, 6, 0, 70));
        Assert.Equal((double?)6, RailLayout.Gap(5, 76, 6, 0, 500));
    }
}
