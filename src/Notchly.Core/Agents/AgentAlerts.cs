// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure alert rules in upstream Sources/Vorssaint/Services/AgentUsage/AgentUsageService.swift:
// limit warnings once per crossing, renewals of warned windows, the daily budget once a day (ignored when it was
// already passed at launch), and which events reach the island given the person's choices.
namespace Notchly.Core.Agents;

/// <summary>
/// What the usage service remembers between readings to tell news from history. Not thread-safe: confine it with
/// the store. Call <see cref="Begin"/> once the first reading is done; nothing is reported before.
/// </summary>
public sealed class AgentAlerts
{
    private Dictionary<AgentProvider, AgentLimits> previous = [];
    /// <summary>Warned windows, each waiting for its renewal.</summary>
    private readonly Dictionary<string, (AgentProvider Provider, AgentLimitWindow Window)> warned = [];
    private DateTimeOffset? budgetDay;

    public bool ReportsTransitions { get; private set; }

    /// <summary>The first reading is done: what it found is history. A budget already passed today is not news.</summary>
    public void Begin(IReadOnlyDictionary<AgentProvider, AgentLimits> limits, IEnumerable<AgentUsageRecord> records, double? budget,
                      DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        ReportsTransitions = true;
        previous = new(limits);
        var today = AgentCalendar.StartOfDay(now, zone);
        if (budget is { } limit && records.Where(r => r.Date >= today).Sum(r => r.Cost ?? 0) >= limit) budgetDay = today;
    }

    /// <summary>Forgets everything, as when the section is turned off.</summary>
    public void Reset()
    {
        previous = [];
        warned.Clear();
        budgetDay = null;
        ReportsTransitions = false;
    }

    /// <summary>Another account signed in: its windows are not the ones warned about.</summary>
    public void ForgetProvider(AgentProvider provider)
    {
        previous.Remove(provider);
        foreach (var id in warned.Where(w => w.Value.Provider == provider).Select(w => w.Key).ToList()) warned.Remove(id);
    }

    /// <summary>Whenever limits may have changed: windows that crossed <paramref name="threshold"/> (the person's, or
    /// <see cref="NotchAgentSupport.DefaultLimitThreshold"/> while warnings are off, so renewals still track), and
    /// warned windows a banked reset renewed before their time.</summary>
    public IReadOnlyList<AgentUsageEvent> CheckLimits(IReadOnlyDictionary<AgentProvider, AgentLimits> limits, double? threshold)
    {
        if (!ReportsTransitions) return [];
        var level = threshold ?? NotchAgentSupport.DefaultLimitThreshold;
        var events = new List<AgentUsageEvent>();
        foreach (var (provider, reading) in limits)
        {
            foreach (var window in AgentLimitSupport.Crossings(previous.GetValueOrDefault(provider), reading, level))
            {
                warned[window.Id] = (provider, window);
                events.Add(new AgentUsageEvent.LimitWarning(provider, window));
            }
        }
        foreach (var (id, entry) in warned.ToList())
        {
            if (!limits.TryGetValue(entry.Provider, out var reading) || reading.Windows.FirstOrDefault(w => w.Id == id) is not { } window
                || window.UsedPercent >= level || !AgentLimitSupport.Renewed(entry.Window, window, reading.ObservedAt)) continue;
            warned.Remove(id);
            events.Add(new AgentUsageEvent.LimitReset(entry.Provider, window));
        }
        previous = new(limits);
        return events;
    }

    /// <summary>On every tick: a warned window that renews brings its agent back, worth a word.</summary>
    public IReadOnlyList<AgentUsageEvent> ReportRenewals(DateTimeOffset now)
    {
        var events = new List<AgentUsageEvent>();
        foreach (var (id, entry) in warned.ToList())
        {
            if (entry.Window.ResetsAt is not { } resets || resets > now) continue;
            warned.Remove(id);
            events.Add(new AgentUsageEvent.LimitReset(entry.Provider, entry.Window));
        }
        return events;
    }

    /// <summary>On each publication: today's spending reached the budget, once per day.</summary>
    public AgentUsageEvent? CheckBudget(AgentUsageSnapshot snapshot, double? budget, TimeZoneInfo? zone = null)
    {
        if (!ReportsTransitions || budget is not { } limit) return null;
        var today = AgentCalendar.StartOfDay(snapshot.Now, zone ?? TimeZoneInfo.Local);
        var spent = snapshot.Usage(AgentPeriod.Today).Total.Cost;
        if (spent < limit || budgetDay == today) return null;
        budgetDay = today;
        return new AgentUsageEvent.BudgetReached(spent, limit);
    }

    /// <summary>Whether <paramref name="event"/> reaches the island: its agent is on, and a finish lasted at least
    /// the minimum, while finish notices, limit warnings or the budget are switched on (non-null).</summary>
    public static bool Delivers(AgentUsageEvent @event, IReadOnlySet<AgentProvider> providers, double? finishMinimum, double? limitThreshold,
                                double? dailyBudget) => @event switch
    {
        AgentUsageEvent.Finished finished => providers.Contains(finished.Provider) && finishMinimum is { } minimum && finished.Duration >= minimum,
        AgentUsageEvent.LimitWarning warning => providers.Contains(warning.Provider) && limitThreshold is not null,
        AgentUsageEvent.LimitReset reset => providers.Contains(reset.Provider) && limitThreshold is not null,
        AgentUsageEvent.BudgetReached => dailyBudget is not null,
        _ => false,
    };
}
