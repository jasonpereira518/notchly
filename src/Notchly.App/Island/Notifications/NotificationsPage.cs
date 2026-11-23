// SPDX-License-Identifier: GPL-3.0-or-later
// The island's Notifications page: the Windows counterpart of upstream UI/Notch/NotchNotificationsView.swift.
// One card per notification, newest first: its app's icon and name, the time, then the title and two lines of
// the message; Open and Dismiss appear under the pointer, and a click opens the card in place (title two lines,
// message six, as upstream's held preview). Clear All sits in the island's header. Windows-specific: the page
// says when Notchly still needs its one-time setup or Windows' permission, instead of macOS Accessibility.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Platform.Notifications;
using Notchly.App.Platform.Shell;
using Notchly.Core.Notifications;

namespace Notchly.App.Island.Notifications;

public sealed class NotificationsPage
{
    // Upstream NotchNotificationStrings.enUS, with "this PC" for "this Mac" and Windows' own setup.
    public const string Title = "Notifications";
    public const string Empty = "New notifications will appear here";
    public const string Waiting = "Waiting for the system notification service";
    public const string Privacy = "Shows new notifications only. Messages stay in memory and are cleared when you lock this PC or turn this off.";
    private const string NeedsSetup = "Set up notifications";
    private const string NeedsSetupDetail = "Notchly needs a one-time setup and your permission to show Windows notifications here.";
    private const string AccessOff = "Notification access is off in Windows settings";
    private const string AccessOffDetail = "Allow Notchly to access your notifications, then come back.";

