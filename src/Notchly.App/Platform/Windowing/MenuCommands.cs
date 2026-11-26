// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: what choosing a menu item does, shared by the menu bar at the top and the command bar's menu rows. A short pause lets
// the menu (or the palette) close and the app's own window take its keys back; then the command is sent the way the app's own menu
// would send it.
using Notchly.App.Platform.Input;
using Notchly.Core.MenuBar;
using Serilog;

namespace Notchly.App.Platform.Windowing;

public static class MenuCommands
{
    public static async Task RunAsync(MenuCommand command, ForegroundApp app)
    {
        await Task.Delay(60);
        try
        {
            switch (command)
            {
                case MenuCommand.SendKeys keys: await KeySender.SendAsync(app.Window, keys.Chord); break;
                case MenuCommand.Window window: WindowActions.Perform(window.Action, app); break;
                case MenuCommand.NativeItem native: AppMenus.Choose(app.Window, native.Id); break;
            }
        }
        catch (Exception e) { Log.Warning(e, "Menu command failed"); }
    }
}
