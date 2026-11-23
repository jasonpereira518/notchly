// SPDX-License-Identifier: GPL-3.0-or-later
// The top strip's menu bar: standard menus and their shortcuts, composing with an app's own menus,
// which window the bar follows and its name, fitting beside the notch, and the clock.
using System.Globalization;
using Islet.Core.MenuBar;
using Islet.Core.Shortcuts;

namespace Islet.Core.Tests;

public class MenuBarTests
{
    private static IEnumerable<MenuNode> Commands(MenuTitle title) => title.Items.Where(i => i.Kind == MenuNodeKind.Command);

    [Fact]
    public void AnAppWithoutMenusGetsTheMacsUsualMenus()
    {
        var bar = StandardMenus.Compose("Google Chrome", elevated: false, maximized: true, own: null);
        Assert.Equal(new[] { "Google Chrome", "File", "Edit", "View", "Window", "Help" }, bar.Select(t => t.Title));
        Assert.True(bar[0].Bold);
        Assert.All(bar.Skip(1), t => Assert.False(t.Bold));
        Assert.Equal(new[] { "Hide Google Chrome", "Quit Google Chrome" }, Commands(bar[0]).Select(i => i.Title));
        Assert.Contains(bar[4].Items, i => i.Title == "Restore");
        Assert.Contains(StandardMenus.WindowMenu(maximized: false, elevated: false).Items, i => i.Title == "Maximize");
        Assert.Equal(new[] { "Google Chrome Help" }, Commands(bar[5]).Select(i => i.Title));
    }

    [Fact]
    public void StandardItemsSendTheUsualShortcut()
    {
        static (string, string?) Pair(MenuNode node) => (node.Title, node.Shortcut);
        var edit = Commands(StandardMenus.Edit(false)).Select(Pair).ToList();
        Assert.Equal(new (string, string?)[]
        {
            ("Undo", "Ctrl+Z"), ("Redo", "Ctrl+Y"), ("Cut", "Ctrl+X"), ("Copy", "Ctrl+C"), ("Paste", "Ctrl+V"),
            ("Select all", "Ctrl+A"), ("Find…", "Ctrl+F"),
        }, edit);
        var view = Commands(StandardMenus.View(false)).Select(Pair).ToList();
        Assert.Equal(new (string, string?)[]
        {
            ("Zoom in", "Ctrl+="), ("Zoom out", "Ctrl+-"), ("Actual size", "Ctrl+0"), ("Refresh", "F5"), ("Full screen", "F11"),
        }, view);
        var saveAs = Commands(StandardMenus.File(false)).Single(i => i.Title == "Save as…");
        Assert.Equal(new MenuCommand.SendKeys(new Shortcut('S', ShortcutModifiers.Control | ShortcutModifiers.Shift)), saveAs.Command);
        Assert.Equal("Ctrl+Shift+S", saveAs.Shortcut);
        // Window actions are done to the window, never with keys.
        Assert.All(Commands(StandardMenus.WindowMenu(false, false)), i => Assert.IsType<MenuCommand.Window>(i.Command));
        Assert.IsType<MenuCommand.Window>(Commands(StandardMenus.File(false)).Last().Command);
    }

    [Fact]
    public void AnElevatedAppsItemsAreGreyedWithTheReason()
    {
        var bar = StandardMenus.Compose("Registry Editor", elevated: true, maximized: false, own: null);
        Assert.All(bar.SelectMany(Commands), i =>
        {
            Assert.False(i.Enabled);
            Assert.Equal(StandardMenus.ElevatedNote, i.Note);
        });
    }

    [Fact]
    public void AnAppsOwnMenusReplaceTheStandardOnesAndGainAWindowMenuOnlyWhenMissing()
    {
        var notepad = new[] { new MenuTitle("File", MenuSource.Automation, 0), new MenuTitle("Edit", MenuSource.Automation, 1), new MenuTitle("View", MenuSource.Automation, 2) };
        var bar = StandardMenus.Compose("Notepad", false, false, notepad);
        Assert.Equal(new[] { "Notepad", "File", "Edit", "View", "Window" }, bar.Select(t => t.Title));
        Assert.Equal(MenuSource.Automation, bar[1].Source);
        Assert.Equal(MenuSource.Standard, bar[4].Source);

        var withWindow = new[] { new MenuTitle("File", MenuSource.Native, 0), new MenuTitle("Window", MenuSource.Native, 1) };
        Assert.Equal(new[] { "Notepad++", "File", "Window" }, StandardMenus.Compose("Notepad++", false, false, withWindow).Select(t => t.Title));
    }

