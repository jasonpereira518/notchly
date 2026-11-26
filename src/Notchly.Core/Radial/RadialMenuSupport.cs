// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/RadialMenu/RadialMenuSupport.swift: RadialMenuSupport (payload validation,
// sanitizing, lossy decode/encode, link normalizing, starter wheel, profiles and their migration, mouse-button claims).
// Windows differences: settings come through IRadialSettings instead of UserDefaults; the shortcut grammar is Windows'
// (control+alt+win:<virtualKey>); no trackpad tap (a stored "trackpadTap" still decodes and is dropped); no Now Playing;
// needsAccessibility has no Windows meaning and is not ported; first run builds one General profile with Windows'
// defaults (Ctrl+Alt+Win+Space and the Forward button).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Windowing;

namespace Notchly.Core.Radial;

/// <summary>The few settings the radial menu's rules read. Notchly.App adapts its SettingsStore to this.</summary>
public interface IRadialSettings
{
    /// <summary>The stored string, or null when the key is not set at all.</summary>
    string? GetString(string key);

    bool GetBool(string key);
}

/// <summary>An <see cref="IRadialSettings"/> over the app's settings store (saved values, then registered defaults).</summary>
public sealed class StoreRadialSettings(SettingsStore store) : IRadialSettings
{
    public string? GetString(string key) => store.Node(key) is null ? null : store.GetString(key);
    public bool GetBool(string key) => store.GetBool(key);
}

/// <summary>Where a profile deletion leaves the list and the selection.</summary>
public sealed record RadialProfileDeletion(IReadOnlyList<RadialMenuProfile> Profiles, Guid SelectedId, bool Changed);

public static class RadialMenuSupport
{
    public const int MaxItemsPerWheel = 12;
    /// <summary>Root plus one submenu level. Deeper nesting turns the wheel into a maze.</summary>
    public const int MaxDepth = 2;
    public const int MaxNameLength = 60;
    /// <summary>The largest stored custom icon (64 KB).</summary>
    public const int MaxStoredIconBytes = 65536;

    /// <summary>The key the single pre-profiles wheel kept its items under. Not registered, so a fresh install is detectable.</summary>
    public const string LegacyItemsKey = "radialMenuItems";

    /// <summary>Windows' first-run summoning shortcut (the registered default of radialMenuShortcut).</summary>
    public const string DefaultShortcut = "control+alt+win:32";
    /// <summary>Windows' first-run mouse trigger (the registered default of radialMenuMouseButton).</summary>
    public const string DefaultMouseButton = "forward";

    // MARK: Shortcuts

    /// <summary>
    /// A shortcut a wheel or slice may use: it parses, and it has a primary modifier (Ctrl, Alt or Win; Shift alone does
    /// not count) or is a standalone function key F1 to F24.
    /// </summary>
    public static Shortcut? ParseShortcut(string? storage)
    {
        if (Shortcut.Parse(storage) is not { } shortcut) return null;
        var primary = ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Win;
        var standaloneFunctionKey = shortcut.VirtualKey is >= 0x70 and <= 0x87;
        return (shortcut.Modifiers & primary) != 0 || standaloneFunctionKey ? shortcut : null;
    }

    /// <summary>The Super key is a virtual modifier: it decorates the summoning key but never shows in the physical modifier state, so either keeps the hold alive.</summary>
    public static bool ShortcutIsStillHeld(bool modifiersHeld, bool superKeyHeld) => modifiersHeld || superKeyHeld;

    // MARK: Items

    /// <summary>
    /// Whether the target can actually run for this kind. The editor blocks saving what fails here, and
    /// <see cref="Sanitized(IReadOnlyList{RadialMenuItem}, int)"/> drops it, so the two can never disagree.
    /// </summary>
    public static bool IsValidPayload(RadialMenuItem item) => item.Kind switch
    {
        RadialMenuKind.App or RadialMenuKind.File => item.Payload.Length > 0,
        RadialMenuKind.Url => NormalizedUrl(item.Payload) is not null,
        RadialMenuKind.Shortcut => ParseShortcut(item.Payload) is not null,
        RadialMenuKind.Tool => item.Tool is not null,
        RadialMenuKind.QuickToggle => item.QuickToggle is not null,
        RadialMenuKind.WindowLayout => item.WindowLayoutAction is not null,
        RadialMenuKind.Media => item.MediaKey is not null,
        _ => true,
    };

