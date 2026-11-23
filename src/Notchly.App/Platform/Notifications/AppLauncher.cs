// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: brings a notification's app forward by its AppUserModelID, as upstream's "Open" does once a
// banner's own action is gone (the listener doesn't expose a toast's buttons or link). Packaged apps go through
// IApplicationActivationManager; desktop apps through their Start entry, shell:AppsFolder\<AUMID>.
using System.Diagnostics;
using Serilog;

namespace Notchly.App.Platform.Notifications;

public static class AppLauncher
{
    /// <summary>Starts or brings forward the app; false when Windows could not.</summary>
    public static bool Open(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId) || appUserModelId.IndexOfAny(['"', '\r', '\n']) >= 0) return false;
        // A packaged app's id is "<family>!<app>".
        if (appUserModelId.Contains('!'))
        {
            try
            {
                var manager = (Native.IApplicationActivationManager)new Native.ApplicationActivationManager();
                if (manager.ActivateApplication(appUserModelId, null, 0, out _) >= 0) return true;
            }
            catch (Exception e) { Log.Debug(e, "Packaged activation failed; trying the Start entry"); }
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"shell:AppsFolder\\{appUserModelId}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not open a notification's app");
            return false;
        }
    }
}
