// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/AgentUsageSummary.swift (periods, totals, buckets,
// Claude's five-hour window, burn rate, the incremental AgentUsageSummaryCache, limit pace and crossings), plus
// the activity card's figures from UI/Notch/NotchAgentsView.swift and NotchAgentComponents.swift (streak, active
// days, busiest day, the 13-week heatmap). Calendar days come from an injected TimeZoneInfo.
using System.Runtime.InteropServices;

namespace Notchly.Core.Agents;

public enum AgentPeriod { Today, Week, Month }

public static class AgentPeriods
{
    public static readonly IReadOnlyList<AgentPeriod> All = [AgentPeriod.Today, AgentPeriod.Week, AgentPeriod.Month];

    public static int Days(this AgentPeriod period) => period switch { AgentPeriod.Today => 1, AgentPeriod.Week => 7, _ => 30 };

    public static string RawValue(this AgentPeriod period) => period switch
    {
        AgentPeriod.Today => "today", AgentPeriod.Week => "week", _ => "month",
    };

    /// <summary>The stored period, today when unknown.</summary>
    public static AgentPeriod Parse(string? raw) => All.FirstOrDefault(p => p.RawValue() == raw, AgentPeriod.Today);
}

/// <summary>Sums of responses. <see cref="Unpriced"/> counts responses from a model without a known price, left
/// out of <see cref="Cost"/>.</summary>
public record struct AgentTotals
{
    public AgentTokens Tokens { get; set; }
    public double Cost { get; set; }
    public double Savings { get; set; }
    public long Requests { get; set; }
    public long Unpriced { get; set; }

    public void Add(AgentUsageRecord record)
    {
        Tokens += record.Tokens;
        Requests += Math.Max(0, record.Requests);
        Savings += record.Savings;
        if (record.Cost is { } price) Cost += price; else Unpriced += 1;
    }

    public void Add(AgentTotals other)
    {
        Tokens += other.Tokens;
        Cost += other.Cost;
        Savings += other.Savings;
        Requests += other.Requests;
        Unpriced += other.Unpriced;
    }

    /// <summary>Cost when every response had a price, tokens otherwise, so shares and bars never compare dollars
    /// with token counts.</summary>
    public readonly double Weight(bool byCost) => byCost ? Cost : Tokens.Total;
}

/// <summary>One row of a breakdown: a model or a project.</summary>
public sealed record AgentShare(string Id, string Name, AgentProvider? Provider)
{
    public AgentTotals Totals { get; set; }
}

public sealed class AgentPeriodUsage : IEquatable<AgentPeriodUsage>
{
    public AgentTotals Total;
    public Dictionary<AgentProvider, AgentTotals> ByProvider { get; init; } = [];
    public List<AgentShare> Models { get; set; } = [];
    public List<AgentShare> Projects { get; set; } = [];

    /// <summary>Whether every response in the period could be priced; when not, totals are a minimum ("≥").</summary>
    public bool FullyPriced => Total.Unpriced == 0;

    public AgentPeriodUsage Clone() => new() { Total = Total, ByProvider = new(ByProvider), Models = [.. Models], Projects = [.. Projects] };

    public bool Equals(AgentPeriodUsage? other) =>
        other is not null && Total == other.Total && AgentCollections.SameMap(ByProvider, other.ByProvider)
        && Models.SequenceEqual(other.Models) && Projects.SequenceEqual(other.Projects);

    public override bool Equals(object? obj) => Equals(obj as AgentPeriodUsage);
    public override int GetHashCode() => Total.GetHashCode();
}

/// <summary>A day or an hour of use.</summary>
public sealed class AgentBucket : IEquatable<AgentBucket>
{
    public AgentBucket(DateTimeOffset start) => Start = start;

    public DateTimeOffset Start { get; }
    public Dictionary<AgentProvider, AgentTotals> ByProvider { get; init; } = [];

    public AgentTotals Total
    {
        get
        {
            var total = new AgentTotals();
            foreach (var value in ByProvider.Values) total.Add(value);
            return total;
        }
    }

