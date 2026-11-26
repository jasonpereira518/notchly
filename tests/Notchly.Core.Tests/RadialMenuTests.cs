// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream UtilitiesFeatureTests (radial menu, about lines 733-1190), ScreenshotFeatureTests (defaults, activation mode,
// mouse trigger, profile deletion contract, about lines 4198-4357) and PointerInputFeatureTests (profile triggers, about lines
// 1697-1741), without the trackpad tap, the Now Playing card or the Accessibility checks, which have no Windows counterpart.
using System.Text;
using Notchly.Core.Features;
using Notchly.Core.Radial;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Windowing;

namespace Notchly.Core.Tests;

public class RadialMenuTests
{
    private sealed class FakeSettings : IRadialSettings
    {
        public Dictionary<string, object> Values { get; } = [];
        public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v as string : null;
        public bool GetBool(string key) => Values.TryGetValue(key, out var v) && v is true;
    }

    private static RadialMenuItem Item(RadialMenuKind kind, string payload = "", string name = "") =>
        RadialMenuItem.Create(kind, payload, name);

    private static void AssertButtons(IReadOnlySet<int> actual, params int[] expected) =>
        Assert.True(actual.SetEquals(expected), "claimed: " + string.Join(",", actual));

    private static string RadialAvailabilityKey => FeatureCatalog.Info(AppFeature.RadialMenu).AvailabilityKey;

    private static FakeSettings EnabledSettings() => new()
    {
        Values = { [RadialAvailabilityKey] = true, [DefaultsKey.RadialMenuEnabled] = true },
    };

    // MARK: Geometry

    [Fact]
    public void WheelAnglesRunClockwiseFromTwelveOClock()
    {
        Assert.Equal(0, RadialMenuGeometry.Angle(0, 1));
        Assert.Equal(Math.PI / 2, RadialMenuGeometry.Angle(1, 0), 4);
        Assert.Equal(Math.PI, RadialMenuGeometry.Angle(0, -1), 4);
        Assert.Equal(3 * Math.PI / 2, RadialMenuGeometry.Angle(-1, 0), 4);
    }

    [Fact]
    public void EachSliceClaimsTheArcAroundItsOwnCenter()
    {
        Assert.Equal(0, RadialMenuGeometry.Index(0, 4));
        Assert.Equal(1, RadialMenuGeometry.Index(Math.PI / 2, 4));
        Assert.Equal(2, RadialMenuGeometry.Index(Math.PI, 4));
        Assert.Equal(3, RadialMenuGeometry.Index(3 * Math.PI / 2, 4));
    }

    [Fact]
    public void TheTopSliceClaimsBothSidesOfTwelveOClock()
    {
        Assert.Equal(0, RadialMenuGeometry.Index(2 * Math.PI - 0.01, 12));
        Assert.Equal(11, RadialMenuGeometry.Index(2 * Math.PI - 0.3, 12));
    }

    [Fact]
    public void AnEmptyWheelHighlightsNothing()
    {
        Assert.Null(RadialMenuGeometry.Index(1, 0));
        Assert.Null(RadialMenuGeometry.HighlightedIndex(100, 100, 40, 0));
    }

    [Fact]
    public void TheHubDeadZoneHighlightsNothingAndPastItThePointerPicksASlice()
    {
        Assert.Null(RadialMenuGeometry.HighlightedIndex(10, 0, 40, 4));
        Assert.Equal(1, RadialMenuGeometry.HighlightedIndex(50, 0, 40, 4));
    }

    [Fact]
    public void SliceCentersLandOnTheUnitCircleFromTheTopClockwise()
    {
        var top = RadialMenuGeometry.UnitPosition(0, 6);
        var right = RadialMenuGeometry.UnitPosition(1, 4);
        Assert.True(Math.Abs(top.Dx) < 0.0001 && Math.Abs(top.DyUp - 1) < 0.0001);
        Assert.True(Math.Abs(right.Dx - 1) < 0.0001 && Math.Abs(right.DyUp) < 0.0001);
        Assert.Equal((0.0, 1.0), RadialMenuGeometry.UnitPosition(0, 0));
    }

    [Fact]
    public void TheLayoutConstantsAreUpstreams()
    {
        Assert.Equal(400, RadialMenuLayout.PanelSize);
        Assert.Equal(300, RadialMenuLayout.WheelDiameter);
        Assert.Equal(112, RadialMenuLayout.RingRadius);
        Assert.Equal(52, RadialMenuLayout.ChipSize);
        Assert.Equal(76, RadialMenuLayout.HubDiameter);
        Assert.Equal(40, RadialMenuLayout.DeadZoneRadius);
        Assert.Equal(8, RadialMenuLayout.MoveActivationDistance);
        Assert.Equal(12, RadialMenuSupport.MaxItemsPerWheel);
        Assert.Equal(2, RadialMenuSupport.MaxDepth);
    }

    // MARK: Items

    [Fact]
    public void TheStarterWheelIsAlreadyCleanAndEverySliceHasAGlyph()
    {
        var starter = RadialMenuSupport.StarterItems;
        Assert.Equal(6, starter.Count);
        Assert.Equal(starter, RadialMenuSupport.Sanitized(starter));
        Assert.All(starter, i => Assert.False(string.IsNullOrEmpty(RadialMenuGlyphs.EffectiveGlyph(i))));
    }

    [Fact]
    public void TheEditorOffersABroadDuplicateFreeGlyphCatalog()
    {
        var choices = RadialMenuGlyphs.Choices;
        Assert.True(choices.Count >= 80);
        Assert.Equal(choices.Count, choices.Select(c => c.Name).Distinct().Count());
        Assert.Equal(choices.Count, choices.Select(c => c.Glyph).Distinct().Count());
        Assert.All(choices, c => Assert.True(c.Glyph.Length == 1 && c.Glyph[0] is >= '\uE000' and <= '\uF8FF'));
        Assert.Contains(choices, c => c.Name == "volume");
        Assert.Contains(choices, c => c.Name == "share");
    }