    /// <summary>
    /// Drops what cannot run (unknown tools or media keys, unparseable shortcuts, empty targets, submenus past the depth
    /// cap) and clamps counts, so the wheel never renders a dead slice.
    /// </summary>
    public static IReadOnlyList<RadialMenuItem> Sanitized(IReadOnlyList<RadialMenuItem> items, int depth = 0)
    {
        if (depth >= MaxDepth) return [];
        var seen = new HashSet<Guid>();
        var result = new List<RadialMenuItem>();
        foreach (var original in items)
        {
            if (result.Count >= MaxItemsPerWheel || !seen.Add(original.Id)) continue;
            var item = original with { Name = Prefix(original.Name.Trim(), MaxNameLength), Payload = original.Payload.Trim() };
            if (!IsValidPayload(item)) continue;
            if (item.Kind == RadialMenuKind.Url && NormalizedUrl(item.Payload) is { } normalized)
                item = item with { Payload = normalized };
            if (item.CustomIconData is { } icon && !IsStorableIcon(icon)) item = item with { CustomIconData = null };
            if (item.Kind == RadialMenuKind.Submenu)
            {
                if (depth + 1 >= MaxDepth) continue;
                item = item with { Children = Sanitized(item.Children, depth + 1) };
            }
            else if (item.Children.Count > 0)
            {
                item = item with { Children = [] };
            }
            result.Add(item);
        }
        return result;
    }

    /// <summary>A custom icon is kept when it is small enough and starts like an image (PNG, JPEG, GIF, BMP, ICO or WebP).</summary>
    public static bool IsStorableIcon(byte[] data)
    {
        if (data.Length is 0 or > MaxStoredIconBytes) return false;
        static bool Starts(byte[] d, params byte[] magic) => d.Length >= magic.Length && d.AsSpan(0, magic.Length).SequenceEqual(magic);
        return Starts(data, 0x89, 0x50, 0x4E, 0x47) || Starts(data, 0xFF, 0xD8, 0xFF) || Starts(data, 0x47, 0x49, 0x46, 0x38)
            || Starts(data, 0x42, 0x4D) || Starts(data, 0x00, 0x00, 0x01, 0x00)
            || (Starts(data, 0x52, 0x49, 0x46, 0x46) && data.Length >= 12 && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50);
    }

    private static string Prefix(string value, int textElements)
    {
        var info = new StringInfo(value);
        return info.LengthInTextElements <= textElements ? value : info.SubstringByTextElements(0, textElements);
    }

