// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Clipboard and files: the Windows counterpart of upstream's ClipboardSettings.swift, URLCleanerSettings.swift and ShelfSettings.swift.
// Clipboard history and clearing the clipboard by itself, the plain paste, the link cleaner, image paste into folders and the floating Shelf.
// Everything that records, rewrites or pastes is off until you turn it on.
using System.Windows;
using System.Windows.Controls;
using Notchly.App.Pasteboard;
using Notchly.Core.Clipboard;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Wpf.Ui.Controls;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string HistoryNote = "Remembers text, images and files you copy, to search and paste again from the island or a window. It stays on this PC, never leaves it, " +
        "and skips what password managers and other apps ask not to be recorded.";
    private const string AutoClearNote = "Empties the clipboard by itself. Entries already kept in the history stay.";
    private const string PlainNote = "Pastes what is on the clipboard without its formatting, then puts the clipboard back as it was. " +
        "Files and images are pasted as they are. It cannot paste into apps running as administrator unless Notchly does too.";
    private const string CleanerNote = "When you copy a link, tracking parameters such as utm_source and fbclid are taken out before you paste it. " +
        "Only a lone web link is changed, and only when something is removed.";
    private const string ImagePasteNote = "With a copied picture on the clipboard, Ctrl+V in a folder or on the desktop makes a PNG file there, named Pasted_Image with the date and time. " +
        "Anything else pastes as usual.";
    private const string ShelfNote = "A card that floats over your apps to hold files, links and text while you move them between windows. " +
        "Drag to it, shake the pointer while dragging, or use the shortcut. With the Dynamic Island on, these open the island's Files page instead.";

    private FrameworkElement ClipboardFilesPage()
    {
        var settings = app.Settings;
        var page = Page(PageTitle("Clipboard and files"));

        // History
        page.Children.Add(Section("Clipboard history"));
        page.Children.Add(Caption(HistoryNote));
        if (!settings.IsAvailable(AppFeature.ClipboardHistory)) page.Children.Add(Caption("Install Clipboard history in Features to use it."));
        page.Children.Add(Toggle("Keep a history of what you copy", null, DefaultsKey.ClipboardHistoryEnabled, ""));
        page.Children.Add(Card("Entries to keep", "Pinned entries never count.", "", AgentSettings.NumberChoice(settings, DefaultsKey.ClipboardHistoryLimit,
            ClipboardHistorySupport.Limits.Select(l => (double)l).ToList(), ClipboardHistorySupport.DefaultLimit, v => ClipboardHistorySupport.LimitLabel((int)v))));
        page.Children.Add(Card("Keep for", "Pinned entries are never removed.", "", AgentSettings.NumberChoice(settings, DefaultsKey.ClipboardHistoryRetentionDays,
            ClipboardHistorySupport.RetentionDays.Select(d => (double)d).ToList(), ClipboardHistorySupport.DefaultRetentionDays, v => ClipboardHistorySupport.RetentionLabel((int)v))));
        page.Children.Add(Toggle("Include images and files", null, DefaultsKey.ClipboardHistoryIncludeImagesFiles, ""));
        page.Children.Add(Toggle("Skip text that looks like a password or key", "Things like password=… or a long random token are not kept.", DefaultsKey.ClipboardHistoryFilterSensitiveText, ""));
        page.Children.Add(Toggle("Show the app each copy came from", null, DefaultsKey.ClipboardHistoryShowSourceAppIcons, ""));
        page.Children.Add(Choice("Card size", null, DefaultsKey.NotchClipboardCardSize, [("comfortable", "Comfortable"), ("compact", "Compact")], ""));
        page.Children.Add(Toggle("Open the history with a shortcut", null, DefaultsKey.ClipboardHistoryShortcutEnabled, ""));
        page.Children.Add(Card("History shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.ClipboardHistoryShortcut)));
        page.Children.Add(Toggle("Use the Dynamic Island for that shortcut", "When the island has a Clipboard page, the shortcut opens it; otherwise a window opens.", DefaultsKey.NotchClipboardWindow, ""));
        page.Children.Add(Toggle("Clipboard page on the island", null, DefaultsKey.NotchClipboard, ""));
        page.Children.Add(Card("Apps to leave out", "Program names, separated by commas.", "", ProgramListBox(DefaultsKey.ClipboardHistoryIgnoredApps, "keepass.exe")));
        page.Children.Add(Card("Clear the history", "Removes every entry that is not pinned.", "", ActionButton("Clear history", ConfirmClearHistory)));

        // Clearing the clipboard
        page.Children.Add(Section("Clear the clipboard by itself"));
        page.Children.Add(Caption(AutoClearNote));
        page.Children.Add(Toggle("After a delay", "Counted from the last copy, whatever is on the clipboard.", DefaultsKey.ClipboardAutoClearOnDelay, ""));
        page.Children.Add(Card("Delay", null, "", AgentSettings.NumberChoice(settings, DefaultsKey.ClipboardAutoClearDelaySeconds, [5, 10, 20, 30, 60, 120, 300, 600, 1800, 3600],
            ClipboardAutoClearSupport.DefaultDelaySeconds, v => v < 60 ? $"{v:0} seconds" : v == 60 ? "1 minute" : v < 3600 ? $"{v / 60:0} minutes" : "1 hour")));
        page.Children.Add(Toggle("When the PC goes to sleep", null, DefaultsKey.ClipboardAutoClearOnSleep, ""));
        page.Children.Add(Toggle("When the display turns off", null, DefaultsKey.ClipboardAutoClearOnDisplaySleep, ""));
        page.Children.Add(Toggle("When the PC is locked", null, DefaultsKey.ClipboardAutoClearOnScreenLock, ""));

        // Plain paste
        page.Children.Add(Section("Paste as plain text"));
        page.Children.Add(Caption(PlainNote));
        if (!settings.IsAvailable(AppFeature.PastePlain)) page.Children.Add(Caption("Install Paste as plain text in Features to use it."));
        page.Children.Add(Toggle("Turn on paste as plain text", null, DefaultsKey.PastePlainEnabled, ""));
        page.Children.Add(Card("Shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.PastePlainShortcut)));

        // Link cleaner
        page.Children.Add(Section("Clean links"));
        page.Children.Add(Caption(CleanerNote));
        if (!settings.IsAvailable(AppFeature.UrlCleaner)) page.Children.Add(Caption("Install Clean URL in Features to use it."));
        page.Children.Add(Toggle("Remove tracking parameters from copied links", null, DefaultsKey.UrlCleanerEnabled, ""));
        page.Children.Add(Card("More names to remove", "Parameter names, separated by commas, for every site.", "", ProgramListBox(DefaultsKey.UrlCleanerCustomParameters, "ref, source")));
        page.Children.Add(Card("More names for one site", "As site|name, separated by commas.", "", ProgramListBox(DefaultsKey.UrlCleanerSiteParameters, "weibo.com|sudaref")));
        page.Children.Add(Card("Names to keep", "As |name for every site or site|name for one; |utm_* keeps every utm_ name.", "", ProgramListBox(DefaultsKey.UrlCleanerDisabledParameters, "|utm_*, youtube.com|si")));
        page.Children.Add(Caption("Always removed: " + string.Join(", ", UrlCleaning.DisplayGlobalNames()) + ". Some sites have names of their own (YouTube, X, Instagram, Spotify, Reddit, TikTok, Bilibili, Xiaohongshu)."));

        // Image paste
        page.Children.Add(Section("Paste images into folders"));
        page.Children.Add(Caption(ImagePasteNote));
        if (!settings.IsAvailable(AppFeature.FinderCutPaste)) page.Children.Add(Caption("Install Explorer image paste in Features to use it."));
        page.Children.Add(Toggle("Paste a copied image as a PNG file", null, DefaultsKey.FinderPasteImageAsFile, ""));

        // Shelf
        page.Children.Add(Section("Shelf"));
        page.Children.Add(Caption(ShelfNote));
        if (!settings.IsAvailable(AppFeature.Shelf)) page.Children.Add(Caption("Install Shelf in Features to use it."));
        page.Children.Add(Toggle("Turn on the shelf", null, DefaultsKey.ShelfEnabled, ""));
        page.Children.Add(Toggle("Open it by shaking while dragging", null, DefaultsKey.ShelfShakeToOpen, ""));
        page.Children.Add(Toggle("Open it with a shortcut", null, DefaultsKey.ShelfShortcutEnabled, ""));
        page.Children.Add(Card("Shelf shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.ShelfShortcut)));
        page.Children.Add(Toggle("Shelve the selected files with the shortcut", "In File Explorer, files that are not on the shelf yet are added; otherwise it just opens or closes it.", DefaultsKey.ShelfShortcutAddsSelection, ""));
        page.Children.Add(Toggle("Show a drop zone while dragging", "A small pill that takes a drop, or opens the shelf when you rest the pointer on it.", DefaultsKey.ShelfDropZoneEnabled, ""));
        page.Children.Add(Choice("Drop zone place", null, DefaultsKey.ShelfDockPlacement, [("menuBar", "Above the tray"), ("topCenter", "Top centre")], ""));
        page.Children.Add(Toggle("Peek in from the side of the screen", "Dragging toward the left or right edge slides the shelf in a little.", DefaultsKey.ShelfEdgeDragEnabled, ""));
        page.Children.Add(Toggle("Remove items after dragging them out", "Pinned items always stay.", DefaultsKey.ShelfRemoveAfterDrop, ""));
        page.Children.Add(Toggle("Close the shelf after dragging out", null, DefaultsKey.ShelfCloseAfterDrop, ""));
        page.Children.Add(Toggle("Empty the shelf when it is closed", null, DefaultsKey.ShelfClearOnClose, ""));
        page.Children.Add(Card("Apps that never open it by themselves", "Program names, separated by commas. The shortcut still works.", "", ProgramListBox(DefaultsKey.ShelfAutomaticExclusions, "game.exe")));
        return page;
    }

    private void ConfirmClearHistory()
    {
        if (app.Features.Get<ClipboardHistoryService>(AppFeature.ClipboardHistory) is not { } history) return;
        var answer = System.Windows.MessageBox.Show("Remove every history entry that is not pinned?", "Clear the clipboard history",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.None, System.Windows.MessageBoxResult.Cancel);
        if (answer == System.Windows.MessageBoxResult.OK) history.ClearRecent();
    }
}
