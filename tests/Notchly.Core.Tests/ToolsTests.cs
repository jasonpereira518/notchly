// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream's quick toggles, quit-all, kill-process and port-manager specs: UtilitiesFeatureTests,
// QuickTogglesAlertTests, QuickTogglesQuitAllTests, FeatureCatalogTests (kill process), PortManagerRefreshTests,
// PointerInputFeatureTests (focus follows mouse) and FeatureCatalogTests (pointer acceleration), re-expressed for
// Windows' rows, executables and process times.
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Tools;

namespace Notchly.Core.Tests;

public class ToolsTests
{
    private static SettingsStore NewStore()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    // MARK: Quick toggles

    [Fact]
    public void QuickToggleRowsKeepUpstreamIdsAndAppendWindowsOnes()
    {
        Assert.Equal(["darkMode", "micMute", "emptyTrash", "quitAllApps", "ejectDisks", "hiddenFiles", "desktopIcons", "lockScreen", "displayOff", "screenSaver"],
            QuickToggleSupport.DefaultOrder.Take(10));
        Assert.Equal(["refreshRate", "wifi", "bluetooth"], QuickToggleSupport.DefaultOrder.Skip(10));
        Assert.DoesNotContain("keyboardLight", QuickToggleSupport.DefaultOrder);
        // Every row has a visibility key, and it is on by default.
        var store = NewStore();
        foreach (var id in QuickToggleSupport.DefaultOrder) Assert.True(store.GetBool(QuickToggleSupport.VisibilityKey(id)), id);
        Assert.Equal("panelUtilityMicMute", QuickToggleSupport.VisibilityKey("micMute"));
        Assert.True(store.GetBool(DefaultsKey.PanelShowToggles));
    }

    [Fact]
    public void RowsAreGatedByTheirFeatures()
    {
        Assert.Equal(AppFeature.MicMute, QuickToggleSupport.GatedBy("micMute"));
        Assert.Equal(AppFeature.QuitApps, QuickToggleSupport.GatedBy("quitAllApps"));
        foreach (var id in new[] { "darkMode", "emptyTrash", "lockScreen", "refreshRate", "wifi" })
            Assert.Equal(AppFeature.QuickToggles, QuickToggleSupport.GatedBy(id));
    }

    [Fact]
    public void ASavedOrderIsKeptAndTheRestFollowsInDefaultOrder()
    {
        Assert.Equal(QuickToggleSupport.DefaultOrder, QuickToggleSupport.SanitizedOrder(""));
        Assert.Equal(QuickToggleSupport.DefaultOrder, QuickToggleSupport.SanitizedOrder(null));
        var order = QuickToggleSupport.SanitizedOrder("lockScreen, nonsense, darkMode, lockScreen");
        Assert.Equal(["lockScreen", "darkMode"], order.Take(2));
        Assert.Equal(QuickToggleSupport.DefaultOrder.Length, order.Count);
        Assert.Equal(order.Count, order.Distinct().Count());
    }

    [Fact]
    public void VisibleRowsFollowTheSwitchesAndTheInstalledFeatures()
    {
        var store = NewStore();
        store.SetAvailable(AppFeature.QuickToggles, true);
        Assert.Contains("darkMode", QuickToggleSupport.VisibleRows(store));
        // The quit and microphone rows wait for their own features.
        Assert.DoesNotContain("quitAllApps", QuickToggleSupport.VisibleRows(store));
        Assert.Contains("micMute", QuickToggleSupport.VisibleRows(store));
        store.SetAvailable(AppFeature.MicMute, false);
        Assert.DoesNotContain("micMute", QuickToggleSupport.VisibleRows(store));
        store.SetAvailable(AppFeature.QuitApps, true);
        Assert.Contains("quitAllApps", QuickToggleSupport.VisibleRows(store));
        store.Set(DefaultsKey.PanelToggleDarkMode, false);
        Assert.DoesNotContain("darkMode", QuickToggleSupport.VisibleRows(store));
        store.Set(DefaultsKey.PanelToggleOrder, "lockScreen");
        Assert.Equal("lockScreen", QuickToggleSupport.VisibleRows(store)[0]);
    }

