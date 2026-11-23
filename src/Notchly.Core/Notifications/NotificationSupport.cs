// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/Notch/NotchNotificationSupport.swift: a notification's content and its compact
// text, the rules that turn texts into content, the session inbox, and the banner's and preview's measures.
// Windows-specific: texts come from a toast's ToastGeneric binding (UserNotificationListener) rather than
// from Accessibility labels, so FromToast maps them in order; ids are the listener's numbers.
namespace Notchly.Core.Notifications;

/// <summary>What a notification says: its app, title, optional subtitle and body.</summary>
public sealed record NotificationContent(string App, string Title, string Subtitle, string Body)
{
    /// <summary>The title, or the app for a notification without one.</summary>
    public string CompactTitle => Title.Length == 0 ? App : Title;

    /// <summary>The subtitle and body on one line, joined by a middle dot.</summary>
    public string CompactDetail => string.Join(" · ", new[] { Subtitle, Body }.Where(s => s.Length > 0));

    /// <summary>App, title, subtitle and body in reading order, for a screen reader.</summary>
    public string AccessibilityText => string.Join(", ", new[] { App, Title, Subtitle, Body }.Where(s => s.Length > 0));
}

/// <summary>One notification the island received this session.</summary>
/// <param name="Id">The listener's id for it, unique while Windows keeps it.</param>
/// <param name="AppId">The source app's AppUserModelID, used to open it; null when unknown.</param>
public sealed record SystemNotification(uint Id, NotificationContent Content, DateTimeOffset Received, string? AppId = null);

/// <summary>A labelled text, as upstream reads them (identifier "title", "body", ...; empty when unnamed).</summary>
public sealed record NotificationText(string Identifier, string Value);

public static class NotificationSupport
{
    /// <summary>The most notifications the island keeps (upstream maximumItems).</summary>
    public const int MaximumItems = 50;

    /// <summary>A notification's text has a bounded memory budget (upstream's 16,384 UTF-8 bytes per text).</summary>
    public const int MaximumTextBytes = 16_384;

    private static int Bytes(string value) => System.Text.Encoding.UTF8.GetByteCount(value);

    /// <summary>
    /// Upstream content(from:): two or three unnamed texts are title, optional subtitle and body; otherwise one
    /// title with at most one header (the app), subtitle and body. Stacked senders or messages, a missing title
    /// and oversized text give nothing. Texts with other identifiers (an editable draft) are ignored.
    /// </summary>
    public static NotificationContent? Content(IReadOnlyList<NotificationText> texts)
    {
        List<string> Values(string identifier) => texts.Where(t => t.Identifier == identifier)
            .Select(t => t.Value.Trim()).Where(v => v.Length > 0).ToList();
        var headers = Values("header");
        var titles = Values("title");
        var bodies = Values("body");
        var subtitles = Values("subtitle");
        if (texts.All(t => t.Identifier.Length == 0) && texts.Count is >= 2 and <= 3
            && texts.All(t => t.Value.Length > 0 && Bytes(t.Value) <= MaximumTextBytes))
            return new NotificationContent("", texts[0].Value, texts.Count == 3 ? texts[1].Value : "", texts[^1].Value);
        // Multiple messages in a stack must never be flattened into one sender.
        if (headers.Count > 1 || titles.Count != 1 || bodies.Count > 1 || subtitles.Count > 1
            || texts.Any(t => Bytes(t.Value) > MaximumTextBytes)) return null;
        return new NotificationContent(headers.FirstOrDefault() ?? "", titles[0], subtitles.FirstOrDefault() ?? "", bodies.FirstOrDefault() ?? "");
    }

    /// <summary>
    /// Windows: a toast's texts in order, as the ToastGeneric binding gives them. The first is the title; with
    /// three, the second is the subtitle (a mail's subject, a chat's group); the rest is the body. Blank texts
    /// are dropped; a toast without any, or with oversized text, gives nothing.
    /// </summary>
    public static NotificationContent? FromToast(string app, IEnumerable<string> texts)
    {
        var values = texts.Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).ToList();
        if (values.Count == 0 || values.Any(v => Bytes(v) > MaximumTextBytes)) return null;
        var name = (app ?? "").Trim();
        if (Bytes(name) > 256) name = "";
        return values.Count switch
        {
            1 => new NotificationContent(name, values[0], "", ""),
            2 => new NotificationContent(name, values[0], "", values[1]),
            3 => new NotificationContent(name, values[0], values[1], values[2]),
            _ => new NotificationContent(name, values[0], "", string.Join("\n", values.Skip(1))),
        };
    }
}

