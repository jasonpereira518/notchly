// SPDX-License-Identifier: GPL-3.0-or-later
// The local starred list: identity, toggling, the size cap and saving (a Windows addition).
using Notchly.Core.Notch.Music;

namespace Notchly.Core.Tests;

public class NotchStarTests
{
    [Fact]
    public void IdentityIgnoresCaseAndSpacing()
    {
        Assert.Equal(NotchStarStore.KeyFor("Hello", "Adele"), NotchStarStore.KeyFor("  hello ", "ADELE"));
        Assert.Equal(NotchStarStore.KeyFor("Two  Words", "A"), NotchStarStore.KeyFor("two words", " a"));
        Assert.NotEqual(NotchStarStore.KeyFor("Hello", "Adele"), NotchStarStore.KeyFor("Hello", "Lionel Richie"));
    }

    [Fact]
    public void NamelessSongsCannotBeStarred()
    {
        var store = new NotchStarStore();
        Assert.False(store.Toggle("", "Someone"));
        Assert.False(store.Toggle(null, null));
        Assert.Empty(store.Entries);
        Assert.False(store.Contains("", "Someone"));
    }

    [Fact]
    public void ToggleStarsThenUnstars()
    {
        var store = new NotchStarStore();
        Assert.True(store.Toggle("Song", "Band"));
        Assert.True(store.Contains("SONG", "band"));
        Assert.False(store.Toggle("song", "BAND"));
        Assert.False(store.Contains("Song", "Band"));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void NewestStarComesFirst()
    {
        var store = new NotchStarStore();
        store.Toggle("One", "A");
        store.Toggle("Two", "B");
        Assert.Equal(["Two", "One"], store.Entries.Select(e => e.Title));
    }

    [Fact]
    public void ListIsCappedAndDropsTheOldest()
    {
        var store = new NotchStarStore();
        for (var i = 0; i < NotchStarStore.Limit + 5; i++) store.Toggle($"Song {i}", "Band");
        Assert.Equal(NotchStarStore.Limit, store.Entries.Count);
        Assert.False(store.Contains("Song 0", "Band"));
        Assert.True(store.Contains($"Song {NotchStarStore.Limit + 4}", "Band"));
    }

    [Fact]
    public void SavedListComesBackInOrder()
    {
        var store = new NotchStarStore();
        store.Toggle("One", "A");
        store.Toggle("Two", "B");
        var again = NotchStarStore.FromJson(store.ToJson());
        Assert.Equal(store.Entries, again.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    public void DamagedTextGivesAnEmptyList(string? json) => Assert.Empty(NotchStarStore.FromJson(json).Entries);

    [Fact]
    public void ClearEmptiesTheList()
    {
        var store = new NotchStarStore();
        store.Toggle("One", "A");
        store.Clear();
        Assert.Empty(store.Entries);
    }
}
