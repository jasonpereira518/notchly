// SPDX-License-Identifier: GPL-3.0-or-later
// The Windows counterpart of upstream Services/Notch/NotchNotificationService.swift: keeps only the
// notifications received during this unlocked, opted-in session, in memory, at most 50, and clears them when
// the session locks, the feature goes off or Notchly quits. No notification database or message store is read,
// and no message text is written to disk or the log. Windows-specific: the source is Windows' notification
// listener (package identity plus consent), and Open and Dismiss act through it rather than on the banner.
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform.Notifications;
using Notchly.Core.Notifications;
using Notchly.Core.Settings;
using Serilog;

namespace Notchly.App.Island.Notifications;

public sealed class NotificationsService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settings;
    private readonly NotificationListener _listener;
    private NotificationInbox _inbox = new();
    private bool _wanted, _running;

    public NotificationsService(Dispatcher dispatcher, SettingsStore settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _listener = new NotificationListener(dispatcher);
        _listener.Read += OnRead;
        _listener.LogoLoaded += () => Changed?.Invoke();
        NotificationListener.AccessChanged += OnAccessChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Newest first, at most 50.</summary>
    public IReadOnlyList<SystemNotification> Items => _inbox.Items;
    /// <summary>Whether Windows lets Notchly read notifications, as last checked.</summary>
    public NotificationAccess Access { get; private set; } = NotificationAccess.NoIdentity;
    public bool Running => _running;
    /// <summary>The page and the strip changed: the list, an icon, or the access.</summary>
    public event Action? Changed;
    /// <summary>A notification arrived (each once), for the banner.</summary>
    public event Action<SystemNotification>? Arrived;

    /// <summary>Upstream "Hide the system banner": the original leaves Windows once the island shows it.</summary>
    public bool HidesSystemBanner => _settings.GetBool(DefaultsKey.NotchDismissNativeNotifications);

    /// <summary>Reads while the Notifications page is among the island's pages, and Windows allows it.</summary>
    public void Sync(bool wanted)
    {
        _wanted = wanted;
        var access = wanted ? NotificationListener.Access : NotificationAccess.NoIdentity;
        var changed = access != Access;
        Access = access;
        var run = wanted && access == NotificationAccess.Allowed;
        if (run && !_running) Start();
        else if (!run && _running) Stop();
        else if (changed) Changed?.Invoke();
    }

    private void Start()
    {
        _running = true;
        _inbox = new NotificationInbox();
        try { _listener.Start(); }
        catch (Exception e)
        {
            Log.Warning(e, "The notification listener could not start");
            _listener.Stop();
        }
        Changed?.Invoke();
    }

    private void Stop()
    {
        _running = false;
        _listener.Stop();
        Forget();
    }

    /// <summary>Lets go of every notification and app logo.</summary>
    private void Forget()
    {
        _inbox = new NotificationInbox();
        _listener.ForgetLogos();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        NotificationListener.AccessChanged -= OnAccessChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_running) Stop();
        _listener.Dispose();
    }

    private void OnAccessChanged() => _dispatcher.BeginInvoke(() => Sync(_wanted));

    /// <summary>Locking this PC clears the list, as upstream does when the Mac locks.</summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLock) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_running) return;
            Log.Information("Session locked: notifications cleared");
            Forget();
        });
    }

    private void OnRead(IReadOnlyList<SystemNotification> live)
    {
        if (!_running) return;
        var arrivals = _inbox.Update(live);
        if (arrivals.Count == 0) return;
        Changed?.Invoke();
        // Oldest first (the listener reads them in order), so the newest banner is the one left showing.
        foreach (var item in arrivals)
        {
            // Only the app's name is ever logged, never what the notification says.
            Log.Information("A notification arrived from {App}", item.Content.App.Length > 0 ? item.Content.App : "an unnamed app");
            Arrived?.Invoke(item);
        }
    }

    public SystemNotification? Find(uint id) => _inbox.Find(id);

    public ImageSource? Icon(SystemNotification item) => _listener.Logo(item.Id, item.AppId);

    public bool CanOpen(SystemNotification item) => !string.IsNullOrEmpty(item.AppId);

    /// <summary>Brings the app forward; the notification stays until dismissed, as upstream.</summary>
    public bool Open(SystemNotification item) => item.AppId is { } id && AppLauncher.Open(id);

    /// <summary>Removes it from the island and from Windows' notification centre.</summary>
    public void Dismiss(uint id)
    {
        _inbox.Dismiss(id);
        _listener.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>Dismisses everything the island holds.</summary>
    public void ClearAll()
    {
        foreach (var item in _inbox.Items) _listener.Remove(item.Id);
        _inbox.Clear();
        Changed?.Invoke();
    }

    /// <summary>"Hide the system banner": takes the original out of Windows once the island has shown it.</summary>
    public void HideOriginal(uint id) => _listener.Remove(id);
}
