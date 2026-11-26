// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Core/SettingsBackupSupport.swift: which keys travel in a
// backup and which imported values are accepted. The file is JSON instead of a plist.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notchly.Core.Settings;

public static class SettingsBackup
{
    public const int BackupVersion = 1;
    public const string VersionKey = "notchlyBackupVersion";
    public const string AppVersionKey = "notchlyBackupAppVersion";
    /// <summary>The version key in backups exported before the rename from Islet.</summary>
    public const string LegacyVersionKey = "isletBackupVersion";
    public const string SettingsKey = "settings";

    /// <summary>State about this machine that must not travel to another one.</summary>
    public static readonly IReadOnlySet<string> MachineStateKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        DefaultsKey.OnboardingStep, DefaultsKey.LaunchAtLogin, DefaultsKey.PortedFeaturesSeen,
        DefaultsKey.NotchDownloadsFolderPath, DefaultsKey.NotchCameraDevice,
        // What a mic mute took from this machine's devices has no meaning on another.
        DefaultsKey.MicMuteActive, DefaultsKey.MicMuteSavedVolumes, DefaultsKey.MicMuteMutedDevices, DefaultsKey.MicMuteSavedVolume,
        // Saved sessions, lid and dimming crash guards, displays and the radio debt belong to this machine too.
        DefaultsKey.KeepAwakeSessionEnd, DefaultsKey.LidActionBackup, DefaultsKey.LidDimSavedBrightness,
        DefaultsKey.BrightnessForcedSoftwarePaths, DefaultsKey.BrightnessExtendedDimmingPaths, DefaultsKey.BluetoothSleepRestorePending,
        // The system settings a toggle changed are this machine's too.
        DefaultsKey.FocusFollowsMouseOriginal, DefaultsKey.MouseAccelerationOriginal,
        // The task lives on this machine.
        DefaultsKey.RunElevatedAtLogin,
        // A camera id, a window position and folders of pictures name things on this machine.
        DefaultsKey.CameraPreviewDeviceId, DefaultsKey.ScratchpadWindowOrigin, DefaultsKey.WallpaperOwnPaths, DefaultsKey.WallpaperExcludedOwnPaths,
    };

    public static string Export(SettingsStore store)
    {
        var saved = store.SavedValues();
        var settings = new JsonObject();
        foreach (var key in store.RegisteredKeys.Order(StringComparer.Ordinal))
        {
            if (MachineStateKeys.Contains(key)) continue;
            // Saved choices, or the default so the other machine gets exactly this setup.
            settings[key] = saved.TryGetValue(key, out var value) ? value?.DeepClone() : store.DefaultNode(key);
        }
        var root = new JsonObject
        {
            [VersionKey] = BackupVersion,
            [AppVersionKey] = AppIdentity.Version,
            [SettingsKey] = settings,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public sealed record ImportResult(int Applied, IReadOnlyList<string> Rejected);

    /// <summary>Parses a backup and returns only known keys whose value looks right.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> Validate(SettingsStore store, string json, out IReadOnlyList<string> rejected)
    {
        var accepted = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var refused = new List<string>();
        rejected = refused;
        if (JsonNode.Parse(json) is not JsonObject root || root[SettingsKey] is not JsonObject settings)
            throw new FormatException("This file is not a Notchly settings backup.");
        if ((root[VersionKey] ?? root[LegacyVersionKey])?.GetValue<int>() is not int version || version > BackupVersion)
            throw new FormatException("This backup was made by a newer version of Notchly.");

        foreach (var (key, value) in settings)
        {
            if (MachineStateKeys.Contains(key) || !store.IsRegistered(key) || !ValueLooksRight(store.DefaultNode(key), value))
            {
                refused.Add(key);
                continue;
            }
            accepted[key] = value?.DeepClone();
        }
        return accepted;
    }

    public static ImportResult Import(SettingsStore store, string json)
    {
        var accepted = Validate(store, json, out var rejected);
        store.SetMany(accepted);
        return new ImportResult(accepted.Count, rejected);
    }

    /// <summary>Same JSON kind as the registered default (a number for a number, and so on).</summary>
    public static bool ValueLooksRight(JsonNode? registered, JsonNode? candidate)
    {
        if (registered is null) return candidate is null;
        if (candidate is null) return false;
        return Kind(registered) == Kind(candidate);
    }

    private static JsonValueKind Kind(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.True or JsonValueKind.False => JsonValueKind.True,
        var kind => kind,
    };
}
