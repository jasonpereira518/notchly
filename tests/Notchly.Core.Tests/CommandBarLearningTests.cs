// SPDX-License-Identifier: GPL-3.0-or-later
// Ported from upstream Tests/CommandBarFeatureTests.swift: usage counts, query habits (keyed digests), the in-memory
// query memory and the installation key. Skipped: the UserDefaults persistence of the installation key (the caller
// stores it), and checks that read Swift source text.
using System.Text.RegularExpressions;
using Notchly.Core.CommandBar;

namespace Notchly.Core.Tests;

public class CommandBarLearningTests
{
    private const double Now = 1_800_000_000;
    private static readonly byte[] HabitKey = Enumerable.Repeat((byte)0x31, 32).ToArray();
    private static readonly byte[] OtherHabitKey = Enumerable.Repeat((byte)0x72, 32).ToArray();

    // ---- Usage ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RecordingARunCountsItAndStampsTheTime()
    {
        var usage = new Dictionary<string, CommandBarUse>();
        usage = CommandBarUsage.Recording(usage, "action.screenshot", Now);
        usage = CommandBarUsage.Recording(usage, "action.screenshot", Now + 10);
        usage = CommandBarUsage.Recording(usage, "action.darkMode", Now + 20);
        Assert.Equal(2, usage["action.screenshot"].Count);
        Assert.Equal(Now + 10, usage["action.screenshot"].LastUsed);
        Assert.Equal(usage, CommandBarUsage.Decode(CommandBarUsage.Encode(usage)));
    }

