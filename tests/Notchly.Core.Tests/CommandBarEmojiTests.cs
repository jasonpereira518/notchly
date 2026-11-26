// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of upstream CommandBarEmojiTests (the checks that do not need the Swift service or the UI) and the
// "Command bar emoji" / "Skin tones" part of CommandBarFeatureTests (about lines 1622-1722).
using System.Globalization;
using Notchly.Core.CommandBar;

namespace Notchly.Core.Tests;

public class CommandBarEmojiTests
{
    private const string Vs16 = "️";
    private static IReadOnlyList<EmojiItem> Emoji => CommandBarEmoji.All;
    private static IReadOnlyList<CommandBarEmoji.SkinTone> Tones => CommandBarEmoji.AllTones;
    private static int Characters(string s) => new StringInfo(s).LengthInTextElements;

    // MARK: Command bar emoji

    [Fact]
    public void TheSearchableUnicodeEmojiSetIsThere() => Assert.True(Emoji.Count > 1_000);

    [Fact]
    public void EveryEmojiCarriesTheWordsThatFindIt() =>
        Assert.All(Emoji, e => { Assert.NotEmpty(e.Name); Assert.NotEmpty(e.Glyph); });

    [Fact]
    public void ChatVocabularyFindsLaughter() =>
        Assert.Contains(Emoji, e => e.Glyph == "😂" && e.Name == "face with tears of joy"
            && e.Keywords.Contains("haha") && e.Keywords.Contains("roflmao"));

    [Fact]
    public void ConversationalAliasesFindCommonReactions() =>
        Assert.Contains(Emoji, e => e.Glyph == "🤷" && e.Keywords.Contains("idk") && e.Keywords.Contains("whatever"));

    [Fact]
    public void ChatShorthandAndIntentFindEmoji() =>
        Assert.Contains(Emoji, e => e.Glyph == "🙏" && e.Keywords.Contains("appreciate") && e.Keywords.Contains("thx"));

    [Fact]
    public void TheOnesPeopleLookForByFeelingAreFindable() =>
        Assert.Contains(Emoji, e => e.Name.Contains("heart"));

    [Fact]
    public void CommonEmojiAnswerToBothUnicodeNamesAndHumanAliases() =>
        Assert.Contains(Emoji, e => e.Glyph == "💀" && e.Name == "skull" && e.Keywords.Contains("dead"));

    [Fact]
    public void EmojiPresentationDoesNotChangeAPopularRowsStoredIdentity() =>
        Assert.Contains(Emoji, e => e.Glyph == "🖥" + Vs16 && e.Identity == "🖥");

    [Fact]
    public void AnExistingSelectorRemainsPartOfItsOriginalRowIdentity() =>
        Assert.Contains(Emoji, e => e.Glyph == "❤" + Vs16 && e.Identity == "❤" + Vs16);

    [Fact]
    public void TextDefaultEmojiGetTheSelectorThatDisplaysThemAsEmoji()
    {
        var glyphs = Emoji.Select(e => e.Glyph).ToHashSet();
        Assert.All(new[] { "©" + Vs16, "™" + Vs16, "✂" + Vs16 }, g => Assert.Contains(g, glyphs));
    }

    [Fact]
    public void PopularTextDefaultEmojiKeepTheirEmojiPresentationSelector()
    {
        var glyphs = Emoji.Select(e => e.Glyph).ToHashSet();
        Assert.All(new[] { "🌤", "🌧", "⛈", "🗺", "🖥", "🖱", "🖨", "🛠" }, g => Assert.Contains(g + Vs16, glyphs));
    }

    [Fact]
    public void IncompleteEmojiSequenceComponentsAreNotOfferedAlone()
    {
        var glyphs = Emoji.Select(e => e.Glyph).ToHashSet();
        Assert.All(new[] { "#" + Vs16, "*" + Vs16, "0" + Vs16, "9" + Vs16, "#", "*", "0", "9", "🏻", "🇦", "🦰", "🦱", "🦲", "🦳" },
            g => Assert.DoesNotContain(g, glyphs));
    }

    [Fact]
    public void PopularEmojiKeepAPredictableLeadOverTheLongTail() => Assert.Equal("😀", Emoji[0].Glyph);

    [Fact]
    public void NoEmojiIsOfferedTwice()
    {
        Assert.Equal(Emoji.Count, Emoji.Select(e => e.Glyph).Distinct().Count());
        Assert.Equal(Emoji.Count, Emoji.Select(e => CommandBarEmoji.Canonical(e.Glyph)).Distinct().Count());
        Assert.Equal(Emoji.Count, Emoji.Select(e => e.Identity).Distinct().Count());
    }

