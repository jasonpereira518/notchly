// SPDX-License-Identifier: GPL-3.0-or-later
// The island's AI Agents page: the Windows counterpart of upstream UI/Notch/NotchAgentsView.swift and
// NotchAgentComponents.swift. A period menu heads the page; below, the cards in the person's order (limits per
// agent, spending, the turns now, the trend, models, projects and thirteen weeks of activity), paired at 390
// points and up, the page scrolling once they outgrow the island. Codex account cards and resets are not ported.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Notchly.Core.Agents;
using Notchly.Core.Settings;

namespace Notchly.App.Island.Agents;

/// <summary>How the island draws an agent: its colour, its mark and the colour of a limit running low.</summary>
public static class AgentLook
{
    /// <summary>Each agent keeps one colour on the page, the strip and notices (upstream AgentProvider.tint).</summary>
    public static Color Tint(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => Color.FromRgb(0xD9, 0x78, 0x57),
        AgentProvider.Codex => Color.FromRgb(0x7D, 0x99, 0xFF),
        AgentProvider.OpenCode => Color.FromRgb(0x0F, 0xBA, 0x82),
        AgentProvider.Copilot => Color.FromRgb(0x4D, 0xC7, 0xAD),
        _ => Color.FromRgb(0xC8, 0xC8, 0xD2),
    };

    /// <summary>The agent's colour while there is room, orange from 80% used and red from 95% (agentLimitTint).</summary>
    public static Color LimitTint(AgentProvider provider, double usedFraction) =>
        usedFraction >= 0.95 ? Color.FromRgb(0xFF, 0x45, 0x3A) : usedFraction >= 0.8 ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Tint(provider);

    /// <summary>The letter that stands for an agent whose app or CLI has no icon to borrow.</summary>
    public static string Letter(AgentProvider provider) => provider switch
    {
        AgentProvider.Codex => "X", AgentProvider.OpenCode => "O", AgentProvider.Copilot => "G", _ => "C",
    };

    /// <summary>The agent's own icon, or its letter in a disc of its colour, <paramref name="size"/> across.</summary>
    public static FrameworkElement Mark(AgentProvider provider, ImageSource? icon, double size, double opacity = 1)
    {
        // The company's mark on a transparent background, in the agent's colour, instead of an app icon's tile.
        if (Drawn(provider) is { } drawn)
            return new System.Windows.Shapes.Path
            {
                Data = drawn, Fill = Frozen(Tint(provider)), Width = size, Height = size, Opacity = opacity, Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
        if (icon is not null)
            return new Image { Source = icon, Width = size, Height = size, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Uniform };
        var tint = Tint(provider);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Opacity = opacity, VerticalAlignment = VerticalAlignment.Center,
            Background = Frozen(Color.FromArgb(0x38, tint.R, tint.G, tint.B)),
            Child = new TextBlock
            {
                Text = Letter(provider), FontSize = Math.Max(6, size * 0.62), FontWeight = FontWeights.Bold, Foreground = Frozen(tint),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.None,
            },
        };
    }

    /// <summary>
    /// Simple monochrome marks on a 24-point square, filled even-odd so the inner shapes cut out:
    /// Codex a hexagon holding a prompt, OpenCode a square frame, Copilot a face with two eyes.
    /// Claude is a burst of rays and Cursor a hexagonal cube. All are drawn here, simplified, not the official artwork.
    /// </summary>
    private static readonly Dictionary<AgentProvider, Geometry> DrawnMarks = new()
    {
        [AgentProvider.Claude] = Starburst(),
        [AgentProvider.Cursor] = Geometry.Parse("F0 M12,1.5 L21.1,6.75 V17.25 L12,22.5 L2.9,17.25 V6.75 Z M12,12.4 L18.6,8.4 L12,19.4 Z"),
        [AgentProvider.Codex] = Geometry.Parse("F0 M12,1.5 L21.1,6.75 V17.25 L12,22.5 L2.9,17.25 V6.75 Z M12,4 L18.9,8 V16 L12,20 L5.1,16 V8 Z " +
                                               "M8.2,9.3 L10,8.4 L13.4,12 L10,15.6 L8.2,14.7 L10.9,12 Z M13.6,14.4 H16.4 V15.8 H13.6 Z"),
        [AgentProvider.OpenCode] = Geometry.Parse("F0 M4,2 H20 V22 H4 Z M8,6 H16 V18 H8 Z"),
        [AgentProvider.Copilot] = Geometry.Parse("F0 M5,8 Q5,4 9,4 H15 Q19,4 19,8 V15 Q19,20 12,20 Q5,20 5,15 Z M8,10 H10 V13 H8 Z M14,10 H16 V13 H14 Z " +
                                                 "M2.5,10 H4 V14 H2.5 Z M20,10 H21.5 V14 H20 Z"),
    };

