// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/QuickTogglesSupport.swift (the quit-all half): which apps the list
// shows, which a batch quits, the buttons on offer, keyboard focus movement and type-to-jump. Windows differences:
// an app is identified by its executable name (upstream uses bundle ids), "regular" means it has a visible
// top-level window, and Explorer stands in for upstream's always-kept Finder.
using System.Globalization;
using System.Text;

namespace Notchly.Core.Tools;

public readonly record struct QuitAppsCandidate(int Pid, string Id, string Name, bool IsRegular, bool IsFront = false);

public enum QuitAppsScope { All, AllExceptFront }

public enum QuitAppsButton { Cancel, AllExceptFront, All }

public enum QuitAppsMove { Up, Down, Left, Right, Next, Previous }

/// <summary>Where keyboard focus is: a row of the list or a button in the bar.</summary>
public readonly record struct QuitAppsFocus(int? Row, QuitAppsButton? Button)
{
    public static QuitAppsFocus OnRow(int row) => new(row, null);
    public static QuitAppsFocus OnButton(QuitAppsButton button) => new(null, button);
}

public static class QuitAppsSupport
{
    /// <summary>Apps that are never quit by a batch, and cannot be taken off the list.</summary>
    public static readonly string[] MandatoryExceptions = ["explorer.exe"];
    /// <summary>Notchly's own ids are never listed and never quit.</summary>
    public static readonly string[] SelfIds = ["notchly.exe"];
    /// <summary>Seconds a row shows its spinner after being asked to quit.</summary>
    public const double SettleSeconds = 4;
    /// <summary>Type-to-jump forgets what was typed after this long.</summary>
    public const double TypeAheadSeconds = 1;