    // Beyond the upstream checks: the generated table's own rules.

    [Fact]
    public void UnicodeNamesMatchWhatUpstreamsTransformGives()
    {
        string NameOf(string glyph) => Emoji.First(e => e.Glyph == glyph).Name;
        Assert.Equal("heavy black heart", NameOf("❤" + Vs16));
        Assert.Equal("copyright sign", NameOf("©" + Vs16));
        Assert.Equal("black scissors", NameOf("✂" + Vs16));
        Assert.All(Emoji, e => Assert.Equal(e.Name.ToLowerInvariant(), e.Name));
    }

    [Fact]
    public void PopularRowsLeadInTheirOwnOrderThenTheLongTailIsSortedByName()
    {
        var popular = Emoji.TakeWhile(e => e.Glyph != "🧮").ToList();
        Assert.Equal(["😀", "😃", "😄"], popular.Take(3).Select(e => e.Glyph));
        Assert.Equal("🧩", popular[^1].Glyph);
        var tail = Emoji.Skip(popular.Count).Select(e => e.Name).ToList();
        Assert.Equal(tail.Order(StringComparer.Ordinal), tail);
    }

    [Fact]
    public void EveryAliasBelongsToARow() =>
        Assert.All(CommandBarEmoji.AliasTable.Keys, key =>
            Assert.Contains(Emoji, e => e.Identity == key && e.Keywords == CommandBarEmoji.AliasTable[key]));

    [Fact]
    public void ARowComesBackFromItsStoredIdentity()
    {
        Assert.Equal("🖥" + Vs16, CommandBarEmoji.FindByIdentity("🖥")?.Glyph);
        Assert.Null(CommandBarEmoji.FindByIdentity("nope"));
    }

    [Fact]
    public void TheTableIsLoadedOnceEvenWhenAskedFromManyThreads()
    {
        var seen = Enumerable.Range(0, 16).AsParallel().Select(_ => CommandBarEmoji.All).ToList();
        Assert.All(seen, list => Assert.Same(seen[0], list));
    }

    // MARK: Skin tones

    [Fact]
    public void TheYellowDefaultAndTheFiveTonesUnicodeDefinesAndNothingElse()
    {
        Assert.Equal(6, Tones.Count);
        Assert.Null(CommandBarEmoji.SkinTone.None.Modifier());
        Assert.Equal(
            ["\U0001F3FB", "\U0001F3FC", "\U0001F3FD", "\U0001F3FE", "\U0001F3FF"],
            Tones.Skip(1).Select(t => t.Modifier()));
    }

    [Fact]
    public void TonesKeepTheirStoredNames()
    {
        Assert.Equal(["", "light", "mediumLight", "medium", "mediumDark", "dark"], Tones.Select(t => t.RawValue()));
        Assert.All(Tones, t => Assert.Equal(t, CommandBarEmoji.TryParseTone(t.RawValue())));
    }

    [Fact]
    public void AToneSurvivesStorageAndOneThisVersionDoesNotKnowIsNotATone()
    {
        Assert.Equal(CommandBarEmoji.SkinTone.None, CommandBarEmoji.TryParseTone(""));
        Assert.Equal(CommandBarEmoji.SkinTone.Dark, CommandBarEmoji.TryParseTone("dark"));
        Assert.Null(CommandBarEmoji.TryParseTone("mauve"));
        Assert.Null(CommandBarEmoji.TryParseTone(null));
    }

    [Fact]
    public void AHandTakesAToneWhetherOrNotItCarriesAPresentationSelector()
    {
        Assert.True(CommandBarEmoji.AcceptsSkinTone("👍"));
        Assert.True(CommandBarEmoji.AcceptsSkinTone("☝" + Vs16));
        Assert.True(CommandBarEmoji.AcceptsSkinTone("☝"));
    }

    [Fact]
    public void AFaceAnObjectAndASequenceAreAllLeftAlone()
    {
        Assert.False(CommandBarEmoji.AcceptsSkinTone("😀"));
        Assert.False(CommandBarEmoji.AcceptsSkinTone("🍕"));
        Assert.False(CommandBarEmoji.AcceptsSkinTone("🤷‍♀" + Vs16));
    }

    [Fact]
    public void AToneIsTheModifierAppendedToTheEmoji() =>
        Assert.Equal("👍\U0001F3FD", CommandBarEmoji.Applying(CommandBarEmoji.SkinTone.Medium, "👍"));

    [Fact]
    public void ThePresentationSelectorGoesWithTheTone() =>
        Assert.Equal("☝\U0001F3FD", CommandBarEmoji.Applying(CommandBarEmoji.SkinTone.Medium, "☝" + Vs16));

