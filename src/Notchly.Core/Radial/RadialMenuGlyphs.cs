// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/RadialMenu/RadialMenuSupport.swift: RadialMenuSupport.symbolNames (the editor's
// icon catalog) and the items' defaultSymbolName / effectiveSymbolName. SF Symbols become Segoe Fluent Icons code points
// (single private-use characters, built into Windows 11). RadialMenuItem.SymbolName stays an opaque key: the name of a
// choice below. A name this table does not know (one imported from the macOS app) falls back to the automatic glyph.
using Notchly.Core.Features;
using Notchly.Core.Windowing;

namespace Notchly.Core.Radial;

/// <summary>An icon the editor offers: the stored key and the glyph (one Segoe Fluent Icons character) it draws.</summary>
public readonly record struct RadialGlyphChoice(string Name, string Glyph);

public static class RadialMenuGlyphs
{
    private static string G(int codePoint) => ((char)codePoint).ToString();

    /// <summary>The editor's icon grid: about ninety curated glyphs, each name unique.</summary>
    public static IReadOnlyList<RadialGlyphChoice> Choices { get; } = Build();

    private static readonly Dictionary<string, string> ByName = Choices.ToDictionary(c => c.Name, c => c.Glyph);

    private static IReadOnlyList<RadialGlyphChoice> Build()
    {
        (string, int)[] table =
        [
            ("star", 0xE734), ("starFilled", 0xE735), ("heart", 0xEB51), ("bolt", 0xE945), ("sparkle", 0xE8F1),
            ("folder", 0xE838), ("folderOpen", 0xE8B7), ("document", 0xE8A5), ("page", 0xE7C3), ("terminal", 0xE756),
            ("globe", 0xE774), ("mail", 0xE715), ("chat", 0xE8BD), ("music", 0xE8D6), ("headphones", 0xE7F6),
            ("camera", 0xE722), ("video", 0xE714), ("photo", 0xEB9F), ("game", 0xE7FC), ("calendar", 0xE787),
            ("clock", 0xE916), ("home", 0xE80F), ("cart", 0xE7BF), ("shop", 0xE719), ("color", 0xE790),
            ("keyboard", 0xE765), ("search", 0xE721), ("airplane", 0xE709), ("check", 0xE73E), ("cancel", 0xE711),
            ("add", 0xE710), ("remove", 0xE738), ("warning", 0xE7BA), ("help", 0xE897), ("info", 0xE946),
            ("lock", 0xE72E), ("unlock", 0xE785), ("person", 0xE77B), ("people", 0xE716), ("bell", 0xEA8F),
            ("flag", 0xE7C1), ("pin", 0xE718), ("attach", 0xE723), ("link", 0xE71B), ("cut", 0xE8C6),
            ("copy", 0xE8C8), ("paste", 0xE77F), ("share", 0xE72D), ("download", 0xE896), ("upload", 0xE898),
            ("trash", 0xE74D), ("save", 0xE74E), ("edit", 0xE70F), ("settings", 0xE713), ("monitor", 0xE7F4),
            ("laptop", 0xE7F8), ("speakers", 0xE7F5), ("volume", 0xE767), ("mute", 0xE74F), ("microphone", 0xE720),
            ("play", 0xE768), ("pause", 0xE769), ("stop", 0xE71A), ("previous", 0xE892), ("next", 0xE893),
            ("refresh", 0xE72C), ("sync", 0xE895), ("brightness", 0xE706), ("moon", 0xE708), ("power", 0xE7E8),
            ("eye", 0xE7B3), ("location", 0xE81D), ("map", 0xE707), ("phone", 0xE717), ("wifi", 0xE701),
            ("bluetooth", 0xE702), ("cloud", 0xE753), ("battery", 0xE83F), ("shield", 0xE83D), ("history", 0xE81C),
            ("zoom", 0xE71E), ("filter", 0xE71C), ("back", 0xE72B), ("forward", 0xE72A), ("more", 0xE712),
            ("package", 0xE7B8), ("emoji", 0xE76E), ("sort", 0xE8CB), ("screenshot", 0xF7ED), ("wheel", 0xF158),
            ("clipboard", 0xF0E3), ("tray", 0xF1CB), ("window", 0xE737), ("apps", 0xE7C4), ("disk", 0xEDA2),
            ("usb", 0xE88E), ("mouse", 0xE962), ("font", 0xE8D2), ("like", 0xE8E1), ("selectAll", 0xE8B3),
        ];
        return table.Select(t => new RadialGlyphChoice(t.Item1, G(t.Item2))).ToList();
    }

