// SPDX-License-Identifier: GPL-3.0-or-later
// The Windows counterpart of upstream Services/AgentUsage/AgentUsageService.swift's main-thread half: runs
// AgentLogStore while the AI Agents page is among the island's pages, hands its snapshots to the UI thread at most
// once a second, asks Claude Code for the account's limits and cursor.com for Cursor's (only while something shows or
// alerts on them, and online), keeps the price list up to date, and turns transitions into the island's notices.
using System.Net.NetworkInformation;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform.Agents;
using Notchly.Core.Agents;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Island.Agents;

public sealed class AgentsService : IDisposable
{
    /// <summary>Limits, Cursor and prices are looked at this often (upstream AgentUsageService.tick).</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PublishDelay = TimeSpan.FromSeconds(1);

    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _tickTimer, _publishTimer;
    private readonly AgentAlerts _alerts = new();
    private readonly ClaudeLimitsReader _claude = new();
    private readonly CursorUsageReader _cursor = new();
    private readonly AgentPriceUpdater _prices = new();

    private AgentLogStore? _store;
    private bool _running, _online = true, _pageVisible, _suspended;
    /// <summary>Bumped on every start and stop: work begun for an earlier run is dropped.</summary>
    private int _session;
    private HashSet<AgentProvider> _providers = [];
    private AgentUsageSnapshot? _pending;
    private AgentLimits? _claudeLimits;
    private ClaudeAccountReading.AccountIdentity? _claudeIdentity;
    private bool _alertsBegun;
    /// <summary>The archive was removed since the section was last on: settings changes need not remove it again.</summary>
    private bool _archiveSettled;

