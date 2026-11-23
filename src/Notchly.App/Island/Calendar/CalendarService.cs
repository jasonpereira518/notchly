// SPDX-License-Identifier: GPL-3.0-or-later
// The Windows counterpart of upstream Services/Notch/NotchCalendarService.swift: reads the calendars
// added on this PC (CalendarFeedStore), keeps them parsed in memory only, and publishes the events of
// the week ahead or the month shown, plus the closed island's countdown. Feeds are fetched every 15
// minutes, after sleep and when the network returns; the agenda refreshes at its next start, end,
// countdown transition or midnight, as upstream. No event text is stored, only chosen countdown keys.
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.Core.Calendar;
using Notchly.Core.Settings;
using Serilog;
using IcalCalendar = Ical.Net.Calendar;

namespace Notchly.App.Island.Calendar;

public sealed record CalendarFeedStatus(CalendarFeed Feed, DateTime? Updated, string? Problem);

public sealed class CalendarService : IDisposable
{
    private static readonly TimeSpan FetchInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private sealed class FeedState(CalendarFeed feed)
    {
        public CalendarFeed Feed = feed;
        public IcalCalendar? Calendar;
        public DateTime? Updated, Attempted;
        public string? Problem, ETag;
        public DateTimeOffset? LastModified;
    }

    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _fetchTimer, _refreshTimer;
    private readonly Dictionary<string, FeedState> _feeds = new(StringComparer.Ordinal);
    private bool _running, _fetching;
    /// <summary>Bumped when the calendar stops: reads begun before then are dropped.</summary>
    private int _lifecycle;
    /// <summary>Bumped by each recompute: only the latest one publishes.</summary>
    private int _read;
    private DateTime? _visibleMonth;

    public IReadOnlyList<CalendarEvent> Events { get; private set; } = [];
    public CalendarCountdown? Countdown { get; private set; }
    public bool Loading { get; private set; }
    public bool HasFeeds => _feeds.Count > 0;
    public IReadOnlyList<CalendarFeedStatus> Statuses =>
        _feeds.Values.Select(s => new CalendarFeedStatus(s.Feed, s.Updated, s.Problem)).ToList();
    /// <summary>The event the page scrolls to once, after a click on the countdown.</summary>
    public string? Revealing { get; set; }
    public event Action? Changed;

