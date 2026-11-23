// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Tests/AgentUsageSummaryCacheTests.swift: the incremental summary must equal a full rebuild
// through appends, streamed corrections, a late response, the clock moving on and back, a DST midnight, a time
// zone change, provider toggles, pruning and repricing; and the store keeps its cache in step.
using Notchly.Core.Agents;

namespace Notchly.Core.Tests;

[Collection("AgentPricing")]
public class AgentSummaryCacheTests
{
    [Fact]
    public void TheCachedSummaryEqualsAFullRebuildThroughEveryKindOfChange()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        // The day before the spring DST transition, including future entries.
        var now = AgentTimestamp.Parse("2026-03-07T23:55:00Z")!.Value;
        var cache = new AgentUsageSummaryCache();
        var records = Enumerable.Range(0, 6_000).Select(index => new AgentUsageRecord(
            index % 2 == 0 ? AgentProvider.Claude : AgentProvider.Codex, now.AddSeconds(-index * 1_400.0 + 900),
            index % 2 == 0 ? "claude-opus-5-5" : "gpt-6-sol", "p" + index % 7, "s", new AgentTokens(index + 1, Output: 10),
            index % 13 == 0 ? null : index % 11 / 8.0, 0.125)).ToList();
        var providers = AgentProviders.All.ToHashSet();
        var live = new List<AgentLiveSession>();
        var limits = new Dictionary<AgentProvider, AgentLimits>();
        var plans = new Dictionary<AgentProvider, AgentPlan>();
        void Check()
        {
            var actual = cache.Snapshot(records, limits, live, plans, providers, now, zone);
            var expected = AgentUsageSummary.Snapshot(records, limits, live, plans, providers, now, zone);
            Assert.Equal(expected, actual);
        }
        Check();
        Assert.Equal(records.Count, cache.AccumulatedRecords);
        live = [new AgentLiveSession("live", AgentProvider.Codex, now, now, "gpt-6-sol", "p", default, 0)];
        Check();
        Assert.Equal(0, cache.AccumulatedRecords);
        limits[AgentProvider.Codex] = new AgentLimits(AgentProvider.Codex, [], now, AgentLimitSource.SessionLog);
        plans[AgentProvider.Codex] = new AgentPlan("Pro", 200);
        Check();
        Assert.Equal(0, cache.AccumulatedRecords);
        var appended = new AgentUsageRecord(AgentProvider.Codex, now, "gpt-6-sol", "new", "s", new AgentTokens(50, Output: 10), 0.25, 0.125);
        records.Add(appended);
        cache.RecordChanged(records.Count - 1, null);
        Check();
        Assert.Equal(1, cache.AccumulatedRecords);
        for (var i = 0; i < 3; i++)
        {
            var position = records.Count - 1;
            cache.RecordChanged(position, records[position]);
            records[position] = records[position] with
            {
                Tokens = records[position].Tokens with { Output = records[position].Tokens.Output + 5 },
                Cost = records[position].Cost + 0.125,
            };
        }
        Check();
        Assert.Equal(1, cache.AccumulatedRecords);
        // A response can be appended and revised within the same publication.
        cache.RecordChanged(records.Count, null);
        records.Add(appended);
        cache.RecordChanged(records.Count - 1, appended);
        records[^1] = records[^1] with { Tokens = records[^1].Tokens with { Output = records[^1].Tokens.Output + 1 } };
        Check();
        // A newly discovered old response updates history without entering the sliding windows.
        cache.RecordChanged(records.Count, null);
        records.Add(new AgentUsageRecord(AgentProvider.Claude, now.AddDays(-20), "unknown", "late", "s", new AgentTokens(Output: 5), null, 0));
        Check();
        // An unpriced response becomes priced through a correction.
        cache.RecordChanged(13, records[13]);
        records[13] = records[13] with { Cost = 0.5, Tokens = records[13].Tokens with { Output = records[13].Tokens.Output + 2 } };
        Check();
        now = now.AddHours(1);
        Check();
        Assert.Equal(0, cache.AccumulatedRecords);
        live = [];
        providers = [AgentProvider.Claude];
        Check();
        providers = AgentProviders.All.ToHashSet();
        Check();
        // Midnight across DST rebuilds the daily and hourly boundaries.
        now = now.AddDays(1);
        Check();
        zone = TimeZoneInfo.Utc;
        Check();
        // A backwards clock restores entries that left the sliding windows.
        now = now.AddSeconds(-90_000);
        Check();
        records.RemoveRange(0, 20);
        cache.Invalidate();
        Check();
        for (var i = 0; i < records.Count; i++) records[i] = records[i] with { Cost = 0.5 };
        cache.Invalidate();
        Check();
    }

    [Fact]
    public void IncrementalUpdatesAccumulateAtMostOneRecordEach()
    {
        var zone = TimeZoneInfo.Utc;
        var now = AgentTimestamp.Parse("2026-09-22T12:00:00Z")!.Value;
        var cache = new AgentUsageSummaryCache();
        var records = new List<AgentUsageRecord>();
        var providers = new HashSet<AgentProvider> { AgentProvider.Codex };
        var none = new Dictionary<AgentProvider, AgentLimits>();
        var noPlans = new Dictionary<AgentProvider, AgentPlan>();
        cache.Snapshot(records, none, [], noPlans, providers, now, zone);
        for (var i = 0; i < 50; i++)
        {
            records.Add(new AgentUsageRecord(AgentProvider.Codex, now.AddMinutes(-i), "gpt-6-sol", "p", "s", new AgentTokens(10), 0.01, 0));
            cache.RecordChanged(records.Count - 1, null);
            var snapshot = cache.Snapshot(records, none, [], noPlans, providers, now.AddSeconds(i), zone);
            Assert.InRange(cache.AccumulatedRecords, 0, 1);
            Assert.Equal(AgentUsageSummary.Snapshot(records, none, [], noPlans, providers, now.AddSeconds(i), zone), snapshot);
        }
    }

    [Fact]
    public void TheStoreKeepsItsCachedSummaryInStep()
    {
        var zone = TimeZoneInfo.Utc;
        var now = AgentTimestamp.Parse("2026-03-08T00:55:00Z")!.Value;
        var store = new AgentUsageStore();
        var providers = AgentProviders.All.ToHashSet();
        var plans = new Dictionary<AgentProvider, AgentPlan>();
        void Check() => Assert.Equal(AgentUsageSummary.Snapshot(store.Records, store.Limits, store.Live, plans, providers, now, zone),
                                     store.Snapshot(plans, providers, now, zone));
        Check();
        void Apply(int output, string key, double age = 0)
        {
            var tokens = new AgentTokens(100, Output: output);
            var billable = new AgentBillable(tokens);
            var price = AgentPricing.Cost(billable, "gpt-6-sol");
            var record = new AgentUsageRecord(AgentProvider.Codex, now.Plus(-age), "gpt-6-sol", "project", "s", tokens, price.Cost, price.Savings);
            store.Apply([new AgentLogEntry.Usage(key, record, billable)], "f", AgentProvider.Codex, false, now, now);
        }
        Apply(5, "first");
        Check();
        Apply(15, "first");
        Apply(15, "first");
        Check();
        Assert.Single(store.Records);
        Apply(5, "old", 80 * 86_400);
        Check();
        store.Reprice();
        Check();
        store.DropRecords(now.AddDays(-1));
        Check();
        Assert.Single(store.Records);
    }
}
