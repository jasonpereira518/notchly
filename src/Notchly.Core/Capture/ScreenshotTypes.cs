// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the enums at the top and bottom of upstream
// Sources/Vorssaint/Services/QuickTools/ScreenshotSupport.swift: ScreenCaptureTool,
// ScreenshotSupport.QuickPreviewPosition and ScreenshotDefaultAction.
using Notchly.Core.Features;
using Notchly.Core.Settings;

namespace Notchly.Core.Capture;

/// <summary>
/// The actions that share the screen-selection surface. Availability is read when the chooser
/// opens, so an uninstalled feature never leaves a dead mode behind.
/// </summary>
public enum ScreenCaptureTool { Screenshot, Recording, Text, Color, Scrolling }

public static class ScreenCaptureTools
{
    /// <summary>Case order is the chooser order (upstream CaseIterable).</summary>
    public static readonly IReadOnlyList<ScreenCaptureTool> All =
        [ScreenCaptureTool.Screenshot, ScreenCaptureTool.Recording, ScreenCaptureTool.Text,
         ScreenCaptureTool.Color, ScreenCaptureTool.Scrolling];

    // The recorder's keys are not in DefaultsKey yet (the recorder is a later milestone); they keep
    // the upstream names so a settings backup round-trips.
    private const string RecorderShortcutEnabledKey = "recorderShortcutEnabled";
    private const string RecorderShowCaptureMenuKey = "recorderShowCaptureMenuOnShortcut";

    /// <summary>The key that picks the mode in the chooser.</summary>
    public static string ShortcutKey(this ScreenCaptureTool tool) => tool switch
    {
        ScreenCaptureTool.Screenshot => "1",
        ScreenCaptureTool.Recording => "2",
        ScreenCaptureTool.Text => "3",
        ScreenCaptureTool.Color => "4",
        _ => "5",
    };

    public static ScreenCaptureTool? MatchingShortcut(string? characters)
    {
        if (characters is null) return null;
        foreach (var tool in All) if (tool.ShortcutKey() == characters) return tool;
        return null;
    }

    public static AppFeature Feature(this ScreenCaptureTool tool) => tool switch
    {
        ScreenCaptureTool.Screenshot or ScreenCaptureTool.Scrolling => AppFeature.Screenshot,
        ScreenCaptureTool.Recording => AppFeature.ScreenRecorder,
        ScreenCaptureTool.Text => AppFeature.ScreenOCR,
        _ => AppFeature.ColorPicker,
    };

    /// <summary>The tool's own global-shortcut enable switch (every tool has exactly one).</summary>
    public static string ShortcutEnabledKey(this ScreenCaptureTool tool) => tool switch
    {
        ScreenCaptureTool.Screenshot => DefaultsKey.ScreenshotShortcutEnabled,
        ScreenCaptureTool.Scrolling => DefaultsKey.ScreenshotScrollingShortcutEnabled,
        ScreenCaptureTool.Recording => RecorderShortcutEnabledKey,
        ScreenCaptureTool.Text => DefaultsKey.ScreenOcrShortcutEnabled,
        _ => DefaultsKey.ColorPickerShortcutEnabled,
    };

    public static string ShowCaptureMenuOnShortcutKey(this ScreenCaptureTool tool) => tool switch
    {
        ScreenCaptureTool.Screenshot => DefaultsKey.ScreenshotShowCaptureMenuOnShortcut,
        ScreenCaptureTool.Scrolling => DefaultsKey.ScreenshotScrollingShowCaptureMenuOnShortcut,
        ScreenCaptureTool.Recording => RecorderShowCaptureMenuKey,
        ScreenCaptureTool.Text => DefaultsKey.ScreenOcrShowCaptureMenuOnShortcut,
        _ => DefaultsKey.ColorPickerShowCaptureMenuOnShortcut,
    };

    /// <summary>
    /// A button always opens the capture menu; a shortcut opens it unless the person turned that off.
    /// <paramref name="readBool"/> returns the stored value, or null when nothing is stored (on).
    /// </summary>
    public static bool ShowsCaptureMenu(this ScreenCaptureTool tool, bool fromShortcut, Func<string, bool?> readBool) =>
        !fromShortcut || (readBool(tool.ShowCaptureMenuOnShortcutKey()) ?? true);

    /// <summary>
    /// A running recording blocks the capture menu, because picking recording from it would stop the
    /// take. A shortcut that skips the menu can only reach its own tool, so it may run on top.
    /// </summary>
    public static bool OpensDuringRecording(this ScreenCaptureTool tool, bool fromShortcut, Func<string, bool?> readBool) =>
        tool != ScreenCaptureTool.Recording && !tool.ShowsCaptureMenu(fromShortcut, readBool);

