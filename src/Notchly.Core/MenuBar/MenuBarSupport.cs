// SPDX-License-Identifier: GPL-3.0-or-later
// Which window the bar follows and what it is called, how many titles fit beside the notch, and the
// clock's reading.
using System.Globalization;

namespace Notchly.Core.MenuBar;

public enum ForegroundKind
{
    /// <summary>An app: the bar shows it.</summary>
    App,
    /// <summary>The desktop itself.</summary>
    Desktop,
    /// <summary>A passing shell surface (taskbar, Start, search, Alt+Tab): the bar keeps the app it showed.</summary>
    KeepPrevious,
}

public static class ForegroundRules
{
    private static readonly HashSet<string> PassingClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging", "#32768", "TaskListThumbnailWnd",
    };

    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "ShellHost", "LockApp",
    };

    /// <summary>Electron apps whose own menu bar is drawn in the page (others have native menus or none).</summary>
    private static readonly HashSet<string> ChromiumMenuBars = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "Cursor", "Windsurf", "VSCodium",
    };

    public static ForegroundKind Classify(string className, string processName)
    {
        if (className.Length == 0 || PassingClasses.Contains(className) || ShellHosts.Contains(processName)) return ForegroundKind.KeepPrevious;
        if (className is "Progman" or "WorkerW") return ForegroundKind.Desktop;
        return ForegroundKind.App;
    }

    /// <summary>
    /// The name a Mac would show in bold: the program's own description ("Google Chrome", "Notepad"),
    /// cut before a tagline ("Notepad++ : a free source code editor"); the window title for Store apps,
    /// which all run inside ApplicationFrameHost; File Explorer for explorer; else the process name.
    /// </summary>
    public static string DisplayName(string? fileDescription, string processName, string? windowTitle)
    {
        if (string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(windowTitle) ? "App" : windowTitle.Trim();
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase)) return "File Explorer";
        var description = fileDescription?.Trim() ?? "";
        foreach (var cut in new[] { " : ", " - ", " – " })
        {
            var at = description.IndexOf(cut, StringComparison.Ordinal);
            if (at > 0) description = description[..at].Trim();
        }
        if (description.Length is > 0 and <= 40) return description;
        return processName.Length == 0 ? "App" : char.ToUpperInvariant(processName[0]) + processName[1..];
    }

    /// <summary>
    /// UI Automation is asked only where a menu bar can be: never Chromium windows outside the few
    /// editors that draw one, since querying them switches Chrome's accessibility mode on.
    /// </summary>
    public static bool UsesAutomation(string className, string processName) =>
        !className.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) || ChromiumMenuBars.Contains(processName);

    /// <summary>Every window's own system menu appears to UI Automation as a menu bar too.</summary>
    public static bool IsSystemMenuBar(string? automationId) => automationId == "SystemMenuBar";
}

public static class MenuBarLayout
{
    public const double LeftInset = 10;
    public const double TitleSpacing = 2;

    /// <summary>Half the island's resting width plus a gap: the bar's titles stop there.</summary>
    public static double ReservedHalf(double cameraWidth) => cameraWidth / 2 + 56 + 16;

    /// <summary>Room for titles left of the island, whose centre sits <paramref name="offset"/> from the display's.</summary>
    public static double Available(double displayWidth, double offset, double cameraWidth) =>
        Math.Max(0, displayWidth / 2 + offset - ReservedHalf(cameraWidth) - LeftInset);

    /// <summary>
    /// How many leading titles fit, as macOS hides the menus that would reach the notch. The app's name
    /// always shows, trimmed if it must be.
    /// </summary>
    public static int Fit(IReadOnlyList<double> widths, double available, double spacing = TitleSpacing)
    {
        if (widths.Count == 0) return 0;
        var used = widths[0];
        var count = 1;
        for (var i = 1; i < widths.Count; i++)
        {
            used += spacing + widths[i];
            if (used > available) break;
            count++;
        }
        return count;
    }
}

public static class StripClock
{
    /// <summary>"Sun Nov 22  7:31 PM" or "Sun 22 Nov  19:31": the culture's own order and clock.</summary>
    public static string Format(DateTime now, CultureInfo culture)
    {
        var format = culture.DateTimeFormat;
        var monthDay = format.MonthDayPattern.Replace("MMMM", "MMM");
        var text = now.ToString("ddd", culture) + " " + now.ToString(monthDay, culture) + "  " + now.ToString(format.ShortTimePattern, culture);
        // ICU writes a narrow no-break space before AM/PM; the strip reads better with a plain one.
        return text.Replace(' ', ' ').Replace(' ', ' ');
    }

    /// <summary>Time to the next minute boundary, a little after it so the reading has turned over.</summary>
    public static TimeSpan UntilNextMinute(DateTime now) =>
        TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
}
