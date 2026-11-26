// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the platform-free parts of upstream Sources/Vorssaint/Services/QuickTools/QuickLauncherService.swift: the
// model (selection, edit mode, hosted utility, presentation), run, handlePanelKey, moveSelection, select and
// clampSelection. The panel, monitors and hotkey stay in the app.
using System.ComponentModel;
using Notchly.Core.Settings;

namespace Notchly.Core.Launcher;

/// <summary>What the launcher needs from the app: the window, the clock, feature availability and the tools.</summary>
public interface IQuickLauncherHost
{
    /// <summary>Hides the launcher's window (and, in the island, collapses the Tools page).</summary>
    void Hide();

    /// <summary>Runs <paramref name="action"/> once, after <paramref name="delaySeconds"/>, on the UI thread.</summary>
    void Schedule(double delaySeconds, Action action);

    /// <summary>
    /// Whether the tile's hub feature is installed (and exists on this platform). An unavailable tile leaves the
    /// grid, the hidden list and edit mode, and cannot run.
    /// </summary>
    bool IsAvailable(QuickLauncherItem item);

    /// <summary>
    /// Runs one tile action. The return value matters only for <see cref="QuickLauncherAction.CameraShowInIsland"/>:
    /// true when the camera went into the island (the launcher then hides with no delayed window); every other
    /// action may return true.
    /// </summary>
    bool Perform(QuickLauncherAction action);
}

/// <summary>Keys the launcher's panel reacts to. Map Windows keys with <see cref="QuickLauncherKeys.FromVirtualKey"/>.</summary>
public enum QuickLauncherKey
{
    Other, Escape, Return, Left, Right, Up, Down,
    Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9,
}

[Flags]
public enum QuickLauncherModifiers { None = 0, Control = 1, Alt = 2, Windows = 4, Shift = 8 }

/// <param name="Composing">An input method is composing in the focused text field, so Esc belongs to it.</param>
public readonly record struct QuickLauncherKeyEvent(QuickLauncherKey Key,
                                                    QuickLauncherModifiers Modifiers = QuickLauncherModifiers.None,
                                                    bool Composing = false);

public static class QuickLauncherKeys
{
    /// <summary>
    /// Windows virtual-key codes: Return and keypad Enter (both VK_RETURN), Esc, the arrows, and the top-row and
    /// keypad digits 1 to 9. Anything else is <see cref="QuickLauncherKey.Other"/>.
    /// </summary>
    public static QuickLauncherKey FromVirtualKey(int vk) => vk switch
    {
        0x0D => QuickLauncherKey.Return,
        0x1B => QuickLauncherKey.Escape,
        0x25 => QuickLauncherKey.Left,
        0x26 => QuickLauncherKey.Up,
        0x27 => QuickLauncherKey.Right,
        0x28 => QuickLauncherKey.Down,
        >= 0x31 and <= 0x39 => QuickLauncherKey.Digit1 + (vk - 0x31),
        >= 0x61 and <= 0x69 => QuickLauncherKey.Digit1 + (vk - 0x61),
        _ => QuickLauncherKey.Other,
    };

    /// <summary>The tile index a digit key selects, or null.</summary>
    public static int? DigitIndex(QuickLauncherKey key) =>
        key >= QuickLauncherKey.Digit1 && key <= QuickLauncherKey.Digit9 ? key - QuickLauncherKey.Digit1 : null;
}

/// <summary>
/// The quick launcher's state and rules. Items can be hidden, brought back and reordered; the order and hidden set
/// live in <see cref="DefaultsKey.QuickLauncherItemOrder"/> and <see cref="DefaultsKey.QuickLauncherHiddenItems"/>.
/// </summary>
public sealed class QuickLauncherModel : INotifyPropertyChanged
{
    private readonly IQuickLauncherHost _host;
    private readonly SettingsStore _store;
    private bool _isEditing;
    private QuickLauncherItem? _editingOptionsItem;
    private QuickLauncherItem? _activeUtility;
    private int? _selectedIndex;
    private int? _keyboardIndex;
    private Guid _presentationId = Guid.NewGuid();