    /// <summary>Only the recorder writes sound, so its audio choices are the only controls that belong under the chooser.</summary>
    public static bool CapturesAudio(this ScreenCaptureTool tool) => tool == ScreenCaptureTool.Recording;

    /// <summary>The tools whose feature is installed, in chooser order.</summary>
    public static IReadOnlyList<ScreenCaptureTool> Available(Func<AppFeature, bool> isAvailable) =>
        All.Where(tool => isAvailable(tool.Feature())).ToList();
}

/// <summary>Where the after-capture confirmation sits. Raw values are the stored ones.</summary>
public enum QuickPreviewPosition { Automatic, TopLeft, TopRight, BottomLeft, BottomRight }

public static class QuickPreviewPositions
{
    public static readonly IReadOnlyList<QuickPreviewPosition> All =
        [QuickPreviewPosition.Automatic, QuickPreviewPosition.TopLeft, QuickPreviewPosition.TopRight,
         QuickPreviewPosition.BottomLeft, QuickPreviewPosition.BottomRight];

    /// <summary>Upstream raw value: "" for automatic, lower camel case for the corners.</summary>
    public static string Raw(this QuickPreviewPosition position) => position switch
    {
        QuickPreviewPosition.TopLeft => "topLeft",
        QuickPreviewPosition.TopRight => "topRight",
        QuickPreviewPosition.BottomLeft => "bottomLeft",
        QuickPreviewPosition.BottomRight => "bottomRight",
        _ => "",
    };

    /// <summary>
    /// Null for an unknown value (upstream <c>init?(rawValue:)</c>). Notchly's registered default for
    /// the preference is the word "automatic", so that reads as automatic too.
    /// </summary>
    public static QuickPreviewPosition? FromRaw(string? raw) => raw switch
    {
        "" or "automatic" => QuickPreviewPosition.Automatic,
        "topLeft" => QuickPreviewPosition.TopLeft,
        "topRight" => QuickPreviewPosition.TopRight,
        "bottomLeft" => QuickPreviewPosition.BottomLeft,
        "bottomRight" => QuickPreviewPosition.BottomRight,
        _ => null,
    };

    public static QuickPreviewPosition Current(string? raw) => FromRaw(raw) ?? QuickPreviewPosition.Automatic;
}

/// <summary>
/// The action to run automatically right after a capture, chosen in Settings. <c>None</c> leaves the
/// quick-preview waiting for the user.
/// </summary>
public enum ScreenshotDefaultAction { None, Save, SaveAndCopy, Copy, Edit }

public static class ScreenshotDefaultActions
{
    public static readonly IReadOnlyList<ScreenshotDefaultAction> All =
        [ScreenshotDefaultAction.None, ScreenshotDefaultAction.Save, ScreenshotDefaultAction.SaveAndCopy,
         ScreenshotDefaultAction.Copy, ScreenshotDefaultAction.Edit];

    public static string Raw(this ScreenshotDefaultAction action) => action switch
    {
        ScreenshotDefaultAction.Save => "save",
        ScreenshotDefaultAction.SaveAndCopy => "saveAndCopy",
        ScreenshotDefaultAction.Copy => "copy",
        ScreenshotDefaultAction.Edit => "edit",
        _ => "",
    };

    /// <summary>Null for an unknown value (upstream <c>init?(rawValue:)</c>).</summary>
    public static ScreenshotDefaultAction? FromRaw(string? raw) => raw switch
    {
        "" => ScreenshotDefaultAction.None,
        "save" => ScreenshotDefaultAction.Save,
        "saveAndCopy" => ScreenshotDefaultAction.SaveAndCopy,
        "copy" => ScreenshotDefaultAction.Copy,
        "edit" => ScreenshotDefaultAction.Edit,
        _ => null,
    };

    /// <summary>The persisted choice; an unknown or missing raw value reads as None.</summary>
    public static ScreenshotDefaultAction Current(string? raw) => FromRaw(raw) ?? ScreenshotDefaultAction.None;

    /// <summary>Copy and Save and copy put every capture on the clipboard.</summary>
    public static bool CopiesToClipboard(this ScreenshotDefaultAction action) =>
        action is ScreenshotDefaultAction.Copy or ScreenshotDefaultAction.SaveAndCopy;

    /// <summary>The same choice without its clipboard half, so Save and copy keeps saving.</summary>
    public static ScreenshotDefaultAction WithoutCopy(this ScreenshotDefaultAction action) => action switch
    {
        ScreenshotDefaultAction.Copy => ScreenshotDefaultAction.None,
        ScreenshotDefaultAction.SaveAndCopy => ScreenshotDefaultAction.Save,
        _ => action,
    };
}