/// <summary>
/// The notifications received during this unlocked, opted-in session, newest first and at most 50
/// (upstream NotchNotificationInbox without its macOS-only Notification Center checks). The first look
/// only learns what is already there, so turning the island on never replays old notifications.
/// </summary>
public sealed class NotificationInbox
{
    private readonly List<SystemNotification> _items = [];
    private HashSet<uint> _seen = [];
    private bool _primed;

    public IReadOnlyList<SystemNotification> Items => _items;

    /// <summary>Takes what Windows holds now and returns the arrivals, each delivered once.</summary>
    public IReadOnlyList<SystemNotification> Update(IEnumerable<SystemNotification> live)
    {
        var current = live.ToList();
        var arrivals = new List<SystemNotification>();
        foreach (var item in current)
        {
            if (!_seen.Add(item.Id) || !_primed) continue;
            _items.Insert(0, item);
            if (_items.Count > NotificationSupport.MaximumItems) _items.RemoveAt(_items.Count - 1);
            arrivals.Add(item);
        }
        _primed = true;
        if (_seen.Count > 200) _seen = _items.Select(i => i.Id).Concat(current.Select(i => i.Id)).ToHashSet();
        return arrivals;
    }

    public SystemNotification? Find(uint id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>Removes one; it stays seen, so it never comes back while Windows still holds it.</summary>
    public void Dismiss(uint id) => _items.RemoveAll(i => i.Id == id);

    /// <summary>Empties the island; notifications still in Windows stay seen, later ones arrive as usual.</summary>
    public void Clear() => _items.Clear();
}

/// <summary>
/// The banner beside the camera (upstream NotchNotificationBannerLayout): the app's icon and title on one
/// side, the message on the other, each as wide as it needs within 44–190 points.
/// </summary>
public static class NotificationBanner
{
    public const double IconSize = 22;
    public const double Spacing = 8;
    public static readonly (double Min, double Max) WingRange = (44, 190);
    /// <summary>The inset from the island's curved end, and a little air so fitted text never truncates.</summary>
    public const double Inset = 16;
    public const double Air = 6;
    public const double TitleFontSize = 11;
    public const double MessageFontSize = 11;
    /// <summary>How long a banner shows (upstream NotchEvent.systemNotification.duration).</summary>
    public const double Seconds = 3;
    /// <summary>Its priority among notices: below levels (3) and timers (2), above downloads and tracks (0).</summary>
    public const int Priority = 1;
    /// <summary>Only this much of a message is measured: the widest wing is reached long before.</summary>
    public const int MeasuredCharacters = 240;

    /// <summary>A strip tall enough for two lines wraps a long message onto a second.</summary>
    public static int MessageLines(double stripHeight) => stripHeight >= 30 ? 2 : 1;

    /// <summary>The widest a message may run on its side.</summary>
    public static double MessageRoom => WingRange.Max - Inset - Air;

    /// <summary>
    /// Both wings from the measured title and message. A message that wraps passes the width its lines take
    /// once wrapped at <see cref="MessageRoom"/>, not the whole side.
    /// </summary>
    public static (double Leading, double Trailing) Wings(double titleWidth, double detailWidth, double? wrappedDetailWidth = null)
    {
        var title = IconSize + Spacing + Math.Ceiling(Math.Max(0, titleWidth));
        var detail = Math.Ceiling(Math.Max(0, detailWidth));
        if (wrappedDetailWidth is { } wrapped && detail > MessageRoom) detail = Math.Min(MessageRoom, Math.Ceiling(wrapped));
        static double Fitted(double side) => Math.Min(WingRange.Max, Math.Max(WingRange.Min, side + Inset + Air));
        return (Fitted(title), Fitted(detail));
    }
}

/// <summary>A notification opened in place on the page (upstream NotchNotificationPreviewLayout's limits).</summary>
public static class NotificationPreview
{
    public const double IconSize = 26;
    public const int TitleLines = 2;
    public const int SubtitleLines = 1;
    public const int BodyLines = 6;
    /// <summary>A row closed on the page: the title on one line and two lines of message.</summary>
    public const int CollapsedBodyLines = 2;
}
