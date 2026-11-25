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
    /// The companies' marks (Claude/Anthropic, OpenAI, Cursor, GitHub Copilot, OpenCode) as vector paths on a 24-point square,
    /// from the Simple Icons set (CC0 artwork; the marks remain their owners' trademarks), filled in the agent's colour.
    /// </summary>
    private static readonly Dictionary<AgentProvider, Geometry> DrawnMarks = new()
    {
        [AgentProvider.Claude] = Geometry.Parse("F1 m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z"),
        [AgentProvider.Codex] = Geometry.Parse("F1 M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z"),
        [AgentProvider.Cursor] = Geometry.Parse("F1 M11.503.131 1.891 5.678a.84.84 0 0 0-.42.726v11.188c0 .3.162.575.42.724l9.609 5.55a1 1 0 0 0 .998 0l9.61-5.55a.84.84 0 0 0 .42-.724V6.404a.84.84 0 0 0-.42-.726L12.497.131a1.01 1.01 0 0 0-.996 0M2.657 6.338h18.55c.263 0 .43.287.297.515L12.23 22.918c-.062.107-.229.064-.229-.06V12.335a.59.59 0 0 0-.295-.51l-9.11-5.257c-.109-.063-.064-.23.061-.23"),
        [AgentProvider.Copilot] = Geometry.Parse("F1 M23.922 16.997C23.061 18.492 18.063 22.02 12 22.02 5.937 22.02.939 18.492.078 16.997A.641.641 0 0 1 0 16.741v-2.869a.883.883 0 0 1 .053-.22c.372-.935 1.347-2.292 2.605-2.656.167-.429.414-1.055.644-1.517a10.098 10.098 0 0 1-.052-1.086c0-1.331.282-2.499 1.132-3.368.397-.406.89-.717 1.474-.952C7.255 2.937 9.248 1.98 11.978 1.98c2.731 0 4.767.957 6.166 2.093.584.235 1.077.546 1.474.952.85.869 1.132 2.037 1.132 3.368 0 .368-.014.733-.052 1.086.23.462.477 1.088.644 1.517 1.258.364 2.233 1.721 2.605 2.656a.841.841 0 0 1 .053.22v2.869a.641.641 0 0 1-.078.256Zm-11.75-5.992h-.344a4.359 4.359 0 0 1-.355.508c-.77.947-1.918 1.492-3.508 1.492-1.725 0-2.989-.359-3.782-1.259a2.137 2.137 0 0 1-.085-.104L4 11.746v6.585c1.435.779 4.514 2.179 8 2.179 3.486 0 6.565-1.4 8-2.179v-6.585l-.098-.104s-.033.045-.085.104c-.793.9-2.057 1.259-3.782 1.259-1.59 0-2.738-.545-3.508-1.492a4.359 4.359 0 0 1-.355-.508Zm2.328 3.25c.549 0 1 .451 1 1v2c0 .549-.451 1-1 1-.549 0-1-.451-1-1v-2c0-.549.451-1 1-1Zm-5 0c.549 0 1 .451 1 1v2c0 .549-.451 1-1 1-.549 0-1-.451-1-1v-2c0-.549.451-1 1-1Zm3.313-6.185c.136 1.057.403 1.913.878 2.497.442.544 1.134.938 2.344.938 1.573 0 2.292-.337 2.657-.751.384-.435.558-1.15.558-2.361 0-1.14-.243-1.847-.705-2.319-.477-.488-1.319-.862-2.824-1.025-1.487-.161-2.192.138-2.533.529-.269.307-.437.808-.438 1.578v.021c0 .265.021.562.063.893Zm-1.626 0c.042-.331.063-.628.063-.894v-.02c-.001-.77-.169-1.271-.438-1.578-.341-.391-1.046-.69-2.533-.529-1.505.163-2.347.537-2.824 1.025-.462.472-.705 1.179-.705 2.319 0 1.211.175 1.926.558 2.361.365.414 1.084.751 2.657.751 1.21 0 1.902-.394 2.344-.938.475-.584.742-1.44.878-2.497Z"),
        [AgentProvider.OpenCode] = Geometry.Parse("F1 M22 24H2V0h20zM17 4.8H7v14.4h10z"),
    };

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
