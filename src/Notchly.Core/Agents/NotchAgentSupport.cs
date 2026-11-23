// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/Notch/NotchAgentSupport.swift: the AI page's cards and their saved
// order, the readings beside the camera, alert choices, the page's rows, plus AgentFormat and the agent strip's
// wing from NotchSupport.swift's compactAgentGeometry. Preferences arrive as raw stored values, so the app
// decides where they live; Codex account cards and the Resets card's content are not ported.
using System.Globalization;

namespace Notchly.Core.Agents;

/// <summary>The cards the AI page can show, in the order a person arranges them. Raw values are stored, so cases
/// are never renamed.</summary>
public enum NotchAgentCard { Limits, Spend, Live, Trend, Models, Projects, Activity, Resets }

/// <summary>What the closed island shows beside the camera while an agent works.</summary>
public enum NotchAgentReadout { Elapsed, Tokens, Cost, Limit }

public enum NotchAgentLimitDisplay { Remaining, Used }

/// <summary>Which allowance the closed island shows.</summary>
public enum NotchAgentLimitFocus { MostUsed, Session, Weekly }

/// <param name="Provider">The account a limits card belongs to.</param>
public sealed record NotchAgentTile(NotchAgentCard Card, AgentProvider? Provider)
{
    public string Id => Card.RawValue() + (Provider is { } p ? "." + p.RawValue() : "");
}

public static class NotchAgentEnums
{
    public static readonly IReadOnlyList<NotchAgentCard> AllCards =
    [
        NotchAgentCard.Limits, NotchAgentCard.Spend, NotchAgentCard.Live, NotchAgentCard.Trend,
        NotchAgentCard.Models, NotchAgentCard.Projects, NotchAgentCard.Activity, NotchAgentCard.Resets,
    ];

    public static string RawValue(this NotchAgentCard card) => card.ToString().ToLowerInvariant();
    public static NotchAgentCard? CardFrom(string raw) => AllCards.Cast<NotchAgentCard?>().FirstOrDefault(c => c!.Value.RawValue() == raw);

    /// <summary>Charts need the island's width; everything else pairs up.</summary>
    public static bool FullWidth(this NotchAgentCard card) => card is NotchAgentCard.Trend or NotchAgentCard.Activity;

    /// <summary>Cards that cover the period chosen in the island's top row.</summary>
    public static bool FollowsPeriod(this NotchAgentCard card) =>
        card is NotchAgentCard.Spend or NotchAgentCard.Trend or NotchAgentCard.Models or NotchAgentCard.Projects;

    public static string RawValue(this NotchAgentReadout readout) => readout.ToString().ToLowerInvariant();

    /// <summary>Tokens and cost change only with a new snapshot. Limits still need the clock: an allowance can renew,
    /// or show elapsed time while unknown.</summary>
    public static bool AdvancesWithClock(this NotchAgentReadout readout) => readout is NotchAgentReadout.Elapsed or NotchAgentReadout.Limit;

    public static string RawValue(this NotchAgentLimitDisplay display) => display.ToString().ToLowerInvariant();

    public static string RawValue(this NotchAgentLimitFocus focus) => focus switch
    {
        NotchAgentLimitFocus.MostUsed => "mostUsed", NotchAgentLimitFocus.Session => "session", _ => "weekly",
    };
}

public static class NotchAgentSupport
{
    /// <summary>Seconds.</summary>
    public const double DefaultFinishMinimum = 60;
    public static readonly IReadOnlyList<double> FinishMinimums = [0, 30, 60, 120, 300];
    public const double DefaultLimitThreshold = 80;
    public static readonly IReadOnlyList<double> LimitThresholds = [50, 75, 80, 90, 95];
    /// <summary>Daily budgets in US dollars; 0 is off.</summary>
    public static readonly IReadOnlyList<double> Budgets = [0, 5, 10, 25, 50, 100, 250];
    /// <summary>A turn silent for this long (seconds) is not shown as working.</summary>
    public const double IdleTurn = 10 * 60;

    // MARK: Preferences, from their stored values