    [Fact]
    public void UsageCountsStopAtNineHundredNinetyNine()
    {
        var usage = new Dictionary<string, CommandBarUse> { ["a"] = new CommandBarUse(999, Now) };
        Assert.Equal(999, CommandBarUsage.Recording(usage, "a", Now + 1)["a"].Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData(null)]
    public void CorruptUsageDecodesAsACleanSlate(string? raw) => Assert.Empty(CommandBarUsage.Decode(raw));

    [Fact]
    public void ADamagedUsageEntryIsSkippedAndItsNeighboursAreKept()
    {
        var usage = CommandBarUsage.Decode("{\"a\":{\"count\":2,\"lastUsed\":5},\"b\":{\"count\":\"x\"},\"c\":3}");
        Assert.Equal(["a"], usage.Keys);
        Assert.Equal(new CommandBarUse(2, 5), usage["a"]);
    }

    [Fact]
    public void UsageUsesTheUpstreamJsonShape() =>
        Assert.Equal("{\"a\":{\"count\":1,\"lastUsed\":5}}", CommandBarUsage.Encode(new Dictionary<string, CommandBarUse> { ["a"] = new(1, 5) }));

    [Fact]
    public void TheUsageStoreCapsByDroppingTheOldestIds()
    {
        var crowded = new Dictionary<string, CommandBarUse>();
        for (var index = 0; index < CommandBarUsage.StoredIdLimit + 5; index++)
            crowded = CommandBarUsage.Recording(crowded, $"app.{index}", Now + index);
        Assert.Equal(CommandBarUsage.StoredIdLimit, crowded.Count);
        Assert.False(crowded.ContainsKey("app.0"));
        Assert.True(crowded.ContainsKey($"app.{CommandBarUsage.StoredIdLimit + 4}"));
    }

    [Fact]
    public void AHabitFadesAsItAges() =>
        Assert.True(CommandBarUsage.Boost(new CommandBarUse(3, Now), Now + 60) > CommandBarUsage.Boost(new CommandBarUse(3, Now), Now + 30 * 86400));

    [Fact]
    public void NoHabitOutrunsALiteralTextHit() =>
        Assert.True(CommandBarUsage.Boost(new CommandBarUse(999, Now), Now) < 700);

    [Fact]
    public void NoUsageNoBoost()
    {
        Assert.Equal(0, CommandBarUsage.Boost(null, Now));
        Assert.Equal(0, CommandBarUsage.Boost(new CommandBarUse(0, Now), Now));
    }

    [Theory]
    [InlineData(3, 60.0, 36)]                       // under 2 h: 12 per use
    [InlineData(3, 3 * 3600.0, 24)]                 // under 48 h: 8
    [InlineData(3, 5 * 86400.0, 15)]                // under 14 d: 5
    [InlineData(3, 30 * 86400.0, 6)]                // older: 2
    [InlineData(100, 60.0, 480)]                    // counts count up to 40
    public void RecencyWeightsAreTwelveEightFiveAndTwo(int count, double age, int expected) =>
        Assert.Equal(expected, CommandBarUsage.Boost(new CommandBarUse(count, Now), Now + age));

    [Fact]
    public void EmptyCategoriesLeadWithFrequentAndRecentChoicesThenKeepCatalogOrder()
    {
        var usage = new Dictionary<string, CommandBarUse>
        {
            ["emoji.fire"] = new(3, Now), ["emoji.heart"] = new(3, Now + 10), ["emoji.wave"] = new(1, Now + 20),
        };
        Assert.Equal(["emoji.heart", "emoji.fire", "emoji.wave", "emoji.grin", "emoji.star"],
            CommandBarUsage.CategoryIds(usage, ["emoji.grin", "emoji.fire", "emoji.wave", "emoji.heart", "emoji.star"]));
    }

    [Fact]
    public void AnUnlearnedCategoryPreservesItsUsefulCatalogOrder() =>
        Assert.Equal(["emoji.grin", "emoji.fire", "emoji.wave"],
            CommandBarUsage.CategoryIds(new Dictionary<string, CommandBarUse>(), ["emoji.grin", "emoji.fire", "emoji.wave"]));

    [Fact]
    public void SuggestionsLeadWithTheMostUsedAndFillWithCuratedOnes()
    {
        var usage = new Dictionary<string, CommandBarUse>();
        usage = CommandBarUsage.Recording(usage, "action.screenshot", Now);
        usage = CommandBarUsage.Recording(usage, "action.screenshot", Now + 10);
        usage = CommandBarUsage.Recording(usage, "action.darkMode", Now + 20);
        Assert.Equal(["action.screenshot", "action.darkMode", "action.colorPicker"],
            CommandBarUsage.SuggestionIds(usage,
                ["action.screenshot", "action.darkMode", "action.colorPicker", "action.ocr"],
                ["action.colorPicker", "action.gone", "action.ocr"], 3));
    }

    [Fact]
    public void CuratedSuggestionsSkipWhateverIsUnavailable() =>
        Assert.Equal(["b", "a"], CommandBarUsage.SuggestionIds(new Dictionary<string, CommandBarUse>(), ["a", "b"], ["c", "b", "a"], 5));

    // ---- Query habits --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("w")]
    [InlineData("wa")]
    public void OneAndTwoCharacterChoicesSurviveAStorageRoundTrip(string shortQuery)
    {
        var prepared = CommandBarQueryHabits.Prepare(shortQuery, HabitKey);
        var recorded = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(), prepared, "app.whatever", Now);
        var restored = CommandBarQueryHabits.Decode(CommandBarQueryHabits.Encode(recorded));
        Assert.True(CommandBarQueryHabits.Boost("app.whatever", prepared, restored, Now) > 0);
    }

    [Fact]
    public void RepeatedChoicesTeachTheExactQueryAndALongerRelatedQuery()
    {
        var what = CommandBarQueryHabits.Prepare("what", HabitKey);
        var whatever = CommandBarQueryHabits.Prepare("whatever", HabitKey);
        var store = CommandBarQueryHabits.NewStore();
        store = CommandBarQueryHabits.Recording(store, what, "app./Applications/Whatever.app", Now);
        store = CommandBarQueryHabits.Recording(store, what, "app./Applications/Whatever.app", Now + 10);
        Assert.True(CommandBarQueryHabits.Boost("app./Applications/Whatever.app", what, store, Now + 20) > 0);
        Assert.True(CommandBarQueryHabits.Boost("app./Applications/Whatever.app", whatever, store, Now + 20) > 0);
        // A learned query lifts only the selected result.
        Assert.Equal(0, CommandBarQueryHabits.Boost("app./Applications/Whatever Beta.app", what, store, Now + 20));
    }