    [Fact]
    public void FailuresClearAndPermissionsStay()
    {
        Assert.True(QuickToggleSupport.AutoClears(QuickToggleSupport.RunState.Failed));
        Assert.False(QuickToggleSupport.AutoClears(QuickToggleSupport.RunState.NeedsPermission));
        Assert.False(QuickToggleSupport.AutoClears(QuickToggleSupport.RunState.Running));
        Assert.Equal(2.4, QuickToggleSupport.FailureDisplaySeconds);
    }

    [Fact]
    public void WindowsSettingsReadAsFlags()
    {
        Assert.True(QuickToggleSupport.HiddenFilesShown(1));
        Assert.False(QuickToggleSupport.HiddenFilesShown(2));
        Assert.False(QuickToggleSupport.HiddenFilesShown(null));
        Assert.False(QuickToggleSupport.DesktopIconsShown(1));
        Assert.True(QuickToggleSupport.DesktopIconsShown(0));
        Assert.True(QuickToggleSupport.DesktopIconsShown(null));
        Assert.True(QuickToggleSupport.DarkModeOn(0));
        Assert.False(QuickToggleSupport.DarkModeOn(1));
        Assert.False(QuickToggleSupport.DarkModeOn(null));
    }

    [Fact]
    public void EjectOffersExternalAndRemovableDrivesOnly()
    {
        // An external bus is offered even though Windows calls it fixed media.
        Assert.True(QuickToggleSupport.ShouldOfferEject(new(true, false, true, false, false)));
        Assert.True(QuickToggleSupport.ShouldOfferEject(new(true, false, false, true, false)));
        Assert.True(QuickToggleSupport.ShouldOfferEject(new(true, false, false, false, true)));
        // An internal fixed drive is not; nor the system drive, nor a network one.
        Assert.False(QuickToggleSupport.ShouldOfferEject(new(true, false, false, false, false)));
        Assert.False(QuickToggleSupport.ShouldOfferEject(new(true, true, true, true, true)));
        Assert.False(QuickToggleSupport.ShouldOfferEject(new(false, false, true, true, true)));
    }