    private static readonly SolidColorBrush CardBack = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Message = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush IconBack = Frozen(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));

    private readonly NotchWindow _window;
    private readonly NotificationsService _service;
    private readonly Grid _root;
    private readonly HashSet<uint> _open = [];
    private uint? _reveal;
    private bool _visible;

    /// <summary>Raised when the page's height may have changed, so the island can fit it.</summary>
    public event Action? Resized;

    public NotificationsPage(NotchWindow window, NotificationsService service)
    {
        _window = window;
        _service = service;
        _root = window.NotificationsPage;
        service.Changed += Render;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];

    /// <summary>The page scrolls past the preset's height.</summary>
    public void Layout(double height) => _root.MaxHeight = height;

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) Render();
            else _open.Clear();
        }
    }

    /// <summary>A click on the banner opens the page at that notification, opened in place.</summary>
    public void Reveal(uint id)
    {
        _open.Clear();
        _open.Add(id);
        _reveal = id;
        if (_visible) Render();
    }

    /// <summary>Clear All in the header is offered while there is something to clear.</summary>
    public bool CanClear => _service.Running && _service.Items.Count > 0;

    public void Render()
    {
        if (!_visible) { _window.NotificationsClear.Visibility = Visibility.Collapsed; return; }
        _root.Children.Clear();
        _window.NotificationsClear.Visibility = CanClear ? Visibility.Visible : Visibility.Collapsed;
        _open.IntersectWith(_service.Items.Select(i => i.Id));
        switch (_service.Access)
        {
            case NotificationAccess.NoIdentity or NotificationAccess.Unspecified:
                _root.Children.Add(Prompt("", NeedsSetup, NeedsSetupDetail, "Open Settings",
                    () => App.Current.ShowSettings(Notchly.App.Shell.SettingsPage.Island)));
                break;
            case NotificationAccess.Denied:
                _root.Children.Add(Prompt("", AccessOff, AccessOffDetail, "Open Windows settings",
                    () => ShellActions.Open("ms-settings:privacy-notifications")));
                break;
            default:
                if (_service.Items.Count == 0) _root.Children.Add(EmptyState());
                else _root.Children.Add(List());
                break;
        }
        Resized?.Invoke();
    }

    // MARK: States

    private FrameworkElement Prompt(string glyph, string title, string detail, string action, Action act)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, Margin = new Thickness(0, 18, 0, 18) };
        stack.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 22, Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
        stack.Children.Add(new TextBlock
        {
            Text = detail, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.None, Margin = new Thickness(0, 4, 0, 10),
        });
        var button = new Button { Style = (Style)_window.Resources["ChipButton"], Content = action, HorizontalAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => act();
        stack.Children.Add(button);
        return stack;
    }

    /// <summary>Upstream's empty page: the bell in a tile beside the line and the privacy note.</summary>
    private FrameworkElement EmptyState()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Margin = new Thickness(0, 18, 0, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(18), Background = Frozen(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            Child = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 24, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = _service.Running ? Empty : Waiting, FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = Privacy, FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }

    // MARK: The list

    private FrameworkElement List()
    {
        var stack = new StackPanel();
        FrameworkElement? reveal = null;
        foreach (var item in _service.Items)
        {
            var card = Card(item);
            if (item.Id == _reveal) reveal = card;
            stack.Children.Add(card);
        }
        var scroller = new ScrollViewer
        {
            Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Tag = "NotificationsPage",
        };
        if (reveal is not null)
        {
            _reveal = null;
            reveal.Loaded += (_, _) => reveal.BringIntoView();
        }
        return scroller;
    }

    private FrameworkElement AppIcon(SystemNotification item, double size)
    {
        if (_service.Icon(item) is { } icon)
            return new Image { Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24), Background = IconBack, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "", FontFamily = IconFont, FontSize = size * 0.5, Foreground = Message,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.None,
            },
        };
    }

    /// <summary>Text limited to <paramref name="lines"/> lines at its own line height.</summary>
    private static TextBlock Lines(string text, double size, int lines, Brush brush, FontWeight weight)
    {
        var height = Math.Ceiling(size * 1.34);
        return new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, Foreground = brush, TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = height,
            MaxHeight = height * lines + 0.5,
        };
    }

    private Button Action(string glyph, string tip, Action act)
    {
        var button = new Button
        {
            Style = (Style)_window.Resources["IconButton"], Content = glyph, ToolTip = tip, FontSize = 11, Width = 24, Height = 24,
            Margin = new Thickness(2, 0, 0, 0),
        };
        button.Click += (_, e) => { act(); e.Handled = true; };
        return button;
    }

    private FrameworkElement Card(SystemNotification item)
    {
        var opened = _open.Contains(item.Id);
        var content = item.Content;

        var header = new Grid { Height = opened ? 28 : 22 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = AppIcon(item, opened ? NotificationPreview.IconSize : 18);
        header.Children.Add(icon);
        var app = new TextBlock
        {
            Text = content.App.Length == 0 ? Title : content.App, FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };
        Grid.SetColumn(app, 1);
        header.Children.Add(app);
        var time = new TextBlock
        {
            Text = item.Received.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), FontSize = 11, Foreground = Faint,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        Grid.SetColumn(time, 2);
        header.Children.Add(time);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_service.CanOpen(item)) actions.Children.Add(Action("", "Open", () => _service.Open(item)));
        actions.Children.Add(Action("", "Dismiss", () => _service.Dismiss(item.Id)));
        // Under the pointer only, as upstream's hover controls; an opened card keeps them.
        actions.Visibility = opened ? Visibility.Visible : Visibility.Hidden;
        Grid.SetColumn(actions, 3);
        header.Children.Add(actions);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(Lines(content.Title, opened ? 14 : 13, opened ? NotificationPreview.TitleLines : 1, Brushes.White, FontWeights.SemiBold));
        stack.Children[^1].SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        if (content.Subtitle.Length > 0)
            stack.Children.Add(Lines(content.Subtitle, 12, NotificationPreview.SubtitleLines, Brushes.White, FontWeights.Medium));
        if (content.Body.Length > 0)
        {
            var body = Lines(content.Body, 12, opened ? NotificationPreview.BodyLines : NotificationPreview.CollapsedBodyLines, Message, FontWeights.Normal);
            body.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(body);
        }

        var card = new Border
        {
            Background = CardBack, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 10, 10), Margin = new Thickness(0, 0, 0, 8),
            Child = stack, Cursor = Cursors.Hand, ToolTip = null,
        };
        AutomationProperties(card, content.AccessibilityText);
        card.MouseEnter += (_, _) => actions.Visibility = Visibility.Visible;
        card.MouseLeave += (_, _) => { if (!_open.Contains(item.Id)) actions.Visibility = Visibility.Hidden; };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && InsideButton(source)) return;
            if (!_open.Remove(item.Id)) _open.Add(item.Id);
            Render();
        };
        return card;
    }

    private static void AutomationProperties(UIElement element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private static bool InsideButton(DependencyObject node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }

    /// <summary>Clear All, asked first: the island may hold the only copy once the originals are gone.</summary>
    public void ConfirmClear(Action<bool> modal)
    {
        if (!CanClear) return;
        modal(true);
        try
        {
            var answer = MessageBox.Show(_window, "Clear all notifications? They are also removed from Windows' notification centre.",
                "Clear All", MessageBoxButton.OKCancel, MessageBoxImage.None, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) _service.ClearAll();
        }
        finally { modal(false); }
    }
}
