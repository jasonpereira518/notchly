// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Tools → Quick panel: the Windows counterpart of upstream's launcher settings (QuickToolsSettings.swift): the shortcut, where
// it opens, and a reset of the tiles. The tiles themselves are arranged in the panel (the customize switch) or on the island's Tools page.
using System.Windows.Controls;
using Notchly.App.Launcher;
using Notchly.Core.Features;
using Notchly.Core.Launcher;
using Notchly.Core.Settings;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string LauncherNote = "A floating palette of the tools you reach for most. Press the shortcut, then arrows or 1 to 9 and Return. Press the customize switch in its corner to reorder or hide tiles; click a tile there for its options.";

    private void AddLauncherSection(StackPanel page)
    {
        var settings = app.Settings;
        page.Children.Add(Section("Quick panel"));
        page.Children.Add(Caption(LauncherNote));
        if (!settings.IsAvailable(AppFeature.QuickLauncher)) page.Children.Add(Caption("Install Quick panel in Features to use it."));
        page.Children.Add(Toggle("Open it with a shortcut", null, DefaultsKey.QuickLauncherShortcutEnabled, ""));
        page.Children.Add(Card("Shortcut", "Click, then press the keys. Backspace clears it.", "", new ShortcutRecorder(settings, DefaultsKey.QuickLauncherShortcut)));
        page.Children.Add(Toggle("Open it in the island", "With the island on, the shortcut opens its Tools page instead of a floating panel.", DefaultsKey.NotchQuickPanel, ""));
        page.Children.Add(ActionButton("Open the quick panel", () => app.Features.Get<QuickLauncherService>(AppFeature.QuickLauncher)?.Toggle()));
        page.Children.Add(ActionButton("Reset the tiles", () =>
        {
            settings.Set(DefaultsKey.QuickLauncherItemOrder, "");
            settings.Set(DefaultsKey.QuickLauncherHiddenItems, "");
            Refresh();
        }));
    }
}
