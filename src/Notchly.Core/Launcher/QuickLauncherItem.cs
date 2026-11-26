// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/QuickLauncherService.swift (enum QuickLauncherItem and the
// tile action table inside QuickLauncherService.run).
using Notchly.Core.Features;

namespace Notchly.Core.Launcher;

/// <summary>
/// Everything the quick launcher can hold. Declaration order is the default grid order (the cleaner comes second,
/// right after Keep awake); the storage ids (see <see cref="QuickLauncherItems.Id"/>) are upstream's raw values and
/// keep saved orders and hidden sets valid, so cases can be added but never renamed. Tiles whose feature does not
/// exist on Windows yet stay here; the host reports them unavailable.
/// </summary>
public enum QuickLauncherItem
{
    KeepAwake, Cleaner, Toggles, MicMute, ScreenOcr, ColorPicker, Clipboard, WindowLayout,
    Cleaning, Homebrew, Media, UrlCleaner, Uninstaller, Screenshot, ScreenRecorder,
    CameraPreview, Scratchpad,
}

/// <summary>What a tile does outside the launcher. Each has upstream's contract name (see <see cref="QuickLauncherActions.ContractName"/>).</summary>
public enum QuickLauncherAction
{
    KeepAwakeToggle, MicMuteToggle, ScreenOcrCapture, ScreenshotCapture, ScreenRecorderToggle, ColorPickerPick,
    /// <summary>Shows the camera in the island if that is enabled; <see cref="IQuickLauncherHost.Perform"/> returns whether it did.</summary>
    CameraShowInIsland,
    CameraShow, ScratchpadShow, ClipboardShowHistory, CleaningActivate,
}

/// <summary>How a tile activates: nothing outside (it opens a utility inside the launcher), or an action.</summary>
/// <param name="Action">The action, or null for a hosted utility.</param>
/// <param name="HidesFirst">The launcher hides before the action runs.</param>
/// <param name="DelaySeconds">Wait after hiding before the action runs (0 when it runs immediately).</param>
public sealed record QuickLauncherActivation(QuickLauncherAction? Action, bool HidesFirst, double DelaySeconds)
{
    public bool OpensUtility => Action is null;
}

public static class QuickLauncherItems
{
    /// <summary>Every tile in the default grid order.</summary>
    public static IReadOnlyList<QuickLauncherItem> All { get; } = Enum.GetValues<QuickLauncherItem>();

    /// <summary>The default order as storage ids.</summary>
    public static IReadOnlyList<string> DefaultOrder { get; } = [.. All.Select(Id)];