    /// <summary>Claude's burst: twelve slim rays from the centre, long and short in turn.</summary>
    private static Geometry Starburst()
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            for (var i = 0; i < 12; i++)
            {
                var angle = i * Math.PI / 6;
                var length = i % 2 == 0 ? 11 : 8.2;
                var (sin, cos) = Math.SinCos(angle);
                Point At(double along, double across) => new(12 + cos * along - sin * across, 12 + sin * along + cos * across);
                context.BeginFigure(At(0, -1.15), true, true);
                context.LineTo(At(length - 1, -0.8), true, false);
                context.LineTo(At(length, 0), true, false);
                context.LineTo(At(length - 1, 0.8), true, false);
                context.LineTo(At(0, 1.15), true, false);
            }
        }
        return geometry;
    }

    private static Geometry? Drawn(AgentProvider provider)
    {
        if (!DrawnMarks.TryGetValue(provider, out var geometry)) return null;
        if (!geometry.IsFrozen) geometry.Freeze();
        return geometry;
    }

    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Faded(Color color, double opacity) =>
        Frozen(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B));

    /// <summary>"now", "5 min ago", "3 h ago", "2 d ago" (upstream's abbreviated relative dates).</summary>
    public static string Relative(DateTimeOffset date, DateTimeOffset now)
    {
        var seconds = now.Since(date);
        return seconds < 60 ? "now" : seconds < 3600 ? $"{(int)(seconds / 60)} min ago"
            : seconds < 86_400 ? $"{(int)(seconds / 3600)} h ago" : $"{(int)(seconds / 86_400)} d ago";
    }
}

public sealed class AgentsPage
{
    // Upstream NotchAgentStrings.enUS, with "this PC" for "this Mac".
    private const string Loading = "Reading usage…";
    private const string Empty = "Use Claude Code, Codex, OpenCode, Copilot or Cursor and your usage appears here.";
    private const string NoCards = "Choose what this page shows in Settings.";
    private const string NoActivity = "No activity in this period";
    private const string ValueNote = "API value is what the same work would cost at API list prices. Plans charge a fixed price instead.";
    private const string Unpriced = "Some models have no known price, so this is a minimum.";

    private static readonly SolidColorBrush CardBack = AgentLook.Faded(Colors.White, 0.07);
    private static readonly SolidColorBrush Track = AgentLook.Faded(Colors.White, 0.13);
    private static readonly SolidColorBrush Title = AgentLook.Faded(Colors.White, 0.9);
    private static readonly SolidColorBrush Label = AgentLook.Faded(Colors.White, 0.85);
    private static readonly SolidColorBrush Dim = AgentLook.Faded(Colors.White, 0.6);
    private static readonly SolidColorBrush Faint = AgentLook.Faded(Colors.White, 0.4);
    private static readonly SolidColorBrush Orange = AgentLook.Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    private readonly NotchWindow _window;
    private readonly AgentsService _agents;
    private readonly SettingsStore _settings;
    private readonly Grid _root;
    private readonly DispatcherTimer _timer;
    /// <summary>The live turns' clocks, advanced every second without a redraw.</summary>
    private readonly List<(TextBlock Text, DateTimeOffset Started)> _clocks = [];
    private double _height = 264, _width = 500;
    /// <summary>How far the cards are scrolled, kept across the page's redraws while it stays open.</summary>
    private double _scrollOffset;
    private ScrollViewer? _scroller;
    private bool _visible;
    private int _ticks;

    public AgentsPage(NotchWindow window, AgentsService agents, SettingsStore settings)
    {
        _window = window;
        _agents = agents;
        _settings = settings;
        _root = window.AgentsPage;
        agents.Changed += Render;
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnSecond();
    }