    public AgentBucket Clone() => new(Start) { ByProvider = new(ByProvider) };

    public bool Equals(AgentBucket? other) => other is not null && Start == other.Start && AgentCollections.SameMap(ByProvider, other.ByProvider);
    public override bool Equals(object? obj) => Equals(obj as AgentBucket);
    public override int GetHashCode() => Start.GetHashCode();
}

/// <summary>Claude's allowance renews five hours after the first request that follows a renewal. Local activity
/// alone places that window closely.</summary>
public sealed record AgentBlock(DateTimeOffset Start, DateTimeOffset End, AgentTotals Totals);

public sealed class AgentUsageSnapshot : IEquatable<AgentUsageSnapshot>
{
    /// <summary>The history the island can show: thirteen weeks for the activity map.</summary>
    public const int DayCount = 91;

    public bool Loaded { get; set; }
    public DateTimeOffset Now { get; set; } = AgentTime.DistantPast;
    public Dictionary<AgentPeriod, AgentPeriodUsage> Periods { get; set; } = [];
    /// <summary>Oldest first, ending with today.</summary>
    public List<AgentBucket> Days { get; set; } = [];
    /// <summary>Today's hours, from midnight.</summary>
    public List<AgentBucket> Hours { get; set; } = [];
    public Dictionary<AgentProvider, AgentLimits> Limits { get; set; } = [];
    public Dictionary<AgentProvider, AgentPlan> Plans { get; set; } = [];
    public List<AgentLiveSession> Live { get; set; } = [];
    public AgentBlock? ClaudeBlock { get; set; }
    /// <summary>The last half hour, scaled to an hour.</summary>
    public Dictionary<AgentProvider, AgentTotals> BurnRate { get; set; } = [];
    public Dictionary<AgentProvider, DateTimeOffset> LastActivity { get; set; } = [];
    /// <summary>Providers with anything to show: usage, limits or a turn.</summary>
    public HashSet<AgentProvider> Seen { get; set; } = [];

    public AgentPeriodUsage Usage(AgentPeriod period) => Periods.TryGetValue(period, out var usage) ? usage : new AgentPeriodUsage();
    public IEnumerable<AgentLiveSession> Working(AgentProvider provider) => Live.Where(s => s.Provider == provider);

    /// <summary>A deep copy: later changes to either leave the other alone.</summary>
    public AgentUsageSnapshot Clone() => new()
    {
        Loaded = Loaded, Now = Now,
        Periods = Periods.ToDictionary(p => p.Key, p => p.Value.Clone()),
        Days = Days.Select(d => d.Clone()).ToList(), Hours = Hours.Select(h => h.Clone()).ToList(),
        Limits = new(Limits), Plans = new(Plans), Live = [.. Live], ClaudeBlock = ClaudeBlock,
        BurnRate = new(BurnRate), LastActivity = new(LastActivity), Seen = [.. Seen],
    };

    public bool Equals(AgentUsageSnapshot? other) =>
        other is not null && Loaded == other.Loaded && Now == other.Now
        && AgentCollections.SameMap(Periods, other.Periods) && Days.SequenceEqual(other.Days) && Hours.SequenceEqual(other.Hours)
        && AgentCollections.SameMap(Limits, other.Limits) && AgentCollections.SameMap(Plans, other.Plans)
        && Live.SequenceEqual(other.Live) && Equals(ClaudeBlock, other.ClaudeBlock)
        && AgentCollections.SameMap(BurnRate, other.BurnRate) && AgentCollections.SameMap(LastActivity, other.LastActivity)
        && Seen.SetEquals(other.Seen);

    public override bool Equals(object? obj) => Equals(obj as AgentUsageSnapshot);
    public override int GetHashCode() => HashCode.Combine(Now, Days.Count);
}

internal static class AgentCollections
{
    public static bool SameMap<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> a, IReadOnlyDictionary<TKey, TValue> b) where TKey : notnull =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && EqualityComparer<TValue>.Default.Equals(pair.Value, value));

    /// <summary>The value stored under <paramref name="key"/>, added as default first, by reference.</summary>
    public static ref TValue Slot<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key) where TKey : notnull where TValue : struct =>
        ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
}

