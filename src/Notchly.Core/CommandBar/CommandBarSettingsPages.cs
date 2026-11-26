// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/CommandBar/CommandBarSystemSettings.swift and
// CommandBarSystemSettingsSupport.swift (Copyright (C) 2026 Vorssaint). The macOS app scans the
// Settings extension bundles on disk; Windows ships a static table of ms-settings: pages instead.

namespace Notchly.Core.CommandBar;

/// <summary>One page of the Windows Settings app, offered as a row of the bar.</summary>
/// <param name="Id">Short stable id; the bar's row id is <c>settingspage.&lt;id&gt;</c>.</param>
/// <param name="Title">What Settings calls the page (English).</param>
/// <param name="Keywords">The words the page also answers to, space-joined, de-duplicated.</param>
/// <param name="Uri">The <c>ms-settings:</c> address that opens it.</param>
public sealed record CommandBarSettingsPage(string Id, string Title, string Keywords, string Uri)
{
    /// <summary>The bar's row id for this page.</summary>
    public string RowId => CommandBarSettingsPages.RowIdPrefix + Id;
}

/// <summary>One entry of a search index: a title and comma-separated synonyms.</summary>
public sealed record CommandBarKeywordEntry(string Title, string Index);

/// <summary>A named group of entries (a section of a page). Groups are read in name order.</summary>
public sealed record CommandBarKeywordSection(string Name, IReadOnlyList<CommandBarKeywordEntry> Entries);

/// <summary>The rules behind offering the PC's own Settings pages as rows, and the table itself.
/// Pure, so what counts as a page and what a page answers to are pinned by tests.</summary>
public static class CommandBarSettingsPages
{
    /// <summary>The most terms one page keeps. Keeping this many preserves searches near the end
    /// of a long index while still bounding the normalised index built for every keystroke.
    /// (Upstream's limit of 160 counts terms, not characters.)</summary>
    public const int KeywordLimit = 160;

    /// <summary>The longest single term kept, in characters.</summary>
    public const int WordLimit = 40;

    public const string RowIdPrefix = "settingspage.";
    public const string UriPrefix = "ms-settings:";

