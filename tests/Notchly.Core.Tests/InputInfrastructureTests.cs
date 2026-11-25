// SPDX-License-Identifier: GPL-3.0-or-later
// The marker every event Notchly sends carries (Windows only: upstream tags its synthetic CGEvents with fixed values).
using Notchly.Core.Input;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class InputInfrastructureTests
{
    [Fact]
    public void OurEventsAreRecognisedByTheirMarker()
    {
        foreach (var source in Enum.GetValues<InputSource>().Where(s => s != InputSource.None))
        {
            var marker = InputMarker.Make(source);
            Assert.True(InputMarker.IsOwn(marker), source.ToString());
            Assert.Equal(source, InputMarker.SourceOf(marker));
        }
    }

    [Fact]
    public void AnotherProgramsEventsAreNeverOurs()
    {
        Assert.False(InputMarker.IsOwn(0));
        Assert.False(InputMarker.IsOwn(1));
        Assert.False(InputMarker.IsOwn(0x12345678));
        Assert.False(InputMarker.IsOwn(-1));
        // The low half alone, a purpose without the tag, is not enough.
        Assert.False(InputMarker.IsOwn((long)InputSource.Scroll));
        Assert.Equal(InputSource.None, InputMarker.SourceOf(0x4E544348));
    }

    [Fact]
    public void TheMarkerKeepsItsTagInTheHighHalf()
    {
        Assert.Equal(0x4E544348L, InputMarker.Make(InputSource.Snippet) >> 32);
        Assert.Equal((long)InputSource.Snippet, InputMarker.Make(InputSource.Snippet) & 0xFFFFFFFF);
    }

    [Fact]
    public void InputPausesInGamesByDefault()
    {
        var store = SettingsStore.InMemory();
        store.RegisterDefaults(Defaults.Registered);
        Assert.True(store.GetBool(DefaultsKey.InputPauseInGames));
    }
}
