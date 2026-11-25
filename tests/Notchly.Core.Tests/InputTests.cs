// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream's scroll, mouse button, debounce, super key, snippet and quit protection specs: PointerInputFeatureTests,
// LinearScrollTapTests, ScrollHorizontalModifierTests, KeyboardDebounceTapTests, SuperKeyTapContract, UtilitiesFeatureTests (snippets)
// and the quit protection checks of FeatureCatalogTests. Wheel units are Windows' (120 to a notch); times are in milliseconds.
using System.Globalization;
using Notchly.Core.Input;
using Notchly.Core.Settings;
using static Notchly.Core.Input.ButtonShortcutSupport;

namespace Notchly.Core.Tests;

public class InputTests
{
    private static SettingsStore NewStore()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        return store;
    }

    // MARK: Scroll

    [Fact]
    public void OnlyWholeNotchesComeFromAMouseWheel()
    {
        Assert.True(ScrollSupport.IsMouseWheel(120));
        Assert.True(ScrollSupport.IsMouseWheel(-240));
        Assert.False(ScrollSupport.IsMouseWheel(30));
        Assert.False(ScrollSupport.IsMouseWheel(0));
        // The input source, when Windows names it, decides.
        Assert.True(ScrollSupport.IsMouseWheel(30, sourceIsMouse: true));
        Assert.False(ScrollSupport.IsMouseWheel(120, sourceIsMouse: false));
    }

    [Fact]
    public void LinearScrollCapsAtOneNotch()
    {
        var carry = 0.0;
        Assert.Equal(120, ScrollSupport.LinearDelta(120, 3, 3, ref carry));
        Assert.Equal(120, ScrollSupport.LinearDelta(600, 3, 3, ref carry));
        Assert.Equal(-120, ScrollSupport.LinearDelta(-480, 3, 3, ref carry));
        // More lines per notch than Windows' own setting: 5 against 3.
        Assert.Equal(200, ScrollSupport.LinearDelta(120, 5, 3, ref carry));
        Assert.Equal(0, ScrollSupport.LinearDelta(0, 3, 3, ref carry));
    }

    [Fact]
    public void FractionsOfANotchAddUpExactly()
    {
        var carry = 0.0;
        var total = 0;
        for (var i = 0; i < 4; i++) total += ScrollSupport.LinearDelta(30, 3, 3, ref carry);
        Assert.Equal(120, total);
        // With rounding in play, nothing is lost: seven 17-unit ticks at 5 lines against 3.
        carry = 0;
        total = 0;
        for (var i = 0; i < 7; i++) total += ScrollSupport.LinearDelta(17, 5, 3, ref carry);
        Assert.InRange(total, 198, 199);
        // A reversal drops what was carried.
        carry = 0.4;
        ScrollSupport.LinearDelta(-120, 3, 3, ref carry);
        Assert.Equal(0, carry, 6);
    }

    [Fact]
    public void LinearAndSpeedSettingsAreSanitized()
    {
        Assert.Equal(3, ScrollSupport.SanitizedLines(0));
        Assert.Equal(3, ScrollSupport.SanitizedLines(11));
        Assert.Equal(7, ScrollSupport.SanitizedLines(7));
        Assert.Equal(2, ScrollSupport.SanitizedMultiplier(1));
        Assert.Equal(15, ScrollSupport.SanitizedMultiplier(40));
        Assert.Equal(360, ScrollSupport.SpeedDelta(120, 3));
        Assert.Equal(-360, ScrollSupport.SpeedDelta(-120, 3));
        Assert.Equal(short.MaxValue, ScrollSupport.SpeedDelta(20000, 15));
        Assert.Equal(short.MinValue, ScrollSupport.SpeedDelta(-20000, 15));
    }

    [Fact]
    public void DirectionPlansFollowTheTickAndTheModifier()
    {
        Assert.True(ScrollSupport.Invert(true, false, horizontalTick: false, shiftHeldOnVertical: false).Negate);
        Assert.False(ScrollSupport.Invert(false, true, false, false).Negate);
        Assert.True(ScrollSupport.Invert(false, true, horizontalTick: true, shiftHeldOnVertical: false).Negate);
        Assert.False(ScrollSupport.Invert(true, false, true, false).Negate);
        // A vertical tick Shift turned sideways follows the horizontal switch.
        Assert.True(ScrollSupport.Invert(false, true, false, shiftHeldOnVertical: true).Negate);
        Assert.False(ScrollSupport.Invert(true, false, false, true).Negate);
    }

    [Fact]
    public void SidewaysAndZoomNeedTheirModifierAlone()
    {
        Assert.True(ScrollSupport.HeldAlone(new HashSet<string> { "shift" }, "shift"));
        Assert.False(ScrollSupport.HeldAlone(new HashSet<string> { "shift", "control" }, "shift"));
        Assert.False(ScrollSupport.HeldAlone(new HashSet<string>(), "shift"));
        Assert.False(ScrollSupport.HeldAlone(new HashSet<string> { "shift" }, "none"));
        // Scrolling down (negative) moves right (positive), and back.
        Assert.Equal(120, ScrollSupport.SidewaysDelta(-120));
        Assert.Equal(-120, ScrollSupport.SidewaysDelta(120));
        Assert.Equal("shift", ScrollSupport.SanitizedModifier(null, ScrollSupport.SidewaysModifiers, "shift"));
        Assert.Equal("control", ScrollSupport.SanitizedModifier("shift", ScrollSupport.ZoomModifiers, "control"));
        Assert.Equal("alt", ScrollSupport.SanitizedModifier("ALT", ScrollSupport.ZoomModifiers, "control"));
        Assert.True(ScrollSupport.ZoomNeedsCtrl("alt"));
        Assert.False(ScrollSupport.ZoomNeedsCtrl("control"));
        Assert.False(ScrollSupport.ZoomNeedsCtrl("none"));
    }

    [Fact]
    public void ExceptionsMatchProgramsIgnoringCase()
    {
        Assert.True(ScrollSupport.IsExcepted("Code.exe", ["code.exe", "steam.exe"]));
        Assert.False(ScrollSupport.IsExcepted("chrome.exe", ["code.exe"]));
        Assert.False(ScrollSupport.IsExcepted(null, ["code.exe"]));
        Assert.False(ScrollSupport.IsExcepted("code.exe", []));
    }

    [Fact]
    public void ASmoothGlideFrameFollowsUpstreamsMath()
    {
        Assert.Equal(18.4, ScrollSupport.FrameDelta(100, 1.0 / 60, 65), 0);
        Assert.Equal(0.8, ScrollSupport.FrameDelta(0.8, 1.0 / 60, 65));
        Assert.Equal(1, ScrollSupport.FrameDelta(3, 1.0 / 60, 65), 6);
        Assert.Equal(0.5, ScrollSupport.FrameDelta(3, 1.0 / 120, 65), 6);
        Assert.Equal(ScrollSupport.FrameDelta(100, 1.0 / 20, 65), ScrollSupport.FrameDelta(100, 1, 65));
        Assert.Equal(0, ScrollSupport.FrameDelta(100, double.NaN, 65));
        Assert.Equal(0, ScrollSupport.FrameDelta(0, 1.0 / 60, 65));
        // A more responsive setting covers more per frame.
        Assert.True(ScrollSupport.FrameDelta(100, 1.0 / 60, 100) > ScrollSupport.FrameDelta(100, 1.0 / 60, 0));
    }

    private static List<double> Glide(double distance, int coast)
    {
        var frames = new List<double>();
        var remaining = distance;
        while (remaining > 1e-9 && frames.Count < 5000)
        {
            var stretch = ScrollSupport.CoastStretch(coast, 1 - remaining / distance);
            var emitted = ScrollSupport.FrameDelta(remaining, 1.0 / 60, 65, stretch);
            frames.Add(emitted);
            remaining -= emitted;
        }
        return frames;
    }

    [Fact]
    public void CoastingKeepsTheFirstFrameAndTakesLonger()
    {
        var plain = Glide(40, 0);
        var coasting = Glide(40, 100);
        Assert.Equal(40, plain.Sum(), 6);
        Assert.Equal(40, coasting.Sum(), 6);
        Assert.Equal(plain[0], coasting[0], 6);
        Assert.True(coasting.Count >= plain.Count * 1.5, $"{coasting.Count} frames against {plain.Count}");
    }

    [Fact]
    public void GlideSettingsAreSanitized()
    {
        Assert.Equal(40, ScrollSupport.SanitizedStep(0));
        Assert.Equal(100, ScrollSupport.SanitizedStep(500));
        Assert.Equal(20, ScrollSupport.SanitizedStep(10));
        Assert.Equal(60, ScrollSupport.SanitizedStep(60));
        Assert.Equal(120, ScrollSupport.GlideDistance(120, 40));
        Assert.Equal(240, ScrollSupport.GlideDistance(120, 80));
        Assert.Equal(1, ScrollSupport.CoastStretch(0, 1));
        Assert.Equal(3, ScrollSupport.CoastStretch(100, 1));
        Assert.Equal(1, ScrollSupport.CoastStretch(100, double.NaN));
        var carry = 0.0;
        Assert.Equal(0, ScrollSupport.Emit(0.4, ref carry));
        Assert.Equal(1, ScrollSupport.Emit(0.4, ref carry));
        Assert.Equal(-0.2, carry, 6);
    }

    [Fact]
    public void ScrollDefaultsStartOffWithUpstreamValues()
    {
        var s = NewStore();
        Assert.False(s.GetBool(DefaultsKey.ScrollInverterEnabled));
        Assert.False(s.GetBool(DefaultsKey.SmoothScrollEnabled));
        Assert.Equal(40, s.GetInt(DefaultsKey.SmoothScrollStep, -1));
        Assert.Equal(65, s.GetInt(DefaultsKey.SmoothScrollResponse, -1));
        Assert.Equal(0, s.GetInt(DefaultsKey.SmoothScrollCoast, -1));
        Assert.Equal(3, s.GetInt(DefaultsKey.LinearScrollLines, -1));
        Assert.Equal(3, s.GetInt(DefaultsKey.ScrollSpeedMultiplier, -1));
        Assert.Equal("shift", s.GetString(DefaultsKey.ScrollHorizontalModifier));
        Assert.Equal("control", s.GetString(DefaultsKey.PinchZoomModifier));
    }

    // MARK: Mouse buttons

    [Fact]
    public void OnlyTheExtraButtonsAndTheTiltMap()
    {
        foreach (var b in new[] { 3, 4, 31, SideWheelLeft, SideWheelRight }) Assert.True(IsMappable(b), b.ToString());
        foreach (var b in new[] { 0, 1, 2, 32, -3 }) Assert.False(IsMappable(b), b.ToString());
        Assert.Equal("Back button", Name(3));
        Assert.Equal("Forward button", Name(4));
    }

    [Fact]
    public void AMapRoundTripsAndDropsWhatCannotWork()
    {
        var map = Parse("0=control+alt:37;3=nonsense;4=alt:39;-2=control+alt:38;7=control:65");
        Assert.Equal([-2, 4, 7], map.Keys.OrderBy(k => k));
        Assert.Equal("-2=control+alt:38;4=alt:39;7=control:65", Format(map));
        Assert.Equal(map, Parse(Format(map)));
        Assert.Empty(Parse(""));
        Assert.Empty(Parse(null));
    }

    [Fact]
    public void ATiltCountsOnlyWhenItBeatsTheVerticalTick()
    {
        Assert.Equal(SideWheelRight, SideWheelButton(120, 20));
        Assert.Equal(SideWheelLeft, SideWheelButton(-120, 20));
        Assert.Null(SideWheelButton(20, 120));
        Assert.Null(SideWheelButton(0, 0));
        Assert.Null(SideWheelButton(100, 100));
    }

    [Fact]
    public void ATiltFiresOncePerBurst()
    {
        var gate = new SideWheelGate();
        Assert.True(gate.Fires(SideWheelRight, 1000));
        Assert.False(gate.Fires(SideWheelRight, 1100));
        Assert.False(gate.Fires(SideWheelRight, 1300));
        Assert.True(gate.Fires(SideWheelRight, 1600));
        Assert.True(gate.Fires(SideWheelLeft, 1650));
    }

    [Fact]
    public void TheDesktopDragSwitchesOneDesktopAtATime()
    {
        var drag = new DesktopDragTracker();
        Assert.Equal(DesktopAction.None, drag.Add(100, 0, 1));
        Assert.Equal(DesktopAction.Right, drag.Add(130, 0, 1.1));
        // The cooldown holds the next one back, and one step stays banked.
        Assert.Equal(DesktopAction.None, drag.Add(230, 0, 1.2));
        Assert.Equal(DesktopAction.Right, drag.Add(0, 0, 1.6));
        Assert.Equal(DesktopAction.None, drag.Add(0, 0, 2.5));
        Assert.Equal(DesktopAction.Left, drag.Add(-500, 0, 3));
        drag.Reset();
        Assert.Equal(DesktopAction.TaskView, drag.Add(0, -160, 10));
        Assert.Equal(DesktopAction.None, new DesktopDragTracker().Add(0, 160, 1));
    }

    // MARK: Click debounce

    [Fact]
    public void ABouncingClickIsDroppedWithItsDragsAndRelease()
    {
        var f = new ClickDebounceSupport.Filter(25);
        Assert.Equal(DebounceVerdict.Pass, f.Down(0, 100));
        Assert.Equal(DebounceVerdict.Pass, f.Drag(0, 103));
        Assert.Equal(DebounceVerdict.Pass, f.Up(0, 105));
        // A second press 15 ms after: a bounce.
        Assert.Equal(DebounceVerdict.Suppress, f.Down(0, 120));
        Assert.Equal(DebounceVerdict.Suppress, f.Drag(0, 122));
        Assert.Equal(DebounceVerdict.Suppress, f.Up(0, 125));
        // Exactly 25 ms after the last accepted release is a real click.
        Assert.Equal(DebounceVerdict.Pass, f.Down(0, 130));
        Assert.Equal(DebounceVerdict.Suppress, f.Down(0, 132));
        Assert.Equal(DebounceVerdict.Pass, f.Up(0, 140));
    }

    [Fact]
    public void ClickDebounceKeepsButtonsApartAndFailsOpen()
    {
        var f = new ClickDebounceSupport.Filter(25);
        Assert.Equal(DebounceVerdict.Pass, f.Down(0, 100));
        Assert.Equal(DebounceVerdict.Pass, f.Up(0, 105));
        Assert.Equal(DebounceVerdict.Pass, f.Down(1, 110));
        // A release with no press behind it passes.
        f.Reset();
        Assert.Equal(DebounceVerdict.Pass, f.Up(0, 200));
        // The extra buttons are never filtered.
        Assert.Equal(DebounceVerdict.Pass, f.Down(3, 210));
        Assert.Equal(DebounceVerdict.Pass, f.Down(3, 211));
        // A clock that goes backwards starts again.
        Assert.Equal(DebounceVerdict.Pass, f.Down(0, 500));
        Assert.Equal(DebounceVerdict.Pass, f.Up(0, 505));
        Assert.Equal(DebounceVerdict.Pass, f.Down(0, 10));
    }

    [Fact]
    public void AShortWindowFiltersLess()
    {
        var f = new ClickDebounceSupport.Filter(6);
        Assert.Equal(DebounceVerdict.Pass, f.Down(0, 407));
        Assert.Equal(DebounceVerdict.Pass, f.Up(0, 408));
        Assert.Equal(DebounceVerdict.Suppress, f.Down(0, 413));
        Assert.Equal(DebounceVerdict.Suppress, f.Up(0, 414));
        Assert.Equal(DebounceVerdict.Pass, f.Down(0, 415));
    }

    [Fact]
    public void TheClickWindowIsSanitizedToItsDefault()
    {
        Assert.Equal(25, ClickDebounceSupport.SanitizedWindow(4));
        Assert.Equal(25, ClickDebounceSupport.SanitizedWindow(0));
        Assert.Equal(5, ClickDebounceSupport.SanitizedWindow(5));
        Assert.Equal(100, ClickDebounceSupport.SanitizedWindow(100));
        Assert.Equal(25, ClickDebounceSupport.SanitizedWindow(101));
        Assert.True(ClickDebounceSupport.Applies(2));
        Assert.False(ClickDebounceSupport.Applies(3));
    }

    // MARK: Key debounce

    [Fact]
    public void ABouncingKeyIsDropped()
    {
        var f = new KeyDebounceSupport.Filter(50);
        Assert.Equal(DebounceVerdict.Pass, f.Down(65, false, 10000));
        Assert.Equal(DebounceVerdict.Pass, f.Up(65, 10010));
        Assert.Equal(DebounceVerdict.Suppress, f.Down(65, false, 10030));
        Assert.Equal(DebounceVerdict.Pass, f.Up(65, 10035));
        // The comparison is strict: exactly the window later is a real press.
        Assert.Equal(DebounceVerdict.Pass, f.Down(65, false, 10060));
        // Auto-repeat is never filtered.
        Assert.Equal(DebounceVerdict.Pass, f.Down(65, true, 10070));
    }

    [Fact]
    public void KeyWindowsAreStrictAtTheBoundary()
    {
        var f = new KeyDebounceSupport.Filter(5);
        Assert.Equal(DebounceVerdict.Pass, f.Down(65, false, 45000));
        f.Up(65, 45001);
        Assert.Equal(DebounceVerdict.Suppress, f.Down(65, false, 45005));
        Assert.Equal(DebounceVerdict.Pass, f.Down(65, false, 45006));
        var g = new KeyDebounceSupport.Filter(10);
        g.Down(66, false, 40000);
        g.Up(66, 40004);
        Assert.Equal(DebounceVerdict.Suppress, g.Down(66, false, 40009));
        Assert.Equal(DebounceVerdict.Pass, g.Down(66, false, 40014));
    }

    [Fact]
    public void ADuplicateDownIsSuppressedButTheUpPasses()
    {
        var f = new KeyDebounceSupport.Filter(50);
        Assert.Equal(DebounceVerdict.Pass, f.Down(65, false, 1000));
        Assert.Equal(DebounceVerdict.Suppress, f.Down(65, false, 1001));
        Assert.Equal(DebounceVerdict.Pass, f.Up(65, 1100));
        // A different key is never filtered.
        var g = new KeyDebounceSupport.Filter(1);
        g.Down(65, false, 5000);
        g.Up(65, 5000.2);
        Assert.Equal(DebounceVerdict.Suppress, g.Down(65, false, 5000.5));
        Assert.Equal(DebounceVerdict.Pass, g.Down(66, false, 5001.2));
    }

    [Fact]
    public void PerKeyWindowsOverrideAndAZeroTurnsAKeyOff()
    {
        var f = new KeyDebounceSupport.Filter(20, new Dictionary<int, int> { [37] = 100, [40] = 0 });
        f.Down(37, false, 1000);
        f.Up(37, 1010);
        Assert.Equal(DebounceVerdict.Suppress, f.Down(37, false, 1060));
        f.Down(40, false, 2000);
        f.Up(40, 2001);
        Assert.Equal(DebounceVerdict.Pass, f.Down(40, false, 2002));
    }

    [Fact]
    public void ABackwardsOrIdleClockForgets()
    {
        var f = new KeyDebounceSupport.Filter(50);
        f.Down(65, false, 10000);
        f.Up(65, 10005);
        Assert.Equal(DebounceVerdict.Pass, f.Down(65, false, 9000));
        var g = new KeyDebounceSupport.Filter(50);
        g.Down(65, false, 1000);
        g.Up(65, 1005);
        Assert.Equal(DebounceVerdict.Pass, g.Down(65, false, 7000));
    }

    [Fact]
    public void KeyWindowsEncodeAndDecode()
    {
        Assert.Equal("37:100,40:0", KeyDebounceSupport.Encode(new Dictionary<int, int> { [40] = 0, [37] = 100 }));
        var decoded = KeyDebounceSupport.Decode("37:100,bad,40:0,99:999");
        Assert.Equal(new Dictionary<int, int> { [37] = 100, [40] = 0, [99] = 5 }, decoded);
        Assert.Empty(KeyDebounceSupport.Decode(null));
        Assert.Equal(5, KeyDebounceSupport.SanitizedWindow(-1));
        Assert.Equal(500, KeyDebounceSupport.SanitizedWindow(500));
        Assert.Equal(5, KeyDebounceSupport.SanitizedWindow(501));
    }

    // MARK: Super key

    [Fact]
    public void SuperKeyModifiersFallBackToAllFour()
    {
        Assert.Equal(["control", "alt", "shift", "win"], SuperKeySupport.Modifiers(null));
        Assert.Equal(["control", "alt", "shift", "win"], SuperKeySupport.Modifiers(""));
        Assert.Equal(["control", "alt", "shift", "win"], SuperKeySupport.Modifiers("shift"));
        Assert.Equal(["control", "alt", "shift", "win"], SuperKeySupport.Modifiers("control+"));
        Assert.Equal(["control", "win"], SuperKeySupport.Modifiers("win+control"));
        Assert.Equal(["alt", "shift"], SuperKeySupport.Modifiers("option+shift"));
        Assert.Equal(0x14, SuperKeySupport.VirtualKey("capsLock"));
        Assert.Equal(0xA3, SuperKeySupport.VirtualKey("rightControl"));
        Assert.Equal(0x14, SuperKeySupport.VirtualKey("bogus"));
    }

    [Fact]
    public void ATapAndAHoldMeetAtHalfASecond()
    {
        Assert.Equal(SoloOutcome.Tap, SuperKeySupport.Classify(300));
        Assert.Equal(SoloOutcome.Tap, SuperKeySupport.Classify(499));
        Assert.Equal(SoloOutcome.Hold, SuperKeySupport.Classify(500));
        Assert.Equal(SoloOutcome.Hold, SuperKeySupport.Classify(700));
    }

    [Fact]
    public void TheSoloEffectTable()
    {
        Assert.Equal(SoloEffect.None, SuperKeySupport.Effect(SoloAction.None, false, false));
        Assert.Equal(SoloEffect.PressEscape, SuperKeySupport.Effect(SoloAction.Escape, false, false));
        Assert.Equal(SoloEffect.None, SuperKeySupport.Effect(SoloAction.Escape, false, true));
        Assert.Equal(SoloEffect.ToggleCapsLock, SuperKeySupport.Effect(SoloAction.CapsLock, false, false));
        Assert.Equal(SoloEffect.None, SuperKeySupport.Effect(SoloAction.CapsLock, false, true));
        Assert.Equal(SoloEffect.NextInputSource, SuperKeySupport.Effect(SoloAction.InputSource, false, false));
        Assert.Equal(SoloEffect.ToggleCapsLock, SuperKeySupport.Effect(SoloAction.InputSource, true, false));
        Assert.Equal(SoloEffect.ToggleCapsLock, SuperKeySupport.Effect(SoloAction.InputSource, true, true));
        Assert.Equal(SoloAction.InputSource, SuperKeySupport.ParseSolo("inputSource"));
        Assert.Equal(SoloAction.None, SuperKeySupport.ParseSolo("nonsense"));
    }

    [Fact]
    public void InputSourcesCycleAndWrap()
    {
        Assert.Equal("b", SuperKeySupport.NextInputSource(["a", "b", "c"], "a"));
        Assert.Equal("a", SuperKeySupport.NextInputSource(["a", "b", "c"], "c"));
        Assert.Equal("a", SuperKeySupport.NextInputSource(["a", "b"], "unknown"));
        Assert.Null(SuperKeySupport.NextInputSource(["a"], "a"));
        Assert.Null(SuperKeySupport.NextInputSource([], null));
        Assert.Equal(3, SuperKeySupport.WatchdogSeconds(null));
        Assert.Equal(3, SuperKeySupport.WatchdogSeconds(0.5));
        Assert.Equal(20, SuperKeySupport.WatchdogSeconds(10));
        Assert.Equal(30, SuperKeySupport.WatchdogSeconds(50));
    }

    [Fact]
    public void TheHeldKeyStateMachine()
    {
        var s = new SuperKeySupport.State();
        Assert.True(s.TriggerDown(1000));
        Assert.False(s.TriggerDown(1100));
        Assert.Equal(SoloOutcome.Tap, s.TriggerUp(1300));
        Assert.False(s.Held);
        // Another key while held takes the modifiers and cancels the solo action.
        Assert.True(s.TriggerDown(2000));
        Assert.True(s.OtherInput());
        Assert.Equal(SoloOutcome.Nothing, s.TriggerUp(2200));
        // A long solo press is a hold; another modifier cancels it too.
        s.TriggerDown(3000);
        Assert.Equal(SoloOutcome.Hold, s.TriggerUp(3700));
        s.TriggerDown(4000);
        s.ModifierChanged();
        Assert.Equal(SoloOutcome.Nothing, s.TriggerUp(4100));
        Assert.False(s.OtherInput());
        Assert.Equal(SoloOutcome.Nothing, s.TriggerUp(5000));
    }

    // MARK: Quit protection

    [Fact]
    public void QuitProtectionRangesClampAndDefaultWhenUnreadable()
    {
        Assert.Equal(250, QuitProtectionSupport.SanitizedHold(100));
        Assert.Equal(2000, QuitProtectionSupport.SanitizedHold(3000));
        Assert.Equal(800, QuitProtectionSupport.SanitizedHold(double.NaN));
        Assert.Equal(900, QuitProtectionSupport.SanitizedHold(900));
        Assert.Equal(200, QuitProtectionSupport.SanitizedDouble(100));
        Assert.Equal(1500, QuitProtectionSupport.SanitizedDouble(3000));
        Assert.Equal(600, QuitProtectionSupport.SanitizedDouble(double.NaN));
        Assert.Equal(600, QuitProtectionSupport.SanitizedDouble(double.NegativeInfinity));
        Assert.True(QuitProtectionSupport.IsWithinDoublePressInterval(1000, 2500, 1500));
        Assert.False(QuitProtectionSupport.IsWithinDoublePressInterval(1000, 2500.001, 1500));
        Assert.Equal(QuitGuardMode.DoublePress, QuitProtectionSupport.ParseMode("doublePress"));
        Assert.Equal(QuitGuardMode.Hold, QuitProtectionSupport.ParseMode("nonsense"));
        Assert.Equal("shift", QuitProtectionSupport.SanitizedExtraModifier("command"));
        Assert.Equal(0.5, QuitProtectionSupport.HoldProgress(400, 800));
        Assert.Equal(1, QuitProtectionSupport.HoldProgress(2000, 800));
    }

    [Fact]
    public void OnlyTheRightChordsAreGuarded()
    {
        var alt = new HashSet<string> { "alt" };
        var ctrl = new HashSet<string> { "control" };
        Assert.True(QuitProtectionSupport.IsPlainChord(QuitGuardKind.Quit, 0x73, alt));
        Assert.True(QuitProtectionSupport.IsPlainChord(QuitGuardKind.Quit, 0x51, ctrl));
        Assert.True(QuitProtectionSupport.IsPlainChord(QuitGuardKind.Close, 0x57, ctrl));
        Assert.False(QuitProtectionSupport.IsPlainChord(QuitGuardKind.Close, 0x73, alt));
        Assert.False(QuitProtectionSupport.IsPlainChord(QuitGuardKind.Quit, 0x73, new HashSet<string> { "alt", "shift" }));
        Assert.False(QuitProtectionSupport.IsPlainChord(QuitGuardKind.Quit, 0x51, new HashSet<string> { "alt" }));
        Assert.True(QuitProtectionSupport.IsExtraChord(QuitGuardKind.Quit, 0x73, new HashSet<string> { "alt", "shift" }, "shift"));
        Assert.False(QuitProtectionSupport.IsExtraChord(QuitGuardKind.Quit, 0x73, new HashSet<string> { "alt", "control" }, "shift"));
        Assert.False(QuitProtectionSupport.IsExtraChord(QuitGuardKind.Quit, 0x73, new HashSet<string> { "alt", "shift", "control" }, "shift"));
        Assert.Null(QuitProtectionSupport.ChordModifier(QuitGuardKind.Quit, 0x41));
    }

    [Fact]
    public void ScopeDecidesWhichAppsAreCovered()
    {
        string[] list = ["steam.exe"];
        Assert.True(QuitProtectionSupport.Applies(QuitGuardScope.All, list, "code.exe"));
        Assert.True(QuitProtectionSupport.Applies(QuitGuardScope.SelectedOnly, list, "Steam.exe"));
        Assert.False(QuitProtectionSupport.Applies(QuitGuardScope.SelectedOnly, list, "code.exe"));
        Assert.False(QuitProtectionSupport.Applies(QuitGuardScope.AllExceptSelected, list, "steam.exe"));
        Assert.True(QuitProtectionSupport.Applies(QuitGuardScope.AllExceptSelected, list, null));
    }

    [Fact]
    public void HoldModeNeedsTheFullHold()
    {
        var guard = new QuitProtectionSupport.Guard(QuitGuardMode.Hold, 800, 600);
        Assert.Equal(GuardDecision.SwallowAndStartHold, guard.PlainDown(false, 1000));
        Assert.Equal(GuardDecision.Swallow, guard.PlainDown(true, 1100));
        Assert.Equal(GuardDecision.Swallow, guard.HoldElapsed(1799));
        Assert.Equal(GuardDecision.Confirm, guard.HoldElapsed(1800));
        // Once confirmed it is not confirmed again while the key stays down.
        Assert.Equal(GuardDecision.Swallow, guard.HoldElapsed(1900));
        Assert.Equal(GuardDecision.Pass, guard.Released());
        // Letting go early cancels.
        Assert.Equal(GuardDecision.SwallowAndStartHold, guard.PlainDown(false, 5000));
        Assert.Equal(GuardDecision.Cancel, guard.Released());
        Assert.False(guard.Pending);
    }

    [Fact]
    public void DoublePressNeedsASecondPressInTime()
    {
        var guard = new QuitProtectionSupport.Guard(QuitGuardMode.DoublePress, 800, 600);
        Assert.Equal(GuardDecision.SwallowAndArm, guard.PlainDown(false, 1000));
        Assert.Equal(GuardDecision.Swallow, guard.PlainDown(true, 1050));
        Assert.Equal(GuardDecision.Confirm, guard.PlainDown(false, 1500));
        Assert.Equal(GuardDecision.SwallowAndArm, guard.PlainDown(false, 3000));
        // Too late: it arms again instead.
        Assert.Equal(GuardDecision.SwallowAndArm, guard.PlainDown(false, 3700));
        Assert.False(guard.ArmExpired(4200));
        Assert.True(guard.ArmExpired(4401));
        guard.Cancel();
        Assert.False(guard.Pending);
    }

    [Fact]
    public void AnExtraModifierConfirmsAtOnce()
    {
        var guard = new QuitProtectionSupport.Guard(QuitGuardMode.ExtraModifier, 800, 600);
        Assert.Equal(GuardDecision.SwallowAndHint, guard.PlainDown(false, 1000));
        Assert.Equal(GuardDecision.Swallow, guard.PlainDown(true, 1010));
        Assert.Equal(GuardDecision.Confirm, guard.ExtraDown());
        Assert.Equal(GuardDecision.Pass, new QuitProtectionSupport.Guard(QuitGuardMode.Hold, 800, 600).ExtraDown());
    }

    [Fact]
    public void QuitProtectionSettingsStartOffWithUpstreamValues()
    {
        var s = NewStore();
        foreach (var kind in new[] { QuitGuardKind.Quit, QuitGuardKind.Close })
        {
            Assert.Equal("hold", s.GetString(QuitProtectionSupport.SettingKey(kind, "Mode")));
            Assert.Equal(800, s.GetInt(QuitProtectionSupport.SettingKey(kind, "HoldDurationMs"), -1));
            Assert.Equal(600, s.GetInt(QuitProtectionSupport.SettingKey(kind, "DoubleIntervalMs"), -1));
            Assert.Equal("shift", s.GetString(QuitProtectionSupport.SettingKey(kind, "ExtraModifier")));
            Assert.Equal("all", s.GetString(QuitProtectionSupport.SettingKey(kind, "Scope")));
            Assert.True(s.GetBool(QuitProtectionSupport.SettingKey(kind, "ShowFeedback")));
        }
        Assert.False(s.GetBool(DefaultsKey.QuitProtectionQuitEnabled));
        Assert.False(s.GetBool(DefaultsKey.QuitProtectionCloseEnabled));
    }

    // MARK: Snippets

    private static TextSnippet Snippet(string trigger, string replacement = "x", SnippetExpansion mode = SnippetExpansion.AfterDelimiter,
                                       bool ignoresCase = false, bool enabled = true, string folder = "", string name = "") =>
        new() { Trigger = trigger, Replacement = replacement, Expansion = mode == SnippetExpansion.Immediate ? "immediate" : "afterDelimiter", IgnoresCase = ignoresCase, Enabled = enabled, Folder = folder, Name = name };

    [Fact]
    public void TriggersAreCleanedAndChecked()
    {
        Assert.Equal(";email", SnippetSupport.SanitizedTrigger("  ;e mail\n"));
        Assert.Equal(40, SnippetSupport.SanitizedTrigger(new string('a', 60)).Length);
        Assert.Equal("Work", SnippetSupport.SanitizedFolder("  Work \n"));
        Assert.Equal(TriggerCheck.Valid, SnippetSupport.Check(";email", false, []));
        Assert.Equal(TriggerCheck.TooShort, SnippetSupport.Check(";", false, []));
        Assert.Equal(TriggerCheck.TooLong, SnippetSupport.Check(new string('a', 41), false, []));
        Assert.Equal(TriggerCheck.HasWhitespace, SnippetSupport.Check("a b", false, []));
        Assert.Equal(TriggerCheck.Duplicate, SnippetSupport.Check(";email", false, [Snippet(";email")]));
        Assert.Equal(TriggerCheck.Valid, SnippetSupport.Check(";EMAIL", false, [Snippet(";email")]));
        Assert.Equal(TriggerCheck.Duplicate, SnippetSupport.Check(";EMAIL", true, [Snippet(";email")]));
        Assert.Equal(TriggerCheck.Duplicate, SnippetSupport.Check(";EMAIL", false, [Snippet(";email", ignoresCase: true)]));
    }

    [Fact]
    public void TheBufferKeepsTheLastSixtyFourCharacters()
    {
        var buffer = new SnippetSupport.TypedBuffer();
        foreach (var c in new string('a', 64)) buffer.Append(c);
        Assert.Equal(64, buffer.Text.Length);
        buffer.Append('b');
        Assert.Equal(64, buffer.Text.Length);
        Assert.EndsWith("ab", buffer.Text);
        buffer.Backspace();
        Assert.EndsWith("a", buffer.Text);
        buffer.Clear();
        buffer.Backspace();
        Assert.Equal("", buffer.Text);
    }

    [Fact]
    public void TheLongestTriggerWinsAndModesDoNotCross()
    {
        var snippets = new[]
        {
            Snippet(";email"), Snippet(";email2"), Snippet(";addr", mode: SnippetExpansion.Immediate), Snippet(";off", enabled: false),
        };
        Assert.Equal(";email2", SnippetSupport.Find("hi ;email2", snippets, SnippetExpansion.AfterDelimiter)?.Trigger);
        Assert.Equal(";email", SnippetSupport.Find("hi ;email", snippets, SnippetExpansion.AfterDelimiter)?.Trigger);
        Assert.Null(SnippetSupport.Find("hi ;emai", snippets, SnippetExpansion.AfterDelimiter));
        Assert.Null(SnippetSupport.Find("hi ;off", snippets, SnippetExpansion.AfterDelimiter));
        Assert.Null(SnippetSupport.Find("hi ;addr", snippets, SnippetExpansion.AfterDelimiter));
        Assert.Equal(";addr", SnippetSupport.Find("hi ;addr", snippets, SnippetExpansion.Immediate)?.Trigger);
        Assert.Null(SnippetSupport.Find("hi ;email", snippets, SnippetExpansion.Immediate));
    }

    [Fact]
    public void CaseIsIgnoredOnlyWhereAskedFor()
    {
        var loose = new[] { Snippet(";email", ignoresCase: true) };
        Assert.NotNull(SnippetSupport.Find("x ;EMAIL", loose, SnippetExpansion.AfterDelimiter));
        Assert.NotNull(SnippetSupport.Find("x ;EmAiL", loose, SnippetExpansion.AfterDelimiter));
        Assert.Null(SnippetSupport.Find("x ;EMAIL", [Snippet(";email")], SnippetExpansion.AfterDelimiter));
        Assert.Null(SnippetSupport.Find("x ;e", loose, SnippetExpansion.AfterDelimiter));
    }

    [Fact]
    public void KeysThatEndATriggerOrResetTheBuffer()
    {
        Assert.Equal(' ', SnippetSupport.Delimiter(0x20));
        Assert.Equal('\t', SnippetSupport.Delimiter(0x09));
        Assert.Equal('\r', SnippetSupport.Delimiter(0x0D));
        Assert.Null(SnippetSupport.Delimiter(0x41));
        Assert.True(SnippetSupport.ResetsBuffer(0x25, false, false, false));
        Assert.True(SnippetSupport.ResetsBuffer(0x1B, false, false, false));
        Assert.True(SnippetSupport.ResetsBuffer(0x41, true, false, false));
        Assert.True(SnippetSupport.ResetsBuffer(0x41, false, true, false));
        Assert.False(SnippetSupport.ResetsBuffer(0x41, false, false, false));
        Assert.Equal(6, SnippetSupport.DeleteCount(Snippet(";email")));
        Assert.Equal(4, SnippetSupport.DeleteCount(Snippet(";addr", mode: SnippetExpansion.Immediate)));
    }

    [Fact]
    public void TextIsSentInChunksThatKeepSurrogatePairsWhole()
    {
        var text = new string('a', 19) + "\U0001F600" + "b";
        var chunks = SnippetSupport.Chunks(text);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(new string('a', 19), chunks[0]);
        Assert.Equal("\U0001F600b", chunks[1]);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(SnippetSupport.Chunks(new string('x', 55)), c => Assert.True(c.Length <= 20));
        Assert.Empty(SnippetSupport.Chunks(""));
        Assert.True(SnippetSupport.ShouldPaste("first\nsecond"));
        Assert.False(SnippetSupport.ShouldPaste("one line"));
        Assert.Equal("first\nsecond\n", SnippetSupport.PastePayload("first\nsecond", '\r'));
        Assert.Equal("first\nsecond ", SnippetSupport.PastePayload("first\nsecond", ' '));
        Assert.Equal("first\nsecond", SnippetSupport.PastePayload("first\nsecond", null));
    }

    private static readonly DateTimeOffset Moment = new(2025, 7, 1, 12, 30, 15, TimeSpan.Zero);
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void VariablesFillInTheDateAndTime()
    {
        Assert.Equal("Jul 1, 2025", SnippetSupport.Expand("{{date}}", Moment, null, En));
        Assert.Equal("12:30 PM", SnippetSupport.Expand("{{time}}", Moment, null, En));
        Assert.Equal("Jul 1, 2025, 12:30 PM", SnippetSupport.Expand("{{datetime}}", Moment, null, En));
        Assert.Equal("2025-07-01", SnippetSupport.Expand("{{date:yyyy-MM-dd}}", Moment, null, En));
        Assert.Equal("on 2025-07-01 at 12:30", SnippetSupport.Expand("on {{date:yyyy-MM-dd}} at {{time:HH:mm}}", Moment, null, En));
        Assert.Equal("Tuesday, Jul 1", SnippetSupport.Expand("{{date:EEEE, MMM d}}", Moment, null, En));
        Assert.Equal("12:30 PM", SnippetSupport.Expand("{{time:hh:mm a}}", Moment, null, En));
        Assert.Equal("12:30:15.000", SnippetSupport.Expand("{{time:HH:mm:ss.SSS}}", Moment, null, En));
    }

    [Fact]
    public void TimeZonesOverrideTheClock()
    {
        Assert.Equal("2025-07-01T12:30:15", SnippetSupport.Expand("{{datetime-tz(UTC):yyyy-MM-dd'T'HH:mm:ss}}", Moment, null, En));
        Assert.Equal("21:30", SnippetSupport.Expand("{{time-tz(Asia/Tokyo):HH:mm}}", Moment, null, En));
        Assert.Equal("{{time-tz(Not/ARealZone):HH:mm}}", SnippetSupport.Expand("{{time-tz(Not/ARealZone):HH:mm}}", Moment, null, En));
        Assert.Equal("{{time-tz(America/New York):HH:mm}}", SnippetSupport.Expand("{{time-tz(America/New York):HH:mm}}", Moment, null, En));
    }

    [Fact]
    public void TagsThatCannotBeReadStayAsTyped()
    {
        Assert.Equal("{{unknown}}", SnippetSupport.Expand("{{unknown}}", Moment, null, En));
        Assert.Equal("{{date:}}", SnippetSupport.Expand("{{date:}}", Moment, null, En));
        Assert.Equal("{{date:yyyy", SnippetSupport.Expand("{{date:yyyy", Moment, null, En));
        Assert.Equal("a {{ b", SnippetSupport.Expand("a {{ b", Moment, null, En));
        Assert.Equal("plain text", SnippetSupport.Expand("plain text", Moment, null, En));
    }

    [Fact]
    public void TheClipboardGoesInLastAndIsNeverReadAsATag()
    {
        Assert.True(SnippetSupport.NeedsClipboard("a {{clipboard}} b"));
        Assert.False(SnippetSupport.NeedsClipboard("a {{date}} b"));
        Assert.Equal("a {{date}} b", SnippetSupport.Expand("a {{clipboard}} b", Moment, "{{date}}", En));
        Assert.Equal("a  b", SnippetSupport.Expand("a {{clipboard}} b", Moment, null, En));
        Assert.Equal("x Jul 1, 2025 y copied", SnippetSupport.Expand("x {{date}} y {{clipboard}}", Moment, "copied", En));
    }

    [Fact]
    public void SnippetsRoundTripAsJsonAndToleranceForBadData()
    {
        var snippets = new[] { Snippet(";email", "me@example.com", name: "Email", folder: "Work"), Snippet(";addr", "1 Main St", SnippetExpansion.Immediate, true) };
        var back = SnippetSupport.Decode(SnippetSupport.Encode(snippets));
        Assert.Equal(2, back.Count);
        Assert.Equal(";email", back[0].Trigger);
        Assert.Equal("Work", back[0].Folder);
        Assert.Equal(SnippetExpansion.Immediate, back[1].Mode);
        Assert.True(back[1].IgnoresCase);
        // Optional fields may be missing.
        var minimal = SnippetSupport.Decode("""[{"trigger":";a1","replacement":"x"}]""");
        Assert.True(minimal[0].Enabled);
        Assert.True(minimal[0].ShowsInLibrary);
        Assert.Equal(SnippetExpansion.AfterDelimiter, minimal[0].Mode);
        Assert.Empty(SnippetSupport.Decode("not json"));
        Assert.Empty(SnippetSupport.Decode(null));
    }

    [Fact]
    public void TheLibraryGroupsByFolderAndSearchesEverything()
    {
        var snippets = new[]
        {
            Snippet(";b1", name: "Zebra"), Snippet(";w1", name: "Work mail", folder: "Work"), Snippet(";a1", name: "Alpha", folder: "alpha"),
            Snippet(";h1", name: "Hidden", folder: "Work") with { ShowsInLibrary = false },
        };
        var groups = SnippetSupport.Grouped(snippets);
        Assert.Equal(["alpha", "Work", ""], groups.Select(g => g.Folder));
        Assert.Equal(["Work mail"], groups[1].Snippets.Select(s => s.Name));
        Assert.True(SnippetSupport.MatchesLibrary(Snippet(";caf1", "Café menu", name: "Menu", folder: "Food"), "cafe food"));
        Assert.True(SnippetSupport.MatchesLibrary(Snippet(";a1"), ""));
        Assert.False(SnippetSupport.MatchesLibrary(Snippet(";a1", name: "Alpha"), "beta"));
    }

    [Fact]
    public void SnippetDefaultsStartOff()
    {
        var s = NewStore();
        Assert.False(s.GetBool(DefaultsKey.TextSnippetsEnabled));
        Assert.False(s.GetBool(DefaultsKey.SnippetLibraryEnabled));
        Assert.Equal("[]", s.GetString(DefaultsKey.TextSnippets));
        Assert.NotNull(Shortcuts.Shortcut.Parse(s.GetString(DefaultsKey.SnippetLibraryShortcut)));
        Assert.False(s.GetBool(DefaultsKey.SuperKeyEnabled));
        Assert.Equal("capsLock", s.GetString(DefaultsKey.SuperKeySource));
        Assert.False(s.GetBool(DefaultsKey.MouseClickDebounceEnabled));
        Assert.Equal(25, s.GetInt(DefaultsKey.MouseClickDebounceWindowMs, -1));
        Assert.Equal(5, s.GetInt(DefaultsKey.KeyboardDebounceWindowMs, -1));
    }
}
