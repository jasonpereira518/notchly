// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the contracts in upstream Tests/MixerFeatureTests.swift, SoundOutputSwitchTests.swift,
// AudioPriorityTests.swift, MixerHeadphonesDisconnectTests.swift and the MicMuteSupport rules in
// ScreenshotFeatureTests.swift, with Windows endpoint ids in place of CoreAudio UIDs.
using System.Globalization;
using Notchly.Core.Settings;
using Notchly.Core.Sound;

namespace Notchly.Core.Tests;

public class SoundTests
{
    private static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items);

    // MARK: Levels

    [Fact]
    public void AppVolumeKeepsToItsScale()
    {
        Assert.Equal(1.5, MixerSupport.SanitizedAppVolume(1.5), 6);
        Assert.Equal(2, MixerSupport.SanitizedAppVolume(3), 6);
        Assert.Equal(0, MixerSupport.SanitizedAppVolume(-1), 6);
        Assert.Equal(1, MixerSupport.SanitizedAppVolume(double.PositiveInfinity), 6);
        Assert.Equal(1, MixerSupport.SanitizedAppVolume(double.NaN), 6);
        // Windows plays a session up to 100%; a stored boost is kept but asked of Windows as 100%.
        Assert.Equal(1, MixerSupport.PlayableVolume(1.5), 6);
        Assert.Equal(0.4, MixerSupport.PlayableVolume(0.4), 6);
    }

    [Fact]
    public void TheGainCurveIsPerceptual()
    {
        Assert.Equal(0, MixerSupport.Gain(0), 5);
        Assert.Equal(0.25, MixerSupport.Gain(0.5), 5);
        Assert.Equal(1, MixerSupport.Gain(1), 5);
        Assert.Equal(2, MixerSupport.Gain(2), 5);
        Assert.Equal(0, MixerSupport.Gain(-1), 5);
        Assert.Equal(0.5, MixerSupport.Volume(0.25), 6);
        Assert.Equal(1, MixerSupport.Gain(double.NaN), 5);
    }

    [Fact]
    public void PercentsShowAndParse()
    {
        Assert.Equal(46, MixerSupport.DisplayPercent(0.456));
        Assert.Equal(200, MixerSupport.DisplayPercent(2));
        Assert.Equal(0, MixerSupport.DisplayPercent(double.PositiveInfinity));
        Assert.Equal(0, MixerSupport.DisplayPercent(double.NegativeInfinity));
        Assert.Equal(0, MixerSupport.DisplayPercent(double.NaN));
        Assert.Equal(100_000, MixerSupport.DisplayPercent(1e300));
        var us = CultureInfo.InvariantCulture;
        Assert.Equal(0.4, MixerSupport.VolumeFraction("40", 200, us)!.Value, 6);
        Assert.Equal(0.75, MixerSupport.VolumeFraction(" 75% ", 100, us)!.Value, 6);
        Assert.Equal(2, MixerSupport.VolumeFraction("250", 200, us)!.Value, 6);
        Assert.Equal(0, MixerSupport.VolumeFraction("-10", 100, us)!.Value, 6);
        Assert.Null(MixerSupport.VolumeFraction("loud", 100, us));
        Assert.Null(MixerSupport.VolumeFraction("nan", 100, us));
        Assert.Equal(0.125, MixerSupport.VolumeFraction("12,5%", 100, new CultureInfo("de-DE"))!.Value, 6);
    }

    [Fact]
    public void SilenceAlsoMutesTheOutput()
    {
        Assert.True(MixerSupport.MutesOutput(0));
        Assert.True(MixerSupport.MutesOutput(0.004));
        Assert.False(MixerSupport.MutesOutput(0.005));
        Assert.False(MixerSupport.MutesOutput(1));
        Assert.True(MixerSupport.ShouldMuteOutputLoweredForDisconnect(0, false));
        Assert.False(MixerSupport.ShouldMuteOutputLoweredForDisconnect(5, false));
        Assert.False(MixerSupport.ShouldMuteOutputLoweredForDisconnect(0, true));
        Assert.False(MixerSupport.ShouldMuteOutputLoweredForDisconnect(0, null));
        Assert.Equal(35, MixerSupport.SanitizedHeadphonesDisconnectPercent(35));
        Assert.Equal(0, MixerSupport.SanitizedHeadphonesDisconnectPercent(-5));
        Assert.Equal(100, MixerSupport.SanitizedHeadphonesDisconnectPercent(105));
        Assert.True(MixerSupport.ShouldRestoreOutputVolume(0.25, 0.25));
        Assert.False(MixerSupport.ShouldRestoreOutputVolume(0.25, 0.6));
        Assert.False(MixerSupport.ShouldRestoreOutputVolume(0.25, null));
    }

    // MARK: Rows

    [Fact]
    public void HidingIdleRowsNeverHidesASetting()
    {
        Assert.True(MixerSupport.ShouldShowApp(false, 1, null, false));
        Assert.False(MixerSupport.ShouldShowApp(false, 1, null, true));
        Assert.True(MixerSupport.ShouldShowApp(true, 1, null, true));
        Assert.True(MixerSupport.ShouldShowApp(false, 0.75, null, true));
        Assert.True(MixerSupport.ShouldShowApp(false, 1, "speakers", true));
    }

    [Fact]
    public void RowsAreIdentifiedByAppThenByName()
    {
        var identified = MixerSupport.RowIdentity("Spotify.exe", 501, "Spotify");
        Assert.Equal(("Spotify.exe", "Spotify.exe"), (identified.RowId, identified.PersistenceId));
        var named = MixerSupport.RowIdentity(null, 501, "Retro Game");
        var otherNamed = MixerSupport.RowIdentity(null, 502, "Retro Game");
        Assert.Equal("Retro Game", named.PersistenceId);
        Assert.NotEqual(named.RowId, otherNamed.RowId);
        var nameless = MixerSupport.RowIdentity(null, 501, null);
        Assert.False(string.IsNullOrEmpty(nameless.RowId));
        Assert.Null(nameless.PersistenceId);
        Assert.Null(MixerSupport.RowIdentity("   ", 501, "  ").PersistenceId);
    }

    [Fact]
    public void HiddenAppsFollowTheirOwnKeys()
    {
        var noneHidden = new Dictionary<string, string>();
        Assert.False(MixerSupport.IsHidden(MixerSupport.SystemSoundsId, MixerSupport.HiddenRowIds(noneHidden, true)));
        Assert.True(MixerSupport.IsHidden(MixerSupport.SystemSoundsId, MixerSupport.HiddenRowIds(noneHidden, false)));
        Assert.True(MixerSupport.IsHidden("Player.exe", MixerSupport.HiddenRowIds(new Dictionary<string, string> { ["Player.exe"] = "Player" }, true)));
        Assert.False(MixerSupport.IsHidden("Player.exe", MixerSupport.HiddenRowIds(noneHidden, false)));
        Assert.True(MixerSupport.IsHidden("Bare Tool", MixerSupport.HiddenRowIds(new Dictionary<string, string> { ["Bare Tool"] = "Bare Tool" }, true)));
        Assert.False(MixerSupport.IsHidden(null, Set("Player.exe")));
        var sanitized = MixerSupport.SanitizedHiddenApps(new Dictionary<string, string>
        {
            ["Player.exe"] = "Player", [MixerSupport.SystemSoundsId] = "System sounds", [""] = "Nameless", ["Silent.exe"] = "",
        });
        Assert.Equal(new Dictionary<string, string> { ["Player.exe"] = "Player" }, sanitized);
        Assert.True(MixerSupport.NeedsPersistentSystemSoundsRow(true, false));
        Assert.False(MixerSupport.NeedsPersistentSystemSoundsRow(true, true));
        Assert.False(MixerSupport.NeedsPersistentSystemSoundsRow(false, false));
    }

    [Fact]
    public void AHelpersAudioIsBilledToItsApp()
    {
        var parents = new Dictionary<int, int>
        {
            [500] = 100, [100] = 4, [700] = 4, [900] = 901, [901] = 902, [902] = 903, [903] = 904, [904] = 905, [905] = 906, [906] = 907, [907] = 100,
        };
        int? Owner(int pid) => MixerSupport.OwningAppPid(pid, id => id == 100, id => parents.GetValueOrDefault(id, 0));
        Assert.Equal(100, Owner(100));
        Assert.Equal(100, Owner(500));
        Assert.Null(Owner(700));
        Assert.Null(Owner(42));
        Assert.Null(Owner(900)); // past the depth cap
        Assert.Equal(100, Owner(902));
        Assert.Null(Owner(0));
        Assert.Null(Owner(-1)); // a pid below zero is never merged into a row
    }

    [Fact]
    public void CallAppsAndDawsKeepTheirOwnVolume()
    {
        Assert.True(MixerSupport.BypassesVolumeControl("Zoom.exe", "Zoom"));
        Assert.True(MixerSupport.BypassesVolumeControl("Zoom.exe", "Zoom Workplace"));
        Assert.False(MixerSupport.BypassesVolumeControl("Spotify.exe", "Spotify"));
        Assert.False(MixerSupport.BypassesVolumeControl(null, "Zoomable Notes"));
        Assert.True(MixerSupport.BypassesVolumeControl("Ableton Live.exe", "Live"));
        Assert.True(MixerSupport.BypassesVolumeControl("Cubase13.exe", "Cubase"));
        Assert.False(MixerSupport.BypassesVolumeControl("Notepad.exe", "Notepad"));
        Assert.True(MixerSupport.BypassesVolumeControl("reaper.exe", "REAPER"));
        foreach (var call in new[] { "Discord.exe", "DiscordPTB.exe", "ms-teams.exe", "Teams.exe" })
        {
            Assert.True(MixerSupport.BypassesVolumeControl(call, "Call"), call);
            Assert.False(MixerSupport.BypassesVolumeControl(call, "Call", allowCallAppVolume: true), call);
        }
        Assert.True(MixerSupport.BypassesVolumeControl("Zoom.exe", "Zoom", allowCallAppVolume: true));
    }

    [Fact]
    public void RowsAndDevicesSortByNameThenId()
    {
        Assert.True(MixerSupport.DisplayOrderedBefore("Music", "a", "Safari", "b"));
        Assert.False(MixerSupport.DisplayOrderedBefore("Safari", "a", "Music", "b"));
        Assert.True(MixerSupport.DisplayOrderedBefore("Same", "a", "Same", "b"));
        Assert.False(MixerSupport.DisplayOrderedBefore("Same", "b", "Same", "a"));
        Assert.True(MixerSupport.DeviceDisplayOrderedBefore(true, "Zeta", "z", false, "Alpha", "a"));
        Assert.False(MixerSupport.DeviceDisplayOrderedBefore(false, "Alpha", "a", true, "Zeta", "z"));
    }

    // MARK: Routes

    [Fact]
    public void ARouteFallsBackToTheDefaultWhileItsDeviceIsGone()
    {
        var available = Set("speakers", "headphones");
        Assert.Equal("headphones", MixerSupport.EffectiveDeviceId("headphones", available, "speakers"));
        Assert.Equal("speakers", MixerSupport.EffectiveDeviceId("gone", available, "speakers"));
        Assert.Equal("speakers", MixerSupport.EffectiveDeviceId(null, available, "speakers"));
        Assert.True(MixerSupport.SelectedDeviceUnavailable("gone", available));
        Assert.False(MixerSupport.SelectedDeviceUnavailable("speakers", available));
        Assert.False(MixerSupport.SelectedDeviceUnavailable(null, available));
        Assert.True(MixerSupport.NeedsUnavailableOutputRow("gone", true, ["speakers"]));
        Assert.False(MixerSupport.NeedsUnavailableOutputRow("speakers", true, ["speakers"]));
        Assert.False(MixerSupport.NeedsUnavailableOutputRow("gone", false, ["speakers"]));
    }

    [Fact]
    public void ChoosingTheSystemOutputClearsRoutesAndKeepsVolumes()
    {
        var routes = new Dictionary<string, string> { ["Safari"] = "headphones" };
        var volumes = new Dictionary<string, double> { ["Safari"] = 0.35, ["Music"] = 1.4 };
        var success = MixerSupport.PreferencesAfterUniversalOutputSwitch(routes, volumes, true);
        Assert.Empty(success.OutputDeviceIds);
        Assert.Equal(volumes, success.Volumes);
        var failure = MixerSupport.PreferencesAfterUniversalOutputSwitch(routes, volumes, false);
        Assert.Equal(routes, failure.OutputDeviceIds);
        Assert.Equal(volumes, failure.Volumes);
        Assert.Equal(new Dictionary<string, string> { ["Safari"] = "headphones" },
            MixerSupport.SanitizedRouteMap(new Dictionary<string, string> { ["Safari"] = "headphones", [""] = "x", ["Music"] = "  " }));
    }

    // MARK: Output switcher

    [Fact]
    public void TheShortcutCyclesTheSelectedOutputs()
    {
        var both = Set("speakers", "display");
        Assert.Equal("display", MixerSupport.NextSelectedOutputId("speakers", ["speakers", "display"], both));
        Assert.Equal("speakers", MixerSupport.NextSelectedOutputId("display", ["speakers", "display"], both));
        Assert.Equal("speakers", MixerSupport.NextSelectedOutputId("usb", ["speakers", "display"], both));
        Assert.Equal("display", MixerSupport.NextSelectedOutputId("speakers", ["speakers", "missing", "display"], both));
        // The only selected output already playing is not a switch (the caller reports it as success).
        Assert.Null(MixerSupport.NextSelectedOutputId("speakers", ["speakers"], Set("speakers")));
        // A selection with nothing connected fails.
        Assert.Null(MixerSupport.NextSelectedOutputId("speakers", ["usb"], both));
        Assert.Equal(["speakers", "display"], MixerSupport.SanitizedIdList(["speakers", "display", "speakers", "\n", null]));
    }

    // MARK: Device priority

    private static PriorityTier Tier(string id) => id switch
    {
        "speakers" or "mic" => PriorityTier.BuiltIn,
        "loopback" or "group" => PriorityTier.Virtual,
        _ => PriorityTier.Hardware,
    };

    [Fact]
    public void PriorityFallsThroughTheSavedOrder()
    {
        var available = Set("speakers", "display");
        Assert.Equal("display", MixerSupport.FirstAvailablePriorityId(["offline", "display", "speakers"], available));
        Assert.Null(MixerSupport.FirstAvailablePriorityId(["offline"], available));
        Assert.Null(MixerSupport.FirstAvailablePriorityId([], available));
    }

    [Fact]
    public void AFirstListStartsWithTheDeviceInUse()
    {
        Assert.Equal(["dac", "speakers", "display", "loopback"],
            MixerSupport.InitialPriorityList(["loopback", "display", "speakers", "dac"], "dac", Tier));
        Assert.Equal(["loopback", "speakers"], MixerSupport.InitialPriorityList(["speakers", "loopback"], "loopback", Tier));
    }

    [Fact]
    public void ANewDeviceJoinsWhereItBelongs()
    {
        Assert.Equal(["headphones", "speakers", "display"], MixerSupport.PlacingNewPriorityDevice("headphones", ["speakers", "display"], true, Tier));
        Assert.Equal(["speakers", "display", "loopback"], MixerSupport.PlacingNewPriorityDevice("display", ["speakers", "loopback"], false, Tier));
        Assert.Equal(["loopback", "speakers", "group"], MixerSupport.PlacingNewPriorityDevice("group", ["loopback", "speakers"], false, Tier));
        Assert.Equal(["offline", "speakers"], MixerSupport.PlacingNewPriorityDevice("speakers", ["offline", "speakers"], true, Tier));
    }

    [Fact]
    public void OnlyAConnectionOrDisconnectIsAnEvent()
    {
        var available = Set("speakers", "display");
        Assert.False(MixerSupport.DeviceAvailabilityChanged(null, available));
        Assert.False(MixerSupport.DeviceAvailabilityChanged(available, Set("display", "speakers")));
        Assert.True(MixerSupport.DeviceAvailabilityChanged(available, Set("speakers")));
        Assert.True(MixerSupport.DeviceAvailabilityChanged(available, Set("speakers", "display", "headphones")));
        Assert.True(MixerSupport.ShouldSwitchToDevice("display", "speakers"));
        Assert.False(MixerSupport.ShouldSwitchToDevice("display", "display"));
        Assert.False(MixerSupport.ShouldSwitchToDevice(null, "display"));
    }

    [Fact]
    public void DevicesAreRankedByWhatTheyAre()
    {
        Assert.Equal(PriorityTier.BuiltIn, MixerSupport.TierOf("Speakers (Realtek(R) Audio)", "HDAUDIO"));
        Assert.Equal(PriorityTier.Hardware, MixerSupport.TierOf("Headset (WH-1000XM5)", "BTHENUM"));
        Assert.Equal(PriorityTier.Hardware, MixerSupport.TierOf("Speakers (USB Audio)", "USB"));
        Assert.Equal(PriorityTier.Virtual, MixerSupport.TierOf("CABLE Input (VB-Audio Virtual Cable)", "ROOT"));
        Assert.Equal(PriorityTier.Virtual, MixerSupport.TierOf("Voicemeeter Input", null));
    }

    [Fact]
    public void ListsKeepTheirCap()
    {
        var many = Enumerable.Range(0, 100).Select(i => "device" + i).ToList();
        Assert.Equal(MixerSupport.MaximumListLength, MixerSupport.SanitizedIdList(many).Count);
        Assert.Equal(MixerSupport.MaximumListLength, MixerSupport.PlacingNewPriorityDevice("fresh", many.Take(64).ToList(), true, _ => PriorityTier.Hardware).Count);
    }

    // MARK: Headphones

    [Theory]
    [InlineData("AirPods Pro", "", EndpointFormFactor.Unknown, true)]
    [InlineData("Sony WH-1000XM5", "", EndpointFormFactor.Unknown, true)]
    [InlineData("Headphones (Realtek Audio)", "", EndpointFormFactor.Unknown, true)]
    [InlineData("Speakers (Realtek Audio)", "", EndpointFormFactor.Unknown, false)]
    [InlineData("JBL Flip", "", EndpointFormFactor.Unknown, false)]
    [InlineData("HUAWEI FreeClip 2", "x", EndpointFormFactor.Headphones, true)]
    [InlineData("My renamed audio device", "x", EndpointFormFactor.Headset, true)]
    [InlineData("Soundcore speaker", "x", EndpointFormFactor.Speakers, false)]
    [InlineData("AirPods Pro", "x", EndpointFormFactor.DigitalAudioDisplay, false)]
    [InlineData("AirPods Pro", "x", EndpointFormFactor.Unknown, true)]
    public void HeadphonesAreRecognised(string name, string id, EndpointFormFactor form, bool expected) =>
        Assert.Equal(expected, MixerSupport.OutputLooksLikeHeadphones(name, id, form));

    private sealed class FakeOutputs : IOutputControl
    {
        public Dictionary<string, double> Volumes = [];
        public Dictionary<string, bool> Mutes = [];
        public bool MuteSettable = true;
        public List<bool> MuteWrites = [];
        public double? Volume(string id) => Volumes.TryGetValue(id, out var v) ? v : null;
        public bool SetVolume(string id, double volume)
        {
            if (!Volumes.ContainsKey(id)) return false;
            Volumes[id] = volume;
            return true;
        }
        public bool? Muted(string id) => Mutes.TryGetValue(id, out var m) ? m : null;
        public bool SetMuted(string id, bool muted)
        {
            if (!Mutes.ContainsKey(id) || !MuteSettable) return false;
            MuteWrites.Add(muted);
            Mutes[id] = muted;
            return true;
        }
    }

    private static readonly OutputInfo Headphones = new("headphones", true);
    private static readonly OutputInfo Speakers = new("speakers", false);

    private static FakeOutputs Outputs(double speakerVolume = 0.6, bool? speakerMuted = false)
    {
        var fake = new FakeOutputs { Volumes = { ["headphones"] = 0.5, ["speakers"] = speakerVolume }, Mutes = { ["headphones"] = false } };
        if (speakerMuted is { } muted) fake.Mutes["speakers"] = muted;
        return fake;
    }

    private static LoweredOutputState Disconnect(FakeOutputs fake, int percent) =>
        HeadphoneGuard.Apply(new LoweredOutputState(), "headphones", [Headphones, Speakers], "speakers", [Speakers], true, percent, fake);

    private static LoweredOutputState Reconnect(FakeOutputs fake, LoweredOutputState state) =>
        HeadphoneGuard.Apply(state, "speakers", [Speakers], "headphones", [Headphones, Speakers], true, 0, fake);

    [Fact]
    public void ADisconnectToSilenceMutesAndTheReturnRestores()
    {
        var fake = Outputs();
        var state = Disconnect(fake, 0);
        Assert.Equal(0, fake.Volumes["speakers"]);
        Assert.True(fake.Mutes["speakers"]);
        Assert.True(state.Lowered!.MutedByLowering);
        state = Reconnect(fake, state);
        Assert.Equal(0.6, fake.Volumes["speakers"]);
        Assert.False(fake.Mutes["speakers"]);
        Assert.Null(state.Lowered);
    }

    [Fact]
    public void AnAudibleDisconnectVolumeNeverTouchesTheMute()
    {
        var fake = Outputs();
        var state = Disconnect(fake, 5);
        Assert.False(fake.Mutes["speakers"]);
        Assert.Empty(fake.MuteWrites);
        Assert.Equal(0.05, fake.Volumes["speakers"], 6);
        Assert.False(state.Lowered!.MutedByLowering);
    }

    [Fact]
    public void AMuteTheGuardDidNotSetIsNotUndone()
    {
        var fake = Outputs(speakerMuted: true);
        var state = Disconnect(fake, 0);
        Assert.False(state.Lowered!.MutedByLowering);
        Assert.Empty(fake.MuteWrites);
        state = Reconnect(fake, state);
        Assert.True(fake.Mutes["speakers"]);
        Assert.Empty(fake.MuteWrites);
    }

    [Fact]
    public void AMuteSwitchedOffSinceIsNotWrittenAgain()
    {
        var fake = Outputs();
        var state = Disconnect(fake, 0);
        fake.Mutes["speakers"] = false;
        fake.MuteWrites.Clear();
        Reconnect(fake, state);
        Assert.Empty(fake.MuteWrites);
        Assert.False(fake.Mutes["speakers"]);
    }

    [Fact]
    public void ASpeakerLevelChangedSinceKeepsItsChoice()
    {
        var fake = Outputs();
        var state = Disconnect(fake, 0);
        fake.Volumes["speakers"] = 0.3;
        fake.MuteWrites.Clear();
        state = Reconnect(fake, state);
        Assert.Equal(0.3, fake.Volumes["speakers"]);
        Assert.True(fake.Mutes["speakers"]);
        Assert.Empty(fake.MuteWrites);
        Assert.Null(state.Lowered);
    }

    [Fact]
    public void AMuteSwitchThatCannotBeSetIsNotRecorded()
    {
        var fake = Outputs();
        fake.MuteSettable = false;
        var state = Disconnect(fake, 0);
        Assert.Equal(0, fake.Volumes["speakers"]);
        Assert.False(state.Lowered!.MutedByLowering);
    }

    [Fact]
    public void SpeakersAlreadyAtZeroAreStillRestored()
    {
        var fake = Outputs(speakerVolume: 0);
        var state = Disconnect(fake, 0);
        Assert.True(fake.Mutes["speakers"]);
        Assert.True(state.Lowered!.MutedByLowering);
        state = Reconnect(fake, state);
        Assert.False(fake.Mutes["speakers"]);
        Assert.Null(state.Lowered);
    }

    [Fact]
    public void TheGuardActsOncePerOutputAndOnlyWhenAsked()
    {
        var fake = Outputs();
        var state = Disconnect(fake, 25);
        fake.Volumes["speakers"] = 0.9;
        var again = HeadphoneGuard.Apply(state, "headphones", [Headphones, Speakers], "speakers", [Speakers], true, 25, fake);
        Assert.Equal(0.9, fake.Volumes["speakers"]);
        Assert.Equal(state, again);
        var off = Outputs();
        HeadphoneGuard.Apply(new LoweredOutputState(), "headphones", [Headphones, Speakers], "speakers", [Speakers], false, 0, off);
        Assert.Equal(0.6, off.Volumes["speakers"]);
    }

    // MARK: Arrangement

    [Fact]
    public void TheArrangementKeepsOrderPinsAndAbsentSlots()
    {
        string[] original = ["app.a", "app.b", "app.c", "app.d"];
        var layout = new MixerAppArrangement();
        Assert.Equal(original, layout.Ordered(original, id => id));
        layout.Move("app.c", -1, original);
        string[] arranged = ["app.a", "app.c", "app.b", "app.d"];
        Assert.Equal(arranged, layout.Ordered(original, id => id));
        var reopened = new MixerAppArrangement(layout.RawValue);
        Assert.Equal(new[] { "app.a", "app.b", "app.d" }, reopened.Ordered(["app.a", "app.b", "app.d"], id => id));
        Assert.Equal(arranged, reopened.Ordered(original, id => id));
        layout.Move("app.d", -1, ["app.a", "app.b", "app.d"]);
        Assert.Equal(new[] { "app.a", "app.c", "app.d", "app.b" }, layout.Ordered(original, id => id));
        layout.TogglePin("app.b");
        layout.TogglePin("app.d");
        Assert.Equal(new[] { "app.d", "app.b", "app.a", "app.c" }, layout.Ordered(original, id => id));
        layout.Move("app.b", -1, layout.Ordered(original, id => id).ToList());
        Assert.Equal(new[] { "app.b", "app.d", "app.a", "app.c" }, layout.Ordered(original, id => id));
        string[] visible = ["app.b", "app.d", "app.a", "app.c"];
        Assert.Null(layout.Neighbor("app.d", 1, visible));
        Assert.Null(layout.Neighbor("app.a", -1, visible));
        var saved = new MixerAppArrangement(layout.RawValue);
        layout.Move("missing", -1, original);
        Assert.Equal(saved, layout);
        layout.TogglePin("app.b");
        Assert.False(layout.IsPinned("app.b"));
        Assert.True(layout.IsPinned("app.d"));
        Assert.Equal("app.e", layout.Ordered([.. original, "app.e"], id => id).Last());
    }

    [Fact]
    public void ADragInsertsAnywhere()
    {
        string[] original = ["app.a", "app.b", "app.c", "app.d"];
        var dragged = new MixerAppArrangement();
        dragged.Move("app.a", "app.d", true, original);
        Assert.Equal(new[] { "app.b", "app.c", "app.d", "app.a" }, dragged.Ordered(original, id => id));
        dragged.Move("app.a", "app.b", false, dragged.Ordered(original, id => id).ToList());
        Assert.Equal(original, dragged.Ordered(original, id => id));
        dragged.Move("app.d", "app.a", false, ["app.a", "app.c", "app.d"]);
        Assert.Equal(new[] { "app.d", "app.b", "app.a", "app.c" }, dragged.Ordered(original, id => id));
        var before = new MixerAppArrangement(dragged.RawValue);
        dragged.Move("app.a", "missing", false, original);
        dragged.Move("missing", "app.a", true, original);
        dragged.Move("app.a", "app.a", false, original);
        Assert.Equal(before, dragged);
        dragged.TogglePin("app.d");
        var beforeCross = new MixerAppArrangement(dragged.RawValue);
        dragged.Move("app.a", "app.d", false, original);
        Assert.Equal(beforeCross, dragged);
    }

    [Fact]
    public void ABrokenArrangementFallsBackSafely()
    {
        Assert.Equal(new MixerAppArrangement(), new MixerAppArrangement("invalid"));
        var duplicate = new MixerAppArrangement("{\"order\":[\"app.b\",\"app.b\",\"\",\"app.a\"],\"pinned\":[\"app.b\",\"app.b\",\"\"]}");
        Assert.Equal(new[] { "app.b", "app.a" }, duplicate.Order);
        Assert.Equal(new[] { "app.b" }, duplicate.Pinned);
    }

    // MARK: Mic mute

    [Fact]
    public void OnlyARealLevelIsWorthSaving()
    {
        Assert.True(MicMuteSupport.ShouldSaveVolume(0.8));
        Assert.False(MicMuteSupport.ShouldSaveVolume(0));
        Assert.False(MicMuteSupport.ShouldSaveVolume(0.01));
        Assert.False(MicMuteSupport.ShouldSaveVolume(null));
        Assert.False(MicMuteSupport.ShouldReplaceSavedVolume(0.8, 0.5));
        Assert.True(MicMuteSupport.ShouldReplaceSavedVolume(null, 0.5));
        Assert.True(MicMuteSupport.ShouldReplaceSavedVolume(0, 0.5));
        Assert.False(MicMuteSupport.ShouldReplaceSavedVolume(null, 0));
    }

    [Fact]
    public void AMicTheUserSilencedIsLeftAlone()
    {
        Assert.True(MicMuteSupport.IsCaptureSilenced(true, 0.9));
        Assert.True(MicMuteSupport.IsCaptureSilenced(false, 0));
        Assert.True(MicMuteSupport.IsCaptureSilenced(null, 0.005));
        Assert.False(MicMuteSupport.IsCaptureSilenced(false, 0.8));
        Assert.False(MicMuteSupport.IsCaptureSilenced(null, null));
        Assert.True(MicMuteSupport.ShouldLeaveUntouched(owned: false, captureSilenced: true));
        Assert.False(MicMuteSupport.ShouldLeaveUntouched(owned: true, captureSilenced: true));
        Assert.False(MicMuteSupport.ShouldLeaveUntouched(owned: false, captureSilenced: false));
    }

    [Fact]
    public void AnUnmuteTouchesBothControls()
    {
        Assert.Equal(MicMuteSupport.RestoreKind.AlreadyAudible, MicMuteSupport.NeedFor(false, 0.8).Kind);
        Assert.Equal(new MicMuteSupport.RestoreNeed(MicMuteSupport.RestoreKind.Restore, true, false), MicMuteSupport.NeedFor(true, 0.8));
        Assert.Equal(new MicMuteSupport.RestoreNeed(MicMuteSupport.RestoreKind.Restore, false, true), MicMuteSupport.NeedFor(false, 0));
        Assert.Equal(new MicMuteSupport.RestoreNeed(MicMuteSupport.RestoreKind.Restore, true, true), MicMuteSupport.NeedFor(true, 0));
        Assert.Equal(new MicMuteSupport.RestoreNeed(MicMuteSupport.RestoreKind.Restore, true, false), MicMuteSupport.NeedFor(true, null));
        Assert.Equal(MicMuteSupport.RestoreKind.Unreadable, MicMuteSupport.NeedFor(true, null, hasSavedVolume: true).Kind);
        Assert.Equal(MicMuteSupport.RestoreKind.Unreadable, MicMuteSupport.NeedFor(false, null).Kind);
    }

    [Fact]
    public void ALevelComesBackFromWhatWasSaved()
    {
        var saved = new Dictionary<string, double> { ["usb"] = 0.9, ["tiny"] = 0.001 };
        Assert.Equal(0.9, MicMuteSupport.VolumeToRestore("usb", saved, 0.5));
        Assert.Equal(0.5, MicMuteSupport.VolumeToRestore("tiny", saved, 0.5));
        Assert.Equal(0.75, MicMuteSupport.VolumeToRestore("none", saved, 0));
    }

    [Fact]
    public void AnUnmuteReleasesOnlyWhatWasClaimed()
    {
        string[] present = ["a", "b", "c"];
        Assert.Equal(["b"], MicMuteSupport.RestoreTargets(["b", "gone"], present));
        Assert.Equal(present, MicMuteSupport.RestoreTargets(null, present));
        Assert.Empty(MicMuteSupport.RestoreTargets([], present));
        Assert.Equal(["gone"], MicMuteSupport.AbsentClaims(["b", "gone"], present));
        Assert.Empty(MicMuteSupport.AbsentClaims(null, present));
    }

    [Fact]
    public void RetriesBackOffToHalfAMinute()
    {
        MicMuteSupport.Retry? retry = null;
        var now = 0.0;
        var attempts = new List<int>();
        var deadlines = new List<double?>();
        for (var i = 0; i < 14; i++)
        {
            retry = MicMuteSupport.Retry.Next("usb", false, retry, now);
            attempts.Add(retry.Attempts);
            deadlines.Add(retry.Deadline is { } d ? d - now : null);
        }
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10, 10, 10, 10 }, attempts);
        Assert.Null(deadlines[0]);
        Assert.Null(deadlines[1]);
        Assert.Equal(0.25, deadlines[2]!.Value, 6);
        Assert.Equal(0.5, deadlines[3]!.Value, 6);
        Assert.Equal(1, deadlines[4]!.Value, 6);
        Assert.Equal(30, deadlines[9]!.Value, 6);
        // A new device starts over.
        Assert.Equal(1, MicMuteSupport.Retry.Next("other", false, retry, now).Attempts);
    }

    // MARK: Settings

    [Fact]
    public void SoundSettingsKeepUpstreamsNamesAndDefaults()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        Assert.False(store.GetBool(DefaultsKey.SoundOutputSwitcherEnabled));
        Assert.True(store.GetBool(DefaultsKey.AudioPriorityOutputEnabled));
        Assert.True(store.GetBool(DefaultsKey.AudioPriorityInputEnabled));
        Assert.Equal(25, store.GetInt(DefaultsKey.MixerHeadphonesDisconnectVolumePercent));
        Assert.True(store.GetBool(DefaultsKey.MixerShowSystemSounds));
        Assert.Equal("mixerShowFinder", DefaultsKey.MixerShowSystemSounds);
        Assert.Equal("audioPriorityOutputUIDs", DefaultsKey.AudioPriorityOutputUIDs);
        Assert.NotNull(Notchly.Core.Shortcuts.Shortcut.Parse(store.GetString(DefaultsKey.SoundOutputSwitcherShortcut)));
        Assert.NotNull(Notchly.Core.Shortcuts.Shortcut.Parse(store.GetString(DefaultsKey.MicMuteShortcut)));
        store.Set(DefaultsKey.AppVolumes, new Dictionary<string, double> { ["Spotify.exe"] = 0.4 });
        Assert.Equal(0.4, store.Get(DefaultsKey.AppVolumes, new Dictionary<string, double>())["Spotify.exe"]);
        // What a mic mute took from this machine's devices does not travel in a backup.
        Assert.Contains(DefaultsKey.MicMuteSavedVolumes, SettingsBackup.MachineStateKeys);
        Assert.Contains(DefaultsKey.MicMuteActive, SettingsBackup.MachineStateKeys);
        Assert.DoesNotContain(DefaultsKey.MixerAppArrangement, SettingsBackup.MachineStateKeys);
    }
}