    [Theory]
    [InlineData("&File", "File", null)]
    [InlineData("Save &As...\tCtrl+Shift+S", "Save As...", "Ctrl+Shift+S")]
    [InlineData("Find && Replace", "Find & Replace", null)]
    [InlineData("&Help\t", "Help", null)]
    public void ClassicMenuTextLosesItsAccessKeyMarksAndKeepsItsShortcut(string raw, string title, string? shortcut) =>
        Assert.Equal((title, shortcut), StandardMenus.SplitMenuText(raw));

    [Theory]
    [InlineData("Progman", "explorer", ForegroundKind.Desktop)]
    [InlineData("WorkerW", "explorer", ForegroundKind.Desktop)]
    [InlineData("Shell_TrayWnd", "explorer", ForegroundKind.KeepPrevious)]
    [InlineData("XamlExplorerHostIslandWindow", "explorer", ForegroundKind.KeepPrevious)]
    [InlineData("Windows.UI.Core.CoreWindow", "SearchHost", ForegroundKind.KeepPrevious)]
    [InlineData("Windows.UI.Core.CoreWindow", "StartMenuExperienceHost", ForegroundKind.KeepPrevious)]
    [InlineData("", "chrome", ForegroundKind.KeepPrevious)]
    [InlineData("Chrome_WidgetWin_1", "chrome", ForegroundKind.App)]
    [InlineData("CabinetWClass", "explorer", ForegroundKind.App)]
    public void TheBarFollowsAppsAndTheDesktopButNotPassingShellSurfaces(string className, string process, ForegroundKind kind) =>
        Assert.Equal(kind, ForegroundRules.Classify(className, process));

    [Theory]
    [InlineData("Google Chrome", "chrome", null, "Google Chrome")]
    [InlineData("Notepad++ : a free (GNU) source code editor", "notepad++", null, "Notepad++")]
    [InlineData("Windows Explorer", "explorer", "Downloads", "File Explorer")]
    [InlineData("Application Frame Host", "ApplicationFrameHost", "Settings", "Settings")]
    [InlineData("", "claude", "Claude", "Claude")]
    [InlineData(null, "spotify", null, "Spotify")]
    [InlineData("An extremely long product description that reads like marketing copy", "tool", null, "Tool")]
    public void TheBoldNameIsWhatTheProgramCallsItself(string? description, string process, string? title, string expected) =>
        Assert.Equal(expected, ForegroundRules.DisplayName(description, process, title));

    [Theory]
    [InlineData("Chrome_WidgetWin_1", "chrome", false)]
    [InlineData("Chrome_WidgetWin_1", "claude", false)]
    [InlineData("Chrome_WidgetWin_1", "Code", true)]
    [InlineData("Chrome_WidgetWin_1", "Cursor", true)]
    [InlineData("Notepad", "Notepad", true)]
    [InlineData("WinUIDesktopWin32WindowClass", "AppleMusic", true)]
    public void ChromiumWindowsAreAskedForMenusOnlyWhenTheyDrawOne(string className, string process, bool asks) =>
        Assert.Equal(asks, ForegroundRules.UsesAutomation(className, process));

    [Fact]
    public void EveryWindowsSystemMenuBarIsNotAnAppMenu()
    {
        Assert.True(ForegroundRules.IsSystemMenuBar("SystemMenuBar"));
        Assert.False(ForegroundRules.IsSystemMenuBar("MenuBar"));
        Assert.False(ForegroundRules.IsSystemMenuBar(null));
    }

    [Fact]
    public void TitlesStopBeforeTheNotch()
    {
        // 1536 DIP wide, island centred, camera 135: about 620 DIP for titles.
        var available = MenuBarLayout.Available(1536, 0, 135);
        Assert.Equal(1536 / 2.0 - (135 / 2.0 + 56 + 16) - MenuBarLayout.LeftInset, available);
        Assert.Equal(MenuBarLayout.Available(1536, 0, 135) - 100, MenuBarLayout.Available(1536, -100, 135));
        Assert.Equal(0, MenuBarLayout.Available(200, 0, 135));

        Assert.Equal(0, MenuBarLayout.Fit([], 500));
        Assert.Equal(3, MenuBarLayout.Fit([100, 50, 50, 50], 204));
        Assert.Equal(4, MenuBarLayout.Fit([100, 50, 50, 50], 256));
        Assert.Equal(1, MenuBarLayout.Fit([300, 50], 100)); // the app's name always shows, trimmed
    }

    [Fact]
    public void TheClockReadsInTheCulturesOwnOrder()
    {
        var evening = new DateTime(2026, 11, 22, 19, 31, 5);
        Assert.Equal("Sun Nov 22  7:31 PM", StripClock.Format(evening, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Sun 22 Nov  19:31", StripClock.Format(evening, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal(TimeSpan.FromMilliseconds(54_950 + 50), StripClock.UntilNextMinute(evening.AddMilliseconds(50)));
        Assert.True(StripClock.UntilNextMinute(new DateTime(2026, 1, 1, 0, 0, 59, 999)) > TimeSpan.Zero);
    }
}