    /// <summary>
    /// The words a page answers to, merged out of an index: sections in name order, and inside
    /// each entry its title first (the phrase a person is likeliest to type) and then its comma
    /// separated synonyms. Empty terms, terms over <see cref="WordLimit"/> characters and repeats
    /// (ignoring case) are dropped, and at most <paramref name="limit"/> terms are kept. Sorted by
    /// section name so two PCs produce the same words.
    /// </summary>
    public static string Keywords(IEnumerable<CommandBarKeywordSection> sections, int limit = KeywordLimit)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        foreach (var section in sections.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            foreach (var entry in section.Entries)
            {
                var candidates = new List<string> { entry.Title ?? "" };
                candidates.AddRange((entry.Index ?? "").Split(','));
                foreach (var word in candidates)
                {
                    var clean = word.Trim();
                    if (clean.Length == 0 || clean.Length > WordLimit || !seen.Add(clean)) continue;
                    words.Add(clean);
                    if (words.Count >= limit) return string.Join(' ', words);
                }
            }
        }
        return string.Join(' ', words);
    }

    /// <summary>The words for a page written as one comma-separated line, merged by the same
    /// rules as <see cref="Keywords(IEnumerable{CommandBarKeywordSection}, int)"/>.</summary>
    public static string Keywords(string commaSeparated, int limit = KeywordLimit) =>
        Keywords([new CommandBarKeywordSection("main", [new CommandBarKeywordEntry("", commaSeparated)])], limit);

    /// <summary>The row id of a page.</summary>
    public static string RowId(string pageId) => RowIdPrefix + pageId;

    /// <summary>The page id a row id names, or null when it is not a settings-page row.</summary>
    public static string? PageId(string rowId) =>
        rowId.StartsWith(RowIdPrefix, StringComparison.Ordinal) && rowId.Length > RowIdPrefix.Length
            ? rowId[RowIdPrefix.Length..]
            : null;

    /// <summary>The page a row id names, or null.</summary>
    public static CommandBarSettingsPage? Find(string rowId)
    {
        var id = PageId(rowId);
        return id is null ? null : LoadedById.Value.GetValueOrDefault(id);
    }

    /// <summary>Whether an address is one this table could open: the <c>ms-settings:</c> scheme
    /// followed by a page name of lower-case letters, digits and hyphens (an optional <c>?</c>
    /// query is allowed after it).</summary>
    public static bool IsSettingsUri(string uri)
    {
        if (!uri.StartsWith(UriPrefix, StringComparison.Ordinal)) return false;
        var rest = uri[UriPrefix.Length..];
        var query = rest.IndexOf('?');
        var page = query >= 0 ? rest[..query] : rest;
        return page.Length > 0 && page.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
    }

    /// <summary>Every page, sorted by title (then id), one row per id.</summary>
    public static IReadOnlyList<CommandBarSettingsPage> All => Loaded.Value;

    private static readonly Lazy<IReadOnlyList<CommandBarSettingsPage>> Loaded = new(Build);

    private static readonly Lazy<Dictionary<string, CommandBarSettingsPage>> LoadedById =
        new(() => All.ToDictionary(page => page.Id, StringComparer.Ordinal));

    private static IReadOnlyList<CommandBarSettingsPage> Build()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = new List<CommandBarSettingsPage>();
        foreach (var (id, title, uriName, keywords) in Table)
        {
            if (!seen.Add(id)) continue;
            pages.Add(new CommandBarSettingsPage(id, title, Keywords(keywords), UriPrefix + uriName));
        }
        return pages
            .OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(page => page.Id, StringComparer.Ordinal)
            .ToList();
    }

    // (id, title, ms-settings page name, comma-separated English keywords)
    private static readonly (string Id, string Title, string Uri, string Keywords)[] Table =
    [
        // System
        ("display", "Display", "display", "screen, monitor, resolution, brightness, scale, scaling, orientation, rotate, multiple displays, refresh rate, hdr, night light"),
        ("night-light", "Night light", "nightlight", "blue light, warm, display, evening, color temperature, eye strain"),
        ("graphics", "Graphics", "display-advancedgraphics", "gpu, graphics preference, video card, hardware accelerated gpu scheduling, default graphics settings"),
        ("sound", "Sound", "sound", "audio, volume, speakers, microphone, output, input, headphones, playback, recording, mixer, device"),
        ("notifications", "Notifications", "notifications", "alerts, banners, do not disturb, notification center, app notifications, toast"),
        ("focus", "Focus", "quietmomentshome", "do not disturb, quiet hours, focus assist, concentrate, distractions, priority notifications"),
        ("power", "Power & battery", "powersleep", "power, battery, sleep, screen timeout, power mode, performance, energy, hibernate, lid, plugged in"),
        ("battery-saver", "Battery saver", "batterysaver", "low battery, power saving, battery usage, charge, energy saver"),
        ("storage", "Storage", "storagesense", "disk, drive space, storage sense, free up space, cleanup, temporary files, recycle bin, drives"),
        ("nearby-sharing", "Nearby sharing", "crossdevice", "share, send files, devices nearby, bluetooth sharing, cross device, share across devices"),
        ("multitasking", "Multitasking", "multitasking", "snap windows, snap layouts, virtual desktops, alt tab, windows arrangement, desktops"),
        ("activation", "Activation", "activation", "product key, license, genuine windows, activate windows, change product key"),
        ("troubleshoot", "Troubleshoot", "troubleshoot", "fix problems, diagnose, troubleshooter, repair, other troubleshooters"),
        ("recovery", "Recovery", "recovery", "reset this pc, advanced startup, go back, restore, reinstall windows, safe mode, boot options"),
        ("projecting", "Projecting to this PC", "project", "cast, miracast, wireless display, second screen, project, connect to this pc"),
        ("clipboard", "Clipboard", "clipboard", "copy, paste, clipboard history, sync clipboard, clear clipboard"),
        ("remote-desktop", "Remote Desktop", "remotedesktop", "rdp, remote access, connect remotely, remote pc, terminal services"),
        ("about", "About", "about", "device name, rename pc, specs, processor, ram, system type, windows version, edition, build, device specifications"),

        // Bluetooth & devices
        ("bluetooth", "Bluetooth & devices", "bluetooth", "devices, pair, wireless, connect, add device, headphones, bluetooth, phone"),
        ("printers", "Printers & scanners", "printers", "print, printer, scanner, add printer, print queue, default printer, scan"),
        ("mouse", "Mouse", "mousetouchpad", "pointer speed, cursor, scroll, primary button, mouse buttons, scroll wheel, left handed"),
        ("touchpad", "Touchpad", "devices-touchpad", "trackpad, gestures, tap, scroll, pinch zoom, three finger, laptop pointer"),
        ("typing", "Typing", "typing", "keyboard, autocorrect, spelling, text suggestions, predictions, typing insights"),
        ("pen", "Pen & Windows Ink", "pen", "stylus, ink, handwriting, pen shortcuts, surface pen, write"),
        ("autoplay", "AutoPlay", "autoplay", "removable drive, memory card, usb drive, default action, insert media"),
        ("usb", "USB", "usb", "usb devices, connection notifications, usb battery saver, usb charging, ports"),

        // Network & internet
        ("network", "Network & internet", "network-status", "connection, status, internet, online, offline, network adapter, data usage"),
        ("wifi", "Wi-Fi", "network-wifi", "wireless, wifi, hotspot, known networks, connect, ssid, wlan, password"),
        ("ethernet", "Ethernet", "network-ethernet", "wired, lan, cable, ip address, dns, network adapter, ip settings"),
        ("vpn", "VPN", "network-vpn", "virtual private network, add vpn, tunnel, secure connection, remote access"),
        ("airplane-mode", "Airplane mode", "network-airplanemode", "flight mode, wireless off, radios, disable wifi, disable bluetooth"),
        ("proxy", "Proxy", "network-proxy", "proxy server, manual proxy, automatic proxy, setup script, address, port"),
        ("dial-up", "Dial-up", "network-dialup", "modem, dial up, phone line, set up a new connection, broadband"),
        ("mobile-hotspot", "Mobile hotspot", "network-mobilehotspot", "share internet, tethering, hotspot, share connection, wifi hotspot"),
        ("advanced-network", "Advanced network settings", "network-advancedsettings", "network reset, adapters, data usage, hardware properties, more adapter options"),

        // Personalization
        ("personalization", "Personalization", "personalization", "customize, appearance, look, theme, wallpaper, colors"),
        ("background", "Background", "personalization-background", "wallpaper, desktop picture, slideshow, solid color, spotlight, desktop background"),
        ("colors", "Colors", "colors", "accent color, dark mode, light mode, transparency effects, theme color, night mode"),
        ("lock-screen", "Lock screen", "lockscreen", "lock screen image, screen saver, slideshow, status, sign in screen, background"),
        ("themes", "Themes", "themes", "desktop theme, contrast themes, mouse cursor, sounds, desktop icons, high contrast"),
        ("start", "Start", "personalization-start", "start menu, pinned apps, recommended, recent apps, folders, layout, jump lists"),
        ("taskbar", "Taskbar", "taskbar", "taskbar icons, system tray, search box, task view, widgets, alignment, auto hide, notification area, corner overflow"),
        ("fonts", "Fonts", "fonts", "typeface, install fonts, font files, text, font settings, get more fonts"),
        ("device-usage", "Device usage", "deviceusage", "personalised tips, gaming, creativity, school, family, business, entertainment"),

        // Apps
        ("installed-apps", "Installed apps", "appsfeatures", "uninstall, remove program, programs and features, app list, move app, modify, installed programs"),
        ("default-apps", "Default apps", "defaultapps", "default browser, file associations, open with, default programs, file types, choose default, email, pdf"),
        ("startup-apps", "Startup apps", "startupapps", "run at startup, boot, login items, autostart, background apps, launch at sign in"),
        ("optional-features", "Optional features", "optionalfeatures", "windows features, add a feature, language packs, extra features, openssh, rsat"),
        ("apps-for-websites", "Apps for websites", "appsforwebsites", "web apps, open links in app, website handlers, link associations"),

        // Accounts
        ("your-info", "Your info", "yourinfo", "profile picture, account, name, microsoft account, local account, avatar, sign in with"),
        ("sign-in-options", "Sign-in options", "signinoptions", "password, pin, windows hello, fingerprint, face, security key, dynamic lock, login, lock"),
        ("email-accounts", "Email & accounts", "emailandaccounts", "email, calendar, contacts, mail accounts, add account, work or school, microsoft account"),
        ("family", "Family", "family-group", "family group, parental controls, child account, kids, add family member, microsoft family"),
        ("other-users", "Other users", "otherusers", "add user, guest, kiosk, switch user, remove account, local user, another person"),
        ("access-work-school", "Access work or school", "workplace", "work account, school account, azure ad, organization, mdm, connect, enterprise"),
        ("windows-backup", "Windows backup", "backup", "backup, onedrive, sync settings, restore, back up files, folders, remember my apps"),

        // Time & language
        ("date-time", "Date & time", "dateandtime", "clock, time zone, set time automatically, calendar, daylight saving, sync now, additional clocks"),
        ("language", "Language & region", "regionlanguage", "language, display language, add a language, keyboard layout, input language, preferred languages"),
        ("region", "Region", "regionformatting", "country, regional format, date format, number format, currency, first day of week, locale, home location"),
        ("speech", "Speech", "speech", "voice, text to speech, speech language, microphone, speech recognition, voices, narrator voice"),

        // Gaming
        ("game-bar", "Game Bar", "gaming-gamebar", "xbox game bar, overlay, record, shortcuts, controller, gaming overlay, widgets"),
        ("game-mode", "Game Mode", "gaming-gamemode", "performance, gaming, optimise games, background activity, fps, graphics"),
        ("captures", "Captures", "gaming-captures", "screenshot, record game, clips, capture location, game recording, video quality"),

        // Accessibility
        ("accessibility", "Accessibility", "easeofaccess", "ease of access, assistive technology, disability, vision, hearing, interaction"),
        ("text-size", "Text size", "easeofaccess-display", "larger text, font size, display, scale text, bigger text, readability, accessibility display"),
        ("visual-effects", "Visual effects", "easeofaccess-visualeffects", "animations, transparency, scroll bars, notification duration, motion, accessibility display"),
        ("mouse-pointer", "Mouse pointer and touch", "easeofaccess-mousepointer", "cursor size, pointer color, bigger cursor, touch feedback, accessibility mouse pointer"),
        ("text-cursor", "Text cursor", "easeofaccess-cursor", "caret, cursor indicator, thickness, text cursor color, blink"),
        ("narrator", "Narrator", "easeofaccess-narrator", "screen reader, read aloud, blind, voice, narrator keyboard shortcut"),
        ("magnifier", "Magnifier", "easeofaccess-magnifier", "zoom, magnify, lens, full screen, enlarge, zoom level"),
        ("color-filters", "Color filters", "easeofaccess-colorfilter", "color blind, colour blindness, grayscale, inverted, filter, deuteranopia"),
        ("contrast-themes", "Contrast themes", "easeofaccess-highcontrast", "high contrast, aquatic, desert, dusk, night sky, accessibility colors"),
        ("captions", "Captions", "easeofaccess-closedcaptioning", "closed captions, subtitles, live captions, deaf, hard of hearing, caption style"),
        ("accessibility-keyboard", "Accessibility keyboard", "easeofaccess-keyboard", "sticky keys, filter keys, toggle keys, on-screen keyboard, keyboard shortcuts, underline access keys"),
        ("accessibility-audio", "Accessibility audio", "easeofaccess-audio", "mono audio, audio notifications, flash screen, hearing, sound notifications"),
        ("voice-access", "Speech recognition", "easeofaccess-speechrecognition", "voice access, dictation, voice typing, speak, commands, voice control"),
        ("eye-control", "Eye control", "easeofaccess-eyecontrol", "eye tracker, gaze, look, accessibility, eye tracking"),

        // Privacy & security
        ("privacy", "Privacy & security", "privacy", "permissions, tracking, data, security, app permissions"),
        ("privacy-general", "General privacy", "privacy-general", "advertising id, language list, app launch tracking, suggested content, tailored experiences, general permissions"),
        ("location", "Location", "privacy-location", "gps, location services, location history, geolocation, where am i, default location"),
        ("camera", "Camera", "privacy-webcam", "webcam, camera access, camera privacy, video, app access to camera, face"),
        ("microphone", "Microphone", "privacy-microphone", "mic, recording, microphone access, audio input, voice, app access to microphone"),
        ("privacy-notifications", "Notification access", "privacy-notifications", "notification access, apps that read notifications, notification permissions"),
        ("privacy-files", "File system access", "privacy-broadfilesystemaccess", "files, documents, file access, broad file system access, app permissions, folders"),
        ("privacy-diagnostics", "Diagnostics & feedback", "privacy-feedback", "telemetry, diagnostic data, feedback frequency, send data, tailored experiences"),
        ("activity-history", "Activity history", "privacy-activityhistory", "timeline, history, clear activity, activity feed, recent activity"),

        // Windows Update & security
        ("windows-update", "Windows Update", "windowsupdate", "update, updates, check for updates, pause updates, install, patch, restart, download, upgrade"),
        ("update-history", "Update history", "windowsupdate-history", "installed updates, uninstall updates, failed updates, quality updates, recent updates"),
        ("update-options", "Advanced update options", "windowsupdate-options", "active hours, metered connection, receive updates for other products, restart notifications, optional updates"),
        ("optional-updates", "Optional updates", "windowsupdate-optionalupdates", "driver updates, optional drivers, preview updates, extra updates"),
        ("windows-security", "Windows Security", "windowsdefender", "antivirus, defender, virus, malware, firewall, protection, threat, scan, ransomware, smartscreen, security"),
        ("delivery-optimization", "Delivery Optimization", "delivery-optimization", "download from other pcs, peer to peer, update bandwidth, download limits"),
        ("windows-insider", "Windows Insider Program", "windowsinsider", "insider, preview builds, beta, dev channel, canary, release preview"),
        ("find-my-device", "Find my device", "findmydevice", "locate, lost laptop, device location, track my pc, find my pc"),
        ("developers", "For developers", "developers", "developer mode, sudo, terminal, file explorer settings, dev drive, powershell, long paths"),
    ];
}
