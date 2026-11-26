// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the "The Mac's own Settings panes" block in upstream Tests/CommandBarFeatureTests.swift.
// The macOS-only checks (extension bundle attributes, legacy pane names, per-language resource
// folders) have no Windows counterpart: the pages are a static table. The keyword merge checks are
// kept, and the table itself is pinned.
using Notchly.Core.CommandBar;

namespace Notchly.Core.Tests;

public class CommandBarSettingsPagesTests
{
    private static CommandBarKeywordSection Section(string name, params (string Title, string Index)[] entries) =>
        new(name, entries.Select(e => new CommandBarKeywordEntry(e.Title, e.Index)).ToList());

    // MARK: - Keyword merge rules

    [Fact]
    public void TheWordsAPageAnswersToAreReadFromEveryGroupAndNeverRepeat() =>
        Assert.Equal("Alinear espejo Brillo aclarar atenuar",
            CommandBarSettingsPages.Keywords([
                Section("bSection", ("Brillo", "aclarar, atenuar")),
                Section("aSection", ("Alinear", "espejo,  , Alinear")),
            ]));

    [Fact]
    public void AnEmptyIndexIsNoWordsNotACrash()
    {
        Assert.Equal("", CommandBarSettingsPages.Keywords(Array.Empty<CommandBarKeywordSection>()));
        Assert.Equal("", CommandBarSettingsPages.Keywords([Section("Main")]));
        Assert.Equal("", CommandBarSettingsPages.Keywords([Section("Main", ("", ""), ("  ", " , "))]));
        Assert.Equal("", CommandBarSettingsPages.Keywords(""));
    }

    [Fact]
    public void OnePageNeverContributesMoreWordsThanTheRankingCanUse() =>
        Assert.Equal("one two", CommandBarSettingsPages.Keywords([Section("a", ("one", "two, three"))], limit: 2));

    [Fact]
    public void ImportantTermsBeyondTheOldFortyWordCutoffRemainSearchable()
    {
        var terms = Enumerable.Range(1, 45).Select(i => ($"term{i}", ""));
        Assert.Contains("term45", CommandBarSettingsPages.Keywords([Section("Main", terms.ToArray())]));
    }

    [Fact]
    public void ACapOfOneHundredSixtyTermsIsTheDefault()
    {
        var terms = Enumerable.Range(1, 200).Select(i => ($"term{i}", "")).ToArray();
        var words = CommandBarSettingsPages.Keywords([Section("Main", terms)]).Split(' ');
        Assert.Equal(160, words.Length);
        Assert.Equal("term160", words[^1]);
        Assert.Equal(160, CommandBarSettingsPages.KeywordLimit);
    }

    [Fact]
    public void AWordLongerThanFortyCharactersIsDroppedAndOneOfFortyIsKept()
    {
        var forty = new string('a', 40);
        var forty1 = new string('b', 41);
        Assert.Equal(forty + " ok", CommandBarSettingsPages.Keywords([Section("m", (forty, forty1 + ", ok"))]));
        Assert.Equal(40, CommandBarSettingsPages.WordLimit);
    }

    [Fact]
    public void RepeatsAreRemovedIgnoringCaseAndTheFirstSpellingWins() =>
        Assert.Equal("Wi-Fi wireless", CommandBarSettingsPages.Keywords("Wi-Fi, WI-FI, wireless, Wireless, wi-fi"));

    [Fact]
    public void TheTitleComesBeforeTheSynonymsOfItsEntry() =>
        Assert.Equal("Title b a", CommandBarSettingsPages.Keywords([Section("x", ("Title", "b, a"))]));

    [Fact]
    public void SectionsAreReadInNameOrderWhateverOrderTheyArriveIn()
    {
        var a = Section("a", ("first", ""));
        var b = Section("b", ("second", ""));
        Assert.Equal("first second", CommandBarSettingsPages.Keywords([b, a]));
        Assert.Equal("first second", CommandBarSettingsPages.Keywords([a, b]));
    }

    // MARK: - Row ids

    [Fact]
    public void ARowIdIsThePrefixPlusThePageId()
    {
        Assert.Equal("settingspage.display", CommandBarSettingsPages.RowId("display"));
        Assert.Equal("display", CommandBarSettingsPages.PageId("settingspage.display"));
        Assert.Equal("settingspage.", CommandBarSettingsPages.RowIdPrefix);
    }

