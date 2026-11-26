// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/RadialMenu/RadialMenuSupport.swift: RadialMenuColor, RadialMenuProfile,
// RadialMenuProfilePreset, RadialMenuItem, RadialMenuQuickToggle, RadialMenuTool and RadialMenuMediaKey.
// Windows differences: no trackpad tap on a profile; no `nowPlaying` media key (so the Media preset has three slices
// instead of four); colors are named, never brushes; SymbolName is an opaque glyph key (see RadialMenuGlyphs).
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Windowing;

namespace Notchly.Core.Radial;

/// <summary>Accent colors available for radial menu profiles. The raw (stored) value is the lower-case name.</summary>
public enum RadialMenuColor { Accent, Blue, Purple, Pink, Red, Orange, Yellow, Green, Mint, Cyan, Indigo, Graphite }

public static class RadialMenuColors
{
    public static IReadOnlyList<RadialMenuColor> All { get; } = Enum.GetValues<RadialMenuColor>();

    public static string RawValue(this RadialMenuColor color) => color.ToString().ToLowerInvariant();

    public static string Title(this RadialMenuColor color) => color.ToString();

    public static RadialMenuColor? Parse(string? raw) =>
        All.Cast<RadialMenuColor?>().FirstOrDefault(c => c!.Value.RawValue() == raw);
}

/// <summary>What a slice does. The raw value is the lower-camel name ("quickToggle", "windowLayout").</summary>
public enum RadialMenuKind { App, File, Url, Shortcut, Tool, QuickToggle, WindowLayout, Media, Submenu }

public static class RadialMenuKinds
{
    public static string RawValue(this RadialMenuKind kind) =>
        char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    public static RadialMenuKind? Parse(string? raw) =>
        Enum.GetValues<RadialMenuKind>().Cast<RadialMenuKind?>().FirstOrDefault(k => k!.Value.RawValue() == raw);
}

/// <summary>
/// One action on the wheel. <see cref="Payload"/> carries the target: an app or file path (a leading "~" means the user's
/// profile folder, expanded by the caller), a link, a tool, quick-toggle, media or window-layout id, or a shortcut storage
/// value. Submenus keep their actions in <see cref="Children"/>.
/// </summary>
public sealed record RadialMenuItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RadialMenuKind Kind { get; init; } = RadialMenuKind.App;
    public string Name { get; init; } = "";
    /// <summary>An opaque glyph key picked in the editor ("" for the automatic one); see <see cref="RadialMenuGlyphs"/>.</summary>
    public string SymbolName { get; init; } = "";
    public string Payload { get; init; } = "";
    /// <summary>A small image (at most 64 KB) used in place of the glyph, stored base64 in the JSON.</summary>
    public byte[]? CustomIconData { get; init; }
    public IReadOnlyList<RadialMenuItem> Children { get; init; } = [];

    public static RadialMenuItem Create(RadialMenuKind kind, string payload = "", string name = "") =>
        new() { Kind = kind, Payload = payload, Name = name };

    /// <summary>The tool id when this is a valid tool slice.</summary>
    public string? Tool => Kind == RadialMenuKind.Tool && RadialMenuTools.IsKnown(Payload) ? Payload : null;

    /// <summary>The media id when this is a valid media slice.</summary>
    public string? MediaKey => Kind == RadialMenuKind.Media && RadialMenuMedia.IsKnown(Payload) ? Payload : null;

    /// <summary>The quick-toggle id when this is a valid quick-toggle slice.</summary>
    public string? QuickToggle => Kind == RadialMenuKind.QuickToggle && RadialMenuToggles.IsKnown(Payload) ? Payload : null;

    /// <summary>The placement when this is a valid window-layout slice.</summary>
    public WindowLayoutAction? WindowLayoutAction =>
        Kind == RadialMenuKind.WindowLayout ? WindowLayoutSupport.ParseAction(Payload) : null;

    public bool Equals(RadialMenuItem? other) =>
        other is not null && Id == other.Id && Kind == other.Kind && Name == other.Name && SymbolName == other.SymbolName
        && Payload == other.Payload && SequenceEqual(CustomIconData, other.CustomIconData)
        && Children.SequenceEqual(other.Children);

    public override int GetHashCode() => HashCode.Combine(Id, Kind, Name, SymbolName, Payload, Children.Count);

    private static bool SequenceEqual(byte[]? a, byte[]? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));
}