    public static bool IsSelf(string id) => SelfIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The saved exceptions: trimmed, once each, Explorer first, and never Notchly itself.</summary>
    public static IReadOnlyList<string> SanitizedExceptions(IEnumerable<string> saved)
    {
        var result = new List<string>(MandatoryExceptions);
        foreach (var raw in saved)
        {
            var id = raw.Trim();
            if (id.Length == 0 || IsSelf(id) || result.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(id);
        }
        return result;
    }

    public static bool IsKept(string id, IEnumerable<string> exceptions) => exceptions.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>A batch quits regular apps with an id that are not kept and not Notchly.</summary>
    public static bool ShouldQuitApp(QuitAppsCandidate app, IEnumerable<string> exceptions) =>
        app.IsRegular && app.Id.Length > 0 && !IsSelf(app.Id) && !IsKept(app.Id, exceptions);

    /// <summary>Pids a batch quits, leaving out the front app when asked.</summary>
    public static IReadOnlyList<int> Targets(IEnumerable<QuitAppsCandidate> apps, IEnumerable<string> exceptions, int? keepingPid = null)
    {
        var kept = exceptions.ToList();
        return apps.Where(a => ShouldQuitApp(a, kept) && a.Pid != keepingPid).Select(a => a.Pid).ToList();
    }

    /// <summary>
    /// The list shows regular apps with an id. Explorer and Notchly are never listed; a kept app is listed
    /// with a badge, since picking its row is an explicit request.
    /// </summary>
    public static bool ListsApp(QuitAppsCandidate app) =>
        app.IsRegular && app.Id.Length > 0 && !IsSelf(app.Id)
        && !MandatoryExceptions.Contains(app.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The list: the front app first, then the rest by name, ignoring case.</summary>
    public static IReadOnlyList<QuitAppsCandidate> ListedApps(IEnumerable<QuitAppsCandidate> apps) =>
        apps.Where(ListsApp).OrderByDescending(a => a.IsFront).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A row's own quit works even for a kept app; only Notchly itself is refused.</summary>
    public static bool CanQuitRow(string id) => !IsSelf(id);

    // MARK: Buttons

    /// <summary>Cancel is always there; the batch buttons appear only when they would do something.</summary>
    public static IReadOnlyList<QuitAppsButton> Buttons(bool offersAllExceptFront, bool offersAll)
    {
        var buttons = new List<QuitAppsButton> { QuitAppsButton.Cancel };
        if (offersAllExceptFront) buttons.Add(QuitAppsButton.AllExceptFront);
        if (offersAll) buttons.Add(QuitAppsButton.All);
        return buttons;
    }

    /// <summary>"Quit all except the front app" needs a front app with a row, that is not kept, and another to quit.</summary>
    public static bool OffersAllExceptFront(IReadOnlyList<QuitAppsCandidate> listed, IEnumerable<string> exceptions)
    {
        var kept = exceptions.ToList();
        var front = listed.FirstOrDefault(a => a.IsFront);
        return front.Id is { Length: > 0 } && !IsKept(front.Id, kept)
               && listed.Any(a => a.Pid != front.Pid && ShouldQuitApp(a, kept));
    }

    public static bool OffersAll(IReadOnlyList<QuitAppsCandidate> listed, IEnumerable<string> exceptions)
    {
        var kept = exceptions.ToList();
        return listed.Any(a => ShouldQuitApp(a, kept));
    }

    /// <summary>The button Return rests on: the one the entry point asked for, or Cancel when it is not on offer.</summary>
    public static QuitAppsButton RestingButton(QuitAppsScope? scope, IReadOnlyCollection<QuitAppsButton> available)
    {
        var wanted = scope switch { QuitAppsScope.All => QuitAppsButton.All, QuitAppsScope.AllExceptFront => QuitAppsButton.AllExceptFront, _ => QuitAppsButton.Cancel };
        return available.Contains(wanted) ? wanted : QuitAppsButton.Cancel;
    }

    // MARK: Keyboard focus

    /// <summary>Arrows move through the rows and the bar; Tab wraps the bar.</summary>
    public static QuitAppsFocus Move(QuitAppsFocus focus, QuitAppsMove move, int rowCount, IReadOnlyList<QuitAppsButton> buttons, QuitAppsButton resting)
    {
        if (focus.Row is { } row)
        {
            switch (move)
            {
                case QuitAppsMove.Up: return QuitAppsFocus.OnRow(Math.Max(0, row - 1));
                case QuitAppsMove.Down:
                    return row + 1 < rowCount ? QuitAppsFocus.OnRow(row + 1) : QuitAppsFocus.OnButton(resting);
                default: return focus;
            }
        }
        var current = focus.Button ?? resting;
        var index = Math.Max(0, buttons.ToList().IndexOf(current));
        switch (move)
        {
            case QuitAppsMove.Up: return rowCount > 0 ? QuitAppsFocus.OnRow(rowCount - 1) : focus;
            case QuitAppsMove.Left: return QuitAppsFocus.OnButton(buttons[Math.Max(0, index - 1)]);
            case QuitAppsMove.Right: return QuitAppsFocus.OnButton(buttons[Math.Min(buttons.Count - 1, index + 1)]);
            case QuitAppsMove.Next: return QuitAppsFocus.OnButton(buttons[(index + 1) % buttons.Count]);
            case QuitAppsMove.Previous: return QuitAppsFocus.OnButton(buttons[(index - 1 + buttons.Count) % buttons.Count]);
            default: return focus;
        }
    }

    /// <summary>
    /// After the list changes, a row that vanished hands focus to whichever row now sits at its place; with no
    /// rows left, to the resting button.
    /// </summary>
    public static QuitAppsFocus AfterListChange(QuitAppsFocus focus, int rowCount, QuitAppsButton resting)
    {
        if (focus.Row is not { } row) return focus;
        if (rowCount == 0) return QuitAppsFocus.OnButton(resting);
        return QuitAppsFocus.OnRow(Math.Min(row, rowCount - 1));
    }

    // MARK: Type to jump

    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>The first row whose name starts with what was typed, ignoring case and accents.</summary>
    public static int? Jump(string typed, IReadOnlyList<string> names)
    {
        var prefix = Fold(typed.Trim());
        if (prefix.Length == 0) return null;
        for (var i = 0; i < names.Count; i++)
            if (Fold(names[i]).StartsWith(prefix, StringComparison.Ordinal)) return i;
        return null;
    }

    /// <summary>What has been typed so far: it starts over when a second has passed since the last key.</summary>
    public sealed class TypeAhead
    {
        private string _typed = "";
        private double _lastSeconds = double.NegativeInfinity;

        public string Add(char key, double nowSeconds)
        {
            if (nowSeconds - _lastSeconds > TypeAheadSeconds) _typed = "";
            _typed += key;
            _lastSeconds = nowSeconds;
            return _typed;
        }
    }
}
