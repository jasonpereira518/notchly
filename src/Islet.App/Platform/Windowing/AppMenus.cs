// SPDX-License-Identifier: GPL-3.0-or-later
// An app's own menus, from the two places Windows keeps them:
//   • a classic Win32 menu bar (GetMenu): read across processes, refreshed with WM_INITMENUPOPUP the way
//     the app's own menu loop asks for it, and chosen with WM_COMMAND, exactly what its menu would send;
//   • a UI Automation menu bar (WinUI Notepad, VS Code, WPF, WinForms): titles read on a worker thread
//     within a budget, and opened with ExpandCollapse/Invoke, so the app shows its own menu.
// The "System" bar every window exposes to UI Automation is the window's system menu, not the app's.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Islet.Core.MenuBar;
using Serilog;
using static Islet.App.Platform.Native;

namespace Islet.App.Platform.Windowing;

public sealed class AppMenus : IDisposable
{
    private const int MaxDepth = 4;
    private const int MaxItems = 200;
    private static readonly TimeSpan AutomationBudget = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _worker;
    private readonly ConcurrentDictionary<IntPtr, (DateTime At, IReadOnlyList<AutomationElement> Items)> _automation = new();

    public AppMenus()
    {
        // UI Automation clients belong off the UI thread, which also answers UI Automation for Islet's own windows.
        _worker = new Thread(() => { foreach (var job in _work.GetConsumingEnumerable()) job(); })
        {
            IsBackground = true, Name = "Islet menu bar reader",
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public void Dispose() => _work.CompleteAdding();

    // MARK: Classic Win32 menus

    /// <summary>The titles of a classic menu bar, or null when the window has none.</summary>
    public static IReadOnlyList<MenuTitle>? NativeTitles(IntPtr window)
    {
        var menu = GetMenu(window);
        if (menu == IntPtr.Zero || !IsMenu(menu)) return null;
        var titles = new List<MenuTitle>();
        var count = Math.Min(GetMenuItemCount(menu), 40);
        for (var i = 0; i < count; i++)
        {
            if (Item(menu, i) is not { } item || item.Separator || item.Text.Length == 0) continue;
            titles.Add(new MenuTitle(StandardMenus.SplitMenuText(item.Text).Title, MenuSource.Native, i));
        }
        return titles.Count > 0 ? titles : null;
    }

    /// <summary>
    /// The items under a classic menu title, after asking the app to update them (enabled, checked,
    /// recent files) as its own menu loop would. Nested submenus are read the same way.
    /// </summary>
    public static IReadOnlyList<MenuNode> NativeItems(IntPtr window, int index)
    {
        var menu = GetMenu(window);
        if (menu == IntPtr.Zero) return [];
        var sub = GetSubMenu(menu, index);
        if (sub == IntPtr.Zero) return [];
        Ask(window, WM_INITMENU, menu, IntPtr.Zero);
        var budget = MaxItems;
        return ReadItems(window, sub, index, 0, ref budget);
    }

    private static IReadOnlyList<MenuNode> ReadItems(IntPtr window, IntPtr menu, int position, int depth, ref int budget)
    {
        Ask(window, WM_INITMENUPOPUP, menu, (IntPtr)position);
        var nodes = new List<MenuNode>();
        var count = GetMenuItemCount(menu);
        for (var i = 0; i < count && budget > 0; i++, budget--)
        {
            if (Item(menu, i) is not { } item) continue;
            if (item.Separator)
            {
                if (nodes.Count > 0 && nodes[^1].Kind != MenuNodeKind.Separator) nodes.Add(MenuNode.Separator);
                continue;
            }
            if (item.Text.Length == 0) continue; // owner-drawn or picture items have nothing to show
            var (title, shortcut) = StandardMenus.SplitMenuText(item.Text);
            if (item.SubMenu != IntPtr.Zero)
            {
                var children = depth + 1 < MaxDepth ? ReadItems(window, item.SubMenu, i, depth + 1, ref budget) : [];
                nodes.Add(new MenuNode(title, MenuNodeKind.Submenu) { Children = children, Enabled = !item.Disabled && children.Count > 0 });
            }
            else
            {
                nodes.Add(new MenuNode(title)
                {
                    Command = new MenuCommand.NativeItem((int)item.Id), Shortcut = shortcut, Enabled = !item.Disabled, Checked = item.Checked,
                });
            }
        }
        if (nodes.Count > 0 && nodes[^1].Kind == MenuNodeKind.Separator) nodes.RemoveAt(nodes.Count - 1);
        return nodes;
    }

    /// <summary>Chooses a classic menu item: the same WM_COMMAND the app's own menu posts.</summary>
    public static void Choose(IntPtr window, int id) => PostMessage(window, WM_COMMAND, (IntPtr)(id & 0xFFFF), IntPtr.Zero);

    private static void Ask(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) =>
        SendMessageTimeout(window, message, wParam, lParam, SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, out _);

    private sealed record RawItem(string Text, bool Separator, bool Disabled, bool Checked, uint Id, IntPtr SubMenu);

    private static RawItem? Item(IntPtr menu, int position)
    {
        var buffer = Marshal.AllocHGlobal(512 * sizeof(char));
        try
        {
            var info = new MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_STRING,
                dwTypeData = buffer, cch = 511,
            };
            if (!GetMenuItemInfo(menu, (uint)position, true, ref info)) return null;
            var separator = (info.fType & MFT_SEPARATOR) != 0;
            var picture = (info.fType & (MFT_OWNERDRAW | MFT_BITMAP)) != 0;
            var text = separator || picture ? "" : Marshal.PtrToStringUni(buffer, (int)Math.Min(info.cch, 511)) ?? "";
            return new RawItem(text, separator, (info.fState & MFS_DISABLED) != 0, (info.fState & MFS_CHECKED) != 0, info.wID, info.hSubMenu);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // MARK: UI Automation menu bars

    /// <summary>The titles of the window's UI Automation menu bar, or null when it has none or took too long.</summary>
    public async Task<IReadOnlyList<MenuTitle>?> AutomationTitlesAsync(IntPtr window)
    {
        if (_automation.TryGetValue(window, out var cached) && DateTime.UtcNow - cached.At < CacheLifetime)
            return Titles(cached.Items);
        var job = new TaskCompletionSource<IReadOnlyList<AutomationElement>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryQueue(() =>
        {
            try { job.TrySetResult(FindMenuBarItems(window)); }
            catch (Exception e) { Log.Debug(e, "Menu bar read failed"); job.TrySetResult(null); }
        })) return null;
        var started = DateTime.UtcNow;
        if (await Task.WhenAny(job.Task, Task.Delay(AutomationBudget)) != job.Task)
        {
            Log.Debug("Menu bar read took longer than {Budget} ms", AutomationBudget.TotalMilliseconds);
            return null;
        }
        var items = job.Task.Result;
        Log.Debug("Menu bar read in {Ms} ms ({Count} titles)", (int)(DateTime.UtcNow - started).TotalMilliseconds, items?.Count ?? 0);
        if (items is null) return null;
        _automation[window] = (DateTime.UtcNow, items);
        return Titles(items);
    }

    private static IReadOnlyList<MenuTitle>? Titles(IReadOnlyList<AutomationElement> items)
    {
        var titles = new List<MenuTitle>();
        for (var i = 0; i < items.Count; i++)
        {
            string name;
            try { name = items[i].Current.Name; }
            catch (ElementNotAvailableException) { return null; }
            if (!string.IsNullOrWhiteSpace(name)) titles.Add(new MenuTitle(StandardMenus.SplitMenuText(name).Title, MenuSource.Automation, i));
        }
        return titles.Count > 0 ? titles : null;
    }

    private static IReadOnlyList<AutomationElement>? FindMenuBarItems(IntPtr window)
    {
        var root = AutomationElement.FromHandle(window);
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuBar),
            new NotCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemMenuBar")));
        var bar = root.FindFirst(TreeScope.Descendants, condition);
        if (bar is null || ForegroundRules.IsSystemMenuBar(bar.Current.AutomationId)) return null;
        var items = bar.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
        return items.Count == 0 ? null : items.Cast<AutomationElement>().ToList();
    }

    /// <summary>Opens the app's own menu under one of its UI Automation titles.</summary>
    public void Expand(IntPtr window, int index)
    {
        if (!_automation.TryGetValue(window, out var cached) || index < 0 || index >= cached.Items.Count) return;
        var element = cached.Items[index];
        TryQueue(() =>
        {
            try
            {
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) ((ExpandCollapsePattern)expand).Expand();
                else if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
            }
            catch (Exception e) { Log.Debug(e, "Could not open the app's menu"); }
        });
    }

    public void Forget(IntPtr window) => _automation.TryRemove(window, out _);

    private bool TryQueue(Action job)
    {
        // Only the latest request matters: a slow app must not leave a backlog behind it.
        while (_work.Count > 0 && _work.TryTake(out _)) { }
        try { return _work.TryAdd(job); }
        catch (InvalidOperationException) { return false; }
    }
}