/// <summary>
/// A complete configuration of the radial menu wheel: its items, color theme, keyboard shortcut and mouse button.
/// (Upstream's four-finger trackpad tap is not ported.)
/// </summary>
public sealed record RadialMenuProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public RadialMenuColor Color { get; init; } = RadialMenuColor.Accent;
    /// <summary>A shortcut storage value ("control+alt+win:32"), or "" for none.</summary>
    public string Shortcut { get; init; } = "";
    /// <summary>A <see cref="RadialMouseTrigger"/> raw value: "off", "back", "forward" or "button:N".</summary>
    public string MouseButton { get; init; } = "off";
    public IReadOnlyList<RadialMenuItem> Items { get; init; } = [];
    /// <summary>
    /// The starter set this wheel was created from (a <see cref="RadialMenuProfilePreset"/> raw value), so restoring its
    /// actions brings back that one. Null for a wheel whose origin is not known.
    /// </summary>
    public string? Preset { get; init; }

    public string DisplayName() => Name.Length == 0 ? RadialMenuPresets.Title(RadialMenuProfilePreset.General) : Name;

    /// <summary>
    /// The copy Duplicate adds. The shortcut and the mouse button each open one wheel, so the copy starts without them
    /// and leaves the original's.
    /// </summary>
    public RadialMenuProfile Duplicate(string name) =>
        this with { Id = Guid.NewGuid(), Name = name, Shortcut = "", MouseButton = RadialMouseTrigger.Off.RawValue };

    public bool Equals(RadialMenuProfile? other) =>
        other is not null && Id == other.Id && Name == other.Name && Color == other.Color && Shortcut == other.Shortcut
        && MouseButton == other.MouseButton && Preset == other.Preset && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Color, Shortcut, MouseButton, Preset, Items.Count);
}

/// <summary>Curated starter presets when creating new profiles. The raw value is the lower-camel name.</summary>
public enum RadialMenuProfilePreset { General, Media, Tools, WindowLayout, QuickToggles, Blank }

public static class RadialMenuPresets
{
    public static IReadOnlyList<RadialMenuProfilePreset> All { get; } = Enum.GetValues<RadialMenuProfilePreset>();

    public static string RawValue(this RadialMenuProfilePreset preset) =>
        char.ToLowerInvariant(preset.ToString()[0]) + preset.ToString()[1..];

    public static RadialMenuProfilePreset? Parse(string? raw) =>
        All.Cast<RadialMenuProfilePreset?>().FirstOrDefault(p => p!.Value.RawValue() == raw);

    public static string Title(RadialMenuProfilePreset preset) => preset switch
    {
        RadialMenuProfilePreset.General => "General",
        RadialMenuProfilePreset.Media => "Media",
        RadialMenuProfilePreset.Tools => "Tools",
        RadialMenuProfilePreset.WindowLayout => "Window layout",
        RadialMenuProfilePreset.QuickToggles => "Quick toggles",
        _ => "Blank",
    };

    public static RadialMenuColor DefaultColor(RadialMenuProfilePreset preset) => preset switch
    {
        RadialMenuProfilePreset.General => RadialMenuColor.Accent,
        RadialMenuProfilePreset.Media => RadialMenuColor.Purple,
        RadialMenuProfilePreset.Tools => RadialMenuColor.Cyan,
        RadialMenuProfilePreset.WindowLayout => RadialMenuColor.Orange,
        RadialMenuProfilePreset.QuickToggles => RadialMenuColor.Mint,
        _ => RadialMenuColor.Graphite,
    };

    /// <summary>
    /// The starting actions of a preset. The General set is the shared starter wheel (stable ids); the others are fresh.
    /// Windows: the Media set has three slices because the Now Playing card (upstream's fourth) is not ported.
    /// </summary>
    public static IReadOnlyList<RadialMenuItem> MakeItems(RadialMenuProfilePreset preset)
    {
        static RadialMenuItem Tool(string id) => RadialMenuItem.Create(RadialMenuKind.Tool, id);
        static RadialMenuItem Media(string id) => RadialMenuItem.Create(RadialMenuKind.Media, id);
        static RadialMenuItem Layout(WindowLayoutAction a) =>
            RadialMenuItem.Create(RadialMenuKind.WindowLayout, WindowLayoutSupport.RawName(a));
        static RadialMenuItem Toggle(string id) => RadialMenuItem.Create(RadialMenuKind.QuickToggle, id);

        return preset switch
        {
            RadialMenuProfilePreset.General => RadialMenuSupport.StarterItems,
            RadialMenuProfilePreset.Media => [Media("playPause"), Media("nextTrack"), Media("previousTrack")],
            RadialMenuProfilePreset.Tools =>
            [
                Tool("screenshot"), Tool("colorPicker"), Tool("screenOCR"), Tool("screenRecorder"), Tool("micMute"),
                Tool("scratchpad"),
            ],
            RadialMenuProfilePreset.WindowLayout =>
            [
                Layout(WindowLayoutAction.Maximize), Layout(WindowLayoutAction.RightHalf),
                Layout(WindowLayoutAction.BottomHalf), Layout(WindowLayoutAction.LeftHalf),
                Layout(WindowLayoutAction.TopHalf),
            ],
            RadialMenuProfilePreset.QuickToggles =>
            [
                Toggle("darkMode"), Toggle("desktopIcons"), Toggle("hiddenFiles"), Toggle("lockScreen"), Toggle("emptyTrash"),
            ],
            _ => [],
        };
    }

