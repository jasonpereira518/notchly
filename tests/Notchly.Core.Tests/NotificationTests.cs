// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the content, compact-text, banner and inbox contracts in upstream Tests/NotchNotificationTests.swift,
// plus the Windows toast mapping. The Accessibility reader, native closing and source-label contracts are
// macOS-only and not ported.
using Notchly.Core.Features;
using Notchly.Core.Notifications;
using Notchly.Core.Settings;

namespace Notchly.Core.Tests;

public class NotificationTests
{
    private static readonly NotificationText[] Texts =
        [new("header", "Chat"), new("title", "Alex"), new("body", "Hello")];

    private static int _next = 1;

    private static SystemNotification Item(uint? id = null) =>
        new(id ?? (uint)Interlocked.Increment(ref _next) + 1000, NotificationSupport.Content(Texts)!, DateTimeOffset.Now, "Chat.App");

    [Fact]
    public void ContentKeepsAppTitleAndMessageApart()
    {
        var content = NotificationSupport.Content(Texts)!;
        Assert.Equal(("Chat", "Alex", "Hello"), (content.App, content.Title, content.Body));
        Assert.Null(NotificationSupport.Content([.. Texts, new("title", "Sam")]));
        Assert.Null(NotificationSupport.Content([.. Texts, new("body", "Second")]));
        Assert.Null(NotificationSupport.Content([new("body", "No sender")]));
        Assert.Null(NotificationSupport.Content([new("title", new string('a', 16_385))]));
        Assert.Equal("Hello", NotificationSupport.Content([new("", "Alex"), new("", "Hello")])?.Body);
        Assert.Equal(content, NotificationSupport.Content([.. Texts, new("editableText", "Private draft")]));
    }

    [Fact]
    public void CompactTextKeepsSenderAndMessageInTheirWings()
    {
        var shortBanner = new NotificationContent("Chat", "Alex", "", "Test");
        var longBanner = new NotificationContent("Chat", "Alex", "", string.Concat(Enumerable.Repeat("Message ", 100)));
        Assert.Equal(("Alex", "Test"), (shortBanner.CompactTitle, shortBanner.CompactDetail));
        Assert.Equal(longBanner.Body, longBanner.CompactDetail);
        Assert.Equal("Chat", new NotificationContent("Chat", "", "Group", "Test").CompactTitle);
        var conversation = new NotificationContent("Chat", "Alex", "Trip", "Hello");
        Assert.Equal("Trip · Hello", conversation.CompactDetail);
        Assert.Equal("Chat, Alex, Trip, Hello", conversation.AccessibilityText);
        Assert.Equal("Alex, Hello", new NotificationContent("", "Alex", "", "Hello").AccessibilityText);
    }

    [Fact]
    public void ToastTextsMapInOrder()
    {
        Assert.Equal(new NotificationContent("Teams", "Alex", "", "Hello"), NotificationSupport.FromToast("Teams", ["Alex", "Hello"]));
        Assert.Equal(new NotificationContent("Outlook", "Sam", "Lunch?", "See you at noon"),
            NotificationSupport.FromToast(" Outlook ", ["Sam", "Lunch?", "See you at noon"]));
        Assert.Equal(new NotificationContent("App", "Only a title", "", ""), NotificationSupport.FromToast("App", ["  ", "Only a title"]));
        Assert.Equal("b\nc\nd", NotificationSupport.FromToast("App", ["a", "b", "c", "d"])?.Body);
        Assert.Null(NotificationSupport.FromToast("App", []));
        Assert.Null(NotificationSupport.FromToast("App", [" ", ""]));
        Assert.Null(NotificationSupport.FromToast("App", ["Alex", new string('a', 16_385)]));
        Assert.Equal("", NotificationSupport.FromToast(new string('n', 300), ["Alex"])?.App);
    }

    [Fact]
    public void BannersCloseSoonerThanTimersAtTheirPriority()
    {
        Assert.Equal(3, NotificationBanner.Seconds);
        Assert.Equal(1, NotificationBanner.Priority);
        Assert.Equal(1, NotificationBanner.MessageLines(24));
        Assert.Equal(2, NotificationBanner.MessageLines(32));
    }

