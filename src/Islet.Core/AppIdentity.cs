// SPDX-License-Identifier: GPL-3.0-or-later
namespace Islet.Core;

/// <summary>
/// The single place that names the app. "Islet" is a working name: to rename it, change these
/// constants and find/replace the root namespace. The upstream TRADEMARKS.md forbids forks
/// from using the Vorssaint name, icon or look.
/// </summary>
public static class AppIdentity
{
    public const string Name = "Islet";

    /// <summary>Prefix for the mutex, pipe, Run-key value and app user model id.</summary>
    public const string Id = "Islet.Utils";

    public const string Version = "0.1.0";

    public const string UpstreamRepository = "https://github.com/vorssaint/vorssaint-utils";

    /// <summary>Roaming settings folder, %APPDATA%\Islet.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>Machine-local folder for logs and caches, %LOCALAPPDATA%\Islet.</summary>
    public static string LocalDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(LocalDataDirectory, "logs");

    /// <summary>
    /// Optional dotfile applied at launch: $XDG_CONFIG_HOME/islet/settings.json, defaulting to
    /// %USERPROFILE%\.config. Port of upstream's ~/.config/vorssaint/settings.plist.
    /// </summary>
    public static string DotfilePath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(root, Name.ToLowerInvariant(), "settings.json");
        }
    }
}