    public QuickLauncherModel(IQuickLauncherHost host, SettingsStore store)
    {
        _host = host;
        _store = store;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value, nameof(IsEditing)); }

    /// <summary>
    /// The tile whose inline options card is open in edit mode. Lives here so the Esc key can close the card first,
    /// before leaving edit mode and before hiding the panel.
    /// </summary>
    public QuickLauncherItem? EditingOptionsItem
    {
        get => _editingOptionsItem;
        set => Set(ref _editingOptionsItem, value, nameof(EditingOptionsItem));
    }

    /// <summary>A utility view (Homebrew, Uninstaller...) hosted inside the launcher, replacing the grid.</summary>
    public QuickLauncherItem? ActiveUtility
    {
        get => _activeUtility;
        set => Set(ref _activeUtility, value, nameof(ActiveUtility));
    }

    public int? SelectedIndex { get => _selectedIndex; private set => Set(ref _selectedIndex, value, nameof(SelectedIndex)); }

    /// <summary>
    /// Where the keyboard moved the selection, which the island's rail scrolls to. Hover clears it: centering a
    /// hovered tile slid the next one under the pointer, and the rail kept scrolling by itself.
    /// </summary>
    public int? KeyboardIndex { get => _keyboardIndex; private set => Set(ref _keyboardIndex, value, nameof(KeyboardIndex)); }

    /// <summary>Changes with every presentation, so views can reset what they keep between showings.</summary>
    public Guid PresentationId { get => _presentationId; private set => Set(ref _presentationId, value, nameof(PresentationId)); }

    /// <summary>The raw comma-joined hidden ids as stored.</summary>
    public string HiddenItemsRaw => _store.GetString(DefaultsKey.QuickLauncherHiddenItems);

    // MARK: Items

    /// <summary>Every available tile in the user's order, hidden ones included.</summary>
    public IReadOnlyList<QuickLauncherItem> OrderedItems =>
        [.. QuickToolsSupport.OrderedItems(_store.GetString(DefaultsKey.QuickLauncherItemOrder)).Where(_host.IsAvailable)];

    public IReadOnlyList<QuickLauncherItem> VisibleItems
    {
        get
        {
            var hidden = QuickToolsSupport.HiddenIds(HiddenItemsRaw);
            return [.. OrderedItems.Where(i => !hidden.Contains(i.Id()))];
        }
    }

    public IReadOnlyList<QuickLauncherItem> HiddenItems
    {
        get
        {
            var hidden = QuickToolsSupport.HiddenIds(HiddenItemsRaw);
            return [.. OrderedItems.Where(i => hidden.Contains(i.Id()))];
        }
    }

    /// <summary>Saves a new order (the drag-to-reorder result). Tiles left out rejoin at the end on the next read.</summary>
    public void SetItemOrder(IEnumerable<QuickLauncherItem> items)
    {
        _store.Set(DefaultsKey.QuickLauncherItemOrder, QuickToolsSupport.SerializeItemOrder(items));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrderedItems)));
        ClampSelection();
    }

    public void SetHidden(QuickLauncherItem item, bool hidden)
    {
        var ids = QuickToolsSupport.HiddenIds(HiddenItemsRaw);
        if (hidden)
        {
            ids.Add(item.Id());
            // Hiding the tile whose options card is open would orphan the card below a grid that no longer shows its owner.
            if (EditingOptionsItem == item) EditingOptionsItem = null;
        }
        else
        {
            ids.Remove(item.Id());
        }
        _store.Set(DefaultsKey.QuickLauncherHiddenItems, QuickToolsSupport.SerializeHiddenIds(ids));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HiddenItemsRaw)));
        ClampSelection();
    }

    // MARK: Presentation

    /// <summary>
    /// Both destinations (the floating panel and the island) start with usable keyboard navigation. A utility that
    /// is still installed keeps its working state when the island is reopened.
    /// </summary>
    public void PrepareForPresentation()
    {
        RefreshAvailability();
        PresentationId = Guid.NewGuid();
        IsEditing = false;
        EditingOptionsItem = null;
        SelectedIndex = VisibleItems.Count == 0 ? null : 0;
        KeyboardIndex = SelectedIndex;
    }

    public void RefreshAvailability()
    {
        if (ActiveUtility is { } utility && !_host.IsAvailable(utility)) ActiveUtility = null;
        if (EditingOptionsItem is { } options && !_host.IsAvailable(options)) EditingOptionsItem = null;
        ClampSelection();
    }

    /// <summary>Resets edit state and the hosted utility, then asks the host to hide the window.</summary>
    public void Hide()
    {
        IsEditing = false;
        EditingOptionsItem = null;
        ActiveUtility = null;
        _host.Hide();
    }

    public void CloseUtility() => ActiveUtility = null;

    /// <summary>
    /// The single owner of the dismissal policy: the bare grid behaves like a transient HUD, but a utility hosted
    /// inside the launcher, or edit mode (its options can raise an admin prompt), is a working window that must
    /// survive its own dialogs and clicks in other apps. Every dismissal trigger consults this.
    /// </summary>
    public bool DismissesOnOutsideInteraction => ActiveUtility is null && !IsEditing;

    // MARK: Actions

    public void ActivateSelection()
    {
        if (SelectedIndex is { } index && index >= 0 && index < VisibleItems.Count) Run(VisibleItems[index]);
    }

    public void Activate(int index)
    {
        var items = VisibleItems;
        if (index >= 0 && index < items.Count) Run(items[index]);
    }

    public void MoveSelection(GridDirection direction, GridFlow? flow = null)
    {
        var count = VisibleItems.Count;
        if (count == 0) return;
        SelectedIndex = QuickToolsSupport.GridIndex(SelectedIndex ?? 0, count, flow ?? GridFlow.ByRows(QuickLauncherLayout.Columns), direction);
        KeyboardIndex = SelectedIndex;
    }

    /// <summary>The pointer's selection. The hovered tile is already in view, so the rail stays where it is.</summary>
    public void Select(QuickLauncherItem item)
    {
        var index = VisibleItems.ToList().IndexOf(item);
        SelectedIndex = index < 0 ? null : index;
        KeyboardIndex = null;
    }

    public void Run(QuickLauncherItem item)
    {
        if (IsEditing || !_host.IsAvailable(item)) return;
        var activation = item.Activation();
        if (activation.Action is not { } action)
        {
            ActiveUtility = item;
            return;
        }
        if (action == QuickLauncherAction.CameraShow)
        {
            // An embedded camera switches the island's page before the launcher hides: no close-and-reopen delay.
            var embedded = _host.Perform(QuickLauncherAction.CameraShowInIsland);
            Hide();
            if (!embedded) _host.Schedule(activation.DelaySeconds, () => _host.Perform(QuickLauncherAction.CameraShow));
        }
        else if (activation.HidesFirst)
        {
            Hide();
            _host.Schedule(activation.DelaySeconds, () => _host.Perform(action));
        }
        else
        {
            _host.Perform(action);
        }
    }

    private void ClampSelection()
    {
        var count = VisibleItems.Count;
        SelectedIndex = count == 0 ? null : Math.Min(SelectedIndex ?? 0, count - 1);
    }

    // MARK: Keys

    /// <summary>
    /// Handles a key pressed in the panel. Returns true when the launcher consumed it, false when it should pass on
    /// (upstream returns the event).
    /// </summary>
    public bool HandlePanelKey(QuickLauncherKeyEvent e, GridFlow? flow = null)
    {
        if (e.Key == QuickLauncherKey.Escape)
        {
            // While an input method is composing in a utility's field, Esc belongs to it and drops the candidate;
            // the launcher takes the next one.
            if (e.Composing) return false;
            if (ActiveUtility is not null) ActiveUtility = null;
            // An open options card closes first; a second Esc then leaves edit mode, and a third hides the panel.
            else if (EditingOptionsItem is not null) EditingOptionsItem = null;
            else if (IsEditing) IsEditing = false;
            else Hide();
            return true;
        }
        if (IsEditing || ActiveUtility is not null) return false;
        if ((e.Modifiers & (QuickLauncherModifiers.Control | QuickLauncherModifiers.Alt | QuickLauncherModifiers.Windows)) != 0) return false;
        switch (e.Key)
        {
            case QuickLauncherKey.Return: ActivateSelection(); return true;
            case QuickLauncherKey.Left: MoveSelection(GridDirection.Left, flow); return true;
            case QuickLauncherKey.Right: MoveSelection(GridDirection.Right, flow); return true;
            case QuickLauncherKey.Up: MoveSelection(GridDirection.Up, flow); return true;
            case QuickLauncherKey.Down: MoveSelection(GridDirection.Down, flow); return true;
            default:
                if (QuickLauncherKeys.DigitIndex(e.Key) is { } index)
                {
                    Activate(index);
                    return true;
                }
                return false;
        }
    }
}
