// SPDX-License-Identifier: GPL-3.0-or-later
// The two mouse toggles that flip Windows' own settings (upstream drives an event tap and the HID registry): hover
// activation and "enhance pointer precision". The value Windows had is written down before the change and given back
// when the toggle goes off, when the feature is uninstalled, when Notchly quits and, after a crash, at the next start.
// The changes are for the session only, so even without that a sign-out puts the user's own values back.
using Microsoft.Win32;
using Notchly.App.Platform.Tools;
using Notchly.App.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Tools;

public sealed class FocusFollowsMouseService(SettingsStore settings) : IFeatureService
{
    public void Sync()
    {
        var journal = FocusFollowsSupport.Original.Parse(settings.GetString(DefaultsKey.FocusFollowsMouseOriginal));
        if (!settings.GetBool(DefaultsKey.FocusFollowsMouseEnabled))
        {
            Restore(settings);
            return;
        }
        // Written down once, the first time it goes on; later changes keep that first original.
        if (journal is null)
        {
            if (MouseParameters.ReadTracking() is not { } current) return;
            settings.Set(DefaultsKey.FocusFollowsMouseOriginal, current.Storage);
        }
        var delay = FocusFollowsSupport.SanitizedDelay(settings.GetInt(DefaultsKey.FocusFollowsMouseDelayMilliseconds, FocusFollowsSupport.DefaultDelay));
        var ok = MouseParameters.WriteTracking(new FocusFollowsSupport.Original(true, settings.GetBool(DefaultsKey.FocusFollowsMouseRaise), delay));
        if (!ok) Log.Warning("Hover activation could not be turned on");
    }

    /// <summary>Gives Windows its own values back, if this feature changed them.</summary>
    public static void Restore(SettingsStore settings)
    {
        if (FocusFollowsSupport.Original.Parse(settings.GetString(DefaultsKey.FocusFollowsMouseOriginal)) is not { } original) return;
        if (MouseParameters.WriteTracking(original)) settings.Set(DefaultsKey.FocusFollowsMouseOriginal, "");
    }

    public void Dispose() => Restore(settings);
}

public sealed class PointerAccelerationService : IFeatureService
{
    private readonly SettingsStore _settings;

    public PointerAccelerationService(SettingsStore settings)
    {
        _settings = settings;
        // Windows tells us when the mouse settings change (the user in Settings, or a driver).
        SystemEvents.UserPreferenceChanged += OnPreference;
    }

    private void OnPreference(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Mouse) System.Windows.Application.Current.Dispatcher.BeginInvoke(Sync);
    }

    public void Sync()
    {
        var journal = PointerAccelerationSupport.Original.Parse(_settings.GetString(DefaultsKey.MouseAccelerationOriginal));
        var current = MouseParameters.ReadMouse();
        var step = PointerAccelerationSupport.Plan(_settings.GetBool(DefaultsKey.MouseAccelerationDisabled), journal is not null, current is { Acceleration: not 0 });
        switch (step)
        {
            case PointerAccelerationSupport.Step.CaptureAndApply when current is { } now:
                _settings.Set(DefaultsKey.MouseAccelerationOriginal, now.Storage);
                MouseParameters.WriteMouse(now.WithAccelerationOff());
                break;
            case PointerAccelerationSupport.Step.Reapply when journal is { } original && current is { } live:
                // Only acceleration is switched off again; any speed the user set meanwhile stays.
                MouseParameters.WriteMouse(live.WithAccelerationOff());
                _ = original;
                break;
            case PointerAccelerationSupport.Step.Restore:
                Restore(_settings);
                break;
        }
    }

    /// <summary>Gives the user's values back, if this feature changed them.</summary>
    public static void Restore(SettingsStore settings)
    {
        if (PointerAccelerationSupport.Original.Parse(settings.GetString(DefaultsKey.MouseAccelerationOriginal)) is not { } original) return;
        if (MouseParameters.WriteMouse(original)) settings.Set(DefaultsKey.MouseAccelerationOriginal, "");
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnPreference;
        Restore(_settings);
    }
}