    /// <summary>The glyph of a stored key: a choice by name, or a lone private-use character taken as typed; otherwise null.</summary>
    public static string? Resolve(string? symbolName)
    {
        if (string.IsNullOrEmpty(symbolName)) return null;
        if (ByName.TryGetValue(symbolName, out var glyph)) return glyph;
        return symbolName.Length == 1 && symbolName[0] is >= '' and <= '' ? symbolName : null;
    }

    private const int Fallback = 0xE713, Toggle = 0xE7E8;

    /// <summary>The glyph drawn when the user picked none (app and file items prefer their real icons in the UI; these are the fallbacks).</summary>
    public static string DefaultGlyph(RadialMenuKind kind, string payload) => kind switch
    {
        RadialMenuKind.App => G(0xE71D),
        RadialMenuKind.File => G(0xE8B7),
        RadialMenuKind.Url => G(0xE71B),
        RadialMenuKind.Shortcut => G(0xE765),
        RadialMenuKind.Tool => RadialMenuTools.ToolFeature(payload) is { } feature ? FeatureCatalog.Info(feature).Glyph : G(Fallback),
        RadialMenuKind.QuickToggle => ToggleGlyph(payload),
        RadialMenuKind.WindowLayout => WindowLayoutSupport.ParseAction(payload) is { } action ? LayoutGlyph(action) : G(0xF57E),
        RadialMenuKind.Media => payload switch
        {
            "previousTrack" => G(0xE892),
            "nextTrack" => G(0xE893),
            _ => G(0xE768),
        },
        _ => G(0xE712),
    };

    public static string DefaultGlyph(RadialMenuItem item) => DefaultGlyph(item.Kind, item.Payload);

    /// <summary>The glyph to draw: the chosen one if it resolves, else the automatic one.</summary>
    public static string EffectiveGlyph(RadialMenuItem item) => Resolve(item.SymbolName) ?? DefaultGlyph(item);

    /// <summary>The glyphs the Windows Quick Toggles panel uses, so a wheel slice matches its toggle.</summary>
    public static string ToggleGlyph(string id) => G(id switch
    {
        "darkMode" => 0xE708,
        "micMute" => 0xE720,
        "emptyTrash" => 0xE74D,
        "quitAllApps" => 0xE711,
        "ejectDisks" => 0xE88E,
        "hiddenFiles" => 0xE7B3,
        "desktopIcons" => 0xE8FC,
        "lockScreen" => 0xE72E,
        "displayOff" => 0xE7F4,
        "screenSaver" => 0xE91B,
        "refreshRate" => 0xE895,
        "wifi" => 0xE701,
        "bluetooth" => 0xE702,
        _ => Toggle,
    });

    /// <summary>A glyph for each placement: arrows for the edges and corners, window icons for the rest.</summary>
    public static string LayoutGlyph(WindowLayoutAction action) => G(action switch
    {
        WindowLayoutAction.LeftHalf or WindowLayoutAction.LeftThird or WindowLayoutAction.LeftTwoThirds
            or WindowLayoutAction.LeftQuarter or WindowLayoutAction.LeftMiddleQuarter => 0xE76B,
        WindowLayoutAction.RightHalf or WindowLayoutAction.RightThird or WindowLayoutAction.RightTwoThirds
            or WindowLayoutAction.RightQuarter or WindowLayoutAction.RightMiddleQuarter => 0xE76C,
        WindowLayoutAction.TopHalf or WindowLayoutAction.TopThird or WindowLayoutAction.TopTwoThirds
            or WindowLayoutAction.TopQuarter or WindowLayoutAction.UpperMiddleQuarter => 0xE70E,
        WindowLayoutAction.BottomHalf or WindowLayoutAction.BottomThird or WindowLayoutAction.BottomTwoThirds
            or WindowLayoutAction.BottomQuarter or WindowLayoutAction.LowerMiddleQuarter => 0xE70D,
        WindowLayoutAction.Maximize => 0xE922,
        WindowLayoutAction.MarginMaximize => 0xE73F,
        WindowLayoutAction.FullScreen => 0xE740,
        WindowLayoutAction.Restore => 0xE923,
        WindowLayoutAction.PreviousDisplay => 0xE72B,
        WindowLayoutAction.NextDisplay => 0xE72A,
        _ => 0xF57E,
    });
}