    private static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];
    private static CultureInfo Culture => CultureInfo.CurrentCulture;
    private AgentPeriod Period => AgentPeriods.Parse(_settings.GetString(DefaultsKey.NotchAgentsPeriod));

    /// <summary>The page is as wide as the island's content and scrolls past the preset's height.</summary>
    public void Layout(double height, double width)
    {
        var changed = Math.Abs(width - _width) > 0.5 || Math.Abs(height - _height) > 0.5;
        _height = height;
        _width = width;
        _root.MaxHeight = height;
        if (changed) Render();
    }

    /// <summary>Showing the page asks for fresh limits and redraws every 15 seconds; leaving it stops both.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            _agents.PageVisible = value;
            if (value) { Render(); _timer.Start(); }
            else { _timer.Stop(); _clocks.Clear(); _scrollOffset = 0; _scroller = null; }
        }
    }

    private void OnSecond()
    {
        if (!_visible) return;
        if (++_ticks % 15 == 0) { Render(); return; }
        var now = DateTimeOffset.UtcNow;
        foreach (var (text, started) in _clocks) text.Text = AgentFormat.Clock(now.Since(started));
    }

    /// <summary>Agents with something to show, in the island's order.</summary>
    private IReadOnlyList<AgentProvider> Shown(AgentUsageSnapshot snapshot) =>
        _agents.Providers.Where(snapshot.Seen.Contains).ToList();

    public void Render()
    {
        if (!_visible) return;
        // The cards are drawn anew every 15 seconds: they stay where they were scrolled to.
        if (_scroller is not null) _scrollOffset = _scroller.VerticalOffset;
        _scroller = null;
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _clocks.Clear();
        var snapshot = _agents.Snapshot;
        if (!snapshot.Loaded) { _root.Children.Add(Message("\uE895", Loading)); return; }
        var providers = Shown(snapshot);
        if (providers.Count == 0) { _root.Children.Add(Message("\uE99A", Empty)); return; }
        var cards = NotchAgentSupport.Cards(_settings.GetString(DefaultsKey.NotchAgentsCardOrder), _settings.GetString(DefaultsKey.NotchAgentsHiddenCards));
        var rows = NotchAgentSupport.Rows(NotchAgentSupport.Tiles(cards, providers), _width);
        if (rows.Count == 0) { _root.Children.Add(Message("\uF0E2", NoCards)); return; }

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var now = DateTimeOffset.UtcNow;
        // The period covers several cards, so its menu heads the page while any of them shows.
        if (rows.SelectMany(r => r).Any(t => t.Card.FollowsPeriod())) _root.Children.Add(PeriodMenu());

        var stack = new StackPanel();
        foreach (var row in rows)
        {
            var line = new Grid { Height = NotchAgentSupport.Height(row), Margin = new Thickness(0, 0, 0, row == rows[^1] ? 0 : NotchAgentSupport.Spacing) };
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NotchAgentSupport.Spacing) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var card = Card(row[i], snapshot, providers, now);
                Grid.SetColumn(card, i * 2);
                line.Children.Add(card);
            }
            stack.Children.Add(line);
        }
        var scroller = new ScrollViewer
        {
            Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Tag = "AgentsPage",
        };
        ScrollEdgeFade.Attach(scroller);
        var offset = _scrollOffset;
        if (offset > 0) scroller.Loaded += (_, _) => scroller.ScrollToVerticalOffset(offset);
        _scroller = scroller;
        Grid.SetRow(scroller, 1);
        _root.Children.Add(scroller);
    }

    // MARK: Pieces

    private static FrameworkElement Message(string glyph, string text)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 340, Margin = new Thickness(0, 24, 0, 24) };
        stack.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 22, Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.None, Margin = new Thickness(0, 10, 0, 0),
        });
        return stack;
    }

    private static string PeriodTitle(AgentPeriod period) => period switch
    {
        AgentPeriod.Today => "Today", AgentPeriod.Week => "7 days", _ => "30 days",
    };

    private FrameworkElement PeriodMenu()
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock { Text = PeriodTitle(Period), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = "\uE70D", FontFamily = IconFont, FontSize = 8, Foreground = Dim, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Style = (Style)_window.Resources["ChipButton"], Content = label, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 0, 8), ToolTip = "Period",
        };
        var menu = new ContextMenu();
        foreach (var period in AgentPeriods.All)
        {
            var item = new MenuItem { Header = PeriodTitle(period), IsCheckable = true, IsChecked = period == Period };
            item.Click += (_, _) => _settings.Set(DefaultsKey.NotchAgentsPeriod, period.RawValue());
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        button.Click += (_, _) => { menu.PlacementTarget = button; menu.IsOpen = true; };
        return button;
    }

    private static Border Chrome(UIElement content) => new()
    {
        Background = CardBack, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10, 12, 10), ClipToBounds = true, Child = content,
    };

    private static TextBlock Text(string text, double size, Brush? brush = null, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Brushes.White, FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock Numeric(string text, double size, Brush? brush = null, FontWeight? weight = null)
    {
        var block = Text(text, size, brush, weight);
        System.Windows.Documents.Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

    private static Border Chip(string text, Color tint) => new()
    {
        Background = AgentLook.Faded(tint, 0.14), CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(4, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center, Child = Text(text, 9, AgentLook.Faded(tint, 0.9), FontWeights.SemiBold),
    };

    /// <summary>A dot in the agent's colour while it works (upstream NotchAgentPulse, still).</summary>
    private static Ellipse Pulse(Color tint) => new() { Width = 6, Height = 6, Fill = AgentLook.Frozen(tint), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The card's title row: a mark or glyph, the title, and what sits at its end.</summary>
    private FrameworkElement Header(string title, string glyph, AgentProvider? provider = null, params UIElement[] accessory)
    {
        var grid = new Grid { Height = 17 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement icon = provider is { } agent
            ? AgentLook.Mark(agent, _agents.Icon(agent), 14)
            : new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 11, Foreground = Dim, Width = 14, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);
        var name = Text(title, 10.5, Title, FontWeights.SemiBold);
        name.Margin = new Thickness(6, 0, 4, 0);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var end = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in accessory) end.Children.Add(part);
        Grid.SetColumn(end, 2);
        grid.Children.Add(end);
        return grid;
    }

    /// <summary>A capsule meter with a tick where spending would be if spread evenly over the window.</summary>
    private static FrameworkElement Meter(double value, double? pace, Color tint, bool dimmed = false)
    {
        var fraction = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        var grid = new Grid { Height = 9 };
        grid.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Track, VerticalAlignment = VerticalAlignment.Center });
        var fill = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star), MinWidth = fraction > 0 ? 4 : 0 });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        fill.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = AgentLook.Faded(tint, dimmed ? 0.45 : 0.92) });
        grid.Children.Add(fill);
        if (pace is { } p && double.IsFinite(p) && p > 0.02 && p < 0.98)
        {
            var tick = new Grid();
            tick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(p, GridUnitType.Star) });
            tick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - p, GridUnitType.Star) });
            var mark = new Rectangle { Width = 1.5, Height = 9, RadiusX = 0.75, RadiusY = 0.75, Fill = AgentLook.Faded(Colors.White, 0.85), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-0.75, 0, 0, 0) };
            Grid.SetColumn(mark, 1);
            tick.Children.Add(mark);
            grid.Children.Add(tick);
        }
        return grid;
    }

    private FrameworkElement Card(NotchAgentTile tile, AgentUsageSnapshot snapshot, IReadOnlyList<AgentProvider> providers, DateTimeOffset now) => tile.Card switch
    {
        NotchAgentCard.Limits when tile.Provider is { } provider => LimitsCard(provider, snapshot, now),
        NotchAgentCard.Spend => SpendCard(snapshot, providers),
        NotchAgentCard.Live => LiveCard(snapshot, providers, now),
        NotchAgentCard.Trend => TrendCard(snapshot, providers),
        NotchAgentCard.Models => ShareCard("Models", "\uE8F1", snapshot.Usage(Period).Models, snapshot.Usage(Period).FullyPriced),
        NotchAgentCard.Projects => ShareCard("Projects", "\uE8B7", snapshot.Usage(Period).Projects, snapshot.Usage(Period).FullyPriced),
        NotchAgentCard.Activity => ActivityCard(snapshot, now),
        _ => Chrome(new Grid()),
    };

    // MARK: Limits

    private FrameworkElement LimitsCard(AgentProvider provider, AgentUsageSnapshot snapshot, DateTimeOffset now)
    {
        var limits = snapshot.Limits.GetValueOrDefault(provider);
        var stale = NotchAgentSupport.LimitsAreStale(limits, provider, false, now);
        var all = (limits?.Windows ?? []).Select(w => AgentLimitSupport.Current(w, now)).ToList();
        // Two rows fit: the session and whichever longer window binds first.
        var windows = new[]
        {
            all.FirstOrDefault(w => w.Kind == AgentLimitKind.Session),
            all.Where(w => w.Kind != AgentLimitKind.Session).OrderByDescending(w => w.UsedPercent).FirstOrDefault(),
        }.OfType<AgentLimitWindow>().ToList();

        var accessory = new List<UIElement>();
        if (snapshot.Plans.GetValueOrDefault(provider) is { } plan) accessory.Add(Chip(plan.Name, AgentLook.Tint(provider)));
        if (snapshot.Working(provider).Any()) accessory.Add(Pulse(AgentLook.Tint(provider)));
        var stack = new StackPanel();
        stack.Children.Add(Header(provider.DisplayName(), "", provider, [.. accessory]));
        if (windows.Count == 0) Estimate(stack, provider, snapshot, now);
        else
        {
            var meters = new StackPanel { Opacity = stale ? 0.6 : 1, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var window in windows) meters.Children.Add(LimitRow(provider, window, limits!, now));
            stack.Children.Add(meters);
            if (windows.Count == 1 && limits is { } reading && now.Since(reading.ObservedAt) > 600)
                stack.Children.Add(Small($"Updated {AgentLook.Relative(reading.ObservedAt, now)}", Faint));
        }
        return Chrome(stack);
    }

    private static TextBlock Small(string text, Brush brush)
    {
        var block = Text(text, 9.5, brush);
        block.Margin = new Thickness(0, 3, 0, 0);
        return block;
    }

    /// <summary>What a window covers: its length, or the model it is kept for.</summary>
    private static string WindowLabel(AgentLimitWindow window) => window.Kind switch
    {
        AgentLimitKind.Session => "Session",
        AgentLimitKind.Weekly => window.Scope is { } scope ? $"Week · {scope}" : "Week",
        _ when window.Id == CursorUsageSummary.WindowId => CursorUsageSummary.WindowName,
        _ => window.Minutes is { } minutes ? AgentFormat.Duration(minutes * 60.0, 1) : window.Scope ?? "Limit",
    };

    /// <summary>Days and hours, or hours and minutes, never seconds on a card that redraws every few.</summary>
    private static string Countdown(DateTimeOffset date, DateTimeOffset now)
    {
        var seconds = date.Since(now);
        return AgentFormat.Duration(Math.Max(60, seconds), seconds >= 3600 ? 2 : 1);
    }

    private FrameworkElement LimitRow(AgentProvider provider, AgentLimitWindow window, AgentLimits limits, DateTimeOffset now)
    {
        var display = _agents.LimitDisplay;
        var remaining = display == NotchAgentLimitDisplay.Remaining;
        var tint = AgentLook.LimitTint(provider, window.UsedFraction);
        var pace = AgentLimitSupport.Pace(window, now);
        var reset = AgentLimitSupport.DisplayedReset(window, now) is { } resets
            ? (window.EstimatedResetsAt is null ? "" : "≈ ") + Countdown(resets, now) : null;

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(Text(WindowLabel(window), 10, Label, FontWeights.Medium));
        if (reset is not null)
        {
            var countdown = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            countdown.Children.Add(new TextBlock { Text = "\uE72C", FontFamily = IconFont, FontSize = 8, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 2, 0) });
            countdown.Children.Add(Numeric(reset, 9.5, Dim));
            Grid.SetColumn(countdown, 1);
            line.Children.Add(countdown);
        }
        var share = Numeric(NotchAgentSupport.LimitShare(window, display, s => s + " left", s => s + " used", Culture), 12,
                            AgentLook.Frozen(tint == AgentLook.Tint(provider) ? Colors.White : tint), FontWeights.SemiBold);
        Grid.SetColumn(share, 2);
        line.Children.Add(share);

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        row.Children.Add(line);
        row.Children.Add(Meter(remaining ? window.RemainingFraction : window.UsedFraction,
                               pace is { } p ? (remaining ? 1 - p.Elapsed : p.Elapsed) : null, tint));
        var tip = new List<string> { WindowLabel(window), AgentFormat.Percent(window.UsedFraction, Culture) + " used", AgentFormat.Percent(window.RemainingFraction, Culture) + " left" };
        if (AgentLimitSupport.DisplayedReset(window, now) is { } at)
        {
            tip.Add(at.ToLocalTime().ToString("g", Culture));
            if (window.EstimatedResetsAt is not null) tip.Add("Estimated");
        }
        if (now.Since(limits.ObservedAt) > 600) tip.Add("Updated " + AgentLook.Relative(limits.ObservedAt, now));
        row.ToolTip = string.Join(" · ", tip);
        return row;
    }

    /// <summary>Without a reading: Claude's five-hour window from local activity, OpenCode's day, or why not.</summary>
    private void Estimate(StackPanel stack, AgentProvider provider, AgentUsageSnapshot snapshot, DateTimeOffset now)
    {
        var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(body);
        if (provider == AgentProvider.Claude && snapshot.ClaudeBlock is { } block)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(Text("Session", 10, Label, FontWeights.Medium));
            var left = Numeric("↻ " + Countdown(block.End, now), 9.5, Dim);
            left.Margin = new Thickness(5, 0, 4, 0);
            Grid.SetColumn(left, 1);
            line.Children.Add(left);
            var cost = Numeric(AgentFormat.Cost(block.Totals.Cost, Culture), 12, Brushes.White, FontWeights.SemiBold);
            Grid.SetColumn(cost, 2);
            line.Children.Add(cost);
            body.Children.Add(line);
            var length = block.End.Since(block.Start);
            body.Children.Add(Meter(length > 0 ? now.Since(block.Start) / length : 0, null, AgentLook.Tint(provider), dimmed: true));
            body.ToolTip = "Estimated from this PC";
            if (_agents.ClaudeFailure is { } failure) body.Children.Add(Small(ClaudeFailureText(failure), Faint));
            return;
        }
        if (provider == AgentProvider.Claude)
        {
            body.Children.Add(Text("No session running", 10.5, Dim));
            if (_agents.ClaudeFailure is { } failure) { var why = Small(ClaudeFailureText(failure), Faint); why.TextWrapping = TextWrapping.Wrap; body.Children.Add(why); }
            return;
        }
        if (provider == AgentProvider.Cursor)
        {
            var status = Text(_agents.CursorStatus ?? Loading, 10.5, Dim);
            status.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(status);
            return;
        }
        if (provider == AgentProvider.OpenCode && snapshot.Usage(AgentPeriod.Today).ByProvider.TryGetValue(provider, out var today)
            && (today.Tokens.Total > 0 || today.Requests > 0 || today.Cost > 0))
        {
            var line = new Grid();
            line.Children.Add(Text("Today", 10, Label, FontWeights.Medium));
            var cost = Numeric(AgentFormat.MinimumCost(today.Cost, today.Unpriced, Culture), 12, Brushes.White, FontWeights.SemiBold);
            cost.HorizontalAlignment = HorizontalAlignment.Right;
            line.Children.Add(cost);
            body.Children.Add(line);
            var detail = AgentFormat.Tokens(today.Tokens.Total, Culture) + " tokens";
            if (today.Tokens.CacheHitRate is double rate && rate > 0) detail += " · " + AgentFormat.Percent(rate, Culture) + " from cache";
            body.Children.Add(Small(detail, Dim));
            return;
        }
        body.Children.Add(Text(provider == AgentProvider.OpenCode ? "No session running" : "Limits appear after the next reply", 10.5, Dim));
        if (snapshot.LastActivity.TryGetValue(provider, out var last)) body.Children.Add(Small(AgentLook.Relative(last, now), Faint));
    }

    /// <summary>Why Claude Code could not check the account's limits (upstream claudeCode(_:)).</summary>
    public static string ClaudeFailureText(AgentClaudeServer.Failure failure) => failure switch
    {
        AgentClaudeServer.Failure.Missing => "Claude Code was not found on this PC.",
        AgentClaudeServer.Failure.NeedsSignIn => "Sign in to Claude Code so it can check your account’s limits.",
        AgentClaudeServer.Failure.Outdated => "Update Claude Code so it can check your account’s limits.",
        _ => "Claude Code could not check your account’s limits. It tries again later.",
    };

    // MARK: Spending

    private FrameworkElement SpendCard(AgentUsageSnapshot snapshot, IReadOnlyList<AgentProvider> providers)
    {
        var usage = snapshot.Usage(Period);
        var accessory = new List<UIElement>();
        if (Period == AgentPeriod.Month)
        {
            // Thirty days of API value against a plan's monthly price.
            foreach (var provider in providers)
            {
                if (snapshot.Plans.GetValueOrDefault(provider) is not { MonthlyPrice: double price } plan || price <= 0
                    || !usage.ByProvider.TryGetValue(provider, out var spent) || spent.Cost <= 0) continue;
                var multiple = spent.Cost / price;
                var chip = Chip(multiple.ToString(multiple < 10 ? "0.0" : "0", Culture) + "×", AgentLook.Tint(provider));
                chip.ToolTip = $"{multiple.ToString(multiple < 10 ? "0.0" : "0", Culture)}× the price of {provider.DisplayName()} {plan.Name}";
                accessory.Add(chip);
            }
        }
        var stack = new StackPanel();
        stack.Children.Add(Header("Spending", "\uE8C7", null, [.. accessory]));
        var total = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), ToolTip = usage.FullyPriced ? ValueNote : Unpriced };
        total.Children.Add(Numeric((usage.FullyPriced ? "" : "≥ ") + AgentFormat.Cost(usage.Total.Cost, Culture), 22, Brushes.White, FontWeights.Medium));
        var value = Text("API value", 9, Dim);
        value.Margin = new Thickness(5, 6, 0, 0);
        total.Children.Add(value);
        stack.Children.Add(total);
        stack.Children.Add(Split(usage, providers));
        var footer = AgentFormat.Tokens(usage.Total.Tokens.Total, Culture) + " tokens";
        if (usage.Total.Tokens.CacheHitRate is double rate && rate > 0) footer += " · " + AgentFormat.Percent(rate, Culture) + " from cache";
        var line = Small(footer, Dim);
        line.Margin = new Thickness(0, 5, 0, 0);
        line.ToolTip = AgentFormat.Cost(usage.Total.Savings, Culture) + " saved by the cache";
        stack.Children.Add(line);
        return Chrome(stack);
    }

    /// <summary>One bar split by agent: every agent keeps a visible sliver, the rest shares what is left.</summary>
    private static FrameworkElement Split(AgentPeriodUsage usage, IReadOnlyList<AgentProvider> providers)
    {
        var byCost = usage.FullyPriced;
        var parts = providers.Select(p => (Provider: p, Weight: usage.ByProvider.TryGetValue(p, out var t) ? t.Weight(byCost) : 0)).Where(p => p.Weight > 0).ToList();
        var grid = new Grid { Height = 4 };
        if (parts.Count == 0)
        {
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Track });
            return grid;
        }
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(parts[i].Weight, GridUnitType.Star), MinWidth = 4 });
            var piece = new Border { CornerRadius = new CornerRadius(2), Background = AgentLook.Faded(AgentLook.Tint(parts[i].Provider), 0.9), ToolTip = parts[i].Provider.DisplayName() };
            Grid.SetColumn(piece, i * 2);
            grid.Children.Add(piece);
        }
        return grid;
    }

    // MARK: Now

    private FrameworkElement LiveCard(AgentUsageSnapshot snapshot, IReadOnlyList<AgentProvider> providers, DateTimeOffset now)
    {
        var live = snapshot.Live.Where(s => providers.Contains(s.Provider)).ToList();
        var accessory = new List<UIElement>();
        if (live.Count > 2) accessory.Add(Chip($"+{live.Count - 2}", Colors.White));
        if (live.Count > 0) accessory.Add(Pulse(AgentLook.Tint(live[0].Provider)));
        var stack = new StackPanel();
        stack.Children.Add(Header("Now", "\uE945", null, [.. accessory]));
        var rows = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        stack.Children.Add(rows);
        if (live.Count == 0)
        {
            foreach (var provider in providers.Take(4))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(AgentLook.Mark(provider, _agents.Icon(provider), 11, 0.7));
                var name = Text(provider.DisplayName(), 10.5, Brushes.White, FontWeights.Medium);
                name.Margin = new Thickness(5, 0, 4, 0);
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var when = Text(snapshot.LastActivity.TryGetValue(provider, out var last) ? AgentLook.Relative(last, now) : "Idle", 9.5, Dim);
                Grid.SetColumn(when, 2);
                row.Children.Add(when);
                rows.Children.Add(row);
            }
            return Chrome(stack);
        }
        foreach (var session in live.Take(2))
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(AgentLook.Mark(session.Provider, _agents.Icon(session.Provider), 11));
            var name = Text(session.Project.Length == 0 ? session.Provider.DisplayName() : session.Project, 11, Brushes.White, FontWeights.SemiBold);
            name.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(name, 1);
            line.Children.Add(name);
            var clock = Numeric(AgentFormat.Clock(now.Since(session.Started)), 11, AgentLook.Frozen(AgentLook.Tint(session.Provider)), FontWeights.Medium);
            Grid.SetColumn(clock, 2);
            line.Children.Add(clock);
            _clocks.Add((clock, session.Started));
            // Tokens the agent wrote, as its own window counts them; the context read again is in the cost.
            var detail = string.Join(" · ", new[]
            {
                AgentPricing.DisplayName(session.Model),
                session.Tokens.Output > 0 ? AgentFormat.Tokens(session.Tokens.Output, Culture) + " written" : "",
                session.Cost > 0 ? AgentFormat.Cost(session.Cost, Culture) : "",
            }.Where(p => p.Length > 0));
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
            row.Children.Add(line);
            var second = Text(detail, 9.5, Dim);
            second.Margin = new Thickness(0, 1, 0, 0);
            row.Children.Add(second);
            row.ToolTip = AgentFormat.Tokens(session.Tokens.Total, Culture) + " tokens · "
                          + AgentFormat.Percent(session.Tokens.CacheHitRate ?? 0, Culture) + " from cache";
            rows.Children.Add(row);
        }
        return Chrome(stack);
    }

    // MARK: Trend

    private string BucketLabel(DateTimeOffset start, bool full)
    {
        var local = start.ToLocalTime();
        if (Period == AgentPeriod.Today) return local.ToString(full ? "t" : "%H", Culture);
        if (Period == AgentPeriod.Week && !full) return Culture.DateTimeFormat.GetShortestDayName(local.DayOfWeek);
        return local.ToString(full ? "ddd d MMM" : "d MMM", Culture);
    }

    private static string Value(AgentTotals totals, bool byCost) =>
        byCost ? AgentFormat.Cost(totals.Cost, Culture) : AgentFormat.Tokens(totals.Tokens.Total, Culture) + " tokens";

    private FrameworkElement TrendCard(AgentUsageSnapshot snapshot, IReadOnlyList<AgentProvider> providers)
    {
        var usage = snapshot.Usage(Period);
        var byCost = usage.FullyPriced;
        IReadOnlyList<AgentBucket> buckets = Period == AgentPeriod.Today ? snapshot.Hours : snapshot.Days.TakeLast(Period.Days()).ToList();
        var caption = Numeric(PeriodTitle(Period) + " " + Value(usage.Total, byCost), 9.5, Dim, FontWeights.Medium);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Header("Trend", "\uE9D2", null, caption));

        const double barsHeight = 60;
        var values = buckets.Select(b => providers.Select(p => b.ByProvider.TryGetValue(p, out var t) ? t.Weight(byCost) : 0).ToList()).ToList();
        var peak = Math.Max(values.Select(v => v.Sum()).DefaultIfEmpty(0).Max(), double.Epsilon);
        var bars = new Grid { Height = barsHeight, Margin = new Thickness(0, 6, 0, 2), VerticalAlignment = VerticalAlignment.Bottom };
        var gap = buckets.Count > 24 ? 1.5 : 2;
        for (var index = 0; index < buckets.Count; index++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(gap / 2, 0, gap / 2, 0), Background = Brushes.Transparent };
            var parts = values[index];
            if (parts.Sum() <= 0)
                column.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = AgentLook.Faded(Colors.White, 0.1) });
            else
            {
                var first = true;
                for (var slot = providers.Count - 1; slot >= 0; slot--)
                {
                    if (parts[slot] <= 0) continue;
                    column.Children.Add(new Border
                    {
                        Height = Math.Max(1, barsHeight * parts[slot] / peak), Background = AgentLook.Faded(AgentLook.Tint(providers[slot]), 0.95),
                        CornerRadius = first ? new CornerRadius(2, 2, 0, 0) : new CornerRadius(0),
                    });
                    first = false;
                }
            }
            var bucket = buckets[index];
            column.ToolTip = BucketLabel(bucket.Start, true) + " · " + Value(bucket.Total, byCost) + (byCost ? " · " + Value(bucket.Total, false) : "");
            Grid.SetColumn(column, index);
            bars.Children.Add(column);
        }
        Grid.SetRow(bars, 1);
        grid.Children.Add(bars);

        // Every bar a letter over a week; the ends and the middle otherwise.
        var axis = new Grid { Height = 11 };
        if (buckets.Count > 0)
        {
            var marks = Period == AgentPeriod.Week ? Enumerable.Range(0, buckets.Count).ToList() : [0, buckets.Count / 2, buckets.Count - 1];
            for (var i = 0; i < buckets.Count; i++) axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var index in marks.Distinct())
            {
                var label = Text(BucketLabel(buckets[index].Start, false), 8.5, Faint, FontWeights.Medium);
                label.TextTrimming = TextTrimming.None;
                label.HorizontalAlignment = Period == AgentPeriod.Week ? HorizontalAlignment.Center
                    : index == 0 ? HorizontalAlignment.Left : index == buckets.Count - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                Grid.SetColumn(label, index);
                if (Period != AgentPeriod.Week)
                {
                    // An end label may run past its bar, toward the middle.
                    var span = Math.Min(4, buckets.Count);
                    if (index == buckets.Count - 1) { Grid.SetColumn(label, Math.Max(0, index - span + 1)); }
                    else if (index != 0) { Grid.SetColumn(label, Math.Max(0, index - span / 2)); }
                    Grid.SetColumnSpan(label, span);
                }
                axis.Children.Add(label);
            }
        }
        Grid.SetRow(axis, 2);
        grid.Children.Add(axis);
        return Chrome(grid);
    }

    // MARK: Models and projects

    private FrameworkElement ShareCard(string title, string glyph, IReadOnlyList<AgentShare> shares, bool byCost)
    {
        var shown = shares.Take(3).ToList();
        var peak = Math.Max(shown.Select(s => s.Totals.Weight(byCost)).DefaultIfEmpty(0).Max(), double.Epsilon);
        var stack = new StackPanel();
        stack.Children.Add(Header(title, glyph));
        var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(rows);
        if (shown.Count == 0) rows.Children.Add(Text(NoActivity, 10.5, Dim));
        foreach (var share in shown)
        {
            var tint = share.Provider is { } provider ? AgentLook.Tint(provider) : Colors.White;
            var row = new Grid { Height = 14, Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 40 });
            row.Children.Add(Text(share.Name, 10.5, Brushes.White, FontWeights.Medium));
            var bar = new Border
            {
                Width = Math.Max(3, 44 * share.Totals.Weight(byCost) / peak), Height = 4, CornerRadius = new CornerRadius(2),
                Background = AgentLook.Faded(tint, 0.85), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 6, 0),
            };
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            var value = Numeric(byCost ? AgentFormat.Cost(share.Totals.Cost, Culture) : AgentFormat.Tokens(share.Totals.Tokens.Total, Culture), 10, Dim, FontWeights.Medium);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            row.ToolTip = string.Join(" · ", share.Name, AgentFormat.Tokens(share.Totals.Tokens.Total, Culture) + " tokens", AgentFormat.Cost(share.Totals.Cost, Culture));
            rows.Children.Add(row);
        }
        return Chrome(stack);
    }

    // MARK: Activity

    private FrameworkElement ActivityCard(AgentUsageSnapshot snapshot, DateTimeOffset now)
    {
        var days = snapshot.Days;
        var byCost = AgentActivity.ByCost(days);
        var streak = AgentActivity.Streak(days);
        var accessory = new List<UIElement>();
        if (streak > 1)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = "Days in a row" };
            chip.Children.Add(new TextBlock { Text = "\uE945", FontFamily = IconFont, FontSize = 9, Foreground = Orange, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 2, 0) });
            chip.Children.Add(Numeric(streak.ToString(Culture), 9.5, Orange, FontWeights.SemiBold));
            accessory.Add(chip);
        }
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Header("Activity", "\uE787", null, [.. accessory]));

        const double spacing = 2.5, mapHeight = 70;
        var columns = AgentActivity.Columns(days);
        var cell = Math.Max(1, (mapHeight - spacing * 6) / 7);
        var thresholds = AgentActivity.Thresholds(days, byCost);
        var map = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var today = now.ToLocalTime().Date;
        foreach (var week in columns)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, spacing, 0) };
            for (var row = 0; row < 7; row++)
            {
                var square = new Border { Width = cell, Height = cell, CornerRadius = new CornerRadius(Math.Min(2.5, cell / 3)), Margin = new Thickness(0, 0, 0, row == 6 ? 0 : spacing) };
                if (week[row] is { } day)
                {
                    var value = day.Total.Weight(byCost);
                    square.Background = AgentLook.Faded(Colors.White, AgentActivity.LevelOpacity[AgentActivity.Level(value, thresholds)]);
                    if (day.Start.ToLocalTime().Date == today) { square.BorderBrush = AgentLook.Faded(Colors.White, 0.45); square.BorderThickness = new Thickness(1); }
                    square.ToolTip = day.Start.ToLocalTime().ToString("ddd d MMM", Culture) + " · " + Value(day.Total, byCost);
                }
                column.Children.Add(square);
            }
            map.Children.Add(column);
        }
        var body = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(map);

        // The whole map's total, its active days and its busiest day.
        var total = new AgentTotals();
        foreach (var day in days) total.Add(day.Total);
        var figures = new StackPanel { Margin = new Thickness(11.5, 0, 0, 0) };
        figures.Children.Add(Numeric(Value(total, byCost), 17, Brushes.White, FontWeights.Medium));
        figures.Children.Add(Small($"Last {days.Count} days", Dim));
        figures.Children.Add(Figure("Active days", AgentActivity.ActiveDays(days).ToString(Culture), 8));
        if (AgentActivity.BusiestDay(days, byCost) is { } busiest)
            figures.Children.Add(Figure("Busiest day", busiest.Start.ToLocalTime().ToString("d MMM", Culture) + " · " + Value(busiest.Total, byCost), 2));
        Grid.SetColumn(figures, 1);
        body.Children.Add(figures);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return Chrome(grid);
    }

    private static FrameworkElement Figure(string label, string value, double top)
    {
        var grid = new Grid { Margin = new Thickness(0, top, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Text(label, 10, Dim, FontWeights.Medium));
        var reading = Numeric(value, 10, Brushes.White, FontWeights.Medium);
        reading.HorizontalAlignment = HorizontalAlignment.Right;
        reading.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(reading, 1);
        grid.Children.Add(reading);
        return grid;
    }
}
