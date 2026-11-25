// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the pure parts of upstream Sources/Vorssaint/Services/Audio/MixerRoutingSupport.swift: how a row is
// identified, ordered, hidden and shown, how a typed percent becomes a level, which outputs are headphones,
// and the output a switch or priority list chooses. Windows differences: apps are named by package family
// or executable instead of bundle id, devices by endpoint id, and the app volume tops out at 100%
// (a session cannot be played louder than it is rendered; the 0…2 model and its gain curve are kept for a later boost).
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Notchly.Core.Sound;

public sealed record MixerRowIdentity(string RowId, string? PersistenceId);

public sealed record MixerOutputPreferences(IReadOnlyDictionary<string, string> OutputDeviceIds,
                                           IReadOnlyDictionary<string, double> Volumes);

/// <summary>How Windows classes an endpoint (PKEY_AudioEndpoint_FormFactor).</summary>
public enum EndpointFormFactor
{
    Unknown = -1, RemoteNetworkDevice = 0, Speakers = 1, LineLevel = 2, Headphones = 3, Microphone = 4, Headset = 5,
    Handset = 6, UnknownDigitalPassthrough = 7, SpdifOutput = 8, DigitalAudioDisplay = 9,
}

public enum PriorityTier { BuiltIn, Hardware, Virtual }

public static partial class MixerSupport
{
    /// <summary>The scale volumes are stored on (upstream: 200% boost); Windows plays up to <see cref="PlayableMaximum"/>.</summary>
    public const double MaximumVolume = 2;
    public const double PlayableMaximum = 1;
    /// <summary>The row the system's own sounds get, standing in for upstream's Finder row.</summary>
    public const string SystemSoundsId = "windows.system-sounds";
    public const string UnidentifiedRowPrefix = "process:";
    public const int OwningAppSearchDepth = 6;
    public const int MaximumListLength = 64;
    /// <summary>Muted rows sit at or below this; unmuting returns to the last audible level.</summary>
    public const double MutedThreshold = 0.001;

    // MARK: Levels

    /// <summary>A stored app volume: finite values within 0…2, anything else plays untouched.</summary>
    public static double SanitizedAppVolume(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, MaximumVolume) : 1;

    /// <summary>The level Windows is asked for: the stored position, held to what a session can play.</summary>
    public static double PlayableVolume(double stored) => Math.Min(SanitizedAppVolume(stored), PlayableMaximum);

    public static int DisplayPercent(double volume) =>
        double.IsFinite(volume) ? (int)Math.Round(Math.Clamp(volume, -1000, 1000) * 100, MidpointRounding.AwayFromZero) : 0;

    public static bool IsUnity(double volume) => Math.Abs(volume - 1) < 0.005;

    /// <summary>The gain a slider position plays at: squared below 1 (50% is -12 dB), linear above.</summary>
    public static float Gain(double volume)
    {
        if (!double.IsFinite(volume)) return 1;
        var level = Math.Max(volume, 0);
        return (float)(level < 1 ? level * level : level);
    }

    /// <summary>The slider position that plays <paramref name="gain"/>, the inverse of <see cref="Gain"/>.</summary>
    public static double Volume(double gain)
    {
        if (!double.IsFinite(gain)) return 1;
        var level = Math.Max(gain, 0);
        return level < 1 ? Math.Sqrt(level) : level;
    }

    /// <summary>Whether a level asked of an output also throws its mute switch (a level shown as 0%).</summary>
    public static bool MutesOutput(double volume) => volume < 0.005;