    public static RadialMenuProfile CreateProfile(RadialMenuProfilePreset preset, string? name = null,
                                                  RadialMenuColor? color = null, string shortcut = "",
                                                  string mouseButton = "off") => new()
    {
        Name = name ?? "",
        Color = color ?? DefaultColor(preset),
        Shortcut = shortcut,
        MouseButton = mouseButton,
        Items = MakeItems(preset),
        Preset = preset.RawValue(),
    };
}

/// <summary>
/// The tools a slice can trigger (upstream's RadialMenuTool). The ids persist inside the items blob; never rename them.
/// </summary>
public static class RadialMenuTools
{
    public static IReadOnlyList<string> Ids { get; } =
    [
        "screenshot", "screenRecorder", "colorPicker", "screenOCR", "micMute", "clipboardHistory", "quickLauncher",
        "cameraPreview", "scratchpad", "shelf", "cleaner", "uninstaller", "appUpdates", "cleaningMode", "keepAwake",
    ];

    public static bool IsKnown(string? id) => id is not null && Ids.Contains(id);

    /// <summary>The hub feature behind a tool id; null for an unknown id.</summary>
    public static AppFeature? ToolFeature(string? id) => id switch
    {
        "screenshot" => AppFeature.Screenshot,
        "screenRecorder" => AppFeature.ScreenRecorder,
        "colorPicker" => AppFeature.ColorPicker,
        "screenOCR" => AppFeature.ScreenOCR,
        "micMute" => AppFeature.MicMute,
        "clipboardHistory" => AppFeature.ClipboardHistory,
        "quickLauncher" => AppFeature.QuickLauncher,
        "cameraPreview" => AppFeature.CameraPreview,
        "scratchpad" => AppFeature.Scratchpad,
        "shelf" => AppFeature.Shelf,
        "cleaner" => AppFeature.Cleaner,
        "uninstaller" => AppFeature.Uninstaller,
        "appUpdates" => AppFeature.AppUpdates,
        "cleaningMode" => AppFeature.CleaningMode,
        "keepAwake" => AppFeature.KeepAwake,
        _ => null,
    };

    /// <summary>
    /// Hub availability and a feature's own master switch are separate. A saved Shelf slice stays dormant while Shelf is
    /// explicitly disabled and returns automatically when the user enables it again.
    /// </summary>
    /// <param name="installed">Whether the hub has the feature available.</param>
    /// <param name="boolFor">Reads a boolean setting by key (used for the Shelf master switch).</param>
    public static bool IsRunnable(string? id, Func<AppFeature, bool> installed, Func<string, bool> boolFor)
    {
        if (ToolFeature(id) is not { } feature || !installed(feature)) return false;
        return id != "shelf" || boolFor(DefaultsKey.ShelfEnabled);
    }

    public static bool IsRunnable(string? id, Func<AppFeature, bool> installed, IRadialSettings settings) =>
        IsRunnable(id, installed, settings.GetBool);
}

/// <summary>Media keys a slice can press (Windows: play/pause, previous, next; the Now Playing card is not ported).</summary>
public static class RadialMenuMedia
{
    public static IReadOnlyList<string> Ids { get; } = ["playPause", "previousTrack", "nextTrack"];

    public static bool IsKnown(string? id) => id is not null && Ids.Contains(id);

    /// <summary>The Windows virtual-key code posted for the media key (VK_MEDIA_PLAY_PAUSE, _PREV_TRACK, _NEXT_TRACK).</summary>
    public static int? VirtualKey(string? id) => id switch
    {
        "playPause" => 0xB3,
        "previousTrack" => 0xB1,
        "nextTrack" => 0xB0,
        _ => null,
    };
}

/// <summary>
/// The quick toggles a slice can trigger. The first nine are upstream's raw values (never rename them); the rest are the
/// toggles the Windows Quick Toggles panel adds. All of them are listed here, in one place.
/// </summary>
public static class RadialMenuToggles
{
    /// <summary>Upstream's nine, in upstream's order.</summary>
    public static IReadOnlyList<string> UpstreamIds { get; } =
    [
        "darkMode", "emptyTrash", "quitAllApps", "ejectDisks", "hiddenFiles", "desktopIcons", "lockScreen", "displayOff",
        "screenSaver",
    ];

    /// <summary>The ids Notchly.App's QuickTogglesService offers beyond upstream's nine.</summary>
    public static IReadOnlyList<string> WindowsIds { get; } = ["micMute", "refreshRate", "wifi", "bluetooth"];

    public static IReadOnlyList<string> Ids { get; } = [.. UpstreamIds, .. WindowsIds];

    public static bool IsKnown(string? id) => id is not null && Ids.Contains(id);

    /// <summary>Quit all apps belongs to the Quit apps feature; the rest to Quick toggles.</summary>
    public static AppFeature Feature(string? id) => id == "quitAllApps" ? AppFeature.QuitApps : AppFeature.QuickToggles;
}