    [Fact]
    public void AFreshInstallDecodesToTheStarterWheelAndAnEmptiedWheelStaysEmpty()
    {
        Assert.Equal(RadialMenuSupport.StarterItems, RadialMenuSupport.Decode(null));
        Assert.Equal(RadialMenuSupport.StarterItems, RadialMenuSupport.Decode(""));
        Assert.Empty(RadialMenuSupport.Decode(RadialMenuSupport.Encode([])));
        Assert.Empty(RadialMenuSupport.Decode("not json"));
    }

    private static List<RadialMenuItem> SampleWheel() =>
    [
        Item(RadialMenuKind.App, @"C:\Program Files\Editor\Editor.exe", "  Editor  "),
        Item(RadialMenuKind.Url, "example.com/page"),
        Item(RadialMenuKind.Shortcut, "control+alt+win:49"),
        new RadialMenuItem
        {
            Kind = RadialMenuKind.Submenu, Name = "More",
            Children =
            [
                Item(RadialMenuKind.Media, "playPause"),
                new RadialMenuItem { Kind = RadialMenuKind.Submenu, Children = [Item(RadialMenuKind.Media, "nextTrack")] },
            ],
        },
    ];

    [Fact]
    public void SanitizingTrimsNamesAndCompletesBareLinks()
    {
        var cleaned = RadialMenuSupport.Sanitized(SampleWheel());
        Assert.Equal(4, cleaned.Count);
        Assert.Equal("Editor", cleaned[0].Name);
        Assert.Equal("https://example.com/page", cleaned[1].Payload);
    }

    [Fact]
    public void SubmenusKeepTheirActionsButNeverNestAnotherSubmenu()
    {
        var cleaned = RadialMenuSupport.Sanitized(SampleWheel());
        Assert.Single(cleaned[3].Children);
        Assert.Equal(RadialMenuKind.Media, cleaned[3].Children[0].Kind);
    }

    [Fact]
    public void ItemsRoundTripThroughPersistence()
    {
        var cleaned = RadialMenuSupport.Sanitized(SampleWheel());
        Assert.Equal(cleaned, RadialMenuSupport.Decode(RadialMenuSupport.Encode(cleaned)));
    }

    [Fact]
    public void ASubmenuKeepsEveryActionThroughPersistenceNotJustTwo()
    {
        var full = new[]
        {
            new RadialMenuItem
            {
                Kind = RadialMenuKind.Submenu, Name = "Pack",
                Children = Enumerable.Range(0, 5).Select(i => Item(RadialMenuKind.Url, $"example.com/{i}")).ToList(),
            },
        };
        Assert.Equal(5, RadialMenuSupport.Decode(RadialMenuSupport.Encode(full))[0].Children.Count);
    }

    [Fact]
    public void SlicesThatCannotRunAreDroppedInsteadOfRenderingDead()
    {
        Assert.Empty(RadialMenuSupport.Sanitized(
        [
            Item(RadialMenuKind.App, ""),
            Item(RadialMenuKind.Url, "not a link"),
            Item(RadialMenuKind.Shortcut, "garbage"),
            Item(RadialMenuKind.Tool, "unknownTool"),
            Item(RadialMenuKind.WindowLayout, "unknownLayout"),
            Item(RadialMenuKind.Media, "unknownKey"),
        ]));
    }

    [Fact]
    public void AWheelNeverHoldsMoreThanTwelveSlices() =>
        Assert.Equal(12, RadialMenuSupport.Sanitized(
            Enumerable.Range(0, 20).Select(i => Item(RadialMenuKind.Url, $"example.com/{i}")).ToList()).Count);

    [Fact]
    public void UnknownKindsFromNewerVersionsDropJustThatSlice()
    {
        const string json = """[{"kind":"media","payload":"playPause"},{"kind":"teleport","payload":"x"}]""";
        Assert.Single(RadialMenuSupport.Decode(json));
    }

    [Fact]
    public void ABrokenItemIsDroppedAloneAndAMissingIdGetsANewOne()
    {
        const string json = """
            [{"kind":"media","payload":"playPause"},
             {"kind":"url","payload":5},
             "text",
             {"payload":"nextTrack"},
             {"id":"nope","kind":"media","payload":"nextTrack"},
             {"kind":"file","payload":"~/Downloads","children":null,"name":null,"customIconData":null},
             {"kind":"submenu","children":[{"kind":"bogus"},{"kind":"url","payload":"example.com"}]}]
            """;
        var items = RadialMenuSupport.Decode(json);
        Assert.Equal(3, items.Count);
        Assert.Equal("playPause", items[0].Payload);
        Assert.NotEqual(Guid.Empty, items[0].Id);
        Assert.Equal("~/Downloads", items[1].Payload);
        Assert.Single(items[2].Children);
    }

    [Fact]
    public void ADuplicateItemIdIsDropped()
    {
        var id = Guid.NewGuid();
        var items = RadialMenuSupport.Sanitized(
        [
            new RadialMenuItem { Id = id, Kind = RadialMenuKind.Url, Payload = "a.example" },
            new RadialMenuItem { Id = id, Kind = RadialMenuKind.Url, Payload = "b.example" },
        ]);
        Assert.Single(items);
    }

