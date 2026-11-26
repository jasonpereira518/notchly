// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Processes and ports: the Windows counterparts of upstream's UI/KillProcess/KillProcessView.swift and
// UI/PortManager/PortManagerView.swift. The list refreshes every three seconds while the page is open; every kill is
// confirmed and checked against the creation time the row was listed with; a process Notchly may not stop is offered
// one administrator prompt.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Tools;
using Notchly.App.Tools;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string ProcessNote = "Everything running, with helpers folded under their app. Kill asks an app's windows to close; Force Kill ends it at once. " +
        "Windows' own core processes and Notchly are never listed as killable.";
    private const string PortsNote = "Programs listening for connections. Open goes to the address in your browser.";

    private FrameworkElement ProcessesPage()
    {
        var page = Page(PageTitle("Processes and ports"));
        page.Children.Add(Section("Kill process"));
        page.Children.Add(Caption(ProcessNote));
        var kill = app.Features.Get<KillProcessService>(AppFeature.KillProcess);
        if (kill is null) page.Children.Add(Caption("Install Kill process in Features to use it."));
        else page.Children.Add(KillPanel(kill));

        page.Children.Add(Section("Port manager"));
        page.Children.Add(Caption(PortsNote));
        var ports = app.Features.Get<PortManagerService>(AppFeature.PortManager);
        if (ports is null) page.Children.Add(Caption("Install Port manager in Features to use it."));
        else page.Children.Add(PortPanel(ports));
        return page;
    }

    // MARK: Kill process

    private FrameworkElement KillPanel(KillProcessService service)
    {
        var settings = app.Settings;
        var root = new StackPanel();
        var search = new TextBox { PlaceholderText = "Search by name or PID", Margin = new Thickness(0, 0, 0, 6) };
        var header = new Grid { Margin = new Thickness(8, 0, 8, 2) };
        var list = new StackPanel();
        var scroller = new ScrollViewer { MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var width in new[] { 70, 90, 70, 150 }) header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        var columns = new (string Title, KillSort Sort)[] { ("Name", KillSort.Name), ("CPU", KillSort.Cpu), ("Memory", KillSort.Memory), ("PID", KillSort.Pid) };
        for (var i = 0; i < columns.Length; i++)
        {
            var (title, sort) = columns[i];
            var current = KillProcessSupport.ParseSort(settings.GetString(DefaultsKey.KillProcessSortBy));
            var arrow = current == sort ? (settings.GetBool(DefaultsKey.KillProcessSortAscending) ? " ▲" : " ▼") : "";
            var button = new Button { Content = title + arrow, Appearance = ControlAppearance.Transparent, Padding = new Thickness(4, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 11 };
            button.Click += (_, _) => { service.SortBy(sort); Refresh(); };
            Grid.SetColumn(button, i);
            header.Children.Add(button);
        }
        var group = Toggle("Group helpers with their app", null, DefaultsKey.KillProcessGroupRelated, "");
        root.Children.Add(group);
        root.Children.Add(search);
        root.Children.Add(header);
        root.Children.Add(scroller);
        root.Children.Add(status);

        void Rebuild()
        {
            list.Children.Clear();
            var rows = service.Visible(search.Text);
            foreach (var row in rows.Take(300)) list.Children.Add(KillRowView(service, row, Rebuild));
            status.Text = !service.HasLoadedOnce ? "Loading…" : rows.Count == 0 ? "No matching processes." : $"{rows.Count} shown";
        }
        search.TextChanged += (_, _) => Rebuild();
        // A reading takes a few milliseconds of kernel time: off the UI thread, then the list is redrawn.
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        async void Tick()
        {
            await Task.Run(service.Refresh);
            Rebuild();
        }
        timer.Tick += (_, _) => Tick();
        root.Loaded += (_, _) => { Tick(); timer.Start(); };
        root.Unloaded += (_, _) => timer.Stop();
        Rebuild();
        return root;
    }

    private FrameworkElement KillRowView(KillProcessService service, ProcessView row, Action rebuild)
    {
        var grid = new Grid { Margin = new Thickness(8, 3, 8, 3), MinHeight = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var width in new[] { 70, 90, 70, 150 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) };
        if (row.Path is { } path) LoadRowIcon(path, icon);
        name.Children.Add(icon);
        name.Children.Add(new TextBlock { Text = row.Name + (row.Processes > 1 ? $"  ({row.Processes})" : ""), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 });
        grid.Children.Add(name);
        void Cell(string text, int column)
        {
            var block = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
        Cell($"{row.Cpu:0.0}%", 1);
        Cell(Notchly.Core.Monitor.MetricFormat.Bytes((ulong)row.MemoryBytes), 2);
        Cell(row.Pid.ToString(), 3);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (!row.IsProtected)
        {
            var kill = new Button { Content = "Kill", Appearance = ControlAppearance.Secondary, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 0) };
            kill.Click += (_, _) => Stop(service, row, KillProcessSupport.KillKind.Kill, rebuild);
            var more = new Button { Content = new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), FontSize = 12 }, Appearance = ControlAppearance.Transparent, Padding = new Thickness(6) };
            more.Click += (_, _) => ShowKillMenu(more, service, row, rebuild);
            actions.Children.Add(kill);
            actions.Children.Add(more);
        }
        else actions.Children.Add(new TextBlock { Text = "Protected", FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(actions, 4);
        grid.Children.Add(actions);
        return grid;
    }

    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> RowIcons = new(StringComparer.OrdinalIgnoreCase);

    private static async void LoadRowIcon(string path, Image image)
    {
        if (!RowIcons.TryGetValue(path, out var icon)) RowIcons[path] = icon = await ShellThumbnails.GetAsync(path, 36);
        image.Source = icon;
    }

    private void ShowKillMenu(FrameworkElement anchor, KillProcessService service, ProcessView row, Action rebuild)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Item("Force Kill", () => Stop(service, row, KillProcessSupport.KillKind.Force, rebuild));
        Item("Kill all named “" + row.Name + "”", () => Stop(service, row, KillProcessSupport.KillKind.KillAll, rebuild));
        Item("Kill with its child processes", () => Stop(service, row, KillProcessSupport.KillKind.Tree, rebuild));
        menu.Items.Add(new Separator());
        Item("Copy PID", () => Clipboard.SetText(row.Pid.ToString()));
        if (row.Path is { } path) Item("Copy path", () => Clipboard.SetText(path));
        menu.IsOpen = true;
    }

    private async void Stop(KillProcessService service, ProcessView row, KillProcessSupport.KillKind kind, Action rebuild)
    {
        if (MessageBox.Show(OwnerWindow(), kind == KillProcessSupport.KillKind.Force ? "It ends at once and unsaved work is lost." : "Unsaved work may be lost.",
                KillProcessSupport.ConfirmTitle(kind, row.Name), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        var force = kind == KillProcessSupport.KillKind.Force;
        // Everything the kill reaches, each checked against its own creation time.
        var targets = kind switch
        {
            KillProcessSupport.KillKind.Tree => service.TreeOf(row.Pid).Append(row.Pid).ToList(),
            KillProcessSupport.KillKind.KillAll => service.SameName(row.Name).ToList(),
            _ => [row.Pid],
        };
        var denied = new List<(int, long)>();
        await Task.Run(() =>
        {
            foreach (var pid in targets)
            {
                var start = pid == row.Pid ? row.StartTime : ProcessTools.StartTime(pid);
                if (KillProcessSupport.IsProtected(pid, service.NameOf(pid) ?? "", Environment.ProcessId, start is not null)) continue;
                if (ProcessTools.Stop(pid, start, force || kind != KillProcessSupport.KillKind.Kill) == ProcessTools.Outcome.AccessDenied && start is { } s)
                    denied.Add((pid, s));
            }
        });
        // One administrator prompt for everything that was refused.
        if (denied.Count > 0 && MessageBox.Show(OwnerWindow(), "Windows needs administrator rights to stop this.",
                "Stop with administrator rights?", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await Task.Run(() => ProcessTools.StopElevated(denied));
        await Task.Delay(500);
        await Task.Run(service.Refresh);
        rebuild();
    }

    private static Window? OwnerWindow() => Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();

    // MARK: Port manager

    private FrameworkElement PortPanel(PortManagerService service)
    {
        var root = new StackPanel();
        var top = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filter = new TextBox { PlaceholderText = "Filter by port, process or PID" };
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(6, 0, 0, 0), Appearance = ControlAppearance.Secondary };
        Grid.SetColumn(refresh, 1);
        top.Children.Add(filter);
        top.Children.Add(refresh);
        var list = new StackPanel();
        var scroller = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(top);
        root.Children.Add(scroller);
        root.Children.Add(status);

        void Rebuild()
        {
            list.Children.Clear();
            var rows = service.Filtered(filter.Text);
            foreach (var entry in rows) list.Children.Add(PortRow(service, entry, () => _ = service.RefreshAsync()));
            status.Text = service.RefreshFailed
                ? "The ports could not be read just now" + (service.HasLoadedOnce ? "; the last list is shown." : ".")
                : !service.HasLoadedOnce ? "Loading…" : rows.Count == 0 ? "Nothing is listening." : $"{rows.Count} listening";
        }
        filter.TextChanged += (_, _) => Rebuild();
        refresh.Click += async (_, _) => await service.RefreshAsync();
        void OnChanged() => root.Dispatcher.BeginInvoke(Rebuild);
        root.Loaded += async (_, _) => { service.Changed += OnChanged; Rebuild(); await service.RefreshAsync(); };
        root.Unloaded += (_, _) => service.Changed -= OnChanged;
        return root;
    }

    private FrameworkElement PortRow(PortManagerService service, PortEntry entry, Action refresh)
    {
        var grid = new Grid { Margin = new Thickness(8, 3, 8, 3), MinHeight = 30 };
        foreach (var width in new[] { 60, 190 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(string text, int column, bool dim = false)
        {
            var block = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = dim ? 0.65 : 1, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
        Cell(entry.Port.ToString(), 0);
        Cell(entry.Address + (PortManagerSupport.ListensOnAllInterfaces(entry.Address) ? "  🌐" : ""), 1, dim: true);
        Cell($"{entry.ProcessName}  (PID {entry.Pid})", 2);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (PortManagerSupport.BrowserUrl(entry.Protocol, entry.Address, entry.Port) is { } url)
        {
            var open = new Button { Content = "Open", Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 3, 8, 3) };
            open.Click += (_, _) => Platform.Shell.ShellActions.Open(url);
            actions.Children.Add(open);
        }
        var canStop = service.CanTerminate && entry.StartedAt is not null && !KillProcessSupport.IsProtected(entry.Pid, entry.ProcessName, Environment.ProcessId, true);
        var stop = new Button { Content = "Stop", Appearance = ControlAppearance.Secondary, Padding = new Thickness(10, 3, 10, 3), IsEnabled = canStop, Margin = new Thickness(4, 0, 0, 0),
            ToolTip = canStop ? null : service.CanTerminate ? "This process cannot be stopped from here." : "Install Kill process in Features to stop programs from here." };
        stop.Click += async (_, _) =>
        {
            if (MessageBox.Show($"This closes port {entry.Port} by ending {entry.ProcessName} (PID {entry.Pid}).", $"End {entry.ProcessName}?", MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            var outcome = await Task.Run(() => service.Terminate(entry, force: false));
            if (outcome == ProcessTools.Outcome.AccessDenied && MessageBox.Show("Windows needs administrator rights to end this.", "Stop with administrator rights?",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK && entry.StartedAt is { } start)
                await Task.Run(() => ProcessTools.StopElevated([(entry.Pid, start)]));
            await Task.Delay(500);
            refresh();
        };
        actions.Children.Add(stop);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);
        return grid;
    }
}
