// SPDX-License-Identifier: GPL-3.0-or-later
// The list of open apps with quit buttons: the Windows counterpart of upstream's QuitAppsPanelController. Rows show the
// front app first with its icon, a "Kept" badge for apps a batch leaves alone and a spinner for four seconds after an
// app was asked to quit. Keys: arrows and Tab move, Return acts, Delete quits the row, Ctrl+Return quits all,
// Alt+Return quits all except the front app, Esc closes, and typing jumps to an app by name.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Tools;
using Notchly.App.Tools;
using Notchly.Core.Tools;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal sealed class QuitAppsWindow : FluentWindow
{
    private const int RowHeight = 42, VisibleRows = 8;

    private readonly QuitAppsService _service;
    private readonly QuitAppsScope? _scope;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _empty = new() { Text = "No other apps are open.", Opacity = 0.7, Margin = new Thickness(4, 8, 0, 8) };
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, DateTime> _settling = [];
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly QuitAppsSupport.TypeAhead _typed = new();
    private IReadOnlyList<OpenApp> _apps = [];
    private IReadOnlyList<QuitAppsButton> _buttons = [QuitAppsButton.Cancel];
    private QuitAppsButton _resting = QuitAppsButton.Cancel;
    private QuitAppsFocus _focus = QuitAppsFocus.OnButton(QuitAppsButton.Cancel);

    public QuitAppsWindow(QuitAppsService service, QuitAppsScope? scope)
    {
        _service = service;
        _scope = scope;
        Title = "Quit apps";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Acrylic;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = RowHeight * VisibleRows, Content = _rows };
        var root = new StackPanel { Margin = new Thickness(20, 30, 20, 16) };
        root.Children.Add(new TextBlock { Text = "Quit apps", FontSize = 18, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "Apps ask about unsaved work before they close.", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 10) });
        root.Children.Add(_scroller);
        root.Children.Add(_empty);
        root.Children.Add(_bar);
        Content = root;
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnText;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Reload();
        Loaded += (_, _) => { Reload(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    // MARK: Content

    private void Reload()
    {
        var apps = _service.Apps().OrderByDescending(a => a.IsFront).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var now = DateTime.Now;
        // An app that is gone stops showing its spinner; one that is still there after four seconds gets its button back.
        foreach (var key in _settling.Keys.ToList())
            if (apps.All(a => a.Key != key) || _settling[key] <= now) _settling.Remove(key);
        var changed = apps.Count != _apps.Count || apps.Zip(_apps).Any(p => p.First.Key != p.Second.Key || p.First.IsFront != p.Second.IsFront);
        _apps = apps;
        var listed = apps.Select(a => a.Candidate).ToList();
        var exceptions = _service.Exceptions;
        _buttons = QuitAppsSupport.Buttons(QuitAppsSupport.OffersAllExceptFront(listed, exceptions), QuitAppsSupport.OffersAll(listed, exceptions));
        _resting = QuitAppsSupport.RestingButton(_scope, _buttons);
        if (changed || _rows.Children.Count == 0) _focus = _rows.Children.Count == 0 ? QuitAppsFocus.OnButton(_resting) : QuitAppsSupport.AfterListChange(_focus, apps.Count, _resting);
        if (_focus.Button is { } button && !_buttons.Contains(button)) _focus = QuitAppsFocus.OnButton(_resting);
        Render();
    }

    private void Render()
    {
        _rows.Children.Clear();
        _empty.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroller.Visibility = _apps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var exceptions = _service.Exceptions;
        for (var i = 0; i < _apps.Count; i++) _rows.Children.Add(Row(_apps[i], i, exceptions));
        _bar.Children.Clear();
        foreach (var button in _buttons) _bar.Children.Add(BarButton(button));
        if (_focus.Row is { } row && row < _rows.Children.Count) ((FrameworkElement)_rows.Children[row]).BringIntoView();
    }

    private FrameworkElement Row(OpenApp app, int index, IReadOnlyList<string> exceptions)
    {
        var focused = _focus.Row == index;
        var grid = new Grid { Height = RowHeight, Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 24, Height = 24, Margin = new Thickness(8, 0, 10, 0) };
        if (app.ExePath is { } path) LoadIcon(path, icon);
        grid.Children.Add(icon);
        var names = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = app.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center });
        if (app.IsFront) names.Children.Add(Badge("Front"));
        if (QuitAppsSupport.IsKept(app.Id, exceptions)) names.Children.Add(Badge("Kept"));
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);
        FrameworkElement action;
        if (_settling.ContainsKey(app.Key)) action = new ProgressRing { IsIndeterminate = true, Width = 16, Height = 16, Margin = new Thickness(0, 0, 12, 0) };
        else
        {
            var quit = new Button { Content = "Quit", Appearance = ControlAppearance.Secondary, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
            quit.Click += (_, _) => QuitRow(app);
            action = quit;
        }
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        var border = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(8), Cursor = Cursors.Arrow,
            Background = focused ? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)) : Brushes.Transparent,
        };
        border.MouseLeftButtonDown += (_, _) => { _focus = QuitAppsFocus.OnRow(index); Render(); };
        return border;
    }

    private static Border Badge(string text) => new()
    {
        Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), Child = new TextBlock { Text = text, FontSize = 10, Opacity = 0.8 },
    };

    private Button BarButton(QuitAppsButton kind)
    {
        var (text, primary) = kind switch
        {
            QuitAppsButton.All => ("Quit all", true),
            QuitAppsButton.AllExceptFront => ("Quit all except " + (_apps.FirstOrDefault(a => a.IsFront)?.Name ?? "the front app"), false),
            _ => ("Cancel", false),
        };
        var focused = _focus.Button == kind;
        var button = new Button
        {
            Content = text, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6),
            Appearance = primary || focused ? ControlAppearance.Primary : ControlAppearance.Secondary,
        };
        button.Click += (_, _) => Press(kind);
        return button;
    }

    private async void LoadIcon(string path, Image image)
    {
        if (!_icons.TryGetValue(path, out var icon)) _icons[path] = icon = await ShellThumbnails.GetAsync(path, 48);
        image.Source = icon;
    }

    // MARK: Actions

    private void QuitRow(OpenApp app)
    {
        if (_service.Quit(app)) _settling[app.Key] = DateTime.Now.AddSeconds(QuitAppsSupport.SettleSeconds);
        Render();
    }

    private void Press(QuitAppsButton kind)
    {
        if (kind == QuitAppsButton.Cancel) { Close(); return; }
        foreach (var app in _service.QuitBatch(_apps, kind == QuitAppsButton.AllExceptFront))
            _settling[app.Key] = DateTime.Now.AddSeconds(QuitAppsSupport.SettleSeconds);
        Render();
    }

    // MARK: Keyboard

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || e.Key == Key.System;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        QuitAppsMove? move = key switch
        {
            Key.Up => QuitAppsMove.Up, Key.Down => QuitAppsMove.Down, Key.Left => QuitAppsMove.Left, Key.Right => QuitAppsMove.Right,
            Key.Tab => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? QuitAppsMove.Previous : QuitAppsMove.Next,
            _ => null,
        };
        if (key == Key.Escape) { Close(); e.Handled = true; return; }
        if (move is { } m)
        {
            _focus = QuitAppsSupport.Move(_focus, m, _apps.Count, _buttons, _resting);
            Render();
            e.Handled = true;
            return;
        }
        if (key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            if (ctrl && _buttons.Contains(QuitAppsButton.All)) Press(QuitAppsButton.All);
            else if (alt && _buttons.Contains(QuitAppsButton.AllExceptFront)) Press(QuitAppsButton.AllExceptFront);
            else if (_focus.Row is { } row && row < _apps.Count) QuitRow(_apps[row]);
            else Press(_focus.Button ?? _resting);
            return;
        }
        if (key == Key.Delete && _focus.Row is { } selected && selected < _apps.Count)
        {
            QuitRow(_apps[selected]);
            e.Handled = true;
        }
    }

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1 || char.IsControl(e.Text[0])) return;
        var typed = _typed.Add(e.Text[0], Environment.TickCount64 / 1000.0);
        if (QuitAppsSupport.Jump(typed, _apps.Select(a => a.Name).ToList()) is { } index)
        {
            _focus = QuitAppsFocus.OnRow(index);
            Render();
        }
        e.Handled = true;
    }
}