    [Theory]
    [InlineData("app.x")]
    [InlineData("settings.general")]
    [InlineData("macsettings.com.apple.Sound")]
    [InlineData("settingspage.")]
    [InlineData("")]
    public void AnythingElseIsNotASettingsPageRow(string rowId)
    {
        Assert.Null(CommandBarSettingsPages.PageId(rowId));
        Assert.Null(CommandBarSettingsPages.Find(rowId));
    }

    [Fact]
    public void ARowIdFindsItsPage()
    {
        var page = CommandBarSettingsPages.Find("settingspage.wifi");
        Assert.Equal("Wi-Fi", page?.Title);
        Assert.Equal("ms-settings:network-wifi", page?.Uri);
        Assert.Equal("settingspage.wifi", page?.RowId);
        Assert.Null(CommandBarSettingsPages.Find("settingspage.nope"));
    }

    // MARK: - The table

    [Fact]
    public void EveryPageHasAnIdATitleKeywordsAndAnAddress()
    {
        Assert.NotEmpty(CommandBarSettingsPages.All);
        foreach (var page in CommandBarSettingsPages.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Id), "id");
            Assert.False(string.IsNullOrWhiteSpace(page.Title), page.Id + " title");
            Assert.False(string.IsNullOrWhiteSpace(page.Keywords), page.Id + " keywords");
            Assert.True(CommandBarSettingsPages.IsSettingsUri(page.Uri), page.Id + " uri " + page.Uri);
        }
    }

    [Fact]
    public void IdsAreUniqueLowerCaseAndSafeInARowId()
    {
        var ids = CommandBarSettingsPages.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", id));
    }

    [Fact]
    public void NoTwoPagesOpenTheSameAddress()
    {
        var uris = CommandBarSettingsPages.All.Select(p => p.Uri).ToList();
        Assert.Equal(uris.Count, uris.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PagesAreSortedByTitle()
    {
        var titles = CommandBarSettingsPages.All.Select(p => p.Title).ToList();
        Assert.Equal(titles.Order(StringComparer.OrdinalIgnoreCase), titles);
    }

    [Fact]
    public void KeywordsRespectTheLimitsAndCarryNoRepeats()
    {
        foreach (var page in CommandBarSettingsPages.All)
        {
            // Terms may hold spaces, so check the limits on the table's own comma-free form.
            Assert.True(page.Keywords.Length <= CommandBarSettingsPages.KeywordLimit * (CommandBarSettingsPages.WordLimit + 1));
            Assert.DoesNotContain("  ", page.Keywords);
            Assert.Equal(page.Keywords.Trim(), page.Keywords);
        }
    }

    [Fact]
    public void ThereAreNoNotchlySettingsPagesInTheTable() =>
        Assert.DoesNotContain(CommandBarSettingsPages.All,
            page => page.Title.Contains("Notchly", StringComparison.OrdinalIgnoreCase)
                || page.Keywords.Contains("Notchly", StringComparison.OrdinalIgnoreCase));

    [Theory]
    // System
    [InlineData("display", "ms-settings:display")]
    [InlineData("sound", "ms-settings:sound")]
    [InlineData("notifications", "ms-settings:notifications")]
    [InlineData("focus", "ms-settings:quietmomentshome")]
    [InlineData("power", "ms-settings:powersleep")]
    [InlineData("battery-saver", "ms-settings:batterysaver")]
    [InlineData("storage", "ms-settings:storagesense")]
    [InlineData("nearby-sharing", "ms-settings:crossdevice")]
    [InlineData("multitasking", "ms-settings:multitasking")]
    [InlineData("activation", "ms-settings:activation")]
    [InlineData("troubleshoot", "ms-settings:troubleshoot")]
    [InlineData("recovery", "ms-settings:recovery")]
    [InlineData("projecting", "ms-settings:project")]
    [InlineData("clipboard", "ms-settings:clipboard")]
    [InlineData("remote-desktop", "ms-settings:remotedesktop")]
    [InlineData("about", "ms-settings:about")]
    // Bluetooth & devices
    [InlineData("bluetooth", "ms-settings:bluetooth")]
    [InlineData("printers", "ms-settings:printers")]
    [InlineData("mouse", "ms-settings:mousetouchpad")]
    [InlineData("touchpad", "ms-settings:devices-touchpad")]
    [InlineData("typing", "ms-settings:typing")]
    [InlineData("pen", "ms-settings:pen")]
    [InlineData("autoplay", "ms-settings:autoplay")]
    [InlineData("usb", "ms-settings:usb")]
    // Network
    [InlineData("wifi", "ms-settings:network-wifi")]
    [InlineData("ethernet", "ms-settings:network-ethernet")]
    [InlineData("vpn", "ms-settings:network-vpn")]
    [InlineData("airplane-mode", "ms-settings:network-airplanemode")]
    [InlineData("proxy", "ms-settings:network-proxy")]
    [InlineData("dial-up", "ms-settings:network-dialup")]
    // Personalization
    [InlineData("background", "ms-settings:personalization-background")]
    [InlineData("colors", "ms-settings:colors")]
    [InlineData("lock-screen", "ms-settings:lockscreen")]
    [InlineData("themes", "ms-settings:themes")]
    [InlineData("start", "ms-settings:personalization-start")]
    [InlineData("taskbar", "ms-settings:taskbar")]
    [InlineData("fonts", "ms-settings:fonts")]
    [InlineData("device-usage", "ms-settings:deviceusage")]
    // Apps
    [InlineData("installed-apps", "ms-settings:appsfeatures")]
    [InlineData("default-apps", "ms-settings:defaultapps")]
    [InlineData("startup-apps", "ms-settings:startupapps")]
    [InlineData("optional-features", "ms-settings:optionalfeatures")]
    // Accounts
    [InlineData("your-info", "ms-settings:yourinfo")]
    [InlineData("sign-in-options", "ms-settings:signinoptions")]
    [InlineData("email-accounts", "ms-settings:emailandaccounts")]
    [InlineData("family", "ms-settings:family-group")]
    // Time & language
    [InlineData("date-time", "ms-settings:dateandtime")]
    [InlineData("language", "ms-settings:regionlanguage")]
    [InlineData("region", "ms-settings:regionformatting")]
    [InlineData("speech", "ms-settings:speech")]
    // Gaming
    [InlineData("game-bar", "ms-settings:gaming-gamebar")]
    [InlineData("game-mode", "ms-settings:gaming-gamemode")]
    // Accessibility
    [InlineData("text-size", "ms-settings:easeofaccess-display")]
    [InlineData("mouse-pointer", "ms-settings:easeofaccess-mousepointer")]
    [InlineData("narrator", "ms-settings:easeofaccess-narrator")]
    [InlineData("magnifier", "ms-settings:easeofaccess-magnifier")]
    [InlineData("color-filters", "ms-settings:easeofaccess-colorfilter")]
    [InlineData("captions", "ms-settings:easeofaccess-closedcaptioning")]
    [InlineData("accessibility-keyboard", "ms-settings:easeofaccess-keyboard")]
    // Privacy
    [InlineData("privacy-general", "ms-settings:privacy-general")]
    [InlineData("location", "ms-settings:privacy-location")]
    [InlineData("camera", "ms-settings:privacy-webcam")]
    [InlineData("microphone", "ms-settings:privacy-microphone")]
    [InlineData("privacy-notifications", "ms-settings:privacy-notifications")]
    [InlineData("privacy-files", "ms-settings:privacy-broadfilesystemaccess")]
    // Update & security
    [InlineData("windows-update", "ms-settings:windowsupdate")]
    [InlineData("update-history", "ms-settings:windowsupdate-history")]
    [InlineData("windows-security", "ms-settings:windowsdefender")]
    public void TheRequiredPagesAreThereWithTheirAddress(string id, string uri) =>
        Assert.Equal(uri, CommandBarSettingsPages.Find(CommandBarSettingsPages.RowId(id))?.Uri);

    [Fact]
    public void APageAnswersToTheWordsAPersonWouldType()
    {
        Assert.Contains("resolution", CommandBarSettingsPages.Find("settingspage.display")!.Keywords);
        Assert.Contains("wireless", CommandBarSettingsPages.Find("settingspage.wifi")!.Keywords);
        Assert.Contains("dark mode", CommandBarSettingsPages.Find("settingspage.colors")!.Keywords);
        Assert.Contains("uninstall", CommandBarSettingsPages.Find("settingspage.installed-apps")!.Keywords);
        Assert.Contains("antivirus", CommandBarSettingsPages.Find("settingspage.windows-security")!.Keywords);
    }

    [Theory]
    [InlineData("ms-settings:display", true)]
    [InlineData("ms-settings:network-wifi", true)]
    [InlineData("ms-settings:privacy-webcam?x=1", true)]
    [InlineData("ms-settings:", false)]
    [InlineData("ms-settings:Display", false)]
    [InlineData("ms-settings: display", false)]
    [InlineData("https://settings", false)]
    [InlineData("x-apple.systempreferences:com.apple.Sound", false)]
    [InlineData("", false)]
    public void OnlyAnMsSettingsAddressIsAPageAddress(string uri, bool expected) =>
        Assert.Equal(expected, CommandBarSettingsPages.IsSettingsUri(uri));
}