    [Fact]
    public void EjectExclusionsMatchByNameIdOrPath()
    {
        Assert.True(QuickToggleSupport.IsExcluded(["BACKUP"], "backup", null, @"E:\"));
        Assert.True(QuickToggleSupport.IsExcluded(["{1234}"], "Disk", "{1234}", @"E:\"));
        Assert.True(QuickToggleSupport.IsExcluded([@"e:"], "Disk", null, @"E:\"));
        Assert.True(QuickToggleSupport.IsExcluded([@"E:\"], "Disk", null, @"E:"));
        Assert.False(QuickToggleSupport.IsExcluded(["Other", " "], "Disk", "{1234}", @"E:\"));
        Assert.False(QuickToggleSupport.IsExcluded([], "Disk", null, @"E:\"));
    }

    [Fact]
    public void TheRefreshRateMovesBetweenTheHighestAndSixty()
    {
        int[] rates = [60, 120, 240];
        Assert.Equal(60, QuickToggleSupport.NextRefreshRate(240, rates));
        Assert.Equal(240, QuickToggleSupport.NextRefreshRate(60, rates));
        Assert.Equal(240, QuickToggleSupport.NextRefreshRate(120, rates));
        Assert.Null(QuickToggleSupport.NextRefreshRate(60, [60]));
        Assert.Null(QuickToggleSupport.NextRefreshRate(60, []));
        Assert.Equal(48, QuickToggleSupport.NextRefreshRate(75, [48, 75]));
    }

    [Fact]
    public void TheConfirmationOpensOnlyForARunnableAction()
    {
        Assert.True(QuickToggleSupport.OpensConfirmation(true, QuickToggleSupport.RunState.Idle));
        Assert.False(QuickToggleSupport.OpensConfirmation(false, QuickToggleSupport.RunState.Idle));
        Assert.False(QuickToggleSupport.OpensConfirmation(true, QuickToggleSupport.RunState.Running));
        Assert.Equal(QuickToggleSupport.ConfirmChoice.Confirm, QuickToggleSupport.ChoiceForKey("\r"));
        Assert.Equal(QuickToggleSupport.ConfirmChoice.Cancel, QuickToggleSupport.ChoiceForKey("\u001b"));
        Assert.Equal(QuickToggleSupport.ConfirmChoice.None, QuickToggleSupport.ChoiceForKey("x"));
    }

    // MARK: Quit apps

    private static readonly QuitAppsCandidate[] Apps =
    [
        new(10, "textedit.exe", "TextEdit", true), new(11, "safari.exe", "Safari", true), new(12, "notes.exe", "Notes", true),
        new(13, "explorer.exe", "Explorer", true), new(14, "agent.exe", "Agent", false), new(15, "notchly.exe", "Notchly", true),
    ];

    [Fact]
    public void QuitAllExceptionsAreSanitized()
    {
        var saved = new[] { "  safari.exe  ", "safari.exe", "explorer.exe", "", "notchly.exe", "keep.exe", "NOTCHLY.EXE" };
        Assert.Equal(["explorer.exe", "safari.exe", "keep.exe"], QuitAppsSupport.SanitizedExceptions(saved));
        Assert.Equal(["explorer.exe"], QuitAppsSupport.SanitizedExceptions([]));
    }

    [Fact]
    public void BatchTargetsSkipKeptBackgroundAndSelfApps()
    {
        var exceptions = QuitAppsSupport.SanitizedExceptions(["safari.exe"]);
        Assert.Equal([10, 12], QuitAppsSupport.Targets(Apps, exceptions));
        Assert.Equal([10], QuitAppsSupport.Targets(Apps, exceptions, keepingPid: 12));
        Assert.Equal([10, 12], QuitAppsSupport.Targets(Apps, exceptions, keepingPid: 13));
        Assert.False(QuitAppsSupport.ShouldQuitApp(new(1, "", "Nameless", true), exceptions));
    }

    [Fact]
    public void TheListShowsKeptAppsButNeverExplorerOrSelf()
    {
        var apps = Apps.Select(a => a.Pid == 12 ? a with { IsFront = true } : a).ToArray();
        var listed = QuitAppsSupport.ListedApps(apps);
        Assert.Equal([12, 11, 10], listed.Select(a => a.Pid));
        Assert.DoesNotContain(listed, a => a.Id == "explorer.exe" || a.Id == "notchly.exe" || a.Pid == 14);
        // A kept app is listed, and its own row can still be quit.
        Assert.True(QuitAppsSupport.ListsApp(new(11, "safari.exe", "Safari", true)));
        Assert.True(QuitAppsSupport.CanQuitRow("safari.exe"));
        Assert.False(QuitAppsSupport.CanQuitRow("notchly.exe"));
    }

    [Fact]
    public void ButtonsDependOnWhatCanBeQuit()
    {
        Assert.Equal([QuitAppsButton.Cancel, QuitAppsButton.AllExceptFront, QuitAppsButton.All], QuitAppsSupport.Buttons(true, true));
        Assert.Equal([QuitAppsButton.Cancel, QuitAppsButton.All], QuitAppsSupport.Buttons(false, true));
        Assert.Equal([QuitAppsButton.Cancel], QuitAppsSupport.Buttons(false, false));
        var exceptions = QuitAppsSupport.SanitizedExceptions(["safari.exe"]);
        var listed = QuitAppsSupport.ListedApps(Apps.Select(a => a.Pid == 10 ? a with { IsFront = true } : a));
        Assert.True(QuitAppsSupport.OffersAllExceptFront(listed, exceptions));
        Assert.True(QuitAppsSupport.OffersAll(listed, exceptions));
        // A kept front app has no "except front" button; nothing to quit means no "all".
        var keptFront = QuitAppsSupport.ListedApps(Apps.Select(a => a.Pid == 11 ? a with { IsFront = true } : a));
        Assert.False(QuitAppsSupport.OffersAllExceptFront(keptFront, exceptions));
        Assert.False(QuitAppsSupport.OffersAll([new(11, "safari.exe", "Safari", true)], exceptions));
        // The only quittable app is the front one: there is nothing else for "except front" to quit.
        Assert.False(QuitAppsSupport.OffersAllExceptFront([new(10, "textedit.exe", "TextEdit", true, true)], exceptions));
    }

    [Fact]
    public void ReturnRestsOnTheButtonTheEntryPointAskedFor()
    {
        var all = new[] { QuitAppsButton.Cancel, QuitAppsButton.AllExceptFront, QuitAppsButton.All };
        Assert.Equal(QuitAppsButton.All, QuitAppsSupport.RestingButton(QuitAppsScope.All, all));
        Assert.Equal(QuitAppsButton.AllExceptFront, QuitAppsSupport.RestingButton(QuitAppsScope.AllExceptFront, all));
        Assert.Equal(QuitAppsButton.Cancel, QuitAppsSupport.RestingButton(QuitAppsScope.AllExceptFront, [QuitAppsButton.Cancel, QuitAppsButton.All]));
        Assert.Equal(QuitAppsButton.Cancel, QuitAppsSupport.RestingButton(null, all));
    }

    [Fact]
    public void FocusMovesThroughRowsAndButtons()
    {
        var buttons = new[] { QuitAppsButton.Cancel, QuitAppsButton.AllExceptFront, QuitAppsButton.All };
        var resting = QuitAppsButton.AllExceptFront;
        // Down from the last row lands on the resting button; up from a button goes to the last row.
        Assert.Equal(QuitAppsFocus.OnButton(resting), QuitAppsSupport.Move(QuitAppsFocus.OnRow(2), QuitAppsMove.Down, 3, buttons, resting));
        Assert.Equal(QuitAppsFocus.OnRow(1), QuitAppsSupport.Move(QuitAppsFocus.OnRow(0), QuitAppsMove.Down, 3, buttons, resting));
        Assert.Equal(QuitAppsFocus.OnRow(0), QuitAppsSupport.Move(QuitAppsFocus.OnRow(0), QuitAppsMove.Up, 3, buttons, resting));
        Assert.Equal(QuitAppsFocus.OnRow(2), QuitAppsSupport.Move(QuitAppsFocus.OnButton(resting), QuitAppsMove.Up, 3, buttons, resting));
        // With no rows, up stays on the button.
        Assert.Equal(QuitAppsFocus.OnButton(resting), QuitAppsSupport.Move(QuitAppsFocus.OnButton(resting), QuitAppsMove.Up, 0, buttons, resting));
        // Left and right clamp at the ends of the bar; Tab and Shift+Tab wrap.
        Assert.Equal(QuitAppsFocus.OnButton(QuitAppsButton.Cancel), QuitAppsSupport.Move(QuitAppsFocus.OnButton(QuitAppsButton.Cancel), QuitAppsMove.Left, 3, buttons, resting));
        Assert.Equal(QuitAppsFocus.OnButton(QuitAppsButton.All), QuitAppsSupport.Move(QuitAppsFocus.OnButton(QuitAppsButton.All), QuitAppsMove.Right, 3, buttons, resting));
        Assert.Equal(QuitAppsFocus.OnButton(QuitAppsButton.Cancel), QuitAppsSupport.Move(QuitAppsFocus.OnButton(QuitAppsButton.All), QuitAppsMove.Next, 3, buttons, resting));
        Assert.Equal(QuitAppsFocus.OnButton(QuitAppsButton.All), QuitAppsSupport.Move(QuitAppsFocus.OnButton(QuitAppsButton.Cancel), QuitAppsMove.Previous, 3, buttons, resting));
    }

    [Fact]
    public void AVanishedRowHandsFocusToTheOneNowThere()
    {
        Assert.Equal(QuitAppsFocus.OnRow(1), QuitAppsSupport.AfterListChange(QuitAppsFocus.OnRow(1), 3, QuitAppsButton.All));
        Assert.Equal(QuitAppsFocus.OnRow(1), QuitAppsSupport.AfterListChange(QuitAppsFocus.OnRow(2), 2, QuitAppsButton.All));
        Assert.Equal(QuitAppsFocus.OnButton(QuitAppsButton.All), QuitAppsSupport.AfterListChange(QuitAppsFocus.OnRow(0), 0, QuitAppsButton.All));
        Assert.Equal(QuitAppsFocus.OnButton(QuitAppsButton.Cancel), QuitAppsSupport.AfterListChange(QuitAppsFocus.OnButton(QuitAppsButton.Cancel), 0, QuitAppsButton.All));
    }

    [Fact]
    public void TypeToJumpMatchesNamePrefixesIgnoringCaseAndAccents()
    {
        string[] names = ["Alpha", "Safari", "Notes", "\u00C9clair", "Numbers", "Zed"];
        Assert.Equal(1, QuitAppsSupport.Jump("SAF", names));
        Assert.Equal(3, QuitAppsSupport.Jump("ecl", names));
        Assert.Equal(2, QuitAppsSupport.Jump("no", names));
        Assert.Equal(4, QuitAppsSupport.Jump("nu", names));
        Assert.Equal(5, QuitAppsSupport.Jump("z", names));
        Assert.Null(QuitAppsSupport.Jump("x", names));
        Assert.Null(QuitAppsSupport.Jump("", names));
    }

    [Fact]
    public void TypeAheadForgetsAfterASecond()
    {
        var typed = new QuitAppsSupport.TypeAhead();
        Assert.Equal("s", typed.Add('s', 10));
        Assert.Equal("sa", typed.Add('a', 10.5));
        Assert.Equal("n", typed.Add('n', 12));
    }

    [Fact]
    public void QuitAppsDefaults()
    {
        var s = NewStore();
        Assert.False(s.GetBool(DefaultsKey.QuitAppsShortcutEnabled));
        Assert.False(s.GetBool(DefaultsKey.QuitAppsExceptFrontShortcutEnabled));
        Assert.Equal("explorer.exe", s.GetString(DefaultsKey.QuitAppsKeptApps));
        Assert.NotNull(Shortcuts.Shortcut.Parse(s.GetString(DefaultsKey.QuitAppsShortcut)));
        Assert.NotNull(Shortcuts.Shortcut.Parse(s.GetString(DefaultsKey.QuitAppsExceptFrontShortcut)));
        Assert.False(FeatureCatalog.Info(AppFeature.QuitApps).InstalledByDefault);
        Assert.Empty(FeatureCatalog.Info(AppFeature.QuitApps).EnabledKeys);
    }

    // MARK: Kill process

    [Fact]
    public void ProtectedProcessesAreNeverKilled()
    {
        Assert.True(KillProcessSupport.IsProtected(0, "System Idle Process", 999, true));
        Assert.True(KillProcessSupport.IsProtected(4, "System", 999, true));
        Assert.True(KillProcessSupport.IsProtected(612, "csrss.exe", 999, true));
        Assert.True(KillProcessSupport.IsProtected(700, "LSASS.EXE", 999, true));
        Assert.True(KillProcessSupport.IsProtected(900, "dwm.exe", 999, true));
        Assert.True(KillProcessSupport.IsProtected(999, "anything", 999, true));
        Assert.True(KillProcessSupport.IsProtected(1200, "Notchly.exe", 999, true));
        // A row whose start time could not be read cannot be identified safely.
        Assert.True(KillProcessSupport.IsProtected(12345, "chrome.exe", 999, false));
        Assert.False(KillProcessSupport.IsProtected(12345, "chrome.exe", 999, true));
    }

    [Fact]
    public void OrderingIsStrictAndTiesBreakByPid()
    {
        Assert.True(KillProcessSupport.NumberComesBefore(90, 10, ascending: false));
        Assert.False(KillProcessSupport.NumberComesBefore(10, 10, ascending: false));
        Assert.True(KillProcessSupport.NumberComesBefore(10, 90, ascending: true));
        Assert.False(KillProcessSupport.NameComesBefore("Same", "same", ascending: true));
        Assert.True(KillProcessSupport.NameComesBefore("Alpha", "Zulu", ascending: true));
        Assert.True(KillProcessSupport.NameComesBefore("Zulu", "Alpha", ascending: false));
        var rows = new KillRow[] { new(30, "b", 5, 100), new(10, "a", 5, 300), new(20, "c", 9, 200) };
        Assert.Equal([20, 10, 30], KillProcessSupport.Sorted(rows, KillSort.Cpu, false).Select(r => r.Pid));
        Assert.Equal([10, 20, 30], KillProcessSupport.Sorted(rows, KillSort.Memory, false).Select(r => r.Pid));
        Assert.Equal([10, 30, 20], KillProcessSupport.Sorted(rows, KillSort.Name, true).Select(r => r.Pid));
        Assert.Equal([30, 20, 10], KillProcessSupport.Sorted(rows, KillSort.Pid, false).Select(r => r.Pid));
    }

    [Fact]
    public void SortingAndSearchingFollowUpstream()
    {
        Assert.Equal(KillSort.Cpu, KillProcessSupport.ParseSort("nonsense"));
        Assert.Equal(KillSort.Memory, KillProcessSupport.ParseSort("memory"));
        Assert.Equal("pid", KillProcessSupport.SortName(KillSort.Pid));
        Assert.True(KillProcessSupport.DefaultAscending(KillSort.Name));
        Assert.False(KillProcessSupport.DefaultAscending(KillSort.Cpu));
        Assert.False(KillProcessSupport.DefaultAscending(KillSort.Pid));
        Assert.True(KillProcessSupport.Matches("chro", "Chrome", 1));
        Assert.True(KillProcessSupport.Matches("1234", "Other", 1234));
        Assert.False(KillProcessSupport.Matches("123", "Other", 1234));
        Assert.True(KillProcessSupport.Matches("  ", "Anything", 1));
        var s = NewStore();
        Assert.Equal("cpu", s.GetString(DefaultsKey.KillProcessSortBy));
        Assert.False(s.GetBool(DefaultsKey.KillProcessSortAscending));
        Assert.True(s.GetBool(DefaultsKey.KillProcessGroupRelated));
        Assert.True(FeatureCatalog.Info(AppFeature.KillProcess).IsBeta);
    }

    [Fact]
    public void DescendantsAreBreadthFirstAndCycleSafe()
    {
        (int Pid, int Parent)[] pairs = [(10, 1), (20, 10), (21, 10), (30, 20), (40, 1), (50, 50), (22, 21), (21, 22)];
        Assert.Equal([20, 21, 30, 22], KillProcessSupport.Descendants(pairs, 10));
        Assert.Empty(KillProcessSupport.Descendants(pairs, 30));
        Assert.Empty(KillProcessSupport.Descendants(pairs, 50));
        // A tree kill takes the deepest first.
        Assert.Equal([22, 30, 21, 20], KillProcessSupport.TreeKillOrder(pairs, 10));
    }

    [Fact]
    public void DescendantsAreCapped()
    {
        var pairs = Enumerable.Range(2, 6000).Select(i => (i, 1)).ToList();
        Assert.Equal(KillProcessSupport.DescendantCap, KillProcessSupport.Descendants(pairs, 1).Count);
    }

    [Fact]
    public void AKillNeedsTheSameStartTime()
    {
        Assert.True(KillProcessSupport.SameProcess(100, 100));
        Assert.False(KillProcessSupport.SameProcess(100, 101));
        Assert.False(KillProcessSupport.SameProcess(null, 100));
        Assert.False(KillProcessSupport.SameProcess(100, null));
    }

    [Fact]
    public void AFailedReadNeverWipesTheList()
    {
        Assert.False(KillProcessSupport.ShouldReplaceList(null));
        Assert.False(KillProcessSupport.ShouldReplaceList([]));
        Assert.True(KillProcessSupport.ShouldReplaceList([new KillRow(1, "a", 0, 0)]));
    }

    [Fact]
    public void ConfirmationsNameWhatWillHappen()
    {
        Assert.Equal("Kill Chrome?", KillProcessSupport.ConfirmTitle(KillProcessSupport.KillKind.Kill, "Chrome"));
        Assert.Equal("Force Kill Chrome?", KillProcessSupport.ConfirmTitle(KillProcessSupport.KillKind.Force, "Chrome"));
        Assert.Equal("Kill all \u201CChrome\u201D processes?", KillProcessSupport.ConfirmTitle(KillProcessSupport.KillKind.KillAll, "Chrome"));
        Assert.Equal("Kill Chrome and all its child processes?", KillProcessSupport.ConfirmTitle(KillProcessSupport.KillKind.Tree, "Chrome"));
    }

    // MARK: Port manager

    [Fact]
    public void PortsAreDedupedAndSorted()
    {
        PortEntry[] entries =
        [
            new(3000, "TCP", "127.0.0.1", 123, "Example Server", 1), new(3000, "TCP", "127.0.0.1", 123, "Example Server", 1),
            new(3000, "TCP", "[::1]", 123, "Example Server", 1), new(3001, "TCP", "*", 123, "Example Server", 1),
            new(3000, "TCP", "*", 456, "Another", 2),
        ];
        var result = PortManagerSupport.DedupedAndSorted(entries);
        Assert.Equal([3000, 3000, 3000, 3001], result.Select(e => e.Port));
        Assert.Equal(3, result.Count(e => e.Pid == 123));
        Assert.Equal("Another", result[0].ProcessName);
    }

    [Fact]
    public void WildcardAddressesAreMarkedAsExposed()
    {
        foreach (var exposed in new[] { "*", "0.0.0.0", "::", "[::]" }) Assert.True(PortManagerSupport.ListensOnAllInterfaces(exposed), exposed);
        foreach (var local in new[] { "127.0.0.1", "[::1]", "192.168.1.20", "[fe80::1]", "", ":80" }) Assert.False(PortManagerSupport.ListensOnAllInterfaces(local), local);
    }

    [Fact]
    public void BrowserLinksFollowUpstream()
    {
        Assert.Equal("http://localhost:3000", PortManagerSupport.BrowserUrl("TCP", "*", 3000));
        Assert.Equal("http://127.0.0.1:3000", PortManagerSupport.BrowserUrl("TCP", "0.0.0.0", 3000));
        Assert.Equal("http://[::1]:3000", PortManagerSupport.BrowserUrl("TCP", "[::]", 3000));
        Assert.Equal("http://[::1]:3000", PortManagerSupport.BrowserUrl("TCP", "::", 3000));
        Assert.Equal("http://[fe80::1%25eth0]:3000", PortManagerSupport.BrowserUrl("TCP", "[fe80::1%eth0]", 3000));
        Assert.Equal("http://[fe80::1]:3000", PortManagerSupport.BrowserUrl("TCP", "fe80::1", 3000));
        Assert.Equal("http://192.168.1.20:8080", PortManagerSupport.BrowserUrl("TCP", "192.168.1.20", 8080));
        Assert.Null(PortManagerSupport.BrowserUrl("UDP", "*", 3000));
        Assert.Null(PortManagerSupport.BrowserUrl("TCP", "", 3000));
        Assert.Null(PortManagerSupport.BrowserUrl("TCP", "*", 0));
        Assert.Null(PortManagerSupport.BrowserUrl("TCP", "*", 65536));
    }

    [Fact]
    public void ThePortFilterMatchesPortProcessPidAndAddress()
    {
        var entry = new PortEntry(5173, "TCP", "127.0.0.1", 4242, "Node.js", 1);
        Assert.True(PortManagerSupport.Matches(entry, "5173"));
        Assert.True(PortManagerSupport.Matches(entry, "NODE"));
        Assert.True(PortManagerSupport.Matches(entry, "4242"));
        Assert.True(PortManagerSupport.Matches(entry, "127.0"));
        Assert.True(PortManagerSupport.Matches(entry, "  "));
        Assert.False(PortManagerSupport.Matches(entry, "python"));
    }

    [Fact]
    public void AStartTimeIsKeptOnlyWhenItDidNotMove()
    {
        Assert.Equal(100, PortManagerSupport.KeepStart(100, 100));
        Assert.Null(PortManagerSupport.KeepStart(100, 101));
        Assert.Null(PortManagerSupport.KeepStart(null, 100));
        Assert.Null(PortManagerSupport.KeepStart(100, null));
    }

    [Fact]
    public void RefreshIsSingleFlightAndAFailureKeepsTheList()
    {
        var state = new PortManagerSupport.RefreshState();
        Assert.True(state.TryBegin());
        Assert.False(state.TryBegin());
        // A first-run failure: nothing loaded, flagged.
        state.Complete(null);
        Assert.True(state.RefreshFailed);
        Assert.False(state.HasLoadedOnce);
        Assert.Empty(state.Entries);
        Assert.False(state.IsRefreshing);
        // A success loads and clears the flag.
        Assert.True(state.TryBegin());
        var entry = new PortEntry(3000, "TCP", "*", 1, "node", 1);
        state.Complete([entry]);
        Assert.False(state.RefreshFailed);
        Assert.True(state.HasLoadedOnce);
        Assert.Equal([entry], state.Entries);
        // A later failure keeps what was shown.
        Assert.True(state.TryBegin());
        state.Complete(null);
        Assert.True(state.RefreshFailed);
        Assert.True(state.HasLoadedOnce);
        Assert.Equal([entry], state.Entries);
        // An empty success replaces the list and clears the failure.
        Assert.True(state.TryBegin());
        state.Complete([]);
        Assert.False(state.RefreshFailed);
        Assert.Empty(state.Entries);
    }

    // MARK: Mouse toggles

    [Fact]
    public void TheFocusDelayIsClamped()
    {
        Assert.Equal(100, FocusFollowsSupport.SanitizedDelay(0));
        Assert.Equal(3000, FocusFollowsSupport.SanitizedDelay(4000));
        foreach (var ms in new[] { 100, 250, 1000, 2000, 2500, 3000 }) Assert.Equal(ms, FocusFollowsSupport.SanitizedDelay(ms));
    }

    [Fact]
    public void MouseTogglesDefaultToOffAndKeepTheirNames()
    {
        var s = NewStore();
        Assert.False(s.GetBool(DefaultsKey.FocusFollowsMouseEnabled));
        Assert.Equal(250, s.GetInt(DefaultsKey.FocusFollowsMouseDelayMilliseconds, -1));
        Assert.True(s.GetBool(DefaultsKey.FocusFollowsMouseRaise));
        Assert.False(s.GetBool(DefaultsKey.MouseAccelerationDisabled));
        Assert.True(FeatureCatalog.Info(AppFeature.MouseAcceleration).InstalledByDefault);
        Assert.False(FeatureCatalog.Info(AppFeature.FocusFollowsMouse).InstalledByDefault);
    }

    [Fact]
    public void TheSavedOriginalsRoundTripAndRejectGarbage()
    {
        var focus = new FocusFollowsSupport.Original(true, false, 500);
        Assert.Equal(focus, FocusFollowsSupport.Original.Parse(focus.Storage));
        Assert.Null(FocusFollowsSupport.Original.Parse(""));
        Assert.Null(FocusFollowsSupport.Original.Parse("2;1;5"));
        Assert.Null(FocusFollowsSupport.Original.Parse("1;1;-5"));
        var mouse = new PointerAccelerationSupport.Original(6, 10, 1);
        Assert.Equal(mouse, PointerAccelerationSupport.Original.Parse(mouse.Storage));
        Assert.Equal(new PointerAccelerationSupport.Original(6, 10, 0), mouse.WithoutAcceleration);
        Assert.Null(PointerAccelerationSupport.Original.Parse("6;10"));
        Assert.Null(PointerAccelerationSupport.Original.Parse("6;10;9"));
        Assert.Null(PointerAccelerationSupport.Original.Parse("a;b;c"));
    }

    [Fact]
    public void AccelerationIsCapturedBeforeItIsChangedAndRestoredAfter()
    {
        Assert.Equal(PointerAccelerationSupport.Step.CaptureAndApply, PointerAccelerationSupport.Plan(wantsOff: true, journaled: false, accelerationOnNow: true));
        // Already off with nothing written down: nothing to take and nothing to give back.
        Assert.Equal(PointerAccelerationSupport.Step.Nothing, PointerAccelerationSupport.Plan(true, false, false));
        // On, and something changed it back: apply again, keeping the first original.
        Assert.Equal(PointerAccelerationSupport.Step.Reapply, PointerAccelerationSupport.Plan(true, true, true));
        Assert.Equal(PointerAccelerationSupport.Step.Nothing, PointerAccelerationSupport.Plan(true, true, false));
        // Off (or quit, or a crash found at launch): give the original back.
        Assert.Equal(PointerAccelerationSupport.Step.Restore, PointerAccelerationSupport.Plan(false, true, false));
        Assert.Equal(PointerAccelerationSupport.Step.Nothing, PointerAccelerationSupport.Plan(false, false, true));
    }

    [Fact]
    public void ToolJournalsStayOutOfBackups()
    {
        var s = NewStore();
        s.Set(DefaultsKey.MouseAccelerationOriginal, "6;10;1");
        s.Set(DefaultsKey.FocusFollowsMouseOriginal, "0;0;500");
        s.Set(DefaultsKey.QuitAppsKeptApps, "explorer.exe,keep.exe");
        var export = SettingsBackup.Export(s);
        Assert.DoesNotContain("mouseAccelerationOriginal", export);
        Assert.DoesNotContain("focusFollowsMouseOriginal", export);
        Assert.Contains("quitAppsKeptApps", export);
    }
}