    [Fact]
    public void EveryToneOfAnEmojiIsStillOneCharacterToTypeAndToDelete() =>
        Assert.All(Tones, t => Assert.Equal(1, Characters(CommandBarEmoji.Applying(t, "☝" + Vs16))));

    [Fact]
    public void TheDefaultAndAnEmojiWithNoToneToGiveAreReturnedUntouched()
    {
        Assert.Equal("👍", CommandBarEmoji.Applying(CommandBarEmoji.SkinTone.None, "👍"));
        Assert.Equal("🍕", CommandBarEmoji.Applying(CommandBarEmoji.SkinTone.Dark, "🍕"));
    }

    [Fact]
    public void ThePickerShowsADifferentHandForEveryToneItOffers() =>
        Assert.Equal(Tones.Count, Tones.Select(t => t.Swatch()).Distinct().Count());

    [Fact]
    public void TheOfferedSetHasEmojiThatTakeAToneAndEmojiThatDoNot()
    {
        Assert.Contains(Emoji, e => CommandBarEmoji.AcceptsSkinTone(e.Glyph));
        Assert.Contains(Emoji, e => !CommandBarEmoji.AcceptsSkinTone(e.Glyph));
    }

    [Fact]
    public void EveryToneChangesTheCharacterSoAnIdCarryingOneWouldMoveWithIt() =>
        Assert.All(Tones.Skip(1), t => Assert.NotEqual("👍", CommandBarEmoji.Applying(t, "👍")));

    [Fact]
    public void TheFamilyStaysUnchangedInsteadOfOfferingUnsupportedSkinTones()
    {
        Assert.False(CommandBarEmoji.AcceptsSkinTone("👪"));
        Assert.All(Tones, t => Assert.Equal("👪", CommandBarEmoji.Applying(t, "👪")));
        Assert.Empty(CommandBarEmoji.AlternateTones("👪", CommandBarEmoji.SkinTone.None));
    }

    [Fact]
    public void ExcludingTheFamilyPreservesSupportedSingleAndMultiPersonTones() =>
        Assert.All(new[] { "👍", "☝" + Vs16, "🤝", "👫", "👬", "👭", "💏", "💑" },
            g => Assert.True(CommandBarEmoji.AcceptsSkinTone(g), g));

    [Fact]
    public void RowIdentitiesStayStableWhateverTheDefaultTone()
    {
        var originalIds = Emoji.Select(e => "emoji." + e.Identity).ToList();
        foreach (var tone in Tones)
        {
            // A row's id comes from its identity, its shown character from the tone.
            var rows = Emoji.Select(e => (Id: "emoji." + e.Identity, Title: CommandBarEmoji.Applying(tone, e.Glyph) + "  " + e.Name)).ToList();
            Assert.Equal(originalIds, rows.Select(r => r.Id));
            Assert.Contains(rows, r => r.Id == "emoji.👍" && r.Title.StartsWith(CommandBarEmoji.Applying(tone, "👍") + "  "));
        }
    }

    [Fact]
    public void EachDefaultOffersTheOtherFiveDistinctTonesAndNotItself()
    {
        foreach (var current in Tones)
        {
            var alternates = CommandBarEmoji.AlternateTones("👍", current);
            Assert.Equal(5, alternates.Count);
            Assert.Equal(5, alternates.Select(a => a.Glyph).Distinct().Count());
            Assert.DoesNotContain(alternates, a => a.Tone == current);
            Assert.DoesNotContain(alternates, a => a.Glyph == CommandBarEmoji.Applying(current, "👍"));
        }
    }

    // MARK: Emoji search scope

    [Fact]
    public void AnOrdinarySearchNeverOpensTheEmojiIndex() => Assert.Null(CommandBarEmoji.EmojiQuery("fire"));

    [Fact]
    public void ALeadingColonScopesTheSearchAndStaysOutOfTheEmojiQuery()
    {
        Assert.Equal("fire", CommandBarEmoji.EmojiQuery(":fire"));
        Assert.Equal("heart", CommandBarEmoji.EmojiQuery("  : heart  "));
    }

    [Fact]
    public void AColonByItselfOpensTheEmojiIndexForBrowsing()
    {
        Assert.Equal("", CommandBarEmoji.EmojiQuery(":"));
        var rows = CommandBarEmoji.Browse();
        Assert.Equal(CommandBarEmoji.ResultLimit, rows.Count);
        Assert.Equal(40, rows.Count);
        Assert.Equal(Emoji.Take(40), rows);
    }
}