/// <summary>Calendar days in a time zone: the instant each local day starts, as Foundation's Calendar places it.</summary>
public static class AgentCalendar
{
    /// <summary>The first instant of the local day <paramref name="instant"/> falls in.</summary>
    public static DateTimeOffset StartOfDay(DateTimeOffset instant, TimeZoneInfo zone) =>
        StartOf(TimeZoneInfo.ConvertTime(instant, zone).Date, zone);

    /// <summary>The start of the local day <paramref name="days"/> after the one <paramref name="dayStart"/> falls in.</summary>
    public static DateTimeOffset AddDays(DateTimeOffset dayStart, int days, TimeZoneInfo zone) =>
        StartOf(TimeZoneInfo.ConvertTime(dayStart, zone).Date.AddDays(days), zone);

    public static bool SameDay(DateTimeOffset a, DateTimeOffset b, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(a, zone).Date == TimeZoneInfo.ConvertTime(b, zone).Date;

    public static DayOfWeek Weekday(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).DayOfWeek;

    /// <summary>Local midnight of <paramref name="date"/>, or the first valid moment after it when a clock change
    /// skips midnight.</summary>
    private static DateTimeOffset StartOf(DateTime date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        var step = 0;
        while (zone.IsInvalidTime(local) && step++ < 240) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

public static class AgentUsageSummary
{
    public const double BlockLength = 5 * 3600;
    /// <summary>Each window starts where the one before it ended, so a day of history covers any real stretch.</summary>
    public const double BlockHistory = 24 * 3600;
    public const double BurnWindow = 30 * 60;

    public static AgentUsageSnapshot Snapshot(IReadOnlyList<AgentUsageRecord> records, IReadOnlyDictionary<AgentProvider, AgentLimits> limits,
                                              IReadOnlyList<AgentLiveSession> live, IReadOnlyDictionary<AgentProvider, AgentPlan> plans,
                                              IReadOnlySet<AgentProvider> providers, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var snapshot = new AgentUsageSnapshot { Loaded = true, Now = now };
        snapshot.Limits = limits.Where(p => providers.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        snapshot.Plans = plans.Where(p => providers.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        snapshot.Live = live.Where(s => providers.Contains(s.Provider)).OrderBy(s => s.Started).ToList();
        snapshot.Seen = [.. snapshot.Limits.Keys, .. snapshot.Live.Select(s => s.Provider)];

        var today = AgentCalendar.StartOfDay(now, zone);
        var starts = Enumerable.Range(0, AgentUsageSnapshot.DayCount).Reverse().Select(back => AgentCalendar.AddDays(today, -back, zone)).ToList();
        var tomorrow = AgentCalendar.AddDays(today, 1, zone);
        var hourStarts = Enumerable.Range(0, 24).Select(h => today.AddHours(h)).ToList();
        var days = starts.Select(s => new AgentBucket(s)).ToList();
        var hours = hourStarts.Select(s => new AgentBucket(s)).ToList();
        var periods = new Dictionary<AgentPeriod, AgentPeriodUsage>();
        var models = new Dictionary<AgentPeriod, Dictionary<string, AgentShare>>();
        var projects = new Dictionary<AgentPeriod, Dictionary<string, AgentShare>>();
        var claude = new List<AgentUsageRecord>();
        var names = new Dictionary<string, string>();
        var recent = now.Plus(-BurnWindow);
        var lastDay = starts.Count - 1;

        foreach (var record in records)
        {
            if (!providers.Contains(record.Provider)) continue;
            snapshot.Seen.Add(record.Provider);
            snapshot.LastActivity[record.Provider] = AgentTime.Max(snapshot.LastActivity.GetValueOrDefault(record.Provider, AgentTime.DistantPast), record.Date);
            if (record.Provider == AgentProvider.Claude && record.Date > now.Plus(-BlockHistory) && record.Date <= now) claude.Add(record);
            if (record.Date >= recent && record.Date <= now) AgentCollections.Slot(snapshot.BurnRate, record.Provider).Add(record);
            if (record.Date < starts[0] || record.Date >= tomorrow || Index(record.Date, starts) is not { } day) continue;
            AgentCollections.Slot(days[day].ByProvider, record.Provider).Add(record);
            if (day == lastDay && Index(record.Date, hourStarts) is { } hour) AgentCollections.Slot(hours[hour].ByProvider, record.Provider).Add(record);
            // One name per model string, not per response: the history can hold tens of thousands of them.
            if (!names.TryGetValue(record.Model, out var name))
            {
                name = AgentPricing.DisplayName(record.Model);
                names[record.Model] = name;
            }
            var modelId = record.Provider.RawValue() + ":" + name;
            foreach (var period in AgentPeriods.All)
            {
                if (day <= lastDay - period.Days()) continue;
                if (!periods.TryGetValue(period, out var usage)) periods[period] = usage = new AgentPeriodUsage();
                usage.Total.Add(record);
                AgentCollections.Slot(usage.ByProvider, record.Provider).Add(record);
                var model = Share(models, period, modelId, name.Length == 0 ? "?" : name, record.Provider);
                model.Totals = Added(model.Totals, record);
                if (record.Project.Length > 0)
                {
                    var share = Share(projects, period, record.Project, record.Project, null);
                    share.Totals = Added(share.Totals, record);
                }
            }
        }
        foreach (var period in AgentPeriods.All)
        {
            var usage = periods.TryGetValue(period, out var found) ? found : new AgentPeriodUsage();
            var byCost = usage.FullyPriced;
            usage.Models = Sorted(models.TryGetValue(period, out var m) ? m.Values : [], byCost);
            usage.Projects = Sorted(projects.TryGetValue(period, out var p) ? p.Values : [], byCost);
            periods[period] = usage;
        }
        foreach (var provider in snapshot.BurnRate.Keys.ToList())
        {
            var rate = snapshot.BurnRate[provider];
            var scale = 3600 / BurnWindow;
            rate.Cost *= scale;
            rate.Savings *= scale;
            rate.Tokens = new AgentTokens((long)(rate.Tokens.Input * scale), (long)(rate.Tokens.CacheWrite * scale),
                                          (long)(rate.Tokens.CacheRead * scale), (long)(rate.Tokens.Output * scale),
                                          (long)(rate.Tokens.Reasoning * scale));
            snapshot.BurnRate[provider] = rate;
        }
        snapshot.Periods = periods;
        snapshot.Days = days;
        snapshot.Hours = hours;
        snapshot.ClaudeBlock = CurrentBlock(claude, now);
        return snapshot;
    }

    private static AgentTotals Added(AgentTotals totals, AgentUsageRecord record)
    {
        totals.Add(record);
        return totals;
    }

    internal static AgentShare Share(Dictionary<AgentPeriod, Dictionary<string, AgentShare>> shares, AgentPeriod period, string id,
                                     string name, AgentProvider? provider)
    {
        if (!shares.TryGetValue(period, out var map)) shares[period] = map = [];
        if (!map.TryGetValue(id, out var share)) map[id] = share = new AgentShare(id, name, provider);
        return share;
    }

    /// <summary>Whether a snapshot reads differently at <paramref name="now"/> with nothing new read: the last half
    /// hour slides, a Claude window can end and a new day moves every total.</summary>
    public static bool MovesWithClock(AgentUsageSnapshot snapshot, DateTimeOffset now, TimeZoneInfo? zone = null) =>
        snapshot.BurnRate.Count > 0 || snapshot.ClaudeBlock is not null
        || (snapshot.Now == AgentTime.DistantPast || !AgentCalendar.SameDay(snapshot.Now, now, zone ?? TimeZoneInfo.Local));

    /// <summary>By cost when every response is priced, by tokens otherwise; ties by name. Copies, so the list never
    /// changes with what it was sorted from.</summary>
    public static List<AgentShare> Sorted(IEnumerable<AgentShare> shares, bool byCost) =>
        shares.Select(s => s with { }).OrderByDescending(s => s.Totals.Weight(byCost)).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();

    /// <summary>The last start at or before <paramref name="date"/>, by bisection.</summary>
    public static int? Index(DateTimeOffset date, IReadOnlyList<DateTimeOffset> starts)
    {
        if (starts.Count == 0 || date < starts[0]) return null;
        int low = 0, high = starts.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (starts[middle] <= date) low = middle; else high = middle - 1;
        }
        return low;
    }

    /// <summary>Windows start at the first request after the previous one ended and last five hours from that
    /// moment, the renewal time the provider's own usage page shows; null once the last one has ended.</summary>
    public static AgentBlock? CurrentBlock(IReadOnlyList<AgentUsageRecord> records, DateTimeOffset now)
    {
        AgentBlock? block = null;
        foreach (var record in records.OrderBy(r => r.Date))
        {
            if (block is not null && record.Date < block.End)
            {
                var totals = block.Totals;
                totals.Add(record);
                block = block with { Totals = totals };
                continue;
            }
            var next = new AgentTotals();
            next.Add(record);
            block = new AgentBlock(record.Date, record.Date.Plus(BlockLength), next);
        }
        return block is not null && block.End > now ? block : null;
    }
}

/// <summary>
/// History totals kept between publications. Ordinary log updates add only new responses or the difference from a
/// streamed response. Calendar changes, provider changes, a clock moved back, a new day, pruning and repricing
/// rebuild from the authoritative records instead of trying to undo old buckets. The short sliding windows are
/// evaluated at publication time. Not thread-safe: confine it with the store.
/// </summary>
public sealed class AgentUsageSummaryCache
{
    private AgentUsageSnapshot? history;
    private TimeZoneInfo? zone;
    private HashSet<AgentProvider> providers = [];
    private readonly Dictionary<AgentPeriod, Dictionary<string, AgentShare>> models = [];
    private readonly Dictionary<AgentPeriod, Dictionary<string, AgentShare>> projects = [];
    private readonly Dictionary<string, string> names = [];
    private List<DateTimeOffset> starts = [];
    private List<DateTimeOffset> hourStarts = [];
    private DateTimeOffset tomorrow = AgentTime.DistantPast;
    private readonly Dictionary<int, AgentUsageRecord?> changes = [];
    private readonly HashSet<int> recentPositions = [];
    private DateTimeOffset lastTime = AgentTime.DistantPast;

    /// <summary>Records accumulated on the last publication: the whole history on a rebuild, zero or one per
    /// changed response otherwise.</summary>
    public int AccumulatedRecords { get; private set; }

    /// <summary>The next publication rebuilds from the records, as after pruning or repricing.</summary>
    public void Invalidate()
    {
        history = null;
        changes.Clear();
        recentPositions.Clear();
    }

    /// <summary>Notes that the record at <paramref name="position"/> was added (<paramref name="previous"/> null) or
    /// changed from <paramref name="previous"/>. Only the value before the first change since the last publication
    /// is kept.</summary>
    public void RecordChanged(int position, AgentUsageRecord? previous)
    {
        if (history is null || changes.ContainsKey(position)) return;
        changes[position] = previous;
    }

    public AgentUsageSnapshot Snapshot(IReadOnlyList<AgentUsageRecord> records, IReadOnlyDictionary<AgentProvider, AgentLimits> limits,
                                       IReadOnlyList<AgentLiveSession> live, IReadOnlyDictionary<AgentProvider, AgentPlan> plans,
                                       IReadOnlySet<AgentProvider> providers, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        AccumulatedRecords = 0;
        var rebuild = history is null || this.zone is null || !this.zone.Equals(zone) || !this.providers.SetEquals(providers)
            || now < lastTime || lastTime == AgentTime.DistantPast || !AgentCalendar.SameDay(lastTime, now, zone);
        if (rebuild)
        {
            this.zone = zone;
            this.providers = [.. providers];
            models.Clear();
            projects.Clear();
            names.Clear();
            recentPositions.Clear();
            history = AgentUsageSummary.Snapshot([], new Dictionary<AgentProvider, AgentLimits>(), [],
                                                 new Dictionary<AgentProvider, AgentPlan>(), providers, now, zone);
            starts = history.Days.Select(d => d.Start).ToList();
            hourStarts = history.Hours.Select(h => h.Start).ToList();
            tomorrow = AgentCalendar.AddDays(AgentCalendar.StartOfDay(now, zone), 1, zone);
            for (var position = 0; position < records.Count; position++)
            {
                Accumulate(records[position], null);
                if (IsRecent(records[position], now)) recentPositions.Add(position);
            }
        }
        else
        {
            foreach (var position in changes.Keys.Order())
            {
                Accumulate(records[position], changes[position]);
                if (IsRecent(records[position], now)) recentPositions.Add(position);
            }
        }
        var built = history!;
        if (rebuild || changes.Count > 0)
        {
            foreach (var period in AgentPeriods.All)
            {
                var usage = built.Periods[period];
                var byCost = usage.FullyPriced;
                usage.Models = AgentUsageSummary.Sorted(models.TryGetValue(period, out var m) ? m.Values : [], byCost);
                usage.Projects = AgentUsageSummary.Sorted(projects.TryGetValue(period, out var p) ? p.Values : [], byCost);
            }
        }
        changes.Clear();
        lastTime = now;
        var result = built.Clone();
        result.Now = now;
        result.Limits = limits.Where(p => providers.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        result.Plans = plans.Where(p => providers.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        result.Live = live.Where(s => providers.Contains(s.Provider)).OrderBy(s => s.Started).ToList();
        result.Seen.UnionWith(result.Limits.Keys);
        result.Seen.UnionWith(result.Live.Select(s => s.Provider));
        var claude = new List<AgentUsageRecord>();
        // Stable store order also preserves the original accumulation order.
        foreach (var position in recentPositions.Order().ToList())
        {
            var record = records[position];
            if (!IsRecent(record, now)) { recentPositions.Remove(position); continue; }
            if (record.Date > now) continue;
            if (record.Provider == AgentProvider.Claude) claude.Add(record);
            if (record.Date >= now.Plus(-AgentUsageSummary.BurnWindow)) AgentCollections.Slot(result.BurnRate, record.Provider).Add(record);
        }
        foreach (var provider in result.BurnRate.Keys.ToList())
        {
            // Scale amounts, not the number of responses or unpriced entries.
            var rate = result.BurnRate[provider];
            rate.Tokens += rate.Tokens;
            rate.Cost *= 2;
            rate.Savings *= 2;
            result.BurnRate[provider] = rate;
        }
        result.ClaudeBlock = AgentUsageSummary.CurrentBlock(claude, now);
        return result;
    }

    private bool IsRecent(AgentUsageRecord record, DateTimeOffset now)
    {
        if (!providers.Contains(record.Provider)) return false;
        return record.Provider == AgentProvider.Claude
            ? record.Date > now.Plus(-AgentUsageSummary.BlockHistory)
            : record.Date >= now.Plus(-AgentUsageSummary.BurnWindow);
    }

    private void Accumulate(AgentUsageRecord record, AgentUsageRecord? previous)
    {
        if (!providers.Contains(record.Provider)) return;
        var built = history!;
        AccumulatedRecords += 1;
        built.Seen.Add(record.Provider);
        built.LastActivity[record.Provider] = AgentTime.Max(built.LastActivity.GetValueOrDefault(record.Provider, AgentTime.DistantPast), record.Date);
        if (record.Date >= tomorrow || AgentUsageSummary.Index(record.Date, starts) is not { } day) return;
        var delta = new AgentTotals();
        delta.Add(record);
        if (previous is not null)
        {
            delta.Tokens -= previous.Tokens;
            delta.Cost -= previous.Cost ?? 0;
            delta.Savings -= previous.Savings;
            delta.Requests -= previous.Requests;
            delta.Unpriced -= previous.Cost is null ? 1 : 0;
        }
        AgentCollections.Slot(built.Days[day].ByProvider, record.Provider).Add(delta);
        if (day == starts.Count - 1 && AgentUsageSummary.Index(record.Date, hourStarts) is { } hour)
            AgentCollections.Slot(built.Hours[hour].ByProvider, record.Provider).Add(delta);
        if (!names.TryGetValue(record.Model, out var name))
        {
            name = AgentPricing.DisplayName(record.Model);
            names[record.Model] = name;
        }
        var modelId = record.Provider.RawValue() + ":" + name;
        foreach (var period in AgentPeriods.All)
        {
            if (day <= starts.Count - 1 - period.Days()) continue;
            var usage = built.Periods[period];
            usage.Total.Add(delta);
            AgentCollections.Slot(usage.ByProvider, record.Provider).Add(delta);
            var model = AgentUsageSummary.Share(models, period, modelId, name.Length == 0 ? "?" : name, record.Provider);
            var totals = model.Totals;
            totals.Add(delta);
            model.Totals = totals;
            if (record.Project.Length > 0)
            {
                var project = AgentUsageSummary.Share(projects, period, record.Project, record.Project, null);
                var projectTotals = project.Totals;
                projectTotals.Add(delta);
                project.Totals = projectTotals;
            }
        }
    }
}

/// <summary>How spending compares with the time a window has left: <see cref="Elapsed"/> is how much of the
/// window's time has passed, from 0 to 1.</summary>
public readonly record struct AgentPace(double Elapsed);

public static class AgentLimitSupport
{
    /// <summary>A window that renewed since it was read has nothing spent on it yet.</summary>
    public static AgentLimitWindow Current(AgentLimitWindow window, DateTimeOffset now) =>
        window.ResetsAt is { } resets && resets <= now
            ? new AgentLimitWindow(window.Id, window.Kind, window.Minutes, window.Scope, 0, null)
            : window;

    /// <summary>Once an estimate has passed, the countdown is unknown until another reading places it. That does
    /// not mean the allowance has renewed.</summary>
    public static DateTimeOffset? DisplayedReset(AgentLimitWindow window, DateTimeOffset now) =>
        (window.EstimatedResetsAt ?? window.ResetsAt) is { } reset && reset > now ? reset : null;

    public static AgentPace? Pace(AgentLimitWindow window, DateTimeOffset now)
    {
        if (window.Minutes is not { } minutes || minutes <= 0 || DisplayedReset(window, now) is not { } resets) return null;
        var length = minutes * 60.0;
        var elapsed = now.Since(resets.Plus(-length));
        if (elapsed <= 0) return null;
        return new AgentPace(Math.Min(1, elapsed / length));
    }

    /// <summary>The window that will stop work first: the most spent, and on a tie the one renewing last.</summary>
    public static AgentLimitWindow? Binding(AgentLimits? limits, DateTimeOffset now)
    {
        if (limits is null) return null;
        AgentLimitWindow? best = null;
        foreach (var window in limits.Windows.Select(w => Current(w, now)))
        {
            if (best is null) { best = window; continue; }
            var increasing = best.UsedPercent != window.UsedPercent
                ? best.UsedPercent < window.UsedPercent
                : (best.ResetsAt ?? AgentTime.DistantPast) < (window.ResetsAt ?? AgentTime.DistantPast);
            if (increasing) best = window;
        }
        return best;
    }

    /// <summary>Windows that reached <paramref name="threshold"/> percent between two readings of the same account.
    /// A renewed window starts again from zero; the first reading never warns.</summary>
    public static IReadOnlyList<AgentLimitWindow> Crossings(AgentLimits? previous, AgentLimits current, double threshold)
    {
        if (threshold <= 0) return [];
        return current.Windows.Where(window =>
        {
            if (window.UsedPercent < threshold) return false;
            if (previous?.Windows.FirstOrDefault(w => w.Id == window.Id) is not { } before) return false;
            return before.UsedPercent < threshold || Renewed(before, window, current.ObservedAt)
                || (current.Source == AgentLimitSource.Account && Began(window, previous.ObservedAt));
        }).ToList();
    }

    /// <summary>Whether the account's exact renewal places the start of <paramref name="window"/> after
    /// <paramref name="time"/>.</summary>
    private static bool Began(AgentLimitWindow window, DateTimeOffset time) =>
        window.Minutes is { } minutes && window.ResetsAt is { } resets && resets.Plus(-minutes * 60.0) > time;

    /// <summary>Whether <paramref name="window"/>, read at <paramref name="observedAt"/>, comes after the one
    /// <paramref name="before"/> saw: its usage fell, or its renewal moved on once the old one went by.</summary>
    public static bool Renewed(AgentLimitWindow before, AgentLimitWindow window, DateTimeOffset observedAt)
    {
        if (window.UsedPercent + 1 < before.UsedPercent) return true;
        if (before.ResetsAt is not { } was || window.ResetsAt is not { } resets) return false;
        return resets.Since(was) > 60 && observedAt >= was;
    }
}

/// <summary>The activity card's figures over a snapshot's days.</summary>
public static class AgentActivity
{
    /// <summary>Whether every day could be priced, so the map compares costs rather than tokens.</summary>
    public static bool ByCost(IReadOnlyList<AgentBucket> days) => days.All(d => d.Total.Unpriced == 0);

    /// <summary>Days in a row with a response, ending today, or yesterday while today has none yet.</summary>
    public static int Streak(IReadOnlyList<AgentBucket> days)
    {
        var count = 0;
        for (var i = days.Count - 1; i >= 0; i--)
        {
            if (days[i].Total.Requests > 0) count += 1;
            else if (count > 0 || i != days.Count - 1) break;
        }
        return count;
    }

    public static int ActiveDays(IReadOnlyList<AgentBucket> days) => days.Count(d => d.Total.Requests > 0);

    /// <summary>The active day that weighed most; the first of equals.</summary>
    public static AgentBucket? BusiestDay(IReadOnlyList<AgentBucket> days, bool byCost)
    {
        AgentBucket? best = null;
        foreach (var day in days.Where(d => d.Total.Requests > 0))
        {
            if (best is null || best.Total.Weight(byCost) < day.Total.Weight(byCost)) best = day;
        }
        return best;
    }

    /// <summary>Days laid into weeks that start on <paramref name="firstDay"/>, a column a week, seven rows each.</summary>
    public static IReadOnlyList<AgentBucket?[]> Columns(IReadOnlyList<AgentBucket> days, TimeZoneInfo? zone = null, DayOfWeek? firstDay = null)
    {
        zone ??= TimeZoneInfo.Local;
        var first = firstDay ?? System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var weeks = new List<AgentBucket?[]>();
        var current = new AgentBucket?[7];
        foreach (var day in days)
        {
            var weekday = ((int)AgentCalendar.Weekday(day.Start, zone) - (int)first + 7) % 7;
            if (weekday == 0 && current.Any(c => c is not null))
            {
                weeks.Add(current);
                current = new AgentBucket?[7];
            }
            current[weekday] = day;
        }
        if (current.Any(c => c is not null)) weeks.Add(current);
        return weeks;
    }

    /// <summary>Quartiles of the active days, so one heavy day does not wash out the rest.</summary>
    public static IReadOnlyList<double> Thresholds(IReadOnlyList<AgentBucket> days, bool byCost)
    {
        var values = days.Select(d => d.Total.Weight(byCost)).Where(v => v > 0).Order().ToList();
        if (values.Count == 0) return [];
        return new[] { 0.25, 0.5, 0.75 }.Select(q => values[Math.Min(values.Count - 1, (int)(values.Count * q))]).ToList();
    }

    /// <summary>A day's shade: 0 for a day without use, 1 to 4 by quartile otherwise.</summary>
    public static int Level(double value, IReadOnlyList<double> thresholds) =>
        value <= 0 ? 0 : Math.Min(3, thresholds.Count(t => value > t)) + 1;

    /// <summary>The cell opacity upstream draws each level with.</summary>
    public static readonly IReadOnlyList<double> LevelOpacity = [0.07, 0.28, 0.45, 0.65, 0.9];
}