    /// <summary>
    /// A missing blob (null or blank) means a fresh install and yields the starter wheel; a present blob, even an empty
    /// list, is the user's own menu. A broken item is dropped alone; an unreadable blob is an empty wheel.
    /// </summary>
    public static IReadOnlyList<RadialMenuItem> Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return StarterItems;
        return Sanitized(TryParseArray(json) is { } array ? ReadItems(array) : []);
    }

    public static string Encode(IReadOnlyList<RadialMenuItem> items) => ItemsToNode(Sanitized(items)).ToJsonString();

    // MARK: Links

    /// <summary>
    /// Accepts "example.com/page" style input by assuming https, keeps explicit schemes (mailto:, app links) as typed, and
    /// rejects anything that cannot become a loadable URL. A single letter before the colon is a drive, not a scheme.
    /// </summary>
    public static string? NormalizedUrl(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return null;
        if (HasExplicitScheme(trimmed))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme is "http" or "https") return uri.Host.Length > 0 ? trimmed : null;
            return trimmed;
        }
        var candidate = "https://" + trimmed;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var web) && web.Host.Length > 0 ? candidate : null;
    }

    /// <summary>
    /// A leading URL scheme, minding that "example.com:8080" is a host and port while "tel:5551234" is a scheme: digits
    /// after the colon only mean a port when the part before it looks like a host.
    /// </summary>
    public static bool HasExplicitScheme(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal)) return true;
        if (value.Length == 0 || !char.IsLetter(value[0])) return false;
        var colon = value.IndexOf(':');
        if (colon < 0) return false;
        for (var i = 0; i < colon; i++)
        {
            var c = value[i];
            if (!(char.IsLetterOrDigit(c) || c is '+' or '-' or '.')) return false;
        }
        if (colon == 1) return false; // "C:\path": a drive letter
        if (colon + 1 >= value.Length) return false;
        if (!char.IsDigit(value[colon + 1])) return true;
        var prefix = value[..colon];
        return !prefix.Contains('.') && !prefix.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    // MARK: Starter wheel

    /// <summary>
    /// The wheel a fresh install starts with: media around the top, tools and the Downloads folder below. Names stay empty
    /// so labels are derived. A single evaluation keeps the seed ids stable for the whole session, so equality against a
    /// decoded seed behaves.
    /// </summary>
    public static IReadOnlyList<RadialMenuItem> StarterItems { get; } =
    [
        RadialMenuItem.Create(RadialMenuKind.Media, "playPause"),
        RadialMenuItem.Create(RadialMenuKind.Media, "nextTrack"),
        RadialMenuItem.Create(RadialMenuKind.Tool, "screenshot"),
        RadialMenuItem.Create(RadialMenuKind.File, "~/Downloads"),
        RadialMenuItem.Create(RadialMenuKind.Tool, "colorPicker"),
        RadialMenuItem.Create(RadialMenuKind.Media, "previousTrack"),
    ];

    /// <summary>True when any item, at any level, is a window-layout slice.</summary>
    public static bool UsesWindowLayout(IReadOnlyList<RadialMenuItem> items) =>
        items.Any(i => i.Kind == RadialMenuKind.WindowLayout || (i.Kind == RadialMenuKind.Submenu && UsesWindowLayout(i.Children)));

    // MARK: Profiles

    /// <summary>Whether anything opens this wheel. A wheel without a shortcut or a mouse button never opens outside of Try it.</summary>
    public static bool HasTrigger(RadialMenuProfile profile) =>
        ParseShortcut(profile.Shortcut) is not null || !RadialMouseTrigger.Sanitized(profile.MouseButton).IsOff;

    /// <summary>
    /// Gives one wheel a mouse button. A button only ever opens the first wheel that has it, so claiming it here releases
    /// it from the others.
    /// </summary>
    public static IReadOnlyList<RadialMenuProfile> Assigning(string mouseButton, Guid profileId,
                                                             IReadOnlyList<RadialMenuProfile> profiles)
    {
        var button = RadialMouseTrigger.Sanitized(mouseButton).ButtonNumber;
        return profiles.Select(profile =>
        {
            if (profile.Id == profileId) return profile with { MouseButton = mouseButton };
            if (button is not null && RadialMouseTrigger.Sanitized(profile.MouseButton).ButtonNumber == button)
                return profile with { MouseButton = RadialMouseTrigger.Off.RawValue };
            return profile;
        }).ToList();
    }

    /// <summary>
    /// The mouse buttons the wheels are bound to, read from the stored buttons alone (no items are decoded). The legacy
    /// fallback is the one <see cref="DecodeProfiles"/> migrates from, so the two answers cannot drift apart for a user who
    /// has not saved a profile yet.
    /// </summary>
    public static IReadOnlyList<int> ClaimedMouseButtons(string? profilesJson, IRadialSettings settings)
    {
        if (TryParseArray(profilesJson) is { } array)
        {
            var buttons = new List<int>();
            foreach (var element in array.EnumerateArray())
            {
                var raw = element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("mouseButton", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                if (RadialMouseTrigger.Sanitized(raw).ButtonNumber is { } button) buttons.Add(button);
            }
            return buttons;
        }
        var legacy = RadialMouseTrigger.Sanitized(settings.GetString(DefaultsKey.RadialMenuMouseButton) ?? DefaultMouseButton);
        return legacy.ButtonNumber is { } number ? [number] : [];
    }

    /// <summary>
    /// The set of extra mouse buttons (3 = Back, 4 = Forward, ...) the radial menu owns right now: empty unless the feature
    /// is available and switched on. Mouse navigation and the Mouse Button Shortcuts feature ask this on every event of a
    /// side-button gesture, so it reads the stored buttons only: never the items, and never <see cref="DecodeProfiles"/>.
    /// </summary>
    public static IReadOnlySet<int> ClaimedMouseButtons(IRadialSettings settings)
    {
        if (!settings.GetBool(FeatureCatalog.Info(AppFeature.RadialMenu).AvailabilityKey)
            || !settings.GetBool(DefaultsKey.RadialMenuEnabled)) return new HashSet<int>();
        return ClaimedMouseButtons(settings.GetString(DefaultsKey.RadialMenuProfiles), settings).ToHashSet();
    }

    /// <summary>Whether the radial menu currently owns this extra button as its summoner.</summary>
    public static bool ClaimsMouseButton(int button, IRadialSettings settings) => ClaimedMouseButtons(settings).Contains(button);

    /// <summary>
    /// Decodes the saved profiles. When nothing usable is saved (a blank value on first run, or an unreadable one), it
    /// migrates the legacy items / shortcut / mouse button, which on a fresh Windows install are the starter wheel with
    /// Ctrl+Alt+Win+Space and the Forward button, as one General profile.
    /// </summary>
    public static IReadOnlyList<RadialMenuProfile> DecodeProfiles(string? json, IRadialSettings settings)
    {
        if (TryParseArray(json) is { } array)
        {
            var sanitized = SanitizedProfiles(ReadProfiles(array));
            if (sanitized.Count > 0) return sanitized;
        }
        var legacyItems = Decode(settings.GetString(LegacyItemsKey));
        var legacyShortcut = settings.GetString(DefaultsKey.RadialMenuShortcut) ?? DefaultShortcut;
        var legacyMouse = settings.GetString(DefaultsKey.RadialMenuMouseButton) ?? DefaultMouseButton;
        return
        [
            new RadialMenuProfile
            {
                Name = "",
                Color = RadialMenuColor.Accent,
                Shortcut = ParseShortcut(legacyShortcut) is null ? "" : legacyShortcut.Trim(),
                MouseButton = RadialMouseTrigger.Sanitized(legacyMouse).RawValue,
                Items = legacyItems,
                // The single wheel that came before profiles started from the general starter set.
                Preset = RadialMenuProfilePreset.General.RawValue(),
            },
        ];
    }

    /// <summary>
    /// The combinations the wheels answer to. Before any profile is saved that is the shortcut the first wheel migrates
    /// from, and a wheel without one claims nothing.
    /// </summary>
    public static IReadOnlyList<Shortcut> ProfileShortcuts(IRadialSettings settings) =>
        DecodeProfiles(settings.GetString(DefaultsKey.RadialMenuProfiles), settings)
            .Select(p => ParseShortcut(p.Shortcut)).OfType<Shortcut>().ToList();

    /// <summary>
    /// The other wheel that already opens on a combination. Two wheels on one combination would leave one of them dead,
    /// so Settings refuses the second.
    /// </summary>
    public static RadialMenuProfile? ProfileUsing(Shortcut shortcut, IReadOnlyList<RadialMenuProfile> profiles, Guid excluding) =>
        profiles.FirstOrDefault(p => p.Id != excluding && ParseShortcut(p.Shortcut) == shortcut);

    public static string EncodeProfiles(IReadOnlyList<RadialMenuProfile> profiles) =>
        ProfilesToNode(SanitizedProfiles(profiles)).ToJsonString();

    public static IReadOnlyList<RadialMenuProfile> SanitizedProfiles(IReadOnlyList<RadialMenuProfile> profiles)
    {
        var seenIds = new HashSet<Guid>();
        var seenButtons = new HashSet<int>();
        var result = new List<RadialMenuProfile>();
        foreach (var original in profiles)
        {
            if (!seenIds.Add(original.Id)) continue;
            var shortcut = original.Shortcut.Trim();
            if (shortcut.Length > 0 && ParseShortcut(shortcut) is null) shortcut = "";
            var mouse = RadialMouseTrigger.Sanitized(original.MouseButton);
            // A button opens only the first wheel that has it, so a later wheel saved with the same one reads as off.
            if (mouse.ButtonNumber is { } button && !seenButtons.Add(button)) mouse = RadialMouseTrigger.Off;
            result.Add(original with
            {
                Name = Prefix(original.Name.Trim(), MaxNameLength),
                Items = Sanitized(original.Items),
                Shortcut = shortcut,
                MouseButton = mouse.RawValue,
            });
        }
        if (result.Count == 0)
            result.Add(RadialMenuPresets.CreateProfile(RadialMenuProfilePreset.General, shortcut: DefaultShortcut));
        return result;
    }

    /// <summary>
    /// Deletes the profile the user confirmed (which the selection may no longer point at by the time the dialog closes).
    /// The last profile is never deleted, and an unknown id changes nothing. After a deletion the selection moves to the
    /// profile now at the deleted one's position (or the last), as upstream does.
    /// </summary>
    public static RadialProfileDeletion DeleteProfile(IReadOnlyList<RadialMenuProfile> profiles, Guid selectedId, Guid id)
    {
        var index = profiles.ToList().FindIndex(p => p.Id == id);
        if (profiles.Count <= 1 || index < 0) return new RadialProfileDeletion(profiles, selectedId, false);
        var remaining = profiles.Where((_, i) => i != index).ToList();
        return new RadialProfileDeletion(remaining, remaining[Math.Min(index, remaining.Count - 1)].Id, true);
    }

    // MARK: JSON (lossy, in upstream's Codable shape)

    private static JsonElement? TryParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<RadialMenuItem> ReadItems(JsonElement array)
    {
        var items = new List<RadialMenuItem>();
        foreach (var element in array.EnumerateArray())
            if (TryReadItem(element, out var item)) items.Add(item);
        return items;
    }

    private static List<RadialMenuProfile> ReadProfiles(JsonElement array)
    {
        var profiles = new List<RadialMenuProfile>();
        foreach (var element in array.EnumerateArray())
            if (TryReadProfile(element, out var profile)) profiles.Add(profile);
        return profiles;
    }

    // Swift's decodeIfPresent: absent or null gives the default, any other wrong type fails the whole element.
    private static bool TryString(JsonElement owner, string name, string fallback, out string value)
    {
        value = fallback;
        if (!owner.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? fallback;
        return true;
    }

    private static bool TryId(JsonElement owner, out Guid id)
    {
        id = Guid.NewGuid();
        if (!owner.TryGetProperty("id", out var property) || property.ValueKind == JsonValueKind.Null) return true;
        return property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out id);
    }

    private static bool TryReadItem(JsonElement element, out RadialMenuItem item)
    {
        item = null!;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String
            || RadialMenuKinds.Parse(kindElement.GetString()) is not { } kind) return false;
        if (!TryId(element, out var id) || !TryString(element, "name", "", out var name)
            || !TryString(element, "symbolName", "", out var symbol) || !TryString(element, "payload", "", out var payload))
            return false;
        byte[]? icon = null;
        if (element.TryGetProperty("customIconData", out var iconElement) && iconElement.ValueKind != JsonValueKind.Null)
        {
            if (iconElement.ValueKind != JsonValueKind.String) return false;
            try { icon = Convert.FromBase64String(iconElement.GetString() ?? ""); }
            catch (FormatException) { return false; }
        }
        IReadOnlyList<RadialMenuItem> children = [];
        if (element.TryGetProperty("children", out var childrenElement) && childrenElement.ValueKind != JsonValueKind.Null)
        {
            if (childrenElement.ValueKind != JsonValueKind.Array) return false;
            children = ReadItems(childrenElement);
        }
        item = new RadialMenuItem
        {
            Id = id, Kind = kind, Name = name, SymbolName = symbol, Payload = payload, CustomIconData = icon, Children = children,
        };
        return true;
    }

    private static bool TryReadProfile(JsonElement element, out RadialMenuProfile profile)
    {
        profile = null!;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!TryId(element, out var id) || !TryString(element, "name", "", out var name)
            || !TryString(element, "shortcut", "", out var shortcut)
            || !TryString(element, "mouseButton", RadialMouseTrigger.Off.RawValue, out var mouse)
            || !TryString(element, "color", "accent", out var colorRaw) || RadialMenuColors.Parse(colorRaw) is not { } color)
            return false;
        string? preset = null;
        if (element.TryGetProperty("preset", out var presetElement) && presetElement.ValueKind != JsonValueKind.Null)
        {
            if (presetElement.ValueKind != JsonValueKind.String) return false;
            preset = presetElement.GetString();
        }
        IReadOnlyList<RadialMenuItem> items = [];
        if (element.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind != JsonValueKind.Null)
        {
            if (itemsElement.ValueKind != JsonValueKind.Array) return false;
            items = ReadItems(itemsElement);
        }
        // A "trackpadTap" written by the macOS app is ignored: the four-finger tap is not ported.
        profile = new RadialMenuProfile
        {
            Id = id, Name = name, Color = color, Shortcut = shortcut, MouseButton = mouse, Items = items, Preset = preset,
        };
        return true;
    }

    private static JsonArray ItemsToNode(IReadOnlyList<RadialMenuItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var node = new JsonObject
            {
                ["id"] = item.Id.ToString("D").ToUpperInvariant(),
                ["kind"] = item.Kind.RawValue(),
                ["name"] = item.Name,
                ["symbolName"] = item.SymbolName,
                ["payload"] = item.Payload,
            };
            if (item.CustomIconData is { } icon) node["customIconData"] = Convert.ToBase64String(icon);
            node["children"] = ItemsToNode(item.Children);
            array.Add(node);
        }
        return array;
    }

    private static JsonArray ProfilesToNode(IReadOnlyList<RadialMenuProfile> profiles)
    {
        var array = new JsonArray();
        foreach (var profile in profiles)
        {
            var node = new JsonObject
            {
                ["id"] = profile.Id.ToString("D").ToUpperInvariant(),
                ["name"] = profile.Name,
                ["color"] = profile.Color.RawValue(),
                ["shortcut"] = profile.Shortcut,
                ["mouseButton"] = profile.MouseButton,
                ["items"] = ItemsToNode(profile.Items),
            };
            if (profile.Preset is { } preset) node["preset"] = preset;
            array.Add(node);
        }
        return array;
    }
}