    /// <summary>The text beside a slider as a level; the caller gives the limit (100 for a device, 200 for the stored scale).</summary>
    public static double? VolumeFraction(string? text, int maximumPercent, CultureInfo? culture = null)
    {
        if (maximumPercent < 0 || text is null) return null;
        culture ??= CultureInfo.CurrentCulture;
        var normalized = text.Trim();
        if (normalized.EndsWith('%')) normalized = normalized[..^1].Trim();
        var separator = culture.NumberFormat.NumberDecimalSeparator;
        if (separator != ".") normalized = normalized.Replace(separator, ".", StringComparison.Ordinal);
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || !double.IsFinite(percent))
            return null;
        return Math.Clamp(percent, 0, maximumPercent) / 100;
    }

    // MARK: Rows

    /// <summary>Inactive rows with a custom level or route stay listed, so hiding idle ones never hides a setting.</summary>
    public static bool ShouldShowApp(bool isPlaying, double volume, string? selectedOutputId, bool hideInactiveApps) =>
        !hideInactiveApps || isPlaying || !IsUnity(volume) || selectedOutputId is not null;

    public static string? SanitizedId(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 512) return null;
        return trimmed.Any(char.IsControl) ? null : trimmed;
    }

    /// <summary>
    /// A row's identity: the package family name or executable name when known (what a volume is saved
    /// under, since process ids recycle), else a per-process row saved under its display name.
    /// </summary>
    public static MixerRowIdentity RowIdentity(string? appId, int ownerPid, string? displayName)
    {
        if (SanitizedId(appId) is not { } id)
            return new MixerRowIdentity($"{UnidentifiedRowPrefix}{ownerPid}", SanitizedId(displayName));
        return new MixerRowIdentity(id, id);
    }

    public static IReadOnlyDictionary<string, string> SanitizedHiddenApps(IReadOnlyDictionary<string, string>? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawId, rawName) in raw ?? new Dictionary<string, string>())
            if (SanitizedId(rawId) is { } id && id != SystemSoundsId && SanitizedId(rawName) is { } name) result[id] = name;
        return result;
    }

    /// <summary>Every id a refresh leaves out: what the user hid, plus the system sounds row while its toggle says so.</summary>
    public static IReadOnlySet<string> HiddenRowIds(IReadOnlyDictionary<string, string> hiddenApps, bool showSystemSounds)
    {
        var ids = new HashSet<string>(hiddenApps.Keys, StringComparer.Ordinal);
        if (!showSystemSounds) ids.Add(SystemSoundsId);
        return ids;
    }

    public static bool IsHidden(string? persistenceId, IReadOnlySet<string> hiddenIds) =>
        persistenceId is not null && hiddenIds.Contains(persistenceId);

    public static bool NeedsPersistentSystemSoundsRow(bool showSystemSounds, bool hasRow) => showSystemSounds && !hasRow;

    /// <summary>The application a helper's audio belongs to: the process itself or the nearest regular app up its parents.</summary>
    public static int? OwningAppPid(int pid, Func<int, bool> isRegularApp, Func<int, int> parentPid)
    {
        if (pid <= 0) return null;
        if (isRegularApp(pid)) return pid;
        var current = pid;
        for (var i = 0; i < OwningAppSearchDepth; i++)
        {
            current = parentPid(current);
            if (current <= 4) return null;
            if (isRegularApp(current)) return current;
        }
        return null;
    }

    // MARK: Call apps and pro audio

    private static readonly string[] CallApps = ["discord", "discordptb", "discordcanary", "ms-teams", "teams", "msteams"];
    private static readonly string[] ZoomApps = ["zoom", "zoom.us", "zoommeetings", "zoomworkplace", "zoomit"];
    private static readonly string[] ProAudioApps =
    [
        "ableton live", "cubase", "nuendo", "dorico", "studio one", "reaper", "fl64", "fl studio", "bitwig studio",
        "protools", "pro tools", "reason", "cakewalk", "samplitude", "ardour", "mixcraft",
    ];

    private static string Normalize(string? name)
    {
        var text = (name ?? "").Trim().ToLowerInvariant();
        return text.EndsWith(".exe", StringComparison.Ordinal) ? text[..^4] : text;
    }

    /// <summary>Call apps' audio is left alone unless the user opts in: volume changes echo into screen shares.</summary>
    public static bool NeedsScreenSharingProtection(string? exeName) => CallApps.Contains(Normalize(exeName));

    /// <summary>Apps the mixer shows with a caption instead of a slider: Zoom, DAWs, and call apps unless allowed.</summary>
    public static bool BypassesVolumeControl(string? exeName, string? displayName, bool allowCallAppVolume = false)
    {
        if (!allowCallAppVolume && NeedsScreenSharingProtection(exeName)) return true;
        var exe = Normalize(exeName);
        var name = Normalize(displayName);
        if (ZoomApps.Contains(exe) || name is "zoom" or "zoom.us" or "zoom workplace") return true;
        // A version number on the executable (Cubase13.exe) names the same app.
        var unversioned = exe.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return ProAudioApps.Any(app => exe == app || unversioned == app || exe == app.Replace(" ", "") || unversioned == app.Replace(" ", "") || name == app);
    }

    // MARK: Order

    public static int CompareNames(string left, string right) =>
        string.Compare(left, right, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    /// <summary>Rows by display name, then id: two apps with one name keep their places between refreshes.</summary>
    public static bool DisplayOrderedBefore(string name, string id, string otherName, string otherId)
    {
        var order = CompareNames(name, otherName);
        return order != 0 ? order < 0 : string.CompareOrdinal(id, otherId) < 0;
    }

    /// <summary>Device lists: the default first, then name, then id.</summary>
    public static bool DeviceDisplayOrderedBefore(bool isDefault, string name, string id, bool otherIsDefault, string otherName, string otherId) =>
        isDefault != otherIsDefault ? isDefault : DisplayOrderedBefore(name, id, otherName, otherId);

    // MARK: Routes

    public static IReadOnlyDictionary<string, string> SanitizedRouteMap(IReadOnlyDictionary<string, string>? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawId, rawDevice) in raw ?? new Dictionary<string, string>())
            if (SanitizedId(rawId) is { } app && SanitizedId(rawDevice) is { } device) result[app] = device;
        return result;
    }

    /// <summary>The device a row plays to: its chosen one while that is present, else the default.</summary>
    public static string? EffectiveDeviceId(string? selectedId, IReadOnlySet<string> availableIds, string? defaultId) =>
        selectedId is not null && availableIds.Contains(selectedId) ? selectedId : defaultId;

    public static bool SelectedDeviceUnavailable(string? selectedId, IReadOnlySet<string> availableIds) =>
        selectedId is not null && !availableIds.Contains(selectedId);

    /// <summary>An "Output unavailable" menu row is needed only when the chosen output is not listed anyway.</summary>
    public static bool NeedsUnavailableOutputRow(string? selectedId, bool isUnavailable, IReadOnlyList<string> listedIds) =>
        selectedId is not null && isUnavailable && !listedIds.Contains(selectedId);

    /// <summary>Choosing the system output clears every per-app route and keeps the volumes.</summary>
    public static MixerOutputPreferences PreferencesAfterUniversalOutputSwitch(IReadOnlyDictionary<string, string> outputDeviceIds,
        IReadOnlyDictionary<string, double> volumes, bool switchSucceeded) =>
        new(switchSucceeded ? new Dictionary<string, string>() : outputDeviceIds, volumes);

    // MARK: Headphones

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();

    private static readonly string[] HeadphoneTerms =
    [
        "headphone", "headphones", "headset", "earphone", "earphones", "earbud", "earbuds", "airpod", "airpods",
        "earpod", "earpods", "galaxy buds", "pixel buds", "beats", "bose qc", "sony wh", "sony wf", "jabra", "soundcore",
    ];

    /// <summary>
    /// Headphones by the endpoint's form factor when it says so (a speaker form factor wins over brand
    /// words), else by the name keywords upstream uses.
    /// </summary>
    public static bool OutputLooksLikeHeadphones(string name, string id, EndpointFormFactor formFactor = EndpointFormFactor.Unknown)
    {
        if (formFactor is EndpointFormFactor.Headphones or EndpointFormFactor.Headset) return true;
        if (formFactor is EndpointFormFactor.Speakers or EndpointFormFactor.DigitalAudioDisplay or EndpointFormFactor.SpdifOutput
            or EndpointFormFactor.LineLevel) return false;
        var text = NonWord().Replace((name + " " + id).ToLowerInvariant(), " ");
        return HeadphoneTerms.Any(text.Contains);
    }

    /// <summary>A volume the app lowered only goes back up while it is still the value the app set.</summary>
    public static bool ShouldRestoreOutputVolume(double appliedVolume, double? currentVolume) =>
        currentVolume is { } current && Math.Abs(current - appliedVolume) < 0.005;

    /// <summary>Level 0 can still be faintly audible, so a disconnect lowered to 0 also mutes an unmuted output.</summary>
    public static bool ShouldMuteOutputLoweredForDisconnect(int lowerToPercent, bool? currentlyMuted) =>
        lowerToPercent <= 0 && currentlyMuted == false;

    public static int SanitizedHeadphonesDisconnectPercent(int value) => Math.Clamp(value, 0, 100);

    // MARK: Lists

    /// <summary>Valid ids once each, in order, up to <paramref name="limit"/>.</summary>
    public static IReadOnlyList<string> SanitizedIdList(IEnumerable<string?>? raw, int limit = MaximumListLength)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var item in raw ?? [])
            if (SanitizedId(item) is { } id && seen.Add(id) && result.Count < limit) result.Add(id);
        return result;
    }

    /// <summary>The next output of the cycle: only selected, present ones count; none current, so the first.</summary>
    public static string? NextSelectedOutputId(string? currentId, IEnumerable<string?> selectedIds, IReadOnlySet<string> availableIds)
    {
        var candidates = SanitizedIdList(selectedIds).Where(availableIds.Contains).ToList();
        if (candidates.Count == 0) return null;
        var index = currentId is null ? -1 : candidates.IndexOf(currentId);
        if (index < 0) return candidates[0];
        return candidates.Count > 1 ? candidates[(index + 1) % candidates.Count] : null;
    }

    /// <summary>The ordered priority list's first present device, or null to leave the current choice alone.</summary>
    public static string? FirstAvailablePriorityId(IEnumerable<string?> orderedIds, IReadOnlySet<string> availableIds) =>
        SanitizedIdList(orderedIds, int.MaxValue).FirstOrDefault(availableIds.Contains);

    public static bool ShouldSwitchToDevice(string? targetId, string? currentId) => targetId is not null && targetId != currentId;

    /// <summary>The first snapshot is a baseline; afterwards only a device coming or going is an event, not a new default.</summary>
    public static bool DeviceAvailabilityChanged(IReadOnlySet<string>? previous, IReadOnlySet<string> current) =>
        previous is not null && !previous.SetEquals(current);

    private static readonly string[] VirtualMarkers =
    [
        "virtual", "voicemeeter", "vb-audio", "cable", "loopback", "steam streaming", "nvidia broadcast", "obs", "blackhole",
        "krisp", "wave link", "remote audio", "oculus", "meta quest",
    ];

    /// <summary>Where a device ranks when nothing ranked it yet: built-in codecs, other hardware, virtual last.</summary>
    public static PriorityTier TierOf(string name, string? enumeratorName)
    {
        var lowered = name.ToLowerInvariant();
        if (VirtualMarkers.Any(lowered.Contains)) return PriorityTier.Virtual;
        if (enumeratorName is not null && enumeratorName.ToUpperInvariant() is "HDAUDIO" or "INTELAUDIO" or "ACPI")
            return PriorityTier.BuiltIn;
        return PriorityTier.Hardware;
    }

    /// <summary>The order a first-time list starts in: the device in use, then built-in, hardware, virtual.</summary>
    public static IReadOnlyList<string> InitialPriorityList(IEnumerable<string> availableIds, string? currentId, Func<string, PriorityTier> tier)
    {
        var unique = SanitizedIdList(availableIds, int.MaxValue);
        var lead = unique.Where(id => id == currentId);
        var rest = unique.Select((id, offset) => (id, offset)).Where(x => x.id != currentId)
            .OrderBy(x => tier(x.id)).ThenBy(x => x.offset).Select(x => x.id);
        return lead.Concat(rest).Take(MaximumListLength).ToList();
    }

    /// <summary>Where a device the list never saw joins: first when it is the one in use, else above the virtual ones.</summary>
    public static IReadOnlyList<string> PlacingNewPriorityDevice(string rawId, IReadOnlyList<string> list, bool isCurrent, Func<string, PriorityTier> tier)
    {
        if (SanitizedId(rawId) is not { } id || list.Contains(id)) return list;
        if (isCurrent) return Cap([id, .. list]);
        var firstVirtual = list.ToList().FindIndex(item => tier(item) == PriorityTier.Virtual);
        if (tier(id) == PriorityTier.Virtual || firstVirtual < 0) return Cap([.. list, id]);
        var placed = list.ToList();
        placed.Insert(firstVirtual, id);
        return Cap(placed);
    }

    private static IReadOnlyList<string> Cap(IEnumerable<string> ids) => ids.Take(MaximumListLength).ToList();
}

