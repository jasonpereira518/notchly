// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/URLCleanerService.swift: a copied link loses its tracking parameters before anything
// pastes it. It acts on a copy only (never on a paste), only on a lone web link with nothing else on the clipboard but its text, its
// link flavour and small harmless HTML, and only when something was actually removed. History, which runs after it, records the clean link.
using Notchly.App.Shell;
using Notchly.Core.Clipboard;
using Notchly.Core.Settings;
using Notchly.Core.Tools;

namespace Notchly.App.Pasteboard;

public sealed class UrlCleanerService(SettingsStore settings, ClipboardMonitor monitor) : IFeatureService, IClipboardConsumer
{
    private IDisposable? _registration;

    public int Order => 0;
    public bool WantsImage => false;

    public void Sync()
    {
        var on = settings.GetBool(DefaultsKey.UrlCleanerEnabled);
        if (on && _registration is null) _registration = monitor.Add(this);
        else if (!on && _registration is not null)
        {
            _registration.Dispose();
            _registration = null;
        }
    }

    public UrlCleanRules Rules() => UrlCleanRules.FromStorage(settings.GetString(DefaultsKey.UrlCleanerCustomParameters),
        settings.GetString(DefaultsKey.UrlCleanerSiteParameters), settings.GetString(DefaultsKey.UrlCleanerDisabledParameters));

    public void OnChange(ClipboardChange change)
    {
        var s = change.Snapshot;
        if (s.Text is null || !ClipboardFormats.IsPlainLinkContent(s.Formats, s.CanIncludeInHistory, s.CanUploadToCloud)) return;
        if (UrlCleaning.Clean(s.Text, Rules()) is not { Removed.Count: > 0 } result) return;
        if (!UrlCleaning.HtmlAllowsRewrite(s.Html, s.Text.Trim())) return;
        var write = monitor.Access.WriteText(result.Url, excludeFromMonitoring: true);
        monitor.RememberOwnWrite(write);
        if (!write.Ok) return;
        change.Replace(new ClipboardSnapshot(write.ToSequence, [ClipboardFormats.UnicodeText], result.Url, SourceProgram: s.SourceProgram));
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _registration = null;
    }
}
