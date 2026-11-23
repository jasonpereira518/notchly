// SPDX-License-Identifier: GPL-3.0-or-later
namespace Notchly.Core;

/// <summary>
/// The single place that names the app. To rename it, change these constants and find/replace
/// the root namespace. The upstream TRADEMARKS.md forbids forks from using the Vorssaint name,
/// icon or look.
/// </summary>
public static class AppIdentity
{
    public const string Name = "Notchly";

    /// <summary>Prefix for the mutex, pipe, Run-key value and app user model id.</summary>
    public const string Id = "Notchly.Utils";

    public const string Version = "0.1.0";

    /// <summary>The app's earlier working name, whose folders are migrated once at launch.</summary>
    public const string LegacyName = "Islet";

    public const string UpstreamRepository = "https://github.com/vorssaint/vorssaint-utils";

    /// <summary>Roaming settings folder, %APPDATA%\Notchly.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>Machine-local folder for logs and caches, %LOCALAPPDATA%\Notchly.</summary>
    public static string LocalDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>%APPDATA%\Islet, where builds before the rename kept their settings.</summary>
    public static string LegacyDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(LocalDataDirectory, "logs");

    /// <summary>
    /// Optional dotfile applied at launch: $XDG_CONFIG_HOME/notchly/settings.json, defaulting to
    /// %USERPROFILE%\.config. Port of upstream's ~/.config/vorssaint/settings.plist.
    /// </summary>
    public static string DotfilePath => Path.Combine(DotfileDirectory, "settings.json");

    public static string DotfileDirectory => Path.Combine(ConfigRoot, Name.ToLowerInvariant());

    /// <summary>$XDG_CONFIG_HOME/islet, the dotfile folder before the rename.</summary>
    public static string LegacyDotfileDirectory => Path.Combine(ConfigRoot, LegacyName.ToLowerInvariant());

    private static string ConfigRoot
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
        }
    }
}