/// <summary>
/// The user's order and pins for the mixer (upstream MixerAppArrangement), stored as JSON
/// <c>{"order":[...],"pinned":[...]}</c>. Absent apps keep their slots; pinned rows lead and moves stay inside a group.
/// </summary>
public sealed class MixerAppArrangement : IEquatable<MixerAppArrangement>
{
    private sealed record Stored(List<string>? Order, List<string>? Pinned);
    private List<string> _order = [];
    private List<string> _pinned = [];

    public MixerAppArrangement(string rawValue = "")
    {
        try
        {
            if (JsonSerializer.Deserialize<Stored>(rawValue, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } stored)
            {
                _order = Unique(stored.Order);
                _pinned = Unique(stored.Pinned);
            }
        }
        catch (Exception e) when (e is JsonException or ArgumentNullException) { }
    }

    public IReadOnlyList<string> Order => _order;
    public IReadOnlyList<string> Pinned => _pinned;

    public string RawValue => JsonSerializer.Serialize(new { order = _order, pinned = _pinned });

    public bool IsPinned(string? id) => id is not null && _pinned.Contains(id);

    public IReadOnlyList<T> Ordered<T>(IEnumerable<T> items, Func<T, string?> identity)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _order.Count; i++) ranks[_order[i]] = i;
        return items.Select((item, offset) => (item, offset))
            .OrderBy(x => IsPinned(identity(x.item)) ? 0 : 1)
            .ThenBy(x => identity(x.item) is { } key && ranks.TryGetValue(key, out var rank) ? rank : int.MaxValue)
            .ThenBy(x => x.offset).Select(x => x.item).ToList();
    }

    public void TogglePin(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!_pinned.Remove(id)) _pinned.Add(id);
    }

    public string? Neighbor(string id, int offset, IReadOnlyList<string> visibleIds)
    {
        var group = visibleIds.Where(item => IsPinned(item) == IsPinned(id)).ToList();
        var index = group.IndexOf(id);
        return index >= 0 && index + offset >= 0 && index + offset < group.Count ? group[index + offset] : null;
    }

    public void Move(string id, int offset, IReadOnlyList<string> visibleIds)
    {
        if (Neighbor(id, offset, visibleIds) is { } neighbor) Move(id, neighbor, offset > 0, visibleIds);
    }

    public void Move(string id, string target, bool after, IReadOnlyList<string> visibleIds)
    {
        if (id == target || !visibleIds.Contains(id) || !visibleIds.Contains(target) || IsPinned(id) != IsPinned(target)) return;
        var group = Unique(visibleIds.Where(item => IsPinned(item) == IsPinned(id)));
        group.Remove(id);
        var index = group.IndexOf(target);
        if (index < 0) return;
        group.Insert(index + (after ? 1 : 0), id);
        var moving = group.ToHashSet(StringComparer.Ordinal);
        using var reordered = group.GetEnumerator();
        // Only this group's visible slots are replaced; closed and hidden apps and the other group keep theirs.
        _order = Unique(_order.Concat(visibleIds)).Select(item => moving.Contains(item) && reordered.MoveNext() ? reordered.Current : item).ToList();
    }

    private static List<string> Unique(IEnumerable<string>? ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return (ids ?? []).Where(id => !string.IsNullOrEmpty(id) && seen.Add(id)).ToList();
    }

    public bool Equals(MixerAppArrangement? other) => other is not null && _order.SequenceEqual(other._order) && _pinned.SequenceEqual(other._pinned);
    public override bool Equals(object? obj) => Equals(obj as MixerAppArrangement);
    public override int GetHashCode() => HashCode.Combine(_order.Count, _pinned.Count);
}