    public static DayOfWeek FirstDay => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    public CalendarService(Dispatcher dispatcher, SettingsStore settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _fetchTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FetchInterval };
        _fetchTimer.Tick += (_, _) => FetchAll(force: true);
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Recompute(); };
    }

    // MARK: Settings (upstream NotchCalendarSupport.showsCountdown, showsTimeLeft, excludedCalendars)

    private bool CountdownForEvery => _settings.GetBool(DefaultsKey.NotchCalendarCountdown);
    private bool TimeLeft => _settings.GetBool(DefaultsKey.NotchCalendarTimeLeft);
    private TimeSpan Lead => CalendarSupport.CountdownLeadTime(_settings.GetInt(DefaultsKey.NotchCalendarCountdownLead, CalendarSupport.DefaultCountdownLeadMinutes));
    private HashSet<string> Excluded => _settings.GetList(DefaultsKey.NotchCalendarExcluded).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, DateTime> ChosenCountdowns(SettingsStore settings)
    {
        try
        {
            var text = settings.GetString(DefaultsKey.NotchCalendarChosenCountdowns);
            return text.Length == 0 ? new Dictionary<string, DateTime>()
                : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(text) ?? new Dictionary<string, DateTime>();
        }
        catch (JsonException) { return new Dictionary<string, DateTime>(); }
    }

    private void StoreChosen(IReadOnlyDictionary<string, DateTime> choices) =>
        _settings.Set(DefaultsKey.NotchCalendarChosenCountdowns, choices.Count == 0 ? "" : JsonSerializer.Serialize(choices));

    public bool IsChosen(CalendarEvent calendarEvent) =>
        CalendarSupport.IsChosen(calendarEvent, ChosenCountdowns(_settings).Keys.ToHashSet(StringComparer.Ordinal));

    /// <summary>Whether a timed event yet to start may count down on its own (its menu has something to offer).</summary>
    public bool OffersCountdown(CalendarEvent calendarEvent, DateTime now) =>
        !CountdownForEvery && !calendarEvent.AllDay && calendarEvent.Start > now && calendarEvent.CountdownKey.Length > 0;

    public void SetCountdown(bool chosen, CalendarEvent calendarEvent)
    {
        if (calendarEvent.CountdownKey.Length == 0) return;
        var choices = new Dictionary<string, DateTime>(ChosenCountdowns(_settings), StringComparer.Ordinal);
        if (chosen) choices[calendarEvent.CountdownKey] = calendarEvent.End;
        else choices.Remove(calendarEvent.CountdownKey);
        StoreChosen(choices);
        Recompute();
    }

    // MARK: Lifecycle

    /// <summary>Runs while the island shows the Calendar page among its pages and the calendar is on.</summary>
    public void Sync(bool wanted)
    {
        if (wanted && !_running) Start();
        else if (!wanted && _running) Stop();
        else if (_running) Recompute();
    }

    private void Start()
    {
        _running = true;
        LoadFeeds();
        CalendarFeedStore.Changed += OnFeedsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _fetchTimer.Start();
        FetchAll(force: true);
        Log.Information("Calendar started ({Count} calendars)", _feeds.Count);
    }

    private void Stop()
    {
        _running = false;
        _lifecycle++;
        _read++;
        CalendarFeedStore.Changed -= OnFeedsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _fetchTimer.Stop();
        _refreshTimer.Stop();
        _feeds.Clear();
        _visibleMonth = null;
        Events = [];
        Countdown = null;
        Loading = false;
        Changed?.Invoke();
        Log.Information("Calendar stopped");
    }

    public void Dispose() { if (_running) Stop(); }

    private void OnFeedsChanged() => _dispatcher.BeginInvoke(() =>
    {
        if (!_running) return;
        LoadFeeds();
        FetchAll(force: false);
    });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _dispatcher.BeginInvoke(() => { if (_running) FetchAll(force: true); });
    }

    private void OnTimeChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => { if (_running) Recompute(); });

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _dispatcher.BeginInvoke(() => { if (_running) FetchAll(force: true); });
    }

    private void LoadFeeds()
    {
        var feeds = CalendarFeedStore.Load();
        foreach (var gone in _feeds.Keys.Except(feeds.Select(f => f.Id)).ToList()) _feeds.Remove(gone);
        foreach (var feed in feeds)
        {
            if (_feeds.TryGetValue(feed.Id, out var state))
            {
                var sourceChanged = state.Feed.Source != feed.Source;
                state.Feed = feed;
                if (sourceChanged) { state.Calendar = null; state.ETag = null; state.LastModified = null; state.Attempted = null; }
            }
            else _feeds[feed.Id] = new FeedState(feed);
        }
    }

    /// <summary>The page opened: data older than five minutes is read again.</summary>
    public void PageOpened()
    {
        if (_running && _feeds.Values.Any(s => s.Attempted is null || DateTime.Now - s.Attempted > StaleAfter)) FetchAll(force: true);
    }

    public void ShowMonth(DateTime? month)
    {
        _visibleMonth = month;
        Recompute();
    }

    // MARK: Reading

    private async void FetchAll(bool force)
    {
        if (!_running || _fetching) return;
        _fetching = true;
        var generation = _lifecycle;
        var targets = _feeds.Values.Where(s => force || s.Calendar is null).ToList();
        Loading = Events.Count == 0 && targets.Count > 0;
        if (Loading) Changed?.Invoke();
        try
        {
            await Task.WhenAll(targets.Select(async state =>
            {
                var feed = state.Feed;
                var read = await CalendarFeedStore.ReadAsync(feed, state.Calendar is null ? null : state.ETag,
                    state.Calendar is null ? null : state.LastModified, CancellationToken.None);
                IcalCalendar? parsed = null;
                if (read.Payload is { } bytes) parsed = await Task.Run(() => IcsReader.Parse(Encoding.UTF8.GetString(bytes)));
                if (generation != _lifecycle || !_feeds.TryGetValue(feed.Id, out var current) || current != state) return;
                state.Attempted = DateTime.Now;
                if (read.Problem is { } problem) state.Problem = problem;
                else if (read.NotModified) { state.Problem = null; state.Updated = DateTime.Now; }
                else if (parsed is null) state.Problem = "That link didn't return a calendar";
                else
                {
                    state.Calendar = parsed;
                    state.Problem = null;
                    state.Updated = DateTime.Now;
                    state.ETag = read.ETag;
                    state.LastModified = read.LastModified;
                }
                if (state.Problem is not null)
                    Log.Information("Calendar from {Where} not read: {Problem}", feed.IsFile ? "a file" : new Uri(feed.Source).Host, state.Problem);
            }));
        }
        finally { _fetching = false; }
        if (generation == _lifecycle) Recompute();
    }

    /// <summary>
    /// Events for the month shown or the week ahead, the countdown from the current week (read separately
    /// when another month is shown, as upstream), chosen countdowns refreshed, and the next refresh timed.
    /// </summary>
    private async void Recompute()
    {
        if (!_running) return;
        var generation = ++_read;
        var now = DateTime.Now;
        var firstDay = FirstDay;
        var visible = CalendarSupport.ReadInterval(_visibleMonth, now, firstDay);
        var current = CalendarSupport.ReadInterval(null, now, firstDay);
        var chosenNow = ChosenCountdowns(_settings);
        var chosen = chosenNow.Keys.ToHashSet(StringComparer.Ordinal);
        var countdownWanted = CountdownForEvery || TimeLeft || chosen.Count > 0;
        var needsCurrent = CalendarSupport.NeedsCurrentRead(visible, current, countdownWanted);
        var excluded = Excluded;
        var sources = _feeds.Values.Where(s => s.Calendar is not null && !excluded.Contains(s.Feed.Id))
            .Select(s => (s.Calendar!, s.Feed)).ToList();
        var (shown, currentEvents) = await Task.Run(() =>
        {
            var read = sources.SelectMany(s => IcsReader.Occurrences(s.Item1, visible, s.Feed)).ToList();
            var currentRead = needsCurrent ? sources.SelectMany(s => IcsReader.Occurrences(s.Item1, current, s.Feed)).ToList() : read;
            return (CalendarSupport.Ordered(read), CalendarSupport.Ordered(currentRead));
        });
        if (generation != _read || !_running) return;
        now = DateTime.Now;
        if (CalendarSupport.RefreshedChoices(chosenNow, currentEvents, now) is { } refreshed)
        {
            StoreChosen(refreshed);
            chosen = refreshed.Keys.ToHashSet(StringComparer.Ordinal);
        }
        Events = shown;
        Countdown = CalendarSupport.Countdown(currentEvents, now, CountdownForEvery, TimeLeft, chosen, Lead);
        Loading = false;
        var next = CalendarSupport.NextRefresh(shown, now);
        if (CalendarSupport.CountdownTransition(currentEvents, now, CountdownForEvery, TimeLeft, chosen, Lead) is { } transition && transition < next)
            next = transition;
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, (next - now).TotalMilliseconds + 250));
        _refreshTimer.Start();
        Changed?.Invoke();
    }

    /// <summary>The event's own link, else the web calendar's day view where its site has one.</summary>
    public string? OpenLink(CalendarEvent calendarEvent) =>
        calendarEvent.Url ?? (_feeds.TryGetValue(calendarEvent.CalendarId, out var state)
            ? CalendarSupport.DayLink(state.Feed.Source, calendarEvent.Start) : null);

    /// <summary>The web calendar of the first calendar that has one, for "Open calendar".</summary>
    public string? CalendarLink(DateTime day) =>
        _feeds.Values.Select(s => CalendarSupport.DayLink(s.Feed.Source, day)).FirstOrDefault(l => l is not null);
}