    [Fact]
    public void BoostIsUseBoostTimesFivePlusPrefixLengthTimesSixCappedAt720()
    {
        var what = CommandBarQueryHabits.Prepare("what", HabitKey);
        var store = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(), what, "app.w", Now);
        // One use under two hours: 12 x 5 = 60, plus 4 characters x 6 = 24; shorter prefixes are worth less, the longest wins.
        Assert.Equal(60 + 24, CommandBarQueryHabits.Boost("app.w", what, store, Now));
        var crowded = new Dictionary<string, CommandBarUse> { ["app.w"] = new(999, Now) };
        var heavy = CommandBarQueryHabits.NewStore();
        foreach (var key in what.Keys) heavy[key.Digest] = crowded;
        Assert.Equal(720, CommandBarQueryHabits.Boost("app.w", what, heavy, Now));
    }

    [Fact]
    public void QueryHabitsRoundTripAsPerInstallKeyedDigestsPreparedOncePerQuery()
    {
        var what = CommandBarQueryHabits.Prepare("what", HabitKey);
        var store = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(), what, "app.whatever", Now);
        var other = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(),
            CommandBarQueryHabits.Prepare("what", OtherHabitKey), "app.test", Now);

        Assert.All(store.Keys, key => Assert.Matches(new Regex("^[0-9a-f]{24}$"), key));
        Assert.Equal(4, what.KeyCount);
        Assert.Empty(store.Keys.Intersect(other.Keys));
        Assert.Equal(store.Count, CommandBarQueryHabits.Decode(CommandBarQueryHabits.Encode(store)).Count);
        Assert.Equal(store, CommandBarQueryHabits.Decode(CommandBarQueryHabits.Encode(store)));
    }

    [Fact]
    public void TheDigestIsTheFirstTwelveBytesOfHmacSha256()
    {
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(HabitKey, "wh"u8.ToArray()))[..24].ToLowerInvariant();
        Assert.Equal(expected, CommandBarQueryHabits.Digest("wh", HabitKey));
        Assert.Equal(24, expected.Length);
    }

    [Fact]
    public void ForgettingAResultRemovesItsLearnedQueryChoices()
    {
        var store = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(),
            CommandBarQueryHabits.Prepare("what", HabitKey), "app./Applications/Whatever.app", Now);
        Assert.Empty(CommandBarQueryHabits.Removing("app./Applications/Whatever.app", store));
    }

    [Fact]
    public void RemovingOneResultKeepsTheOthersForTheSameQuery()
    {
        var prepared = CommandBarQueryHabits.Prepare("ab", HabitKey);
        var store = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(), prepared, "one", Now);
        store = CommandBarQueryHabits.Recording(store, prepared, "two", Now + 1);
        var removed = CommandBarQueryHabits.Removing("one", store);
        Assert.Equal(2, removed.Count);
        Assert.All(removed.Values, choices => Assert.Equal(["two"], choices.Keys));
    }

    [Fact]
    public void OnlyFourResultsAreKeptPerQueryDroppingTheLeastRecent()
    {
        var prepared = CommandBarQueryHabits.Prepare("a", HabitKey);
        var store = CommandBarQueryHabits.NewStore();
        for (var i = 0; i < 6; i++) store = CommandBarQueryHabits.Recording(store, prepared, $"row.{i}", Now + i);
        var choices = store[prepared.Keys[0].Digest];
        Assert.Equal(CommandBarQueryHabits.ResultLimit, choices.Count);
        Assert.DoesNotContain("row.0", choices.Keys);
        Assert.Contains("row.5", choices.Keys);
    }

    [Fact]
    public void OnlyThreeHundredTwentyQueriesAreKeptDroppingTheOldest()
    {
        var store = CommandBarQueryHabits.NewStore();
        for (var queryIndex = 0; queryIndex < CommandBarQueryHabits.StoredQueryLimit; queryIndex++)
            store[queryIndex.ToString("x24")] = new Dictionary<string, CommandBarUse> { ["app.0"] = new(1, Now + queryIndex) };
        var prepared = CommandBarQueryHabits.Prepare("z", HabitKey);
        var next = CommandBarQueryHabits.Recording(store, prepared, "app.0", Now + 1000);
        Assert.Equal(CommandBarQueryHabits.StoredQueryLimit, next.Count);
        Assert.False(next.ContainsKey(0.ToString("x24")));
        Assert.True(next.ContainsKey(prepared.Keys[0].Digest));
    }

    [Fact]
    public void AKeyThatIsNotThirtyTwoBytesPreparesNothing()
    {
        Assert.True(CommandBarQueryHabits.Prepare("what", null).IsEmpty);
        Assert.True(CommandBarQueryHabits.Prepare("what", new byte[16]).IsEmpty);
        Assert.True(CommandBarQueryHabits.Prepare("", HabitKey).IsEmpty);
    }

    [Fact]
    public void OnlyTheFirstTwentyFourCharactersOfTheQueryAreHashed() =>
        Assert.Equal(24, CommandBarQueryHabits.Prepare(new string('a', 40), HabitKey).KeyCount);

    [Fact]
    public void AMaximumLearnedQueryStoreIsDecodedOnceNotOncePerKeystroke()
    {
        var store = CommandBarQueryHabits.NewStore();
        for (var queryIndex = 0; queryIndex < CommandBarQueryHabits.StoredQueryLimit; queryIndex++)
        {
            var choices = new Dictionary<string, CommandBarUse>();
            for (var resultIndex = 0; resultIndex < 4; resultIndex++)
                choices[$"app.{resultIndex}"] = new CommandBarUse(resultIndex + 1, Now + queryIndex * 4 + resultIndex);
            store[queryIndex.ToString("x24")] = choices;
        }
        var payload = CommandBarQueryHabits.Encode(store);
        var decodes = 0;
        var cache = new CommandBarQueryHabitStoreCache();
        cache.Reload(payload, raw => { decodes++; return CommandBarQueryHabits.Decode(raw); });
        for (var length = 3; length <= 24; length++)
        {
            var prepared = CommandBarQueryHabits.Prepare("abcdefghijklmnopqrstuvwx"[..length], HabitKey);
            _ = CommandBarQueryHabits.Boost("app.0", prepared, cache.Store, Now);
        }
        Assert.Equal(1, decodes);
        Assert.Equal(320, cache.Store.Count);
    }

    [Fact]
    public void ExtendingAQueryHashesOnlyEachNewlyAddedPrefix()
    {
        var digests = 0;
        var cache = new CommandBarQueryHabits.PreparationCache();
        CommandBarQueryHabits.PreparedQuery last = CommandBarQueryHabits.PreparedQuery.Empty;
        string Count(string prefix, byte[] _)
        {
            digests++;
            return new string('0', 24 - prefix.Length.ToString().Length) + prefix.Length;
        }
        for (var length = 3; length <= 24; length++)
            last = CommandBarQueryHabits.Prepare("abcdefghijklmnopqrstuvwx"[..length], HabitKey, cache, Count);
        Assert.Equal(24, digests);
        Assert.Equal(24, last.KeyCount);

        // Deleting from a prepared query reuses its matching prefix slice.
        CommandBarQueryHabits.Prepare("abcdefghijkl", HabitKey, cache, (_, _) => { digests++; return "unused"; });
        Assert.Equal(24, digests);
    }

    [Fact]
    public void EditingAQueryInTheMiddleRehashes()
    {
        var digests = 0;
        var cache = new CommandBarQueryHabits.PreparationCache();
        string Count(string prefix, byte[] _) { digests++; return prefix; }
        CommandBarQueryHabits.Prepare("abcd", HabitKey, cache, Count);
        Assert.Equal(4, digests);
        var edited = CommandBarQueryHabits.Prepare("abxd", HabitKey, cache, Count);
        Assert.Equal(8, digests);
        Assert.Equal(["a", "ab", "abx", "abxd"], edited.Keys.Select(k => k.Digest));
    }

    [Fact]
    public void ChangingTheKeyRebuildsThePreparedQuery()
    {
        var cache = new CommandBarQueryHabits.PreparationCache();
        var first = CommandBarQueryHabits.Prepare("abc", HabitKey, cache);
        var second = CommandBarQueryHabits.Prepare("abc", OtherHabitKey, cache);
        Assert.NotEqual(first.Keys[0].Digest, second.Keys[0].Digest);
    }

    [Fact]
    public void TheDecodedStoreStaysInStepWithRecordingForgettingAndReloading()
    {
        var what = CommandBarQueryHabits.Prepare("what", HabitKey);
        var cache = new CommandBarQueryHabitStoreCache();
        var store = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(), what, "app./Applications/Whatever.app", Now);
        var encoded = CommandBarQueryHabits.Encode(store);

        cache.Record(what, "action.screenshot", Now);
        Assert.NotEmpty(cache.Store);
        cache.Remove("action.screenshot");
        Assert.Empty(cache.Store);
        cache.Record(what, "action.screenshot", Now);
        cache.ForgetAll();
        Assert.Empty(cache.Store);
        cache.Reload(encoded);
        Assert.Equal(store, cache.Store);
    }

    // ---- Installation key ----------------------------------------------------------------------------------------

    [Fact]
    public void AValidStoredKeyIsKeptSoDigestsMeanTheSameThingAfterARelaunch()
    {
        var first = CommandBarLearning.ResolveInstallationKey(null);
        Assert.True(first.WasReplaced);
        Assert.Equal(32, first.Key.Length);
        var again = CommandBarLearning.ResolveInstallationKey(first.StorageValue);
        Assert.False(again.WasReplaced);
        Assert.Equal(first.Key, again.Key);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("AAAA")]
    public void AnInvalidKeyIsReplacedSoDigestsThatCanNoLongerBeMatchedGetDropped(string stored)
    {
        var resolved = CommandBarLearning.ResolveInstallationKey(stored);
        Assert.True(resolved.WasReplaced);
        Assert.Equal(32, resolved.Key.Length);
        Assert.Equal(resolved.StorageValue, Convert.ToBase64String(resolved.Key));
    }

    [Fact]
    public void AChosenAppKeepsItsSearchPriorityAfterReloadingWithTheSameKey()
    {
        var key = CommandBarLearning.ResolveInstallationKey(null);
        var saved = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(),
            CommandBarQueryHabits.Prepare("wa", key.Key), "app.whatsapp", Now);
        var reloadedKey = CommandBarLearning.ResolveInstallationKey(key.StorageValue);
        var reloaded = CommandBarQueryHabits.Decode(CommandBarQueryHabits.Encode(saved));
        Assert.True(CommandBarQueryHabits.Boost("app.whatsapp", CommandBarQueryHabits.Prepare("wa", reloadedKey.Key), reloaded, Now) > 0);
    }

    [Fact]
    public void AChosenEmojiKeepsItsSearchPriorityAfterReloading()
    {
        var prepared = CommandBarQueryHabits.Prepare("thumb", HabitKey);
        var saved = CommandBarQueryHabits.Recording(CommandBarQueryHabits.NewStore(), prepared, "emoji.\U0001F44D", Now);
        var reloaded = CommandBarQueryHabits.Decode(CommandBarQueryHabits.Encode(saved));
        Assert.True(CommandBarQueryHabits.Boost("emoji.\U0001F44D", prepared, reloaded, Now) > 0);
    }

    // ---- Query memory --------------------------------------------------------------------------------------------

    [Fact]
    public void ChoosingARowForWhatWasTypedAlsoAnswersEveryShorterPieceOfIt() =>
        Assert.Equal(["w", "wh", "wha"], CommandBarQueryMemory.Prefixes("wha"));

    [Fact]
    public void WhatIsRememberedIsFoldedTheWayTheRankingFoldsAccentsAndAll() =>
        Assert.Equal(["r", "re", "res", "resu", "resum", "resume"], CommandBarQueryMemory.Prefixes("  Résumé "));

    [Fact]
    public void AnEmptyFieldTeachesNothing() => Assert.Empty(CommandBarQueryMemory.Prefixes("   "));

    [Fact]
    public void PastAWordsWorthOfLettersTheRankingAlreadyKnowsWhatToDo() =>
        Assert.Equal(CommandBarQueryMemory.LongestPrefix, CommandBarQueryMemory.Prefixes(new string('a', 40)).Count);

    [Fact]
    public void ARowNeverChosenForTheseLettersIsWorthNothingExtra()
    {
        var memory = new CommandBarQueryMemory();
        Assert.True(memory.IsEmpty);
        Assert.Equal(0, memory.Boost("pri", "app.primary"));
    }

    [Fact]
    public void TheRowChosenForAWordAnswersToTheLettersOnTheWayToIt()
    {
        var memory = new CommandBarQueryMemory();
        memory.Record("primary", "app.primary", 1);
        Assert.True(memory.Boost("pri", "app.primary") > 0);
        Assert.Equal(0, memory.Boost("pri", "app.other"));
        Assert.True(memory.Boost("prim", "app.primary") > 0);
        // Letters that were never typed on their own teach nothing.
        Assert.Equal(0, memory.Boost("primary extra", "app.primary"));
    }

    [Fact]
    public void ChoosingTheSameRowAgainStopsAddingUpOnceItIsCertain()
    {
        var memory = new CommandBarQueryMemory();
        for (var step = 1; step <= 4; step++) memory.Record("primary", "app.primary", step);
        Assert.Equal(CommandBarQueryMemory.MaximumBoost, memory.Boost("pri", "app.primary"));
        Assert.Equal(3, CommandBarQueryMemory.MaximumBoost);
    }

    [Fact]
    public void ForgettingOneRowTakesItOutOfEveryPrefixAndTheWholeSessionCanBeForgotten()
    {
        var memory = new CommandBarQueryMemory();
        memory.Record("primary", "app.primary", 1);
        memory.Forget("app.primary");
        Assert.True(memory.IsEmpty);
        memory.Record("a", "one", 1);
        memory.Clear();
        Assert.True(memory.IsEmpty);
    }

    [Fact]
    public void OnePrefixRemembersAFewRowsAndTheOnePickedLeastDropsOut()
    {
        var memory = new CommandBarQueryMemory();
        for (var index = 0; index < CommandBarQueryMemory.IdsPerQuery + 1; index++)
            for (var repeat = 0; repeat <= (index == 0 ? 0 : 3); repeat++)
                memory.Record("x", $"row.{index}", index * 10 + repeat);
        Assert.Equal(0, memory.Boost("x", "row.0"));
        Assert.True(memory.Boost("x", "row.4") > 0);
    }

    [Fact]
    public void OnlySixtyPrefixesAreRememberedTheOnesNotUsedForLongestGoFirst()
    {
        var memory = new CommandBarQueryMemory();
        for (var i = 0; i < 70; i++) memory.Record($"q{i:D3}", $"row.{i}", i);
        // The oldest queries' prefixes were pruned, the newest are still there.
        Assert.Equal(0, memory.Boost("q000", "row.0"));
        Assert.True(memory.Boost("q069", "row.69") > 0);
    }

    [Fact]
    public void FoldedLettersAskTheQueryMemoryTheSameQuestionTheRawOnesDo()
    {
        var memory = new CommandBarQueryMemory();
        Assert.Equal(0, memory.BoostNormalized("pri", "app.primary"));
        memory.Record("Primary", "app.primary", 1);
        var folded = memory.BoostNormalized(CommandBarSearch.Normalized("PRÍ"), "app.primary");
        Assert.True(folded > 0);
        Assert.Equal(folded, memory.Boost("PRÍ", "app.primary"));
    }
}