    /// <summary>The storage id (upstream raw value) of the user's order and hidden set.</summary>
    public static string Id(this QuickLauncherItem item) => item switch
    {
        QuickLauncherItem.KeepAwake => "keepAwake",
        QuickLauncherItem.Cleaner => "cleaner",
        QuickLauncherItem.Toggles => "toggles",
        QuickLauncherItem.MicMute => "micMute",
        QuickLauncherItem.ScreenOcr => "screenOCR",
        QuickLauncherItem.ColorPicker => "colorPicker",
        QuickLauncherItem.Clipboard => "clipboard",
        QuickLauncherItem.WindowLayout => "windowLayout",
        QuickLauncherItem.Cleaning => "cleaning",
        QuickLauncherItem.Homebrew => "homebrew",
        QuickLauncherItem.Media => "media",
        QuickLauncherItem.UrlCleaner => "urlCleaner",
        QuickLauncherItem.Uninstaller => "uninstaller",
        QuickLauncherItem.Screenshot => "screenshot",
        QuickLauncherItem.ScreenRecorder => "screenRecorder",
        QuickLauncherItem.CameraPreview => "cameraPreview",
        QuickLauncherItem.Scratchpad => "scratchpad",
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    public static QuickLauncherItem? FromId(string? id) => All.Cast<QuickLauncherItem?>().FirstOrDefault(i => i!.Value.Id() == id);

    /// <summary>The hub feature behind the tile; off in the hub removes it from the grid, the hidden list and edit mode.</summary>
    public static AppFeature Feature(this QuickLauncherItem item) => item switch
    {
        QuickLauncherItem.KeepAwake => AppFeature.KeepAwake,
        QuickLauncherItem.Cleaner => AppFeature.Cleaner,
        QuickLauncherItem.Toggles => AppFeature.QuickToggles,
        QuickLauncherItem.MicMute => AppFeature.MicMute,
        QuickLauncherItem.ScreenOcr => AppFeature.ScreenOCR,
        QuickLauncherItem.ColorPicker => AppFeature.ColorPicker,
        QuickLauncherItem.Clipboard => AppFeature.ClipboardHistory,
        QuickLauncherItem.WindowLayout => AppFeature.WindowLayout,
        QuickLauncherItem.Cleaning => AppFeature.CleaningMode,
        QuickLauncherItem.Homebrew => AppFeature.Homebrew,
        QuickLauncherItem.Media => AppFeature.MediaTools,
        QuickLauncherItem.UrlCleaner => AppFeature.UrlCleaner,
        QuickLauncherItem.Uninstaller => AppFeature.Uninstaller,
        QuickLauncherItem.Screenshot => AppFeature.Screenshot,
        QuickLauncherItem.ScreenRecorder => AppFeature.ScreenRecorder,
        QuickLauncherItem.CameraPreview => AppFeature.CameraPreview,
        QuickLauncherItem.Scratchpad => AppFeature.Scratchpad,
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    /// <summary>
    /// The per-tile table of upstream's run(): a toggle runs at once; a capture or window tile hides the launcher
    /// and acts after a short delay so the launcher is gone from the screen; the rest open a utility inside it.
    /// </summary>
    public static QuickLauncherActivation Activation(this QuickLauncherItem item) => item switch
    {
        QuickLauncherItem.KeepAwake => new(QuickLauncherAction.KeepAwakeToggle, false, 0),
        QuickLauncherItem.MicMute => new(QuickLauncherAction.MicMuteToggle, false, 0),
        QuickLauncherItem.ScreenOcr => new(QuickLauncherAction.ScreenOcrCapture, true, 0.15),
        QuickLauncherItem.Screenshot => new(QuickLauncherAction.ScreenshotCapture, true, 0.15),
        QuickLauncherItem.ScreenRecorder => new(QuickLauncherAction.ScreenRecorderToggle, true, 0.15),
        QuickLauncherItem.ColorPicker => new(QuickLauncherAction.ColorPickerPick, true, 0.15),
        QuickLauncherItem.CameraPreview => new(QuickLauncherAction.CameraShow, true, 0.15),
        QuickLauncherItem.Scratchpad => new(QuickLauncherAction.ScratchpadShow, true, 0.15),
        QuickLauncherItem.Clipboard => new(QuickLauncherAction.ClipboardShowHistory, true, 0.1),
        QuickLauncherItem.Cleaning => new(QuickLauncherAction.CleaningActivate, true, 0.1),
        _ => new(null, false, 0),
    };
}

public static class QuickLauncherActions
{
    /// <summary>Upstream's contract string for the action (what its tests record), e.g. "keepAwake.toggle", "camera.notch".</summary>
    public static string ContractName(this QuickLauncherAction action) => action switch
    {
        QuickLauncherAction.KeepAwakeToggle => "keepAwake.toggle",
        QuickLauncherAction.MicMuteToggle => "micMute.toggle",
        QuickLauncherAction.ScreenOcrCapture => "screenOCR.capture",
        QuickLauncherAction.ScreenshotCapture => "screenshot.capture",
        QuickLauncherAction.ScreenRecorderToggle => "recorder.toggle",
        QuickLauncherAction.ColorPickerPick => "colorPicker.pick",
        QuickLauncherAction.CameraShowInIsland => "camera.notch",
        QuickLauncherAction.CameraShow => "camera.show",
        QuickLauncherAction.ScratchpadShow => "scratchpad.show",
        QuickLauncherAction.ClipboardShowHistory => "clipboard.showHistoryWindow",
        QuickLauncherAction.CleaningActivate => "cleaning.activate",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