    /// <summary>Every card in the saved order (comma-separated raw values); unknown and repeated ones are dropped and
    /// cards added later join at the end.</summary>
    public static IReadOnlyList<NotchAgentCard> OrderedCards(string? stored)
    {
        var parsed = (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(NotchAgentEnums.CardFrom).OfType<NotchAgentCard>();
        var seen = new HashSet<NotchAgentCard>();
        return parsed.Concat(NotchAgentEnums.AllCards).Where(seen.Add).ToList();
    }

    public static IReadOnlySet<NotchAgentCard> HiddenCards(string? stored) =>
        (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(NotchAgentEnums.CardFrom).OfType<NotchAgentCard>().ToHashSet();

    public static IReadOnlyList<NotchAgentCard> Cards(string? order, string? hidden)
    {
        var hide = HiddenCards(hidden);
        return OrderedCards(order).Where(c => !hide.Contains(c)).ToList();
    }

    /// <summary>The agents switched on, in order; a provider without a stored choice is on.</summary>
    public static IReadOnlyList<AgentProvider> Providers(Func<AgentProvider, bool?> stored) =>
        AgentProviders.All.Where(p => stored(p) ?? true).ToList();

    public static NotchAgentLimitDisplay LimitDisplay(string? stored) =>
        stored == "used" ? NotchAgentLimitDisplay.Used : NotchAgentLimitDisplay.Remaining;

    public static NotchAgentLimitFocus LimitFocus(string? stored) => stored switch
    {
        "session" => NotchAgentLimitFocus.Session, "weekly" => NotchAgentLimitFocus.Weekly, _ => NotchAgentLimitFocus.MostUsed,
    };

    public static NotchAgentReadout Readout(string? stored) => stored switch
    {
        "tokens" => NotchAgentReadout.Tokens, "cost" => NotchAgentReadout.Cost, "limit" => NotchAgentReadout.Limit, _ => NotchAgentReadout.Elapsed,
    };

    /// <summary>The shortest turn (seconds) worth a notice when it ends; null while those are off. A missing switch
    /// is on; the value is clamped to 0–3600.</summary>
    public static double? FinishMinimum(bool? alert, double? stored)
    {
        if (!(alert ?? true)) return null;
        var value = stored ?? DefaultFinishMinimum;
        return double.IsFinite(value) ? Math.Min(3600, Math.Max(0, value)) : DefaultFinishMinimum;
    }

    /// <summary>Percent used that earns a warning; null while warnings are off. Clamped to 1–100.</summary>
    public static double? LimitThreshold(bool? alert, double? stored)
    {
        if (!(alert ?? true)) return null;
        var value = stored ?? DefaultLimitThreshold;
        return double.IsFinite(value) ? Math.Min(100, Math.Max(1, value)) : DefaultLimitThreshold;
    }

    /// <summary>A positive daily budget in US dollars, or null when off.</summary>
    public static double? DailyBudget(double stored) => double.IsFinite(stored) && stored > 0 ? stored : null;

    /// <summary>Whether anything shows or alerts on the plan limits: their alerts, the live reading when it shows a
    /// limit, the resting island's allowance or, while the AI page shows, its Limits card. Nothing asks for them
    /// otherwise.</summary>
    public static bool LimitsInUse(bool pageShown, double? limitThreshold, bool showsLiveActivity, NotchAgentReadout readout,
                                   bool idleShowsAgents, IReadOnlySet<NotchAgentCard> hiddenCards) =>
        limitThreshold is not null || (showsLiveActivity && readout == NotchAgentReadout.Limit) || idleShowsAgents
        || (pageShown && !hiddenCards.Contains(NotchAgentCard.Limits));

    // MARK: Limits

    /// <summary>The allowance the closed island shows: the window the person chose, or the one closest to running out
    /// while the account reports no such window. A model's own allowance is never the chosen one.</summary>
    public static AgentLimitWindow? FocusedLimit(AgentLimits? limits, NotchAgentLimitFocus focus, DateTimeOffset now) =>
        ChosenLimit(limits, focus, now) ?? AgentLimitSupport.Binding(limits, now);

    private static AgentLimitWindow? ChosenLimit(AgentLimits? limits, NotchAgentLimitFocus focus, DateTimeOffset now)
    {
        AgentLimitKind kind;
        switch (focus)
        {
            case NotchAgentLimitFocus.Session: kind = AgentLimitKind.Session; break;
            case NotchAgentLimitFocus.Weekly: kind = AgentLimitKind.Weekly; break;
            default: return null;
        }
        return limits?.Windows.FirstOrDefault(w => w.Kind == kind && w.Scope is null) is { } window ? AgentLimitSupport.Current(window, now) : null;
    }

    /// <summary>The resting island's allowance across every account: the most spent of the chosen windows, or of each
    /// account's most used window while no account reports the chosen one.</summary>
    public static (AgentProvider Provider, AgentLimitWindow Window)? RestingLimit(AgentUsageSnapshot snapshot, NotchAgentLimitFocus focus, DateTimeOffset now)
    {
        (AgentProvider Provider, AgentLimitWindow Window)? MostSpent(Func<AgentLimits, AgentLimitWindow?> pick)
        {
            (AgentProvider Provider, AgentLimitWindow Window)? best = null;
            foreach (var (provider, limits) in snapshot.Limits)
            {
                if (pick(limits) is not { } window) continue;
                if (best is not { } current) { best = (provider, window); continue; }
                var increasing = current.Window.UsedPercent != window.UsedPercent
                    ? current.Window.UsedPercent < window.UsedPercent
                    : string.CompareOrdinal(current.Provider.RawValue(), provider.RawValue()) > 0;
                if (increasing) best = (provider, window);
            }
            return best;
        }
        return MostSpent(l => ChosenLimit(l, focus, now)) ?? MostSpent(l => AgentLimitSupport.Binding(l, now));
    }

    /// <summary>A reading old enough to have missed use elsewhere shows quieter: an account card's after missed
    /// checks (ten minutes), and among the others only Claude's after half an hour.</summary>
    public static bool LimitsAreStale(AgentLimits? limits, AgentProvider provider, bool account, DateTimeOffset now)
    {
        if (limits is null) return false;
        var age = now.Since(limits.ObservedAt);
        if (account) return age >= AccountStaleness;
        return provider == AgentProvider.Claude && age >= 30 * 60;
    }

    // MARK: Strip

    /// <summary>A wing is never narrower than the music strip's, nor wide enough to crowd the menus beside the camera.</summary>
    public const double StripWingMinimum = 44, StripWingMaximum = 80;
    /// <summary>Air between the camera and what sits beside it.</summary>
    public const double StripCameraGap = 6;

    public static double StripTextSize(double height) => Math.Min(15, height - 7);

    /// <summary>Both wings take the width the reading needs, rounded up and kept to 44–80.</summary>
    public static double FittedWing(double wing) =>
        Math.Min(StripWingMaximum, Math.Max(StripWingMinimum, double.IsFinite(wing) ? Math.Ceiling(wing) : 0));

    /// <summary>The wing drawn beside the camera: the fitted one, as far as the menus leave room; none (the strip
    /// keeps to the cutout, never below it) when they leave less than the minimum.</summary>
    public static double StripWing(double wing, double? sideRoom)
    {
        var room = sideRoom ?? 0;
        return double.IsFinite(room) && room >= StripWingMinimum ? Math.Min(FittedWing(wing), room) : 0;
    }

    /// <summary>What the strip shows beside the camera while agents work: the chosen reading, or the time elapsed
    /// while that one is unknown.</summary>
    public static string StripReading(AgentUsageSnapshot snapshot, NotchAgentReadout readout, NotchAgentLimitDisplay display,
                                      NotchAgentLimitFocus focus, DateTimeOffset now, CultureInfo? culture = null)
    {
        var live = snapshot.Live;
        string Elapsed() => AgentFormat.Clock(now.Since(live.Count > 0 ? live.Min(s => s.Started) : now));
        switch (readout)
        {
            case NotchAgentReadout.Elapsed:
                return Elapsed();
            case NotchAgentReadout.Tokens:
                // What the agent wrote, the count its own window shows; the context it reads again is in the cost.
                return AgentFormat.Tokens(live.Sum(s => s.Tokens.Output), culture);
            case NotchAgentReadout.Cost:
                return AgentFormat.Cost(live.Sum(s => s.Cost), culture);
            default:
                var provider = AgentProviders.All.Cast<AgentProvider?>().FirstOrDefault(p => live.Any(s => s.Provider == p));
                if (provider is not { } working || FocusedLimit(snapshot.Limits.GetValueOrDefault(working), focus, now) is not { } window) return Elapsed();
                return AgentFormat.Percent(display == NotchAgentLimitDisplay.Used ? window.UsedFraction : window.RemainingFraction, culture);
        }
    }

    /// <summary>Settings illustrate the selected reading even before any agent has reported an allowance. Example data
    /// stays in this copy.</summary>
    public static AgentUsageSnapshot StripSampleSnapshot(AgentUsageSnapshot snapshot, AgentProvider provider, NotchAgentLimitFocus focus, DateTimeOffset now)
    {
        var sample = snapshot.Clone();
        if (sample.Live.Count == 0)
            sample.Live = [new AgentLiveSession("example", provider, now.Plus(-754), now, "", "", new AgentTokens(Input: 1_180_000, Output: 20_000), 4.56)];
        var working = AgentProviders.All.Cast<AgentProvider?>().FirstOrDefault(p => sample.Live.Any(s => s.Provider == p)) ?? provider;
        if (FocusedLimit(sample.Limits.GetValueOrDefault(working), focus, now) is null)
        {
            sample.Limits[working] = new AgentLimits(working,
            [
                new AgentLimitWindow("example-session", AgentLimitKind.Session, 300, null, 32, now.Plus(3600)),
                new AgentLimitWindow("example-week", AgentLimitKind.Weekly, 10_080, null, 57, now.Plus(86_400)),
            ], now, AgentLimitSource.SessionLog);
        }
        return sample;
    }

    /// <summary>A limit's share on the island's card, naming which share it is: a bare 99% left reads as 99% spent.
    /// <paramref name="left"/> and <paramref name="used"/> wrap the percent in the app's words.</summary>
    public static string LimitShare(AgentLimitWindow window, NotchAgentLimitDisplay display, Func<string, string> left, Func<string, string> used,
                                    CultureInfo? culture = null) =>
        display == NotchAgentLimitDisplay.Remaining ? left(AgentFormat.Percent(window.RemainingFraction, culture))
            : used(AgentFormat.Percent(window.UsedFraction, culture));

    /// <summary>Every digit takes the same width, so a reading's shape, not its value, sets the strip's width: "12:34"
    /// and "59:59" match, "1:00:00" is wider.</summary>
    public static string ReadingShape(string reading) => new([.. reading.Select(c => char.IsDigit(c) ? '0' : c)]);

    // MARK: Layout

    public const double Spacing = 10;
    public const double CardHeight = 96;
    public const double ChartHeight = 118;
    /// <summary>An account card's reading, checked every five minutes, has missed two (seconds).</summary>
    public const double AccountStaleness = 10 * 60;
    /// <summary>Below this width every card takes a row of its own.</summary>
    public const double PairWidth = 390;

    /// <summary>The tiles for the chosen cards: a limits card for each agent that reports an allowance. The Resets
    /// card holds Codex's banked resets, which the Windows port does not read, so it shows only on request.</summary>
    public static IReadOnlyList<NotchAgentTile> Tiles(IEnumerable<NotchAgentCard> cards, IReadOnlyList<AgentProvider> providers, bool showsResets = false) =>
        cards.SelectMany(card => card switch
        {
            NotchAgentCard.Limits => providers.Where(p => p.ReportsLimits()).Select(p => new NotchAgentTile(card, p)),
            NotchAgentCard.Resets => showsResets && providers.Contains(AgentProvider.Codex)
                ? [new NotchAgentTile(card, AgentProvider.Codex)] : Enumerable.Empty<NotchAgentTile>(),
            _ => [new NotchAgentTile(card, null)],
        }).ToList();

    /// <summary>Cards pair up in reading order; a chart, or a card left without a partner, takes the whole row.</summary>
    public static IReadOnlyList<IReadOnlyList<NotchAgentTile>> Rows(IEnumerable<NotchAgentTile> tiles, double width)
    {
        var pairs = width >= PairWidth;
        var rows = new List<IReadOnlyList<NotchAgentTile>>();
        NotchAgentTile? waiting = null;
        foreach (var tile in tiles)
        {
            if (tile.Card.FullWidth() || !pairs)
            {
                if (waiting is not null) rows.Add([waiting]);
                waiting = null;
                rows.Add([tile]);
            }
            else if (waiting is not null)
            {
                rows.Add([waiting, tile]);
                waiting = null;
            }
            else
            {
                waiting = tile;
            }
        }
        if (waiting is not null) rows.Add([waiting]);
        return rows;
    }

    public static double Height(IReadOnlyList<NotchAgentTile> row) => row.Any(t => t.Card.FullWidth()) ? ChartHeight : CardHeight;

    public static double ContentHeight(IReadOnlyList<IReadOnlyList<NotchAgentTile>> rows) =>
        rows.Count == 0 ? 0 : rows.Sum(Height) + Spacing * (rows.Count - 1);
}

/// <summary>Numbers in the reader's region; costs in US dollars, the currency the providers list their prices in.</summary>
public static class AgentFormat
{
    /// <summary>"$12.40", "$1,234", "$12K": cents while they matter.</summary>
    public static string Cost(double value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (!double.IsFinite(value)) return "$0";
        var magnitude = Math.Abs(value);
        if (magnitude >= 10_000) return "$" + Scaled(magnitude / 1000, culture) + "K";
        return "$" + value.ToString(magnitude >= 100 ? "N0" : "N2", culture);
    }

    /// <summary>"950", "4.2K", "48K", "1.2M": shortened without rounding up.</summary>
    public static string Tokens(long value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double magnitude = Math.Max(0, value);
        return magnitude switch
        {
            < 1000 => ((long)magnitude).ToString(culture),
            < 1_000_000 => Scaled(magnitude / 1000, culture) + "K",
            < 1_000_000_000 => Scaled(magnitude / 1_000_000, culture) + "M",
            _ => Scaled(magnitude / 1_000_000_000, culture) + "B",
        };
    }

    /// <summary>One decimal below ten, none above, rounded down: "4.2", "48", "120".</summary>
    private static string Scaled(double value, CultureInfo culture)
    {
        var tenths = value < 10;
        var rounded = tenths ? Math.Floor(value * 10) / 10 : Math.Floor(value);
        return rounded.ToString(tenths ? "0.#" : "0", culture);
    }

    /// <summary>A whole percent, clamped to 0–100%.</summary>
    public static string Percent(double fraction, CultureInfo? culture = null)
    {
        var value = double.IsFinite(fraction) ? Math.Min(1, Math.Max(0, fraction)) : 0;
        return value.ToString("P0", culture ?? CultureInfo.CurrentCulture);
    }

    /// <summary>A running stopwatch: "0:42", "12:05", "1:02:05".</summary>
    public static string Clock(double seconds)
    {
        var total = (long)Math.Max(0, double.IsFinite(seconds) ? seconds : 0);
        return total >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }

    /// <summary>"4m 12s", "1h 5m", "2d 3h": the two largest units, in English abbreviations (the app localizes).</summary>
    public static string Duration(double seconds, int units = 2)
    {
        var total = (long)Math.Max(0, double.IsFinite(seconds) ? seconds : 0);
        (long Value, string Unit)[] parts = total >= 86_400 ? [(total / 86_400, "d"), (total / 3600 % 24, "h")]
            : total >= 3600 ? [(total / 3600, "h"), (total / 60 % 60, "m")] : [(total / 60, "m"), (total % 60, "s")];
        var shown = parts.SkipWhile(p => p.Value == 0).Take(Math.Max(1, units)).Where(p => p.Value > 0).ToList();
        return shown.Count == 0 ? "0" + parts[^1].Unit : string.Join(" ", shown.Select(p => p.Value + p.Unit));
    }

    /// <summary>A calendar day kept in UTC, like the price list's, read as that same day everywhere: "Sep 22, 2026".</summary>
    public static string Day(DateTimeOffset date, CultureInfo? culture = null) =>
        date.UtcDateTime.ToString("MMM d, yyyy", culture ?? CultureInfo.CurrentCulture);

    /// <summary>A cost that may be a minimum: "≥ $12.50" when some responses had no price, "—" when none did.</summary>
    public static string MinimumCost(double cost, long unpriced, CultureInfo? culture = null) =>
        unpriced > 0 ? (cost > 0 ? "≥ " + Cost(cost, culture) : "—") : Cost(cost, culture);
}
