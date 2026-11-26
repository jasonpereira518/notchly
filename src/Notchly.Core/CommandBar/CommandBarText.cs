// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/CommandBar/CommandBarSupport.swift: CommandBarText, CommandBarHome,
// CommandBarMenuPath, CommandBarClipboardAccess, CommandBarPresentationLifecycle, CommandBarDeferredRowShortcut
// and CommandBarCompletion.
namespace Notchly.Core.CommandBar;

public static class CommandBarClipboardAccess
{
    public static bool CanUseHistory(bool captureEnabled, bool hasSavedItems) => captureEnabled || hasSavedItems;
}

public static class CommandBarMenuPath
{
    /// <summary>
    /// The trail to a menu command, from the app name down. An app carries a menu named after itself, so a name is
    /// never repeated right after itself.
    /// </summary>
    public static string Crumb(string appName, IEnumerable<string> path)
    {
        var trail = new List<string>();
        foreach (var name in new[] { appName }.Concat(path))
        {
            if (name.Length == 0 || (trail.Count > 0 && trail[^1] == name)) continue;
            trail.Add(name);
        }
        return string.Join(" › ", trail);
    }
}

/// <summary>What an empty field shows. Pure, because the ranking, the panel and the tests all need the same answer.</summary>
public static class CommandBarHome
{
    /// <summary>
    /// Whether an empty field still draws the browse list and its chips. A category is an explicit drill-in and
    /// always shows its rows; a peek is the person asking for the list anyway.
    /// </summary>
    public static bool ShowsBrowseList(bool compact, bool hasCategory, bool isPeeking) => hasCategory || isPeeking || !compact;

    /// <summary>Whether the panel is the field alone, with no divider, list or footer.</summary>
    public static bool IsCollapsed(bool compact, string query, bool hasCategory, bool isPeeking)
    {
        if (!compact || hasCategory || isPeeking) return false;
        return CommandBarSearch.TrimWhitespaces(query).Length == 0;
    }
}

/// <summary>The small text jobs the bar offers for whatever is selected.</summary>
public static class CommandBarText
{
    /// <summary>Words the way a person counts them: runs of anything that is not whitespace.</summary>
    public static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>User-perceived characters: an accented letter is one, however it is encoded.</summary>
    public static int CharacterCount(string text) => CommandBarSearch.ElementCount(text);

    /// <summary>A one-line stand-in for the selection. Newlines become spaces; a long one is cut with an ellipsis.</summary>
    public static string Preview(string text, int limit = 44)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var elements = CommandBarSearch.Elements(flat);
        if (elements.Length <= limit) return flat;
        return CommandBarSearch.TrimWhitespaces(string.Concat(elements.Take(limit))) + "…";
    }

    /// <summary>Whether changing the case of this text would change anything at all. A row that visibly does nothing is worse than no row.</summary>
    public static bool ChangesCase(string text, Func<string, string> transform) => transform(text) != text;
}

/// <summary>
/// The bar can be visible before home has finished preparing, but only the presentation that asked for that work
/// may receive it.
/// </summary>
public sealed class CommandBarPresentationLifecycle
{
    public enum SurfaceKind { Hidden, LoadingHome, Home }

    public readonly record struct SurfaceState(SurfaceKind Kind, Guid Id);

    public SurfaceState Surface { get; private set; } = new(SurfaceKind.Hidden, Guid.Empty);

    public bool IsLoadingHome => Surface.Kind == SurfaceKind.LoadingHome;

    public void BeginHome(Guid id) => Surface = new(SurfaceKind.LoadingHome, id);

    public void Hide() => Surface = new(SurfaceKind.Hidden, Guid.Empty);

    public bool AcceptsHomeHydration(Guid id, bool isVisible) => isVisible && Surface == new SurfaceState(SurfaceKind.LoadingHome, id);

    public bool AcceptsHomeUpdates(Guid id, bool isVisible) => isVisible && Surface == new SurfaceState(SurfaceKind.Home, id);

    /// <summary>
    /// A shared cache is useful to whichever Home is current when its work finishes, even when another presentation
    /// started that work. A hidden panel still rejects the accompanying UI refresh.
    /// </summary>
    public bool AcceptsSharedCacheCompletion(Guid startedBy, Guid currentId, bool isVisible) => AcceptsHomeUpdates(currentId, isVisible);

    public bool CompleteHomeHydration(Guid id, bool isVisible)
    {
        if (!AcceptsHomeHydration(id, isVisible)) return false;
        Surface = new(SurfaceKind.Home, id);
        return true;
    }
}

/// <summary>
/// A shortcut that needs the panel waits for Home's deferred hydration before it enters confirmation, argument or
/// setup mode. Its presentation id keeps a close or newer opening from running yesterday's request.
/// </summary>
public sealed class CommandBarDeferredRowShortcut
{
    private (Guid PresentationId, string StableKey)? _pending;

    public void Schedule(string stableKey, Guid presentationId) => _pending = (presentationId, stableKey);

    public void Cancel() => _pending = null;

    public string? Key(Guid presentationId) => _pending is { } p && p.PresentationId == presentationId ? p.StableKey : null;

    public string? Take(Guid presentationId)
    {
        if (_pending is not { } p || p.PresentationId != presentationId) return null;
        _pending = null;
        return p.StableKey;
    }
}

/// <summary>Tab completion: what the field holds after it and what the learning remembers.</summary>
public static class CommandBarCompletion
{
    public static string CompletedQuery(string current, string title, string? matchTitle) =>
        CommandBarSearch.EmojiQuery(current) is not null ? ":" + (matchTitle ?? title) : matchTitle ?? title;

    public static string QueryForLearning(string current, string? beforeCompletion) => beforeCompletion ?? current;

    public static string? RetainedOriginal(string? original, string? completedValue, string afterChangingTo) =>
        afterChangingTo == completedValue ? original : null;
}