    [Fact]
    public void BannerWingsFitTheirTextWithinTheRange()
    {
        var (lead, trail) = NotificationBanner.Wings(10, 4);
        Assert.Equal(NotificationBanner.IconSize + NotificationBanner.Spacing + 10 + NotificationBanner.Inset + NotificationBanner.Air, lead);
        Assert.Equal(NotificationBanner.WingRange.Min, trail);
        var (wideLead, wideTrail) = NotificationBanner.Wings(900, 900);
        Assert.Equal((190.0, 190.0), (wideLead, wideTrail));
        // A wrapped message takes the width its lines need, never more than its side.
        var (_, wrapped) = NotificationBanner.Wings(10, 600, wrappedDetailWidth: 120);
        Assert.Equal(120 + NotificationBanner.Inset + NotificationBanner.Air, wrapped);
        Assert.Equal(190, NotificationBanner.Wings(10, 600, wrappedDetailWidth: 999).Trailing);
        Assert.Equal(NotificationBanner.WingRange.Min, NotificationBanner.Wings(-5, -5).Trailing);
    }

    [Fact]
    public void TheInboxDeliversEachNewNotificationOnce()
    {
        var inbox = new NotificationInbox();
        SystemNotification existing = Item(), first = Item(), second = Item();
        Assert.Empty(inbox.Update([existing]));
        Assert.Empty(inbox.Items);
        Assert.Equal([first.Id], inbox.Update([existing, first, first]).Select(i => i.Id));
        Assert.Single(inbox.Items);
        Assert.Equal([second.Id], inbox.Update([first, second]).Select(i => i.Id));
        Assert.Equal([second.Id, first.Id], inbox.Items.Select(i => i.Id));
        inbox.Dismiss(second.Id);
        Assert.Empty(inbox.Update([first, second]));
        Assert.DoesNotContain(inbox.Items, i => i.Id == second.Id);
        // Gone from Windows (dismissed there or by "Hide the system banner"), it stays on the island.
        inbox.Update([]);
        Assert.Equal([first.Id], inbox.Items.Select(i => i.Id));
        Assert.Equal(first, inbox.Find(first.Id));
        Assert.Null(inbox.Find(second.Id));
    }

    [Fact]
    public void TheInboxStaysBoundedAndClearsCleanly()
    {
        var inbox = new NotificationInbox();
        inbox.Update([]);
        for (var i = 0; i < 300; i++) inbox.Update([Item()]);
        Assert.Equal(NotificationSupport.MaximumItems, inbox.Items.Count);
        // Locking or switching off starts a new inbox: nothing is kept.
        inbox = new NotificationInbox();
        Assert.Empty(inbox.Items);

        SystemNotification kept = Item(), onScreen = Item(), expired = Item();
        inbox.Update([]);
        inbox.Update([kept, onScreen, expired]);
        inbox.Update([kept, onScreen]);
        inbox.Dismiss(kept.Id);
        Assert.Equal([expired.Id, onScreen.Id], inbox.Items.Select(i => i.Id));
        inbox.Clear();
        Assert.Empty(inbox.Items);
        var next = Item();
        Assert.Equal([next.Id], inbox.Update([kept, onScreen, next]).Select(i => i.Id));
        Assert.Equal([next.Id], inbox.Items.Select(i => i.Id));
        var later = Item();
        Assert.Equal([later.Id], inbox.Update([onScreen, next, later]).Select(i => i.Id));
        Assert.Equal([later.Id, next.Id], inbox.Items.Select(i => i.Id));
        inbox.Clear();
        inbox.Clear();
        Assert.Empty(inbox.Items);
        Assert.Empty(inbox.Update([]));
    }

    [Fact]
    public void NotificationsStartOnAndKeepTheSystemBannerByDefault()
    {
        var defaults = Defaults.Registered.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(true, defaults[DefaultsKey.NotchNotificationsEnabled]);
        Assert.Equal(false, defaults[DefaultsKey.NotchDismissNativeNotifications]);
        var info = FeatureCatalog.Info(AppFeature.NotchNotifications);
        Assert.Equal(WindowsStatus.Ported, info.Status);
        Assert.True(info.InstalledByDefault);
    }
}
