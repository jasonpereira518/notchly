// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only counterpart of upstream's NotchNotificationReader: instead of reading banners through macOS
// Accessibility, Windows' own UserNotificationListener hands over each toast with its app, texts and id. It
// needs package identity (PackageIdentity) and the person's consent, asked only from Settings. Changes arrive
// through NotificationChanged where Windows offers it to this app (with a look every 10 seconds in case one is
// missed), otherwise by looking every 2 seconds.
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notchly.Core;
using Notchly.Core.Notifications;
using Serilog;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Notchly.App.Platform.Notifications;

public enum NotificationAccess { NoIdentity, Unspecified, Allowed, Denied }

public sealed class NotificationListener : IDisposable
{
    /// <summary>How often Notchly looks without change events, and, with them, in case Windows misses one.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2), BackupInterval = TimeSpan.FromSeconds(10);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private UserNotificationListener? _listener;
    private bool _subscribed, _reading, _readAgain, _running;
    private int _lastCount = -1;
    private readonly Dictionary<string, ImageSource?> _logos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the UI thread with what Windows holds now, after any change.</summary>
    public event Action<IReadOnlyList<SystemNotification>>? Read;
    /// <summary>An app's logo finished loading.</summary>
    public event Action? LogoLoaded;

    public NotificationListener(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = PollInterval };
        _poll.Tick += (_, _) => ReadNow();
    }

    /// <summary>Whether Windows lets Notchly read notifications. Never asks.</summary>
    public static NotificationAccess Access
    {
        get
        {
            if (!PackageIdentity.HasIdentity) return NotificationAccess.NoIdentity;
            try
            {
                return UserNotificationListener.Current.GetAccessStatus() switch
                {
                    UserNotificationListenerAccessStatus.Allowed => NotificationAccess.Allowed,
                    UserNotificationListenerAccessStatus.Denied => NotificationAccess.Denied,
                    _ => NotificationAccess.Unspecified,
                };
            }
            catch (Exception e)
            {
                Log.Debug(e, "The notification listener's access could not be read");
                return NotificationAccess.NoIdentity;
            }
        }
    }

    /// <summary>Shows Windows' "Let Notchly access your notifications?" prompt. Only from the person's own click.</summary>
    public static async Task<NotificationAccess> RequestAccessAsync()
    {
        if (!PackageIdentity.HasIdentity) return NotificationAccess.NoIdentity;
        try
        {
            var status = await UserNotificationListener.Current.RequestAccessAsync();
            Log.Information("Notification access: {Status}", status);
            AccessChanged?.Invoke();
            return Access;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Notification access could not be requested");
            return Access;
        }
    }

    /// <summary>Raised after the prompt is answered.</summary>
    public static event Action? AccessChanged;

    public bool Running => _running;
    /// <summary>Whether Windows reports changes as they happen (otherwise Notchly looks every 2 seconds).</summary>
    public bool Live => _subscribed;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _lastCount = -1;
        _listener = UserNotificationListener.Current;
        try
        {
            _listener.NotificationChanged += OnChanged;
            _subscribed = true;
        }
        catch (Exception e)
        {
            Log.Information("Notification changes aren't reported to Notchly ({Message}); looking every 2 s instead", e.Message);
            _subscribed = false;
        }
        _poll.Interval = _subscribed ? BackupInterval : PollInterval;
        _poll.Start();
        ReadNow();
        Log.Information("Notification listener started ({Mode})", _subscribed ? "events" : "polling");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _poll.Stop();
        if (_subscribed && _listener is not null)
        {
            try { _listener.NotificationChanged -= OnChanged; } catch (Exception) { }
        }
        _subscribed = false;
        _listener = null;
        Log.Information("Notification listener stopped");
    }

    public void Dispose() => Stop();

    private void OnChanged(UserNotificationListener sender, Windows.UI.Notifications.UserNotificationChangedEventArgs args)
    {
        Log.Debug("Notification {Change}", args.ChangeKind);
        _dispatcher.BeginInvoke(ReadNow);
    }

    /// <summary>Reads the toasts Windows holds; a change during a read reads again once it ends.</summary>
    private async void ReadNow()
    {
        if (!_running || _listener is not { } listener) return;
        if (_reading) { _readAgain = true; return; }
        _reading = true;
        try
        {
            do
            {
                _readAgain = false;
                IReadOnlyList<UserNotification> raw;
                try { raw = await listener.GetNotificationsAsync(NotificationKinds.Toast); }
                catch (Exception e)
                {
                    Log.Debug(e, "Notifications could not be read");
                    return;
                }
                if (!_running) return;
                if (raw.Count != _lastCount) Log.Debug("Windows holds {Count} notifications", _lastCount = raw.Count);
                var items = new List<SystemNotification>(raw.Count);
                foreach (var notification in raw)
                    if (Convert(notification) is { } item) items.Add(item);
                // Oldest first: the inbox puts each arrival on top, so the newest ends first.
                items.Sort((a, b) => a.Received.CompareTo(b.Received));
                Read?.Invoke(items);
            } while (_readAgain && _running);
        }
        finally { _reading = false; }
    }

    private static SystemNotification? Convert(UserNotification notification)
    {
        try
        {
            var info = notification.AppInfo;
            var appId = info?.AppUserModelId;
            var app = info?.DisplayInfo?.DisplayName ?? "";
            if (IsOwn(appId, app)) return null;
            var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is null) return null;
            var content = NotificationSupport.FromToast(app, binding.GetTextElements().Select(t => t.Text));
            return content is null ? null : new SystemNotification(notification.Id, content, notification.CreationTime, appId);
        }
        catch (Exception e)
        {
            Log.Debug(e, "A notification could not be read");
            return null;
        }
    }

    /// <summary>Notchly's own notices (its tray balloons) never come back as island notifications.</summary>
    private static bool IsOwn(string? appId, string app)
    {
        if (string.Equals(app, AppIdentity.Name, StringComparison.OrdinalIgnoreCase)) return true;
        if (appId is null) return false;
        return appId.StartsWith(PackageIdentity.PackageName + "_", StringComparison.OrdinalIgnoreCase)
               || appId.Contains(AppIdentity.Id, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Path.GetFileName(appId), "Notchly.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Takes a notification out of Windows (its banner and the notification centre).</summary>
    public void Remove(uint id)
    {
        try { _listener?.RemoveNotification(id); }
        catch (Exception e) { Log.Debug(e, "A notification could not be removed from Windows"); }
    }

    /// <summary>The app's logo, loaded once per app; null (a bell) until it arrives or when it has none.</summary>
    public ImageSource? Logo(uint id, string? appId)
    {
        if (appId is null) return null;
        if (_logos.TryGetValue(appId, out var logo)) return logo;
        _logos[appId] = null;
        LoadLogo(id, appId);
        return null;
    }

    private async void LoadLogo(uint id, string appId)
    {
        try
        {
            if (_listener?.GetNotification(id) is not { AppInfo.DisplayInfo: { } display }) return;
            var reference = display.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await reference.OpenReadAsync();
            using var managed = stream.AsStream();
            using var copy = new MemoryStream();
            await managed.CopyToAsync(copy);
            if (copy.Length == 0) return;
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            _logos[appId] = image;
            LogoLoaded?.Invoke();
        }
        catch (Exception e) { Log.Debug(e, "No logo for a notification's app"); }
    }

    /// <summary>Forgets the logos with the notifications (lock, switch-off).</summary>
    public void ForgetLogos() => _logos.Clear();
}
