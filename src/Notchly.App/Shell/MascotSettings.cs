// SPDX-License-Identifier: GPL-3.0-or-later
// The companion's part of the island's settings: the Windows counterpart of upstream
// UI/Settings/NotchMascotSettings.swift, with its words from Core/NotchMascotStrings.swift (enUS). A slice of the
// island where it lives, bigger than life, that acts out a visit or a moment on request; Say Hi; and how it looks and
// behaves. Not ported: the Command Bar card, the menu bar drawn around the island and the sleeping z's. The
// Screenshot and Keep Awake moments wait for those features.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Notchly.App.Island;
using Notchly.App.Island.Mascot;
using Notchly.Core.Features;
using Notchly.Core.Notch;
using Notchly.Core.Notch.Mascot;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal static class MascotSettings
{
    // Upstream NotchMascotStrings.enUS, with "PC" for "Mac".
    public const string Title = "Companion";
    public const string Hint = "A little friend who lives in the Dynamic Island. It rests there when nothing else is showing, " +
        "and comes out over music and activities to say hello and react to what happens.";
    public const string Visits = "Appear now and then";
    public const string VisitsHint = "Every few minutes it passes through the island with a short animation.";
    public const string Frequency = "How often";
    public const string Reactions = "React to what happens";
    public const string ReactionsHint = "It comes out to react to music, timers, downloads, the microphone, unlocking your PC and more.";
    public const string HidesWhenIdle = "Hide when idle";
    public const string HidesWhenIdleHint = "After half a minute with nothing going on, it hops into the island and the island goes back " +
        "to its usual size. Visits and reactions still bring it out.";
    private const string SayHi = "Say Hi", SayHiHint = "Plays a visit in the Dynamic Island now.";
    private const string MomentsTitle = "See how it reacts";
    private const string PreviewHint = "Click to see it react.";
    private const string PetTip = "Rest the pointer on it to pet it. Left alone, it dozes off until you come back.";
    private const string LivesInIsland = "The companion lives in the Dynamic Island.";

    public static readonly (string Value, string Label)[] Frequencies =
        [("rare", "Rarely"), ("normal", "Sometimes"), ("frequent", "Often")];

    private static string Name(MascotStyle style) => style == MascotStyle.Robot ? "Robot" : "Minimal";

    private static string Name(MascotShape shape) => shape switch
    {
        MascotShape.Egg => "Egg", MascotShape.Squircle => "Rounded square", MascotShape.Pill => "Pill", _ => "Ball",
    };

    private static string Name(MascotPalette palette) => palette switch
    {
        MascotPalette.Lemon => "Lemon", MascotPalette.Peach => "Peach", MascotPalette.Rose => "Rose", MascotPalette.Lilac => "Lilac",
        MascotPalette.Sky => "Sky", MascotPalette.Mint => "Mint", _ => "Pearl",
    };

    private static string Name(MascotSide side) => side == MascotSide.Right ? "Right" : "Left";

    /// <summary>The moments the preview acts out, named as the island names them elsewhere.</summary>
    private static readonly (MascotMoment Moment, string Title, string Glyph)[] Moments =
    [
        (MascotMoment.Visit, "A visit", ""),
        (MascotMoment.TimerStarted, "Timer started", ""),
        (MascotMoment.TimeIsUp, "Time is up", ""),
        (MascotMoment.Music, "Now Playing", ""),
        (MascotMoment.Agents, "AI Agents", ""),
        (MascotMoment.EventStarts, "Happening now", ""),
        (MascotMoment.DownloadFinished, "Download complete", ""),
        (MascotMoment.DownloadFailed, "Download failed", ""),
        (MascotMoment.MicMuted, "Mic Mute on", ""),
        (MascotMoment.Charging, "Charging", ""),
        (MascotMoment.LowBattery, "Low battery", ""),
        (MascotMoment.Unlocked, "PC unlocked", ""),
    ];

    private static MascotLook Look(SettingsStore settings) => MascotSupport.Look(settings);

    private static FontFamily Icons => new("Segoe Fluent Icons");

    // MARK: Showing parts only when they apply

    /// <summary>Shows <paramref name="element"/> only while <paramref name="shows"/> holds, checked as settings change.</summary>
    public static T ShowWhen<T>(SettingsStore settings, T element, Func<bool> shows) where T : FrameworkElement
    {
        void Apply() => element.Visibility = shows() ? Visibility.Visible : Visibility.Collapsed;
        void OnChanged(string _) => element.Dispatcher.BeginInvoke(Apply);
        Apply();
        element.Loaded += (_, _) => { settings.Changed -= OnChanged; settings.Changed += OnChanged; Apply(); };
        element.Unloaded += (_, _) => settings.Changed -= OnChanged;
        return element;
    }

    /// <summary>The island is installed and on.</summary>
    private static bool IslandOn(SettingsStore settings) => settings.IsAvailable(AppFeature.Notch) && settings.GetBool(DefaultsKey.NotchEnabled);

    /// <summary>It rests beside a drawn camera, so the side it takes matters. A capsule holds it in the middle.</summary>
    public static bool RestsBesideCamera(SettingsStore settings) =>
        NotchEnum.Parse(settings.GetString(DefaultsKey.NotchSilhouette), NotchSilhouette.Notch) == NotchSilhouette.Notch;

    /// <summary>With the island off the companion has nowhere to live, and the page says so.</summary>
    public static FrameworkElement IslandNote(SettingsStore settings) => ShowWhen(settings, new TextBlock
    {
        Text = LivesInIsland + " Turn on Show the Dynamic Island above to see it there.",
        TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8),
    }, () => !IslandOn(settings));

    // MARK: The stage

    /// <summary>The preview stage, the moments and Say Hi, in one card (upstream NotchMascotStageCard).</summary>
    public static FrameworkElement Stage(App app) => new MascotPreview(app).Card;

    // MARK: Appearance

    /// <summary>Style tiles, shape tiles (minimal only) and colour swatches, each drawing the companion it picks.</summary>
    public static FrameworkElement Appearance(SettingsStore settings)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var styles = new WrapPanel();
        foreach (var style in Enum.GetValues<MascotStyle>())
            styles.Children.Add(Tile(settings, Name(style), DefaultsKey.NotchMascotStyle, MascotSupport.Raw(style),
                () => Look(settings) with { Style = style }, 64, 42, 30));
        stack.Children.Add(Row("Style", "", styles));

        var shapes = new WrapPanel();
        foreach (var shape in Enum.GetValues<MascotShape>())
            shapes.Children.Add(Tile(settings, Name(shape), DefaultsKey.NotchMascotShape, MascotSupport.Raw(shape),
                () => Look(settings) with { Style = MascotStyle.Minimal, Shape = shape }, 58, 40, 24));
        stack.Children.Add(ShowWhen(settings, Row("Shape", "", shapes), () => Look(settings).Style == MascotStyle.Minimal));

        var swatches = new WrapPanel();
        foreach (var palette in Enum.GetValues<MascotPalette>()) swatches.Children.Add(Swatch(settings, palette));
        stack.Children.Add(Row("Color", "", swatches));
        return stack;
    }

    /// <summary>Which side of the camera it rests on, each tile showing it there.</summary>
    public static FrameworkElement SideRow(SettingsStore settings)
    {
        var tiles = new WrapPanel();
        foreach (var side in Enum.GetValues<MascotSide>())
            tiles.Children.Add(Tile(settings, Name(side), DefaultsKey.NotchMascotSide, MascotSupport.Raw(side),
                () => Look(settings), 96, 30, 0, side));
        return ShowWhen(settings, Row("Side of the camera", "", tiles), () => RestsBesideCamera(settings));
    }

    private static CardControl Row(string title, string glyph, FrameworkElement content) => new()
    {
        Header = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontSize = 14 },
                content,
            },
        },
        Icon = new FontIcon { Glyph = glyph, FontFamily = Icons },
        Margin = new Thickness(0, 0, 0, 4),
    };

    /// <summary>
    /// A choice drawn as the companion it gives, on black, outlined in the accent colour while chosen. A side tile
    /// draws a little island with the companion beside its camera instead.
    /// </summary>
    private static FrameworkElement Tile(SettingsStore settings, string title, string key, string value, Func<MascotLook> look,
                                         double width, double height, double size, MascotSide? side = null)
    {
        var stage = new MascotStage(Clock);
        var view = new MascotView(stage);
        FrameworkElement art;
        if (side is { } resting)
        {
            // A small island: the camera in the middle, the companion in its wing.
            var track = MascotSupport.Track(width, height, 30, width - 60, false, height, resting);
            view.Width = track.Width;
            view.Height = track.Height;
            void Show() { stage.ShowOnTrack(look(), track, true, null, MascotMood.Idle, null, MascotService.ReduceMotion); view.Wake(); }
            Show();
            view.Loaded += (_, _) => Show();
            var island = new Grid { Width = width, Height = height };
            island.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(0, 0, height / 2, height / 2) });
            island.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(0x24, 0x28, 0x34)) });
            island.Children.Add(view);
            art = island;
            Refresher(settings, view, Show);
        }
        else
        {
            view.Width = view.Height = size;
            stage.ViewSize = new SizeD(size, size);
            void Show() { stage.ShowAlone(look(), size, MascotMood.Idle, idles: false, MascotService.ReduceMotion); view.Wake(); }
            Show();
            Refresher(settings, view, Show);
            art = new Border { Width = width, Height = height, Background = Brushes.Black, CornerRadius = new CornerRadius(height / 2), Child = view };
        }
        var outline = new Border
        {
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(height / 2 + 4), Padding = new Thickness(2), Child = art,
        };
        var label = new TextBlock { Text = title, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var tile = new StackPanel { Margin = new Thickness(0, 8, 10, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = title };
        tile.Children.Add(outline);
        tile.Children.Add(label);
        void Select() => outline.BorderBrush = settings.GetString(key) == value ? AccentBrush() : Brushes.Transparent;
        Select();
        Refresher(settings, tile, Select);
        tile.MouseLeftButtonUp += (_, _) => settings.Set(key, value);
        return tile;
    }

    /// <summary>A round swatch of a palette's two colours, ringed in the accent colour while chosen.</summary>
    private static FrameworkElement Swatch(SettingsStore settings, MascotPalette palette)
    {
        var raw = MascotSupport.Raw(palette);
        var dot = new Ellipse
        {
            Width = 22, Height = 22, Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)), StrokeThickness = 0.5,
            Fill = new LinearGradientBrush(MascotView.ToColor(palette.Light()), MascotView.ToColor(palette.Shade()), 90),
        };
        var ring = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(15), Padding = new Thickness(2), Child = dot };
        var host = new Border
        {
            Child = ring, Margin = new Thickness(0, 8, 6, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = Name(palette),
        };
        System.Windows.Automation.AutomationProperties.SetName(host, Name(palette));
        void Select() => ring.BorderBrush = MascotSupport.Look(settings).Palette == palette ? AccentBrush() : Brushes.Transparent;
        Select();
        Refresher(settings, host, Select);
        host.MouseLeftButtonUp += (_, _) => settings.Set(DefaultsKey.NotchMascotPalette, raw);
        return host;
    }

    private static Brush AccentBrush() =>
        Application.Current.TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? new SolidColorBrush(SystemParameters.WindowGlassColor);

    /// <summary>Runs <paramref name="refresh"/> on the element's dispatcher whenever a setting changes while it is shown.</summary>
    private static void Refresher(SettingsStore settings, FrameworkElement element, Action refresh)
    {
        void OnChanged(string _) => element.Dispatcher.BeginInvoke(refresh);
        element.Loaded += (_, _) => { settings.Changed -= OnChanged; settings.Changed += OnChanged; };
        element.Unloaded += (_, _) => settings.Changed -= OnChanged;
    }

    private static readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The settings' own clock for the companions they draw.</summary>
    private static double Clock() => Watch.Elapsed.TotalSeconds;

    // MARK: Preview

    /// <summary>
    /// The companion in a slice of the island as it rests there, bigger: it blinks, its eyes follow the pointer, a
    /// moment's rest of the pointer on it pets it, and it acts out a visit or a moment on request. Switched off, it
    /// sleeps (upstream NotchMascotStageCard and NotchMascotStage).
    /// </summary>
    private sealed class MascotPreview
    {
        private const double StageHeight = 104;
        private readonly App _app;
        private readonly SettingsStore _settings;
        private readonly MascotView _view;
        private readonly Canvas _canvas = new() { ClipToBounds = true };
        private readonly Path _island = new() { Fill = Brushes.Black };
        private readonly Ellipse _lens = new() { Fill = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x2A)) };
        private readonly Border _caption = new()
        {
            CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 4, 10, 4), Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x20, 0x22, 0x28)),
        };
        private readonly TextBlock _captionText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 8, 0, 0) };
        private readonly Button _sayHi;
        private readonly TextBlock _tip;
        private readonly DispatcherTimer _playing, _look;
        private MascotTrack? _track;
        private MascotVisit? _visit;
        private MascotReactionEvent? _reaction;
        private MascotMoment? _moment;
        private int _next;
        private (bool Awake, MascotSide Side, bool Visits, bool Reactions, bool Hides, MascotLook Look)? _seen;
        private Action? _then;

        public MascotPreview(App app)
        {
            _app = app;
            _settings = app.Settings;
            _view = new MascotView(new MascotStage(Clock));
            _playing = new DispatcherTimer();
            _playing.Tick += (_, _) => { _playing.Stop(); var then = _then; _then = null; then?.Invoke(); };
            _look = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.35) };
            _look.Tick += (_, _) => { _look.Stop(); if (_visit is null && Awake) React(MascotReaction.Celebrate); };

            var background = new LinearGradientBrush(Color.FromArgb(0x4D, 0x4C, 0x8D, 0xFF), Color.FromArgb(0x14, 0x4C, 0x8D, 0xFF), 35);
            var stage = new Border
            {
                Height = StageHeight, CornerRadius = new CornerRadius(12), Background = background, ClipToBounds = true,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0x80, 0x80, 0x80)), BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, Child = _canvas, ToolTip = null,
            };
            System.Windows.Automation.AutomationProperties.SetName(stage, "Companion preview");
            System.Windows.Automation.AutomationProperties.SetHelpText(stage, PreviewHint);
            _caption.Child = new StackPanel { Orientation = Orientation.Horizontal };
            _canvas.Children.Add(_island);
            _canvas.Children.Add(_lens);
            _canvas.Children.Add(_view);
            _canvas.Children.Add(_caption);
            _canvas.SizeChanged += (_, _) => Layout();
            stage.MouseLeftButtonUp += (_, _) => PlayNext();
            stage.MouseEnter += (_, e) => Pointer(e, 0);
            stage.MouseMove += (_, e) => Pointer(e, 1);
            stage.MouseLeave += (_, e) => Pointer(e, 2);

            _sayHi = new Button { Content = SayHi, ToolTip = SayHiHint, Appearance = ControlAppearance.Secondary, Padding = new Thickness(10, 4, 10, 4) };
            _sayHi.Click += (_, _) =>
            {
                // Out of room in the island just now, it says hello here instead.
                if (_app.Features.Get<IslandService>(AppFeature.Notch)?.GreetMascot() != true) Play(MascotMoment.Visit);
            };
            var header = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = true };
            DockPanel.SetDock(_sayHi, Dock.Right);
            header.Children.Add(_sayHi);
            header.Children.Add(new TextBlock { Text = MomentsTitle, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            foreach (var (moment, title, glyph) in Moments) _chips.Children.Add(Chip(moment, title, glyph));
            _tip = new TextBlock { Text = PreviewHint + " " + PetTip, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

            var stack = new StackPanel();
            stack.Children.Add(stage);
            stack.Children.Add(header);
            stack.Children.Add(_chips);
            stack.Children.Add(_tip);
            Card = new Border
            {
                Child = stack, Padding = new Thickness(16), CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 0, 8),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
            };
            Refresher(_settings, Card, OnSettings);
            Card.Unloaded += (_, _) =>
            {
                _playing.Stop();
                _look.Stop();
                // Never left hidden for the next time the page shows.
                if (_visit?.Kind == MascotSupport.HideAway) _visit = null;
            };
            OnSettings();
        }

        public Border Card { get; }

        /// <summary>Switched on; switched off, it sleeps here.</summary>
        private bool Awake => _settings.IsAvailable(AppFeature.NotchMascot) && _settings.GetBool(DefaultsKey.NotchMascotEnabled);

        private FrameworkElement Chip(MascotMoment moment, string title, string glyph)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Button { Content = row, Appearance = ControlAppearance.Secondary, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), Tag = moment };
            chip.Click += (_, _) => Play(moment);
            return chip;
        }

        /// <summary>The island slice at its real size, scaled to fit: the companion's track, its camera and the island.</summary>
        private void Layout()
        {
            var width = _canvas.ActualWidth;
            if (width <= 0) return;
            var bar = Math.Clamp(_settings.GetDouble(DefaultsKey.NotchHeight, 24), 16, 40);
            var silhouette = RestsBesideCamera(_settings) ? NotchSilhouette.Notch : NotchSilhouette.Capsule;
            var g = new NotchGeometry(new RectD(0, 0, 1920, 1080), bar, silhouette: silhouette);
            SizeD strip;
            MascotTrack track;
            if (g.Floats)
            {
                strip = g.RestingSize(showsContent: false);
                track = MascotSupport.Track(strip.Width, g.StripHeight, 0, 0, true, g.StripBodyHeight);
            }
            else
            {
                // The preview always shows where it would rest, with both wings.
                const double wing = 44;
                strip = new SizeD(g.CameraWidth + wing * 2, g.StripHeight);
                track = MascotSupport.Track(strip.Width, strip.Height, wing, g.CameraWidth, false, g.StripBodyHeight,
                                            MascotSupport.Side(_settings));
            }
            var scale = Math.Max(1, Math.Min(2, Math.Min(width * 0.66 / strip.Width, (StageHeight - 36) / strip.Height)));
            _track = track.Scaled(scale);
            var size = new SizeD(strip.Width * scale, strip.Height * scale);
            var left = (width - size.Width) / 2;
            _island.Data = NotchWindow.ToGeometry(NotchLayout.IslandPath(size, g.FloatingGap is { } gap ? gap * scale : null));
            Canvas.SetLeft(_island, left);
            _island.Clip = null;
            _lens.Visibility = g.Floats ? Visibility.Collapsed : Visibility.Visible;
            _lens.Width = _lens.Height = 6 * scale;
            Canvas.SetLeft(_lens, width / 2 - 3 * scale);
            Canvas.SetTop(_lens, size.Height / 2 - 3 * scale);
            _view.Width = _track.Width;
            _view.Height = _track.Height;
            _view.Clip = _island.Data;
            Canvas.SetLeft(_view, left);
            Canvas.SetTop(_caption, StageHeight - 34);
            Show();
        }

        private void Show()
        {
            if (_track is not { } track) return;
            _view.Stage.ShowOnTrack(Look(_settings), track, true, _visit, MascotMood.Idle, _reaction, MascotService.ReduceMotion, Awake);
            _view.Wake();
            _caption.Visibility = _moment is null ? Visibility.Collapsed : Visibility.Visible;
            if (_moment is { } moment)
            {
                var (_, title, glyph) = Moments.First(m => m.Moment == moment);
                var row = (StackPanel)_caption.Child;
                row.Children.Clear();
                row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                row.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                _caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_caption, (_canvas.ActualWidth - _caption.DesiredSize.Width) / 2);
            }
            var awake = Awake;
            foreach (var chip in _chips.Children.OfType<FrameworkElement>())
            {
                chip.IsEnabled = awake;
                chip.Opacity = _visit is not null && !Equals(chip.Tag, _moment) ? 0.45 : 1;
            }
            _tip.Opacity = awake ? 0.7 : 0.35;
            _sayHi.IsEnabled = MascotSupport.IsEnabled(_settings);
        }

        private void Pointer(MouseEventArgs e, int phase)
        {
            var at = e.GetPosition(_view);
            var point = new PointD(at.X, at.Y);
            if (phase == 0) _view.Stage.PointerEntered(point);
            else if (phase == 1) _view.Stage.PointerMoved(point);
            else _view.Stage.PointerExited();
            _view.Wake();
        }

        /// <summary>What changed in Settings, it shows: waking or dozing, crossing sides, a visit, a perk, hiding.</summary>
        private void OnSettings()
        {
            (bool Awake, MascotSide Side, bool Visits, bool Reactions, bool Hides, MascotLook Look) now =
                (Awake, MascotSupport.Side(_settings), _settings.GetBool(DefaultsKey.NotchMascotVisits),
                 _settings.GetBool(DefaultsKey.NotchMascotReactions), _settings.GetBool(DefaultsKey.NotchMascotHidesWhenIdle), Look(_settings));
            var seen = _seen;
            _seen = now;
            Layout();
            if (seen is not { } before) return;
            if (before.Awake != now.Awake)
            {
                // Switched on, it wakes up with a stretch; off, it yawns and dozes off.
                After(0, null);
                _moment = null;
                if (_visit?.Kind == MascotSupport.HideAway) _visit = null;
                React(now.Awake ? MascotReaction.WakeUp : MascotReaction.Yawn);
                return;
            }
            if (!now.Awake) return;
            if (before.Side != now.Side && _visit is null) StartVisit(MascotVisitKind.Cross, MascotMood.Wink);
            else if (!before.Visits && now.Visits) Play(MascotMoment.Visit);
            else if (!before.Reactions && now.Reactions && _visit is null) React(MascotReaction.Perk);
            else if (!before.Hides && now.Hides && _visit is null)
            {
                // It shows how it goes into the island, and a moment later comes back out.
                _moment = null;
                var away = StartVisit(MascotSupport.HideAway, MascotMood.Idle, clears: false);
                After(away.Duration + 0.8, () => { if (_visit?.Id == away.Id) StartVisit(MascotVisitKind.Arrive, MascotMood.Wink); });
            }
            else if (before.Look != now.Look) { _look.Stop(); _look.Start(); }
        }

        private void PlayNext()
        {
            if (!Awake || _visit is not null) return;
            var reactions = Moments.Where(m => m.Moment.Reaction() is not null).ToArray();
            Play(reactions[_next++ % reactions.Length].Moment);
        }

        private void Play(MascotMoment moment)
        {
            if (!Awake || _visit is not null) return;
            _moment = moment;
            double length;
            if (moment.Reaction() is { } reaction)
            {
                React(reaction);
                length = reaction.Length() + 0.15;
            }
            else length = StartVisit(MascotVisitKind.Lap, MascotMood.Wink).Duration;
            if (moment.FollowUp() is not { } followUp)
            {
                After(length, () => { _moment = null; Show(); });
                return;
            }
            // The second beat, then the caption goes once it is over too.
            After(length + 0.2, () =>
            {
                if (_moment != moment) return;
                React(followUp);
                After(followUp.Length() + 0.15, () => { _moment = null; Show(); });
            });
            Show();
        }

        private void React(MascotReaction reaction)
        {
            _reaction = MascotReactionEvent.Now(reaction, Clock());
            Show();
        }

        /// <summary>Starts a stroll and clears it once it is over.</summary>
        private MascotVisit StartVisit(MascotVisitKind kind, MascotMood greeting, bool clears = true)
        {
            var started = MascotVisit.Now(kind, greeting, Clock());
            _visit = started;
            Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(started.Duration) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                // Gone into the island to show how it hides, it stays there until it comes back out.
                if (_visit?.Id != started.Id || !clears) return;
                _visit = null;
                Show();
            };
            timer.Start();
            return started;
        }

        private void After(double seconds, Action? then)
        {
            _playing.Stop();
            _then = then;
            if (then is null) return;
            _playing.Interval = TimeSpan.FromSeconds(Math.Max(0.01, seconds));
            _playing.Start();
        }
    }
}