    [Fact]
    public void CustomIconsAreKeptOnlyWhenSmallEnoughAndImageShaped()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var kept = RadialMenuSupport.Sanitized([Item(RadialMenuKind.Url, "a.example") with { CustomIconData = png }]);
        Assert.Equal(png, kept[0].CustomIconData);
        var junk = RadialMenuSupport.Sanitized([Item(RadialMenuKind.Url, "a.example") with { CustomIconData = [1, 2, 3] }]);
        Assert.Null(junk[0].CustomIconData);
        var huge = new byte[RadialMenuSupport.MaxStoredIconBytes + 1];
        huge[0] = 0x89; huge[1] = 0x50; huge[2] = 0x4E; huge[3] = 0x47;
        Assert.Null(RadialMenuSupport.Sanitized([Item(RadialMenuKind.Url, "a.example") with { CustomIconData = huge }])[0].CustomIconData);
        // The icon survives a save and load as base64.
        var round = RadialMenuSupport.Decode(RadialMenuSupport.Encode(kept));
        Assert.Equal(png, round[0].CustomIconData);
    }

    [Fact]
    public void NamesAreCappedAtSixtyCharacters()
    {
        var item = RadialMenuSupport.Sanitized([Item(RadialMenuKind.Url, "a.example", new string('x', 100))])[0];
        Assert.Equal(60, item.Name.Length);
    }

    // MARK: Links

    [Fact]
    public void LinkNormalizationKeepsSchemesAndRejectsNonLinks()
    {
        Assert.Equal("https://a.example/x", RadialMenuSupport.NormalizedUrl("https://a.example/x"));
        Assert.Equal("mailto:someone@example.com", RadialMenuSupport.NormalizedUrl("mailto:someone@example.com"));
        Assert.Null(RadialMenuSupport.NormalizedUrl("   "));
        Assert.Null(RadialMenuSupport.NormalizedUrl("two words"));
    }

    [Fact]
    public void DigitsAfterTheColonMeanAPortOnlyWhenThePrefixLooksLikeAHost()
    {
        Assert.Equal("tel:5551234", RadialMenuSupport.NormalizedUrl("tel:5551234"));
        Assert.Equal("https://example.com:8080/x", RadialMenuSupport.NormalizedUrl("example.com:8080/x"));
        Assert.Equal("https://localhost:3000", RadialMenuSupport.NormalizedUrl("localhost:3000"));
    }

    [Fact]
    public void AWindowsPathIsNotALink() => Assert.Null(RadialMenuSupport.NormalizedUrl(@"C:\Users\me\file.txt"));

    // MARK: Window layout, quick toggles, tools, media

    [Fact]
    public void WindowLayoutSlicesKeepAValidPlacementAndItsAutomaticGlyph()
    {
        var layout = Item(RadialMenuKind.WindowLayout, "leftThird");
        Assert.Equal([layout], RadialMenuSupport.Sanitized([layout]));
        Assert.Equal(WindowLayoutAction.LeftThird, layout.WindowLayoutAction);
        Assert.Equal(RadialMenuGlyphs.LayoutGlyph(WindowLayoutAction.LeftThird), RadialMenuGlyphs.EffectiveGlyph(layout));
        Assert.All(Enum.GetValues<WindowLayoutAction>(), a => Assert.False(string.IsNullOrEmpty(RadialMenuGlyphs.LayoutGlyph(a))));
        Assert.True(RadialMenuSupport.UsesWindowLayout([new RadialMenuItem { Kind = RadialMenuKind.Submenu, Children = [layout] }]));
        Assert.False(RadialMenuSupport.UsesWindowLayout(RadialMenuSupport.StarterItems));
    }

    [Fact]
    public void MediaSlicesAreThePlayPauseAndTrackKeysOnly()
    {
        Assert.Equal(["playPause", "previousTrack", "nextTrack"], RadialMenuMedia.Ids);
        Assert.Equal(0xB3, RadialMenuMedia.VirtualKey("playPause"));
        Assert.Equal(0xB1, RadialMenuMedia.VirtualKey("previousTrack"));
        Assert.Equal(0xB0, RadialMenuMedia.VirtualKey("nextTrack"));
        // Windows: upstream's Now Playing card is not ported, so a stored one decodes to nothing.
        Assert.Null(RadialMenuMedia.VirtualKey("nowPlaying"));
        Assert.Empty(RadialMenuSupport.Decode("""[{"kind":"media","payload":"nowPlaying"}]"""));
    }

    [Fact]
    public void QuickToggleSlicesKeepAValidActionAndAnAutomaticGlyph()
    {
        var toggle = Item(RadialMenuKind.QuickToggle, "darkMode");
        Assert.Equal([toggle], RadialMenuSupport.Sanitized([toggle]));
        Assert.Equal("darkMode", toggle.QuickToggle);
        Assert.All(RadialMenuToggles.Ids, id => Assert.False(string.IsNullOrEmpty(RadialMenuGlyphs.ToggleGlyph(id))));
        Assert.Empty(RadialMenuSupport.Sanitized([Item(RadialMenuKind.QuickToggle, "unknownAction")]));
    }

    [Fact]
    public void QuickToggleIdsAreUpstreamsNineAndTheWindowsOnes()
    {
        Assert.Equal(
            ["darkMode", "emptyTrash", "quitAllApps", "ejectDisks", "hiddenFiles", "desktopIcons", "lockScreen", "displayOff", "screenSaver"],
            RadialMenuToggles.UpstreamIds);
        Assert.Equal(13, RadialMenuToggles.Ids.Count);
        Assert.Equal(13, RadialMenuToggles.Ids.Distinct().Count());
        foreach (var id in new[] { "micMute", "refreshRate", "wifi", "bluetooth" })
            Assert.Contains(id, RadialMenuToggles.Ids);
        Assert.Equal(AppFeature.QuitApps, RadialMenuToggles.Feature("quitAllApps"));
        Assert.Equal(AppFeature.QuickToggles, RadialMenuToggles.Feature("darkMode"));
    }

    [Fact]
    public void EveryQuickToggleOfTheWindowsPanelCanBeAWheelSlice()
    {
        // The ids QuickTogglesService (Notchly.App) lists, from its source.
        string[] panel = ["darkMode", "micMute", "emptyTrash", "quitAllApps", "ejectDisks", "hiddenFiles", "desktopIcons",
                          "lockScreen", "displayOff", "screenSaver", "refreshRate", "wifi", "bluetooth"];
        Assert.Equal(panel.Order(), RadialMenuToggles.Ids.Order());
    }

    [Fact]
    public void EveryWheelToolMapsToARealFeatureAndGlyph()
    {
        Assert.Equal(15, RadialMenuTools.Ids.Count);
        Assert.All(RadialMenuTools.Ids, id =>
        {
            Assert.NotNull(RadialMenuTools.ToolFeature(id));
            Assert.False(string.IsNullOrEmpty(RadialMenuGlyphs.DefaultGlyph(RadialMenuKind.Tool, id)));
        });
        Assert.Equal(AppFeature.Screenshot, RadialMenuTools.ToolFeature("screenshot"));
        Assert.Equal(AppFeature.ScreenRecorder, RadialMenuTools.ToolFeature("screenRecorder"));
        Assert.Equal(AppFeature.ScreenOCR, RadialMenuTools.ToolFeature("screenOCR"));
        Assert.Equal(AppFeature.ClipboardHistory, RadialMenuTools.ToolFeature("clipboardHistory"));
        Assert.Equal(AppFeature.Scratchpad, RadialMenuTools.ToolFeature("scratchpad"));
        Assert.Equal(AppFeature.Shelf, RadialMenuTools.ToolFeature("shelf"));
        Assert.Equal(AppFeature.Cleaner, RadialMenuTools.ToolFeature("cleaner"));
        Assert.Equal(AppFeature.Uninstaller, RadialMenuTools.ToolFeature("uninstaller"));
        Assert.Equal(AppFeature.AppUpdates, RadialMenuTools.ToolFeature("appUpdates"));
        Assert.Equal(AppFeature.CleaningMode, RadialMenuTools.ToolFeature("cleaningMode"));
        Assert.Equal(AppFeature.KeepAwake, RadialMenuTools.ToolFeature("keepAwake"));
        Assert.Null(RadialMenuTools.ToolFeature("nope"));
    }

    [Fact]
    public void RecordingAndMaintenanceToolsAreSelectableWhenTheirHubFeaturesAreAvailable()
    {
        foreach (var id in new[] { "screenRecorder", "cleaner", "uninstaller", "appUpdates" })
            Assert.True(RadialMenuTools.IsRunnable(id, _ => true, _ => false));
    }

    [Fact]
    public void ShelfSlicesFollowTheShelfMasterSwitchWithoutAffectingOnDemandTools()
    {
        Assert.False(RadialMenuTools.IsRunnable("shelf", _ => true, _ => false));
        Assert.True(RadialMenuTools.IsRunnable("shelf", _ => true, key => key == DefaultsKey.ShelfEnabled));
        Assert.False(RadialMenuTools.IsRunnable("shelf", _ => false, _ => true));
        Assert.True(RadialMenuTools.IsRunnable("cleaningMode", _ => true, _ => false));
        Assert.False(RadialMenuTools.IsRunnable("nope", _ => true, _ => true));
        var settings = new FakeSettings { Values = { [DefaultsKey.ShelfEnabled] = true } };
        Assert.True(RadialMenuTools.IsRunnable("shelf", _ => true, settings));
    }

    [Fact]
    public void DefaultGlyphsCoverEveryKind()
    {
        foreach (var kind in Enum.GetValues<RadialMenuKind>())
            Assert.False(string.IsNullOrEmpty(RadialMenuGlyphs.DefaultGlyph(kind, "")));
        Assert.Equal(RadialMenuGlyphs.ToggleGlyph("wifi"), RadialMenuGlyphs.DefaultGlyph(RadialMenuKind.QuickToggle, "wifi"));
        var star = RadialMenuGlyphs.Choices.First(c => c.Name == "star").Glyph;
        Assert.Equal(star, RadialMenuGlyphs.EffectiveGlyph(new RadialMenuItem { SymbolName = "star" }));
        Assert.Equal(RadialMenuGlyphs.DefaultGlyph(RadialMenuKind.App, ""),
                     RadialMenuGlyphs.EffectiveGlyph(new RadialMenuItem { SymbolName = "star.fill" }));
    }

    // MARK: Shortcut slices

    [Fact]
    public void AShortcutSliceNeedsAPrimaryModifierOrAFunctionKey()
    {
        Assert.NotNull(RadialMenuSupport.ParseShortcut("control+alt+win:49"));
        Assert.NotNull(RadialMenuSupport.ParseShortcut("win:8"));
        Assert.NotNull(RadialMenuSupport.ParseShortcut("alt:115"));
        Assert.NotNull(RadialMenuSupport.ParseShortcut("shift+control:65"));
        Assert.NotNull(RadialMenuSupport.ParseShortcut(":124")); // a standalone F13
        Assert.Null(RadialMenuSupport.ParseShortcut(":116")); // F5 alone: the Windows shortcut grammar needs F13 or above
        Assert.Null(RadialMenuSupport.ParseShortcut("shift:65"));
        Assert.Null(RadialMenuSupport.ParseShortcut("none"));
        Assert.Null(RadialMenuSupport.ParseShortcut("garbage"));
        Assert.Null(RadialMenuSupport.ParseShortcut(null));
        Assert.Empty(RadialMenuSupport.Sanitized([Item(RadialMenuKind.Shortcut, "shift:65")]));
        Assert.Single(RadialMenuSupport.Sanitized([Item(RadialMenuKind.Shortcut, "win:8")]));
    }

    // MARK: Colors and presets

    [Fact]
    public void TwelveDistinctAccentColors()
    {
        var colors = RadialMenuColors.All;
        Assert.Equal(12, colors.Count);
        Assert.Equal(12, colors.Select(c => c.RawValue()).Distinct().Count());
        Assert.All(colors, c => Assert.Equal(c, RadialMenuColors.Parse(c.RawValue())));
        Assert.Equal("Accent", RadialMenuColor.Accent.Title());
        Assert.Equal("Graphite", RadialMenuColor.Graphite.Title());
        Assert.Null(RadialMenuColors.Parse("teal"));
    }

    [Fact]
    public void SixDistinctPresetsGenerateTheirStartingActions()
    {
        var presets = RadialMenuPresets.All;
        Assert.Equal(6, presets.Count);
        Assert.Equal(6, presets.Select(p => p.RawValue()).Distinct().Count());
        Assert.All(presets, p => Assert.Equal(p, RadialMenuPresets.Parse(p.RawValue())));
        Assert.Equal(6, RadialMenuPresets.MakeItems(RadialMenuProfilePreset.General).Count);
        // Windows: three slices, not upstream's four (no Now Playing slice).
        Assert.Equal(3, RadialMenuPresets.MakeItems(RadialMenuProfilePreset.Media).Count);
        Assert.Equal(6, RadialMenuPresets.MakeItems(RadialMenuProfilePreset.Tools).Count);
        Assert.Equal(5, RadialMenuPresets.MakeItems(RadialMenuProfilePreset.WindowLayout).Count);
        Assert.Equal(5, RadialMenuPresets.MakeItems(RadialMenuProfilePreset.QuickToggles).Count);
        Assert.Empty(RadialMenuPresets.MakeItems(RadialMenuProfilePreset.Blank));
        foreach (var preset in presets)
            Assert.Equal(RadialMenuPresets.MakeItems(preset).Count, RadialMenuSupport.Sanitized(RadialMenuPresets.MakeItems(preset)).Count);
    }

    [Fact]
    public void PresetColorsAreUpstreams()
    {
        Assert.Equal(RadialMenuColor.Accent, RadialMenuPresets.DefaultColor(RadialMenuProfilePreset.General));
        Assert.Equal(RadialMenuColor.Purple, RadialMenuPresets.DefaultColor(RadialMenuProfilePreset.Media));
        Assert.Equal(RadialMenuColor.Cyan, RadialMenuPresets.DefaultColor(RadialMenuProfilePreset.Tools));
        Assert.Equal(RadialMenuColor.Orange, RadialMenuPresets.DefaultColor(RadialMenuProfilePreset.WindowLayout));
        Assert.Equal(RadialMenuColor.Mint, RadialMenuPresets.DefaultColor(RadialMenuProfilePreset.QuickToggles));
        Assert.Equal(RadialMenuColor.Graphite, RadialMenuPresets.DefaultColor(RadialMenuProfilePreset.Blank));
    }

    // MARK: Profiles

    [Fact]
    public void AProfileShowsItsNameOrFallsBackToGeneral()
    {
        Assert.Equal(" Media Wheel ", new RadialMenuProfile { Name = " Media Wheel " }.DisplayName());
        Assert.Equal("General", new RadialMenuProfile().DisplayName());
    }

    [Fact]
    public void AProfileEncodesAndDecodesAccuratelyWithATrimmedName()
    {
        var custom = new RadialMenuProfile
        {
            Name = " Media Wheel ", Color = RadialMenuColor.Purple, Shortcut = "control+win:49",
            MouseButton = RadialMouseTrigger.Back.RawValue, Items = RadialMenuPresets.MakeItems(RadialMenuProfilePreset.Media),
        };
        var decoded = RadialMenuSupport.DecodeProfiles(RadialMenuSupport.EncodeProfiles([custom]), new FakeSettings());
        var profile = Assert.Single(decoded);
        Assert.Equal(custom.Id, profile.Id);
        Assert.Equal("Media Wheel", profile.Name);
        Assert.Equal(RadialMenuColor.Purple, profile.Color);
        Assert.Equal("control+win:49", profile.Shortcut);
        Assert.Equal("back", profile.MouseButton);
        Assert.Equal(3, profile.Items.Count);
    }

    [Fact]
    public void AWheelKeepsTheStarterSetItCameFromWhateverItIsRenamedTo()
    {
        var renamed = RadialMenuPresets.CreateProfile(RadialMenuProfilePreset.Media, name: "Renamed");
        var trip = RadialMenuSupport.DecodeProfiles(RadialMenuSupport.EncodeProfiles([renamed]), new FakeSettings());
        Assert.Equal("media", renamed.Preset);
        Assert.Equal("media", trip[0].Preset);
        Assert.Equal(RadialMenuColor.Purple, trip[0].Color);
    }

    [Fact]
    public void AWheelSavedBeforeTheStarterSetWasRecordedDecodesWithoutOne()
    {
        var json = $$"""[{"id":"{{Guid.NewGuid()}}","name":"Old Wheel","color":"accent","shortcut":"","mouseButton":"off","items":[]}]""";
        Assert.Null(RadialMenuSupport.DecodeProfiles(json, new FakeSettings())[0].Preset);
    }

    [Fact]
    public void ALegacyTrackpadTapStillDecodesAndIsDropped()
    {
        const string json = """[{"name":"Old","shortcut":"","mouseButton":"forward","items":[],"trackpadTap":true}]""";
        var profile = Assert.Single(RadialMenuSupport.DecodeProfiles(json, new FakeSettings()));
        Assert.Equal("Old", profile.Name);
        Assert.Equal("forward", profile.MouseButton);
        Assert.DoesNotContain("trackpad", RadialMenuSupport.EncodeProfiles([profile]));
    }

    [Fact]
    public void ABrokenProfileIsDroppedAloneAndTheOthersSurvive()
    {
        const string json = """[{"name":"Good","color":"blue"},{"name":"Bad","color":"teal"},{"name":"Bad2","items":5},7,{"name":"Fine"}]""";
        var profiles = RadialMenuSupport.DecodeProfiles(json, new FakeSettings());
        Assert.Equal(["Good", "Fine"], profiles.Select(p => p.Name));
        Assert.Equal(RadialMenuColor.Blue, profiles[0].Color);
        Assert.Equal(RadialMenuColor.Accent, profiles[1].Color);
    }

    [Fact]
    public void ABrokenItemInsideAProfileDropsJustThatItem()
    {
        const string json = """[{"name":"W","items":[{"kind":"media","payload":"playPause"},{"kind":"zzz"},{"kind":"tool","payload":"nope"}]}]""";
        Assert.Single(RadialMenuSupport.DecodeProfiles(json, new FakeSettings())[0].Items);
    }

    [Fact]
    public void SanitizedProfilesDeduplicateIdsTrimNamesAndDropInvalidShortcuts()
    {
        var id = Guid.NewGuid();
        var sanitized = RadialMenuSupport.SanitizedProfiles(
        [
            new RadialMenuProfile { Id = id, Name = "First" },
            new RadialMenuProfile { Id = id, Name = "Duplicate" },
            new RadialMenuProfile { Name = "  Trim Me  ", Shortcut = "invalidShortcutFormat" },
        ]);
        Assert.Equal(2, sanitized.Count);
        Assert.Equal("First", sanitized[0].Name);
        Assert.Equal("Trim Me", sanitized[1].Name);
        Assert.Equal("", sanitized[1].Shortcut);
    }

    [Fact]
    public void SanitizingAnEmptyProfileListProvidesOneStarterProfile()
    {
        var sanitized = RadialMenuSupport.SanitizedProfiles([]);
        var profile = Assert.Single(sanitized);
        Assert.Equal(RadialMenuColor.Accent, profile.Color);
        Assert.Equal(RadialMenuSupport.DefaultShortcut, profile.Shortcut);
        Assert.Equal(6, profile.Items.Count);
    }

    [Fact]
    public void AButtonSavedOnTwoWheelsStaysOnTheFirstTheOnlyOneItEverOpened()
    {
        var forward = new RadialMenuProfile { Name = "Forward", MouseButton = RadialMouseTrigger.Forward.RawValue };
        var copy = forward with { Id = Guid.NewGuid() };
        Assert.Equal(["forward", "off"], RadialMenuSupport.SanitizedProfiles([forward, copy]).Select(p => p.MouseButton));
    }

    // MARK: First run and migration (Windows defaults)

    [Fact]
    public void FirstRunBuildsOneGeneralProfileFromWindowsDefaults()
    {
        var profile = Assert.Single(RadialMenuSupport.DecodeProfiles("", new FakeSettings()));
        Assert.Equal("control+alt+win:32", profile.Shortcut);
        Assert.Equal("forward", profile.MouseButton);
        Assert.Equal("general", profile.Preset);
        Assert.Equal(RadialMenuSupport.StarterItems, profile.Items);
        Assert.NotNull(RadialMenuSupport.ParseShortcut(profile.Shortcut));
    }

    [Fact]
    public void TheFirstRunDefaultsAreTheRegisteredDefaults()
    {
        var registered = Defaults.Registered.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(RadialMenuSupport.DefaultShortcut, registered[DefaultsKey.RadialMenuShortcut]);
        Assert.Equal(RadialMenuSupport.DefaultMouseButton, registered[DefaultsKey.RadialMenuMouseButton]);
        Assert.Equal("", registered[DefaultsKey.RadialMenuProfiles]);
        Assert.False(registered.ContainsKey(RadialMenuSupport.LegacyItemsKey));
    }

    [Fact]
    public void LegacySettingsMigrateIntoTheFirstProfile()
    {
        var legacyItems = RadialMenuSupport.Encode([Item(RadialMenuKind.Url, "legacy.example")]);
        var settings = new FakeSettings
        {
            Values =
            {
                [RadialMenuSupport.LegacyItemsKey] = legacyItems,
                [DefaultsKey.RadialMenuShortcut] = "control+alt:82",
                [DefaultsKey.RadialMenuMouseButton] = "button:7",
            },
        };
        var profile = Assert.Single(RadialMenuSupport.DecodeProfiles("", settings));
        Assert.Equal("control+alt:82", profile.Shortcut);
        Assert.Equal("button:7", profile.MouseButton);
        Assert.Equal("https://legacy.example", Assert.Single(profile.Items).Payload);
    }

    [Fact]
    public void AnUnreadableProfilesBlobFallsBackToTheLegacyKeys() =>
        Assert.Equal("forward", Assert.Single(RadialMenuSupport.DecodeProfiles("{broken", new FakeSettings())).MouseButton);

    // MARK: Triggers and claims

    [Fact]
    public void ADuplicatedWheelKeepsTheActionsButLeavesTheTriggersToTheOriginal()
    {
        var original = new RadialMenuProfile
        {
            Name = "Tap", Shortcut = "control+alt:32", MouseButton = "forward",
            Items = [Item(RadialMenuKind.App, @"C:\Windows\explorer.exe")],
        };
        var copy = original.Duplicate("Tap 2");
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("Tap 2", copy.Name);
        Assert.Equal(original.Items, copy.Items);
        Assert.Equal("", copy.Shortcut);
        Assert.Equal("off", copy.MouseButton);
        Assert.False(RadialMenuSupport.HasTrigger(copy));
    }

    [Fact]
    public void AShortcutOrAMouseButtonEachOpenAWheel()
    {
        Assert.True(RadialMenuSupport.HasTrigger(new RadialMenuProfile { Shortcut = "control+alt:32" }));
        Assert.True(RadialMenuSupport.HasTrigger(new RadialMenuProfile { Shortcut = RadialMenuSupport.DefaultShortcut }));
        Assert.True(RadialMenuSupport.HasTrigger(new RadialMenuProfile { MouseButton = RadialMouseTrigger.Button(4).RawValue }));
        Assert.False(RadialMenuSupport.HasTrigger(new RadialMenuProfile { Shortcut = "not a shortcut", MouseButton = "off" }));
    }

    [Fact]
    public void GivingAWheelAMouseButtonTakesItOffTheWheelThatOpenedWithIt()
    {
        var forward = new RadialMenuProfile { Name = "Forward", MouseButton = "forward" };
        var other = new RadialMenuProfile { Name = "Other" };
        Assert.Equal(["off", "forward"], RadialMenuSupport.Assigning("forward", other.Id, [forward, other]).Select(p => p.MouseButton));
        Assert.Equal(["forward", "button:5"],
                     RadialMenuSupport.Assigning(RadialMouseTrigger.Button(5).RawValue, other.Id, [forward, other]).Select(p => p.MouseButton));
    }

    [Fact]
    public void ClaimedMouseButtonsComeFromEveryStoredWheel()
    {
        var settings = EnabledSettings();
        var work = new RadialMenuProfile { Name = "Work", MouseButton = RadialMouseTrigger.Back.RawValue };
        var fun = new RadialMenuProfile { Name = "Fun", MouseButton = RadialMouseTrigger.Button(4).RawValue };
        settings.Values[DefaultsKey.RadialMenuProfiles] = RadialMenuSupport.EncodeProfiles([work, fun]);

        Assert.True(RadialMenuSupport.ClaimsMouseButton(RadialMouseTrigger.BackButtonNumber, settings));
        Assert.True(RadialMenuSupport.ClaimsMouseButton(4, settings));
        Assert.False(RadialMenuSupport.ClaimsMouseButton(5, settings));
        AssertButtons(RadialMenuSupport.ClaimedMouseButtons(settings), 3, 4);

        settings.Values[DefaultsKey.RadialMenuEnabled] = false;
        Assert.False(RadialMenuSupport.ClaimsMouseButton(RadialMouseTrigger.BackButtonNumber, settings));
        Assert.Empty(RadialMenuSupport.ClaimedMouseButtons(settings));
    }

    [Fact]
    public void AnUnavailableRadialMenuClaimsNothing()
    {
        var settings = EnabledSettings();
        settings.Values[RadialAvailabilityKey] = false;
        settings.Values[DefaultsKey.RadialMenuProfiles] = RadialMenuSupport.EncodeProfiles(
            [new RadialMenuProfile { MouseButton = "forward" }]);
        Assert.Empty(RadialMenuSupport.ClaimedMouseButtons(settings));
    }

    [Fact]
    public void WithTheFeatureOffNoSideButtonIsClaimedAwayFromNavigation() =>
        Assert.True(!RadialMenuSupport.ClaimsMouseButton(3, new FakeSettings()) && !RadialMenuSupport.ClaimsMouseButton(4, new FakeSettings()));

    [Fact]
    public void BeforeAnyWheelIsSavedTheLegacyButtonIsClaimed()
    {
        var settings = EnabledSettings();
        // Nothing stored at all: the Windows default is the Forward button.
        AssertButtons(RadialMenuSupport.ClaimedMouseButtons(settings), 4);
        settings.Values[DefaultsKey.RadialMenuMouseButton] = "off";
        Assert.Empty(RadialMenuSupport.ClaimedMouseButtons(settings));
        settings.Values[DefaultsKey.RadialMenuMouseButton] = "button:9";
        AssertButtons(RadialMenuSupport.ClaimedMouseButtons(settings), 9);
    }

    [Fact]
    public void ClaimedButtonsAgreeWithTheDecodedProfilesForALegacyUser()
    {
        var settings = EnabledSettings();
        settings.Values[DefaultsKey.RadialMenuMouseButton] = "back";
        var profile = RadialMenuSupport.DecodeProfiles(settings.GetString(DefaultsKey.RadialMenuProfiles), settings)[0];
        Assert.Equal(RadialMouseTrigger.Sanitized(profile.MouseButton).ButtonNumber, RadialMenuSupport.ClaimedMouseButtons(settings).Single());
    }

    [Fact]
    public void WheelsAnswerToTheirOwnShortcuts()
    {
        var settings = new FakeSettings();
        // Before a profile is saved the wheel answers to the shortcut it migrates from.
        Assert.Equal([Shortcut.Parse("control+alt+win:32")!.Value], RadialMenuSupport.ProfileShortcuts(settings));

        var wheel = new Shortcut('W', ShortcutModifiers.Control | ShortcutModifiers.Alt);
        var work = new RadialMenuProfile { Name = "Work", Shortcut = wheel.StorageValue };
        var spare = new RadialMenuProfile { Name = "Spare" };
        settings.Values[DefaultsKey.RadialMenuProfiles] = RadialMenuSupport.EncodeProfiles([work, spare]);
        Assert.Equal([wheel], RadialMenuSupport.ProfileShortcuts(settings));

        Assert.Equal(work.Id, RadialMenuSupport.ProfileUsing(wheel, [work, spare], spare.Id)?.Id);
        Assert.Null(RadialMenuSupport.ProfileUsing(wheel, [work, spare], work.Id));

        // The role key changed after the wheels were saved leaves their shortcuts as they are; until then it is the page's shortcut.
        var seed = new Shortcut('R', ShortcutModifiers.Control | ShortcutModifiers.Win);
        settings.Values[DefaultsKey.RadialMenuShortcut] = seed.StorageValue;
        Assert.Equal([wheel], RadialMenuSupport.ProfileShortcuts(settings));
        settings.Values.Remove(DefaultsKey.RadialMenuProfiles);
        Assert.Equal([seed], RadialMenuSupport.ProfileShortcuts(settings));
    }

    // MARK: Defaults, activation mode and mouse trigger

    [Fact]
    public void TheRadialMenuDefaultsShipAsDesigned()
    {
        var registered = Defaults.Registered.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(false, registered[DefaultsKey.RadialMenuEnabled]);
        Assert.Equal(true, registered[DefaultsKey.RadialMenuAtPointer]);
        Assert.Equal("pressOrHold", registered[DefaultsKey.RadialMenuActivationMode]);
        Assert.Equal(RadialMenuActivationMode.PressOrHold, RadialMenuActivationModes.Sanitized((string?)registered[DefaultsKey.RadialMenuActivationMode]));
    }

    [Fact]
    public void TheActivationModeDecodesKnownValuesAndSafelyFallsBack()
    {
        Assert.Equal(RadialMenuActivationMode.Press, RadialMenuActivationModes.Sanitized("press"));
        Assert.Equal(RadialMenuActivationMode.Hold, RadialMenuActivationModes.Sanitized("hold"));
        Assert.Equal(RadialMenuActivationMode.PressOrHold, RadialMenuActivationModes.Sanitized(null));
        Assert.Equal(RadialMenuActivationMode.PressOrHold, RadialMenuActivationModes.Sanitized("future-mode"));
        Assert.All(Enum.GetValues<RadialMenuActivationMode>(),
                   m => Assert.Equal(m, RadialMenuActivationModes.Sanitized(m.RawValue())));
    }

    [Fact]
    public void OnlyHoldCapableSummonsEnterTheHeldPhase()
    {
        Assert.False(RadialMenuActivationMode.Press.StartsHeld(requestedHold: true, hasHeldButton: false, shortcutHasModifiers: true));
        Assert.True(RadialMenuActivationMode.Hold.StartsHeld(requestedHold: true, hasHeldButton: false, shortcutHasModifiers: true));
        Assert.True(RadialMenuActivationMode.PressOrHold.StartsHeld(requestedHold: false, hasHeldButton: true, shortcutHasModifiers: false));
        Assert.False(RadialMenuActivationMode.Hold.StartsHeld(requestedHold: false, hasHeldButton: false, shortcutHasModifiers: true));
    }

    [Fact]
    public void ReleaseKeepsTheAdaptiveWheelDismissesAnEmptyStrictHoldOrSelectsItsTarget()
    {
        Assert.Equal(RadialMenuReleaseAction.StayOpen, RadialMenuActivationMode.PressOrHold.ReleaseAction(hasSelection: false));
        Assert.Equal(RadialMenuReleaseAction.Dismiss, RadialMenuActivationMode.Hold.ReleaseAction(hasSelection: false));
        Assert.Equal(RadialMenuReleaseAction.Select, RadialMenuActivationMode.Hold.ReleaseAction(hasSelection: true));
        Assert.Equal(RadialMenuReleaseAction.Select, RadialMenuActivationMode.PressOrHold.ReleaseAction(hasSelection: true));
        Assert.Equal(RadialMenuReleaseAction.StayOpen, RadialMenuActivationMode.Press.ReleaseAction(hasSelection: false));
    }

    [Fact]
    public void TheRadialHoldFollowsPhysicalModifiersOrTheVirtualSuperKey()
    {
        Assert.True(RadialMenuSupport.ShortcutIsStillHeld(modifiersHeld: true, superKeyHeld: false));
        Assert.True(RadialMenuSupport.ShortcutIsStillHeld(modifiersHeld: false, superKeyHeld: true));
        Assert.False(RadialMenuSupport.ShortcutIsStillHeld(modifiersHeld: false, superKeyHeld: false));
    }

    [Fact]
    public void TheMouseTriggerRoundTripsEveryExtraButtonAndFallsBackToOff()
    {
        Assert.Equal(RadialMouseTrigger.Back, RadialMouseTrigger.Sanitized("back"));
        Assert.Equal(4, RadialMouseTrigger.Sanitized("forward").ButtonNumber);
        Assert.Equal(3, RadialMouseTrigger.Back.ButtonNumber);
        Assert.All(RadialMouseTrigger.ButtonRange, n =>
            Assert.Equal(n, RadialMouseTrigger.Sanitized(RadialMouseTrigger.Button(n).RawValue).ButtonNumber));
        Assert.Equal(RadialMouseTrigger.Off, RadialMouseTrigger.Sanitized(null));
        Assert.Equal(RadialMouseTrigger.Off, RadialMouseTrigger.Sanitized("teleport"));
        Assert.Equal(RadialMouseTrigger.Off, RadialMouseTrigger.Sanitized("button:2"));
        Assert.Equal(RadialMouseTrigger.Off, RadialMouseTrigger.Sanitized("button:32"));
        Assert.Equal(RadialMouseTrigger.Off, RadialMouseTrigger.Sanitized("button:x"));
        Assert.Null(RadialMouseTrigger.Off.ButtonNumber);
        Assert.Equal("off", RadialMouseTrigger.Off.RawValue);
        Assert.Equal("button:9", RadialMouseTrigger.Button(9).RawValue);
        Assert.Equal("back", RadialMouseTrigger.Button(3).RawValue);
        Assert.Equal("forward", RadialMouseTrigger.Button(4).RawValue);
    }

    // MARK: Profile deletion contract

    [Fact]
    public void ProfileDeletionFollowsTheSettingsContract()
    {
        var first = new RadialMenuProfile { Name = "First" };
        var second = new RadialMenuProfile { Name = "Second" };
        var third = new RadialMenuProfile { Name = "Third" };
        IReadOnlyList<RadialMenuProfile> profiles = [first, second, third];
        var selected = third.Id;

        var step = RadialMenuSupport.DeleteProfile(profiles, selected, first.Id);
        Assert.True(step.Changed);
        Assert.Equal([second.Id, third.Id], step.Profiles.Select(p => p.Id));

        var again = RadialMenuSupport.DeleteProfile(step.Profiles, step.SelectedId, first.Id);
        Assert.False(again.Changed);
        Assert.Equal(2, again.Profiles.Count);

        var afterSecond = RadialMenuSupport.DeleteProfile(again.Profiles, again.SelectedId, second.Id);
        var afterThird = RadialMenuSupport.DeleteProfile(afterSecond.Profiles, afterSecond.SelectedId, third.Id);
        Assert.False(afterThird.Changed);
        Assert.Equal([third.Id], afterThird.Profiles.Select(p => p.Id));
        Assert.Equal(third.Id, afterThird.SelectedId);
    }

    [Fact]
    public void DeletingTheLastSlotSelectsTheNewLastProfile()
    {
        var a = new RadialMenuProfile { Name = "A" };
        var b = new RadialMenuProfile { Name = "B" };
        var result = RadialMenuSupport.DeleteProfile([a, b], b.Id, b.Id);
        Assert.Equal(a.Id, result.SelectedId);
    }

    [Fact]
    public void TheStoreAdapterReadsSavedValuesThenRegisteredDefaults()
    {
        using var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        var settings = new StoreRadialSettings(store);
        Assert.Equal("control+alt+win:32", settings.GetString(DefaultsKey.RadialMenuShortcut));
        Assert.Null(settings.GetString(RadialMenuSupport.LegacyItemsKey));
        Assert.False(settings.GetBool(DefaultsKey.RadialMenuEnabled));
        Assert.Empty(RadialMenuSupport.ClaimedMouseButtons(settings));
        store.Set(DefaultsKey.RadialMenuEnabled, true);
        store.Set(RadialAvailabilityKey, true);
        AssertButtons(RadialMenuSupport.ClaimedMouseButtons(settings), 4);
    }
}