    public AgentsService(Dispatcher dispatcher, SettingsStore settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _tickTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Tick };
        _tickTimer.Tick += (_, _) => OnTick();
        _publishTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = PublishDelay };
        _publishTimer.Tick += (_, _) => FlushPublish();
    }

    /// <summary>The latest snapshot on the UI thread; empty (not loaded) while stopped or reading.</summary>
    public AgentUsageSnapshot Snapshot { get; private set; } = new();
    public bool Running => _running;
    /// <summary>The agents switched on, in the island's order.</summary>
    public IReadOnlyList<AgentProvider> Providers { get; private set; } = [];
    /// <summary>Why Claude Code could not check the account, or null.</summary>
    public AgentClaudeServer.Failure? ClaudeFailure => _claude.LastFailure;
    /// <summary>What the last Cursor check said when it had no numbers, or null.</summary>
    public string? CursorStatus => _cursor.Last?.Message;
    public event Action? Changed;
    /// <summary>A transition worth a notice, already filtered by the person's choices.</summary>
    public event Action<AgentUsageEvent>? Alert;

    private readonly Dictionary<AgentProvider, ImageSource?> _icons = [];

    /// <summary>The agent's own icon from its installed app or CLI, looked up once; null (a letter mark) until found.</summary>
    public ImageSource? Icon(AgentProvider provider)
    {
        if (_icons.TryGetValue(provider, out var icon)) return icon;
        _icons[provider] = null;
        LoadIcon(provider);
        return null;
    }

    private async void LoadIcon(AgentProvider provider)
    {
        ImageSource? icon = null;
        try { icon = await AgentIcons.GetAsync(provider, 48); }
        catch (Exception e) { Log.Debug("No icon for {Agent}: {Error}", provider, e.GetType().Name); }
        if (icon is null) return;
        _icons[provider] = icon;
        Changed?.Invoke();
    }

    // MARK: Settings (upstream NotchAgentSupport preferences)

    private static readonly (AgentProvider Provider, string Key)[] ProviderKeys =
    [
        (AgentProvider.Claude, DefaultsKey.NotchAgentsClaude), (AgentProvider.Codex, DefaultsKey.NotchAgentsCodex),
        (AgentProvider.OpenCode, DefaultsKey.NotchAgentsOpenCode), (AgentProvider.Copilot, DefaultsKey.NotchAgentsCopilot),
        (AgentProvider.Cursor, DefaultsKey.NotchAgentsCursor),
    ];

    public static string KeyFor(AgentProvider provider) => ProviderKeys.First(p => p.Provider == provider).Key;

    public static IReadOnlyList<AgentProvider> ChosenProviders(SettingsStore settings) =>
        NotchAgentSupport.Providers(p => settings.IsRegistered(KeyFor(p)) ? settings.GetBool(KeyFor(p)) : null);

    public NotchAgentReadout Readout => NotchAgentSupport.Readout(_settings.GetString(DefaultsKey.NotchAgentsReadout));
    public NotchAgentLimitDisplay LimitDisplay => NotchAgentSupport.LimitDisplay(_settings.GetString(DefaultsKey.NotchAgentsLimitDisplay));
    public NotchAgentLimitFocus LimitFocus => NotchAgentSupport.LimitFocus(_settings.GetString(DefaultsKey.NotchAgentsLimitFocus));
    public bool ShowsLiveActivity => _settings.GetBool(DefaultsKey.NotchAgentsLiveActivity);
    private double? FinishMinimum => NotchAgentSupport.FinishMinimum(_settings.GetBool(DefaultsKey.NotchAgentsFinishAlert),
        _settings.GetDouble(DefaultsKey.NotchAgentsFinishMinimum, NotchAgentSupport.DefaultFinishMinimum));
    private double? LimitThreshold => NotchAgentSupport.LimitThreshold(_settings.GetBool(DefaultsKey.NotchAgentsLimitAlert),
        _settings.GetDouble(DefaultsKey.NotchAgentsLimitThreshold, NotchAgentSupport.DefaultLimitThreshold));
    private double? DailyBudget => NotchAgentSupport.DailyBudget(_settings.GetDouble(DefaultsKey.NotchAgentsDailyBudget));

    /// <summary>Whether anything shows or alerts on the plan limits, so they are worth asking for.</summary>
    private bool LimitsInUse => NotchAgentSupport.LimitsInUse(_pageVisible, LimitThreshold, ShowsLiveActivity, Readout,
        idleShowsAgents: _settings.GetString(DefaultsKey.NotchIdleContent) == "agents",
        NotchAgentSupport.HiddenCards(_settings.GetString(DefaultsKey.NotchAgentsHiddenCards)));

    // MARK: Lifecycle

    /// <summary>Runs while the island shows the AI page among its pages (feature installed and switched on), as
    /// upstream's syncWithPreferences. Another set of agents starts over; turning it off forgets the archive.</summary>
    public void Sync(bool wanted)
    {
        var providers = ChosenProviders(_settings);
        if (!wanted || providers.Count == 0) { Stop(keepProgress: KeepsProgress); return; }
        Providers = providers;
        var set = providers.ToHashSet();
        if (_running && set.SetEquals(_providers))
        {
            ApplyPreferences();
            return;
        }
        if (_running) Stop(keepProgress: false);
        Start(set);
    }

    /// <summary>Progress survives a stop only while the section is still on (the island closing, say).</summary>
    private bool KeepsProgress => _settings.IsAvailable(AppFeature.NotchAgents) && _settings.GetBool(DefaultsKey.NotchAgentsEnabled);

    private void Start(HashSet<AgentProvider> providers)
    {
        _running = true;
        _archiveSettled = false;
        _session++;
        _providers = providers;
        _alertsBegun = false;
        _alerts.Reset();
        Snapshot = new AgentUsageSnapshot();
        _store = new AgentLogStore();
        _store.Changed += OnStoreChanged;
        _store.Finished += OnStoreFinished;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _online = NetworkInterface.GetIsNetworkAvailable();
        _ = _store.StartAsync(providers);
        _tickTimer.Start();
        ReadClaudeAccount();
        Changed?.Invoke();
        OnTick();
        Log.Information("Agents started ({Count} agents)", providers.Count);
    }

    private void Stop(bool keepProgress)
    {
        if (!_running)
        {
            // Turned off while stopped: what an earlier run saved still goes, once.
            if (!keepProgress && !_archiveSettled) AgentLogStore.RemoveArchive();
            _archiveSettled = !keepProgress;
            return;
        }
        _running = false;
        _session++;
        _tickTimer.Stop();
        _publishTimer.Stop();
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _claude.Cancel();
        if (_store is { } store)
        {
            store.Changed -= OnStoreChanged;
            store.Finished -= OnStoreFinished;
            store.Stop(keepProgress);
            store.Dispose();
        }
        _archiveSettled = !keepProgress;
        _store = null;
        _providers = [];
        Providers = [];
        _pending = null;
        _claudeLimits = null;
        _claudeIdentity = null;
        _alerts.Reset();
        _alertsBegun = false;
        _pageVisible = false;
        _suspended = false;
        Snapshot = new AgentUsageSnapshot();
        Changed?.Invoke();
        Log.Information("Agents stopped ({Kept})", keepProgress ? "progress kept" : "archive removed");
    }

    public void Dispose() => Stop(keepProgress: KeepsProgress);

    /// <summary>The page shows: limits and Cursor are asked for at the page's shorter cadence.</summary>
    public bool PageVisible
    {
        get => _pageVisible;
        set
        {
            if (_pageVisible == value) return;
            _pageVisible = value;
            if (value && _running) CheckLimits();
        }
    }

    /// <summary>Choices that change what is shown or alerted, without another set of agents.</summary>
    private void ApplyPreferences()
    {
        Changed?.Invoke();
        CheckLimits();
    }

    // MARK: Store events (store thread → UI thread)

    private void OnStoreChanged(AgentUsageSnapshot snapshot)
    {
        var session = _session;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_running || session != _session) return;
            _pending = snapshot;
            if (!_publishTimer.IsEnabled)
            {
                FlushPublish();
                _publishTimer.Start();
            }
        });
    }

    /// <summary>The newest snapshot, at most once a second: bursts of writes redraw once.</summary>
    private void FlushPublish()
    {
        if (_pending is not { } next) { _publishTimer.Stop(); return; }
        _pending = null;
        Snapshot = next;
        if (next.Loaded) RunAlerts(next);
        Changed?.Invoke();
    }

    private void OnStoreFinished(AgentUsageEvent.Finished finished)
    {
        var session = _session;
        _dispatcher.BeginInvoke(() =>
        {
            if (_running && session == _session) Report(finished);
        });
    }

    // MARK: Alerts (upstream checkLimits, reportRenewals, checkBudget)

    private async void RunAlerts(AgentUsageSnapshot snapshot)
    {
        if (!_alertsBegun)
        {
            if (_store is not { } store) return;
            _alertsBegun = true;
            var session = _session;
            var records = await store.ReadAsync(s => s.Records.ToList());
            if (!_running || session != _session) return;
            _alerts.Begin(Snapshot.Limits, records, DailyBudget, DateTimeOffset.UtcNow);
            return;
        }
        foreach (var e in _alerts.CheckLimits(snapshot.Limits, LimitThreshold)) Report(e);
        if (_alerts.CheckBudget(snapshot, DailyBudget) is { } budget) Report(budget);
    }

    private void Report(AgentUsageEvent e)
    {
        if (AgentAlerts.Delivers(e, _providers, FinishMinimum, LimitThreshold, DailyBudget)) Alert?.Invoke(e);
    }

    // MARK: The 30-second tick

    private void OnTick()
    {
        if (!_running || _suspended || _store is not { } store) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var e in _alerts.ReportRenewals(now)) Report(e);
        // A session ages out between checks: Claude's reading is placed at now every tick.
        if (_providers.Contains(AgentProvider.Claude) && _claudeLimits is not null)
        {
            if (AgentClaudeServer.Current(_claudeLimits, now) is { } current) store.SetLimits(current);
            else store.ClearLimits(AgentProvider.Claude);
        }
        CheckLimits();
        UpdatePrices();
    }

    /// <summary>Claude Code and cursor.com are asked only while something uses the limits, online and awake.</summary>
    private void CheckLimits()
    {
        if (!_running || _suspended || !_online || !LimitsInUse) return;
        var now = DateTimeOffset.UtcNow;
        if (_providers.Contains(AgentProvider.Claude) && _claude.IsDue(now, _pageVisible)) CheckClaude();
        if (_providers.Contains(AgentProvider.Cursor) && _cursor.IsDue(now, _pageVisible)) CheckCursor();
    }

    private async void CheckClaude()
    {
        var session = _session;
        Changed?.Invoke();
        var result = await _claude.CheckAsync();
        if (!_running || session != _session || _store is not { } store) return;
        if (result.Account.Identity is { } identity && _claudeIdentity is { } previous && identity != previous)
        {
            // Another account signed in: the old one's windows are neither shown nor warned about.
            _claudeLimits = null;
            store.ClearLimits(AgentProvider.Claude);
            _alerts.ForgetProvider(AgentProvider.Claude);
        }
        if (result.Account.Identity is not null) _claudeIdentity = result.Account.Identity;
        if (result.Account.Plan is { } plan) store.SetPlan(AgentProvider.Claude, plan);
        if (result.Limits is { } limits)
        {
            _claudeLimits = limits;
            if (AgentClaudeServer.Current(limits, DateTimeOffset.UtcNow) is { } current) store.SetLimits(current);
        }
        Changed?.Invoke();
    }

    private async void CheckCursor()
    {
        var session = _session;
        var result = await _cursor.CheckAsync();
        if (!_running || session != _session || _store is not { } store) return;
        switch (result)
        {
            case CursorUsageResult.Ok ok:
                store.SetLimits(ok.Limits);
                store.SetPlan(AgentProvider.Cursor, ok.Summary.Plan is { Length: > 0 } name ? new AgentPlan(name, null) : null);
                break;
            case CursorUsageResult.SignedOut or CursorUsageResult.Unrecognized:
                store.ClearLimits(AgentProvider.Cursor);
                store.SetPlan(AgentProvider.Cursor, null);
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>Claude's plan from <c>.claude.json</c>, read off the UI thread when the agents start.</summary>
    private async void ReadClaudeAccount()
    {
        if (!_providers.Contains(AgentProvider.Claude)) return;
        var session = _session;
        var account = await Task.Run(ClaudeLimitsReader.ReadAccount);
        if (!_running || session != _session || _store is not { } store) return;
        _claudeIdentity ??= account.Identity;
        if (account.Plan is { } plan) store.SetPlan(AgentProvider.Claude, plan);
    }

    /// <summary>A newer price list at most once a day, while the person keeps prices up to date.</summary>
    private async void UpdatePrices()
    {
        if (!_settings.GetBool(DefaultsKey.NotchAgentsPriceUpdates) || !_online || !_prices.IsDue(DateTimeOffset.UtcNow)) return;
        var session = _session;
        bool changed;
        try { changed = await _prices.RefreshAsync(); }
        catch (Exception e) { Log.Information("Agent prices not updated: {Error}", e.GetType().Name); return; }
        if (changed && _running && session == _session) _store?.Reprice();
    }

    // MARK: System

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (!_running || e.IsAvailable == _online) return;
        _online = e.IsAvailable;
        if (!_online) return;
        // Back online, the checks start over from the shortest wait.
        _claude.ResetBackoff();
        _cursor.ResetBackoff();
        CheckLimits();
    });

    /// <summary>Asleep, nothing is read; awake again, the logs are read from where they stopped.</summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (!_running || _store is not { } store) return;
        if (e.Mode == PowerModes.Suspend && !_suspended)
        {
            _suspended = true;
            _claude.Cancel();
            store.Pause();
        }
        else if (e.Mode == PowerModes.Resume && _suspended)
        {
            _suspended = false;
            store.Resume();
            OnTick();
        }
    });
}
