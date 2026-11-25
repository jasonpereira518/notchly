// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/Display/LidDimmingSupport.swift and the lid-sleep rule of KeepAwakeAutomationSupport.swift.
// Windows differences: closed-lid mode changes the active power scheme's lid action instead of a system flag,
// so the part to port is the recovery marker and the decision to put the PC to sleep once the lid action is
// back. Upstream also asks whether other power assertions are held; Windows only shows those to an
// administrator, so that input is gone and the rule goes by the lid and the displays alone.
using System.Globalization;

namespace Notchly.Core.Energy;

public static class LidRules
{
    /// <summary>The lid action value for "Do nothing" in a power scheme.</summary>
    public const int DoNothing = 0;
    public const int SleepRetries = 10;
    public const double SleepRetrySeconds = 0.5;
    public const int DimRestoreRetries = 6;

    /// <summary>
    /// After the lid action is back, a lid that is still closed puts the PC to sleep (Windows only reacts to the
    /// next lid event). Unknown values never authorise it; a docked PC (an external display on mains power)
    /// is left running, as Windows' own default does.
    /// </summary>
    public static bool SleepIsAllowed(bool? lidClosed, bool? externalDisplayConnected, bool? onPower)
    {
        if (lidClosed != true) return false;
        if (externalDisplayConnected is null) return false;
        if (externalDisplayConnected == false) return true;
        return onPower == false;
    }

    public enum DimKind { None, Dim, Restore }

    public readonly record struct DimAction(DimKind Kind, double Saved = 0);

    /// <summary>Closing the lid dims the panel, remembering the level it had; an unreadable or zero panel is left alone.</summary>
    public static DimAction LidClosed(double? currentBrightness) =>
        currentBrightness is { } level && level > 0 ? new DimAction(DimKind.Dim, level) : new DimAction(DimKind.None);

    /// <summary>Opening the lid brings back what was saved, if anything was.</summary>
    public static DimAction Restoring(double? saved) =>
        saved is { } level && level > 0 ? new DimAction(DimKind.Restore, level) : new DimAction(DimKind.None);

    /// <summary>The lid actions found before closed-lid mode changed them, saved before the change.</summary>
    public readonly record struct LidActionBackup(int OnPower, int OnBattery)
    {
        public string Storage => OnPower.ToString(CultureInfo.InvariantCulture) + ";" + OnBattery.ToString(CultureInfo.InvariantCulture);

        public static LidActionBackup? Parse(string? storage)
        {
            if (string.IsNullOrWhiteSpace(storage)) return null;
            var parts = storage.Split(';');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ac)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dc)) return null;
            return ac is < 0 or > 4 || dc is < 0 or > 4 ? null : new LidActionBackup(ac, dc);
        }
    }
}
