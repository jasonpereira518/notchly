// SPDX-License-Identifier: GPL-3.0-or-later
// The island's Calendar page: the Windows counterpart of upstream UI/Notch/NotchCalendarView.swift and
// NotchCalendarMonthView.swift. The month's name heads the page (it opens the month grid) beside the way
// through it; below, the week strip over the agenda of the next seven days or of the day picked. A tall
// page shows the month beside the agenda. Right-click an event to count down to it alone.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Notchly.App.Platform.Shell;
using Notchly.Core.Calendar;
using Notchly.Core.Settings;

namespace Notchly.App.Island.Calendar;

public sealed class CalendarPage
{
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CardBack = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Divider = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Today = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush Mint = Frozen(Color.FromRgb(0x63, 0xE6, 0xBE));
    private static readonly SolidColorBrush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    private readonly NotchWindow _window;
    private readonly CalendarService _calendar;
    private readonly SettingsStore _settings;
    private readonly Grid _root;
    private DateTime _focus = DateTime.Today;
    private DateTime? _selected;
    private bool _showingMonth, _visible;
    private double _height = 264, _width = 500;

    public CalendarPage(NotchWindow window, CalendarService calendar, SettingsStore settings)
    {
        _window = window;
        _calendar = calendar;
        _settings = settings;
        _root = window.CalendarPage;
        calendar.Changed += Render;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Brush(CalendarColor color) => Frozen(Color.FromRgb(color.R, color.G, color.B));
    private static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>The page fills the preset's height.</summary>
    public void Layout(double height, double width)
    {
        _height = height;
        _width = width;
        _root.Height = height;
    }

    /// <summary>Showing the page reads the month it shows and any calendar gone stale; leaving it lets go.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value)
            {
                _calendar.ShowMonth(_focus);
                _calendar.PageOpened();
                Render();
            }
            else
            {
                _showingMonth = false;
                _calendar.ShowMonth(null);
            }
        }
    }

    /// <summary>A click on the closed island's countdown lands on that event's day, scrolled to it.</summary>
    public void Reveal(CalendarEvent calendarEvent)
    {
        _selected = null;
        _focus = calendarEvent.Start.Date;
        _showingMonth = false;
        _calendar.Revealing = calendarEvent.Id;
        if (_visible) { _calendar.ShowMonth(_focus); Render(); }
    }

    private bool SideBySide => _height >= 300 && _width >= 420;

    public void Render()
    {
        if (!_visible) return;
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var now = DateTime.Now;

        if (!_calendar.HasFeeds)
        {
            var setup = Setup();
            Grid.SetRowSpan(setup, 3);
            _root.Children.Add(setup);
            return;
        }

        var header = Header();
        _root.Children.Add(header);
        var problems = _calendar.Statuses.Where(s => s.Problem is not null).ToList();
        if (problems.Count > 0)
        {
            var line = new TextBlock
            {
                Text = problems.Count == 1 ? problems[0].Problem : $"{problems[0].Problem} (and {problems.Count - 1} more)",
                Foreground = Orange, FontSize = 10, Margin = new Thickness(2, 2, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = string.Join(Environment.NewLine, problems.Select(p => $"{p.Feed.Name}: {p.Problem}")),
            };
            Grid.SetRow(line, 1);
            _root.Children.Add(line);
        }

        FrameworkElement body;
        if (SideBySide)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var month = MonthGrid(now, Math.Min(_height - 40, 230));
            grid.Children.Add(month);
            var line = new Rectangle { Width = 1, Fill = Divider, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(line, 1);
            grid.Children.Add(line);
            var agenda = Agenda(now);
            Grid.SetColumn(agenda, 2);
            grid.Children.Add(agenda);
            body = grid;
        }
        else if (_showingMonth) body = MonthGrid(now, Math.Max(120, _height - 44));
        else
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(WeekStrip(now));
            var agenda = Agenda(now);
            agenda.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(agenda, 1);
            grid.Children.Add(agenda);
            body = grid;
        }
        Grid.SetRow(body, 2);
        _root.Children.Add(body);
    }

    // MARK: Header

    private Button Icon(string glyph, string tip, Action action)
    {
        var button = new Button { Style = (Style)_window.Resources["IconButton"], Content = glyph, ToolTip = tip, Margin = new Thickness(2, 0, 0, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private FrameworkElement Header()
    {
        var byWeek = !SideBySide && !_showingMonth;
        var title = new Button
        {
            Style = (Style)_window.Resources["PlainButton"], HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 28,
            Content = new TextBlock
            {
                Text = _focus.ToString("MMMM yyyy", Culture), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 0, 5, 0),
            },
            ToolTip = SideBySide ? null : "Month",
            Background = _showingMonth ? Divider : Brushes.Transparent,
        };
        title.Click += (_, _) => { if (!SideBySide) { _showingMonth = !_showingMonth; Render(); } };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        navigation.Children.Add(Icon("", byWeek ? "Previous week" : "Previous month", () => Move(byWeek ? -7 : 0, byWeek ? 0 : -1)));
        navigation.Children.Add(Icon("", "Today", () => { _selected = DateTime.Today; SetFocus(DateTime.Today); _showingMonth = false; Render(); }));
        navigation.Children.Add(Icon("", byWeek ? "Next week" : "Next month", () => Move(byWeek ? 7 : 0, byWeek ? 0 : 1)));
        if (_calendar.CalendarLink(_selected ?? DateTime.Today) is { } link)
            navigation.Children.Add(Icon("", "Open calendar", () => ShellActions.Open(link)));
        return new Grid { Children = { title, navigation } };
    }

    private void Move(int days, int months)
    {
        var target = months != 0 ? new DateTime(_focus.Year, _focus.Month, 1).AddMonths(months) : _focus.AddDays(days);
        _selected = target.Date;
        SetFocus(target);
        Render();
    }

    private void SetFocus(DateTime date)
    {
        var monthChanged = date.Year != _focus.Year || date.Month != _focus.Month;
        _focus = date;
        // A month read covers every week of that month, so moving within it keeps the loaded events.
        if (monthChanged) _calendar.ShowMonth(_focus);
    }

    private void Select(DateTime day)
    {
        _selected = day.Date;
        SetFocus(day);
        if (!SideBySide) _showingMonth = false;
        Render();
    }

    // MARK: Week strip and month grid

    private FrameworkElement DayCell(DateTime day, DateTime now, double circle, double fontSize, bool inMonth, bool showWeekday)
    {
        var isToday = day.Date == now.Date;
        var selected = _selected == day.Date;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (showWeekday)
            stack.Children.Add(new TextBlock
            {
                Text = Culture.DateTimeFormat.GetShortestDayName(day.DayOfWeek), FontSize = 9, FontWeight = FontWeights.Medium,
                Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3),
            });
        stack.Children.Add(new Border
        {
            Width = circle, Height = circle, CornerRadius = new CornerRadius(circle / 2),
            Background = isToday ? Today : selected ? Brushes.White : Brushes.Transparent,
            BorderBrush = selected && isToday ? Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) : null,
            BorderThickness = new Thickness(selected && isToday ? 1.5 : 0),
            Child = new TextBlock
            {
                Text = day.Day.ToString(Culture), FontSize = fontSize, FontWeight = isToday || selected ? FontWeights.Bold : FontWeights.Medium,
                Foreground = selected && !isToday ? Brushes.Black : isToday || inMonth ? Brushes.White : Faint,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 3, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var color in CalendarSupport.EventsOn(_calendar.Events, day).Select(e => e.Color).Distinct().Take(3))
            dots.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = Brush(color), Margin = new Thickness(1, 0, 1, 0) });
        stack.Children.Add(dots);
        var button = new Button { Style = (Style)_window.Resources["PlainButton"], Content = stack, ToolTip = day.ToString("D", Culture) };
        button.Click += (_, _) => Select(day);
        return button;
    }

    private FrameworkElement WeekStrip(DateTime now)
    {
        var grid = new UniformGrid { Columns = 7 };
        foreach (var day in CalendarSupport.WeekDays(_focus, CalendarService.FirstDay))
            grid.Children.Add(DayCell(day, now, 26, 12, inMonth: true, showWeekday: true));
        return grid;
    }

    private FrameworkElement MonthGrid(DateTime now, double height)
    {
        var weekNumbers = _settings.GetBool(DefaultsKey.NotchCalendarWeekNumbers);
        var days = CalendarSupport.MonthDays(_focus, CalendarService.FirstDay);
        var rowHeight = Math.Max(18, (height - 18) / 6);
        var circle = Math.Min(24, rowHeight - 6);
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Top };
        if (weekNumbers) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        for (var r = 0; r < 6; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });
        var offset = weekNumbers ? 1 : 0;
        for (var c = 0; c < 7; c++)
        {
            var name = new TextBlock
            {
                Text = Culture.DateTimeFormat.GetShortestDayName(days[c].DayOfWeek), FontSize = 9, FontWeight = FontWeights.Medium,
                Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(name, c + offset);
            grid.Children.Add(name);
        }
        for (var i = 0; i < days.Count; i++)
        {
            var (row, column) = (i / 7 + 1, i % 7);
            if (weekNumbers && column == 0)
            {
                var number = new TextBlock
                {
                    Text = CalendarSupport.WeekNumber(days[i], Culture).ToString(Culture), FontSize = 9, Foreground = Faint,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip = $"Week {CalendarSupport.WeekNumber(days[i], Culture)}",
                };
                Grid.SetRow(number, row);
                grid.Children.Add(number);
            }
            var cell = DayCell(days[i], now, circle, Math.Min(11, circle * 0.62), days[i].Month == _focus.Month, showWeekday: false);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column + offset);
            grid.Children.Add(cell);
        }
        return grid;
    }

    // MARK: Agenda

    private FrameworkElement Agenda(DateTime now)
    {
        var list = new StackPanel();
        var days = _selected is { } day ? [day] : Enumerable.Range(0, 7).Select(i => now.Date.AddDays(i)).ToList();
        var source = _selected is null ? CalendarSupport.Upcoming(_calendar.Events, now) : _calendar.Events;
        var next = CalendarSupport.Next(_calendar.Events, now);
        FrameworkElement? reveal = null;
        if (_calendar.Loading)
            list.Children.Add(new TextBlock { Text = "Reading calendars…", Foreground = Dim, FontSize = 12, Margin = new Thickness(4, 12, 0, 0) });
        foreach (var group in days.Select(d => (Day: d, Events: CalendarSupport.EventsOn(source, d))).Where(g => g.Events.Count > 0))
        {
            if (_selected is null) list.Children.Add(DayLabel(group.Day, now));
            foreach (var calendarEvent in group.Events)
            {
                var card = Card(calendarEvent, group.Day, now, calendarEvent.Id == next?.Id);
                if (calendarEvent.Id == _calendar.Revealing) reveal = card;
                list.Children.Add(card);
            }
        }
        if (list.Children.Count == 0)
        {
            var empty = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 14, 0, 0) };
            empty.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 24, Foreground = Faint, VerticalAlignment = VerticalAlignment.Center });
            empty.Children.Add(new TextBlock
            {
                Text = _selected is null ? "No upcoming appointments" : "No appointments on this day", Foreground = Dim, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            });
            list.Children.Add(empty);
        }
        var scroller = new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Tag = "CalendarAgenda",
        };
        if (reveal is not null)
        {
            _calendar.Revealing = null;
            reveal.Loaded += (_, _) => reveal.BringIntoView();
        }
        return scroller;
    }

    private static FrameworkElement DayLabel(DateTime day, DateTime now)
    {
        var text = day.Date == now.Date ? "Today" : day.Date == now.Date.AddDays(1) ? "Tomorrow" : day.ToString("dddd", Culture);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 0, 6) };
        panel.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = Brushes.White });
        panel.Children.Add(new TextBlock { Text = "  " + day.ToString(Culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM"), Culture), FontSize = 11, Foreground = Dim });
        return panel;
    }

    private FrameworkElement Card(CalendarEvent calendarEvent, DateTime day, DateTime now, bool isNext)
    {
        var ongoing = !calendarEvent.AllDay && calendarEvent.Start <= now && calendarEvent.End > now;
        var content = new StackPanel { Margin = new Thickness(9, 0, 0, 0) };
        if (ongoing || isNext)
            content.Children.Add(new TextBlock
            {
                Text = ongoing ? "HAPPENING NOW" : "UP NEXT", FontSize = 9, FontWeight = FontWeights.SemiBold,
                Foreground = ongoing ? Mint : Dim, Margin = new Thickness(0, 0, 0, 2),
            });
        content.Children.Add(new TextBlock
        {
            Text = calendarEvent.Title.Length == 0 ? "Untitled event" : calendarEvent.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        time.Children.Add(new TextBlock { Text = TimeRange(calendarEvent, day), FontSize = 11, Foreground = Brushes.White });
        if (_calendar.IsChosen(calendarEvent))
            time.Children.Add(new TextBlock { Text = "  ", FontFamily = IconFont, FontSize = 10, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Event countdown" });
        content.Children.Add(time);
        var detail = calendarEvent.Location.Length > 0 ? $"{calendarEvent.CalendarName} · {calendarEvent.Location}" : calendarEvent.CalendarName;
        content.Children.Add(new TextBlock { Text = detail, FontSize = 10, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Brush(calendarEvent.Color) });
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        var link = _calendar.OpenLink(calendarEvent);
        var card = new Button
        {
            Style = (Style)_window.Resources["PlainButton"], HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Border { Background = CardBack, CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 8, 10, 8), Child = grid },
            Margin = new Thickness(0, 0, 0, 7), Cursor = link is null ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
            ToolTip = link is null ? null : "Open calendar",
        };
        if (link is not null) card.Click += (_, _) => ShellActions.Open(link);
        if (_calendar.OffersCountdown(calendarEvent, now))
        {
            var chosen = _calendar.IsChosen(calendarEvent);
            var item = new MenuItem { Header = chosen ? "Remove Countdown" : "Add Countdown" };
            item.Click += (_, _) => _calendar.SetCountdown(!chosen, calendarEvent);
            card.ContextMenu = new ContextMenu { Items = { item } };
        }
        return card;
    }

    private static string TimeRange(CalendarEvent calendarEvent, DateTime day)
    {
        if (calendarEvent.AllDay) return "All day";
        var pattern = Culture.DateTimeFormat.ShortTimePattern;
        string Time(DateTime t) => t.ToString(pattern, Culture).Replace(' ', ' ');
        var start = calendarEvent.Start.Date == day.Date ? Time(calendarEvent.Start) : calendarEvent.Start.ToString("ddd", Culture) + " " + Time(calendarEvent.Start);
        var end = calendarEvent.End.Date == day.Date || calendarEvent.End == day.Date.AddDays(1) ? Time(calendarEvent.End) : calendarEvent.End.ToString("ddd", Culture) + " " + Time(calendarEvent.End);
        return $"{start} – {end}";
    }

    // MARK: Set-up

    private FrameworkElement Setup()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 340 };
        stack.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 26, Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = "Add your Google, Outlook, Canvas or iCloud calendar by its private iCal link to see your appointments here.",
            FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 10),
        });
        var add = new Button { Style = (Style)_window.Resources["ChipButton"], Content = "Add a calendar", HorizontalAlignment = HorizontalAlignment.Center };
        add.Click += (_, _) => App.Current.ShowSettings(Notchly.App.Shell.SettingsPage.Island);
        stack.Children.Add(add);
        return stack;
    }
}
