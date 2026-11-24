// SPDX-License-Identifier: GPL-3.0-or-later
// The island's Watch page: the Windows counterpart of upstream UI/Notch/NotchWatchView.swift (NotchWatchSetupView,
// NotchWatchAreaCard, NotchWatchRuleRow and the footer). Windows-specific: what is watched and its two actions sit
// in the page's own top row rather than beside the camera, menus are the island's context menus, and setup points
// at Windows' language settings when no text recognition language is installed.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Platform.Capture;
using Notchly.App.Platform.Ocr;
using Notchly.App.Platform.Shell;
using Notchly.Core.Notch;

namespace Notchly.App.Island.Watch;

public sealed class WatchPage
{
    /// <summary>Upstream NotchWatchStrings, in English.</summary>
    public const string Title = "Watch",
        Description = "Turn any part of any window into a live activity in the Dynamic Island, with an alert when it changes, finishes or shows what you are waiting for.",
        SetupHint = "Choose any part of a window, like a progress bar, a build status or a score. The island reads it for you and tells you when it changes.",
        Choose = "Choose Area", ChooseAgain = "Choose Another Area", StopTitle = "Stop Watching", WatchAgainTitle = "Watch Again",
        TellMe = "Tell me", SettlesHint = "After it changes, it must stay still for 30 seconds.",
        TextPlaceholder = "Text, like Done", NumberPlaceholder = "Number, like 100", NoText = "No text",
        InIsland = "Show in the island", Automatic = "Automatic",
        HiddenHint = "The window is hidden. Watching continues when it returns.";
    public const string EyeGlyph = "", HiddenGlyph = "";
    private const string NoOcrHint = "Watch reads text with Windows' own text recognition, which needs a language with " +
                                     "optical character recognition. Add one in Windows' language settings.";
    private const string NoCaptureHint = "This PC can't capture windows, which Watch needs.";

    public static readonly Color Tint = Color.FromRgb(0xBF, 0x5A, 0xF2);
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Soft = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CardBack = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TileBack = Frozen(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush FieldBack = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Veil = Frozen(Color.FromArgb(0x99, 0, 0, 0));
    private static readonly SolidColorBrush Green = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly SolidColorBrush Purple = Frozen(Tint);

    private readonly NotchWindow _window;
    private readonly WatchService _watch;
    private readonly Action _chooseArea;
    private readonly Action<bool> _modal;
    private readonly Action _focus;
    private readonly Grid _root;
    private readonly FrameworkElement _setup, _watching;
    private readonly TextBlock _setupText;
    private readonly Button _setupAction;
    private readonly TextBlock _header, _readingText, _full, _status, _since, _settlesHint, _outcomeGlyph, _placeholder;
    private readonly Image _preview;
    private readonly Border _veil;
    private readonly Button _headlineButton, _conditionButton, _again, _chooseAnother;
    private readonly TextBox _field;
    private readonly Grid _fieldBox;
    private readonly StackPanel _finishedRow;
    private bool _visible;
    private NotchWatchCondition _shownCondition = (NotchWatchCondition)(-1);

    /// <param name="chooseArea">Closes the island and opens the area picker.</param>
    /// <param name="modal">Keeps the island open while one of the page's menus, outside it, is up.</param>
    /// <param name="focus">Lets the island take the keyboard, for typing a word or a number.</param>
    public WatchPage(NotchWindow window, WatchService watch, Action chooseArea, Action<bool> modal, Action focus)
    {
        _window = window;
        _watch = watch;
        _chooseArea = chooseArea;
        _modal = modal;
        _focus = focus;
        _root = window.WatchPage;
        _watch.Changed += () => { if (_visible) Update(); };

        // Before a watch: the eye in a tile beside the hint and Choose Area (upstream NotchWatchSetupView).
        var setup = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 400, Margin = new Thickness(12, 0, 12, 0) };
        setup.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        setup.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        setup.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        setup.Children.Add(new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(18), Background = TileBack, VerticalAlignment = VerticalAlignment.Center,
            Child = Glyph(EyeGlyph, 26, Dim),
        });
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _setupText = new TextBlock { FontSize = 12, Foreground = Soft, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        copy.Children.Add(_setupText);
        _setupAction = Chip(Choose, OnSetupAction);
        _setupAction.HorizontalAlignment = HorizontalAlignment.Left;
        _setupAction.Margin = new Thickness(0, 10, 0, 0);
        copy.Children.Add(_setupAction);
        Grid.SetColumn(copy, 2);
        setup.Children.Add(copy);
        _setup = setup;

        // While watching: what is watched, the live area and its reading, the rule, and the footer.
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        var stop = IconButton("", StopTitle, () => _watch.Stop());
        _chooseAnother = IconButton("", ChooseAgain, () => _chooseArea());
        DockPanel.SetDock(stop, Dock.Right);
        DockPanel.SetDock(_chooseAnother, Dock.Right);
        top.Children.Add(stop);
        top.Children.Add(_chooseAnother);
        var eye = Glyph(EyeGlyph, 13, Purple);
        eye.Margin = new Thickness(2, 0, 6, 0);
        DockPanel.SetDock(eye, Dock.Left);
        top.Children.Add(eye);
        _header = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(_header);
        Grid.SetRow(top, 0);
        page.Children.Add(top);

        var card = new Grid();
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _preview = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        _status = new TextBlock { FontSize = 11, Foreground = Soft, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.None };
        _veil = new Border
        {
            Background = Veil, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = _status,
        };
        var area = new Grid { Margin = new Thickness(0, 0, 0, 8), ClipToBounds = true };
        area.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _preview });
        area.Children.Add(_veil);
        card.Children.Add(area);

        var reading = new StackPanel { Orientation = Orientation.Horizontal };
        _outcomeGlyph = Glyph("", 13, Green);
        _outcomeGlyph.Margin = new Thickness(0, 0, 8, 0);
        reading.Children.Add(_outcomeGlyph);
        _readingText = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        reading.Children.Add(_readingText);
        _headlineButton = new Button
        {
            Style = (Style)window.Resources["ChipButton"], Margin = new Thickness(0), Padding = new Thickness(8, 3, 8, 3), ToolTip = InIsland,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis },
                    Glyph("", 9, Dim, new Thickness(6, 2, 0, 0)),
                },
            },
        };
        _headlineButton.Click += (_, _) => ShowHeadlineMenu();
        reading.Children.Add(_headlineButton);
        Grid.SetRow(reading, 1);
        card.Children.Add(reading);
        _full = new TextBlock { FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(_full, 2);
        card.Children.Add(_full);
        var cardBorder = new Border { Background = CardBack, CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Child = card };
        Grid.SetRow(cardBorder, 1);
        page.Children.Add(cardBorder);

        // When to speak up (upstream NotchWatchRuleRow).
        var rule = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var ruleRow = new StackPanel { Orientation = Orientation.Horizontal };
        ruleRow.Children.Add(new TextBlock { Text = TellMe, FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _conditionButton = new Button { Style = (Style)window.Resources["ChipButton"], Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        _conditionButton.Click += (_, _) => ShowConditionMenu();
        ruleRow.Children.Add(_conditionButton);
        _field = new TextBox
        {
            Width = 170, Height = 24, FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0),
        };
        // The island opens without the keyboard; a click in the field asks for it.
        _field.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_field.IsKeyboardFocused) return;
            _focus();
            _field.Dispatcher.BeginInvoke(() => _field.Focus());
        };
        _field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        _field.LostKeyboardFocus += (_, _) => Apply();
        _field.TextChanged += (_, _) => _placeholder!.Visibility = _field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _placeholder = new TextBlock { FontSize = 12, Foreground = Dim, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        _fieldBox = new Grid { Children = { new Border { Background = FieldBack, CornerRadius = new CornerRadius(7) }, _placeholder, _field } };
        ruleRow.Children.Add(_fieldBox);
        rule.Children.Add(ruleRow);
        _settlesHint = new TextBlock { Text = SettlesHint, FontSize = 10, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        rule.Children.Add(_settlesHint);
        Grid.SetRow(rule, 2);
        page.Children.Add(rule);

        var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        _since = new TextBlock { FontSize = 10, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(_since);
        _finishedRow = new StackPanel { Orientation = Orientation.Horizontal };
        _again = Chip(WatchAgainTitle, () => _watch.WatchAgain());
        _finishedRow.Children.Add(_again);
        _finishedRow.Children.Add(Chip(ChooseAgain, () => _chooseArea()));
        footer.Children.Add(_finishedRow);
        Grid.SetRow(footer, 3);
        page.Children.Add(footer);
        _watching = page;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];

    private static TextBlock Glyph(string glyph, double size, Brush brush, Thickness? margin = null) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = brush, TextTrimming = TextTrimming.None,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? default,
    };

    private Button Chip(string text, Action act)
    {
        var button = new Button { Style = (Style)_window.Resources["ChipButton"], Content = text };
        button.Click += (_, _) => act();
        return button;
    }

    private Button IconButton(string glyph, string tip, Action act)
    {
        var button = new Button { Style = (Style)_window.Resources["IconButton"], Content = glyph, FontSize = 12, ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => act();
        return button;
    }

    /// <summary>The page fills the preset's height, as the camera's does.</summary>
    public void Layout(double height)
    {
        _root.Height = Math.Max(NotchLayout.CameraMinimumHeight, height);
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            _watch.PageVisible = value;
            if (value) Update();
        }
    }

    private void OnSetupAction()
    {
        if (!TextRecognizer.IsAvailable) ShellActions.Open("ms-settings:regionlanguage");
        else _chooseArea();
    }

    // MARK: Drawing

    public void Update()
    {
        var watching = _watch.Target is not null;
        var shown = watching ? _watching : _setup;
        if (_root.Children.Count != 1 || !ReferenceEquals(_root.Children[0], shown))
        {
            _root.Children.Clear();
            _root.Children.Add(shown);
        }
        if (!watching)
        {
            var capture = WindowCapture.IsSupported;
            var ocr = TextRecognizer.IsAvailable;
            _setupText.Text = !capture ? NoCaptureHint : !ocr ? NoOcrHint : SetupHint;
            _setupAction.Content = ocr ? Choose : "Open language settings";
            _setupAction.Visibility = capture ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var target = _watch.Target!;
        _header.Text = target.Title;
        _header.ToolTip = target.Title;
        _preview.Source = _watch.Preview;
        _preview.Opacity = _watch.State == WatchState.Hidden ? 0.35 : 1;
        var hidden = _watch.State == WatchState.Hidden;
        var waiting = _watch.Preview is null && _watch.State == WatchState.Watching;
        _status.Text = hidden ? HiddenHint : "Reading…";
        _veil.Visibility = hidden || waiting ? Visibility.Visible : Visibility.Collapsed;

        var finished = _watch.State == WatchState.Finished;
        var lines = NotchWatchSupport.Lines(_watch.Text);
        _outcomeGlyph.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        if (finished && _watch.Outcome is { } outcome)
        {
            var closed = outcome is NotchWatchOutcome.Closed;
            _outcomeGlyph.Text = closed ? "" : "";
            _outcomeGlyph.Foreground = closed ? Dim : Green;
            _readingText.Text = outcome.Title;
            _readingText.FontSize = 13;
            _readingText.Foreground = Brushes.White;
        }
        else
        {
            _readingText.Text = NoText;
            _readingText.FontSize = 15;
            _readingText.Foreground = Dim;
        }
        var menu = !finished && lines.Count > 0;
        _readingText.Visibility = menu ? Visibility.Collapsed : Visibility.Visible;
        _headlineButton.Visibility = menu ? Visibility.Visible : Visibility.Collapsed;
        if (menu && _headlineButton.Content is StackPanel { Children: [TextBlock headline, ..] })
            headline.Text = _watch.Headline.Length == 0 ? NoText : _watch.Headline;
        var full = _watch.Text.Replace('\n', ' ');
        _full.Text = full;
        _full.Visibility = full.Length > 0 && full != _watch.Headline ? Visibility.Visible : Visibility.Collapsed;

        _conditionButton.Content = NotchWatchSupport.Title(_watch.Condition);
        var typed = _watch.Condition is NotchWatchCondition.Contains or NotchWatchCondition.Reaches;
        _fieldBox.Visibility = typed ? Visibility.Visible : Visibility.Collapsed;
        if (_shownCondition != _watch.Condition)
        {
            _shownCondition = _watch.Condition;
            _field.Text = _watch.Condition == NotchWatchCondition.Reaches
                ? _watch.MatchNumber is { } number ? NotchWatchSupport.Formatted(number, CultureInfo.CurrentCulture) : ""
                : _watch.MatchText;
            _placeholder.Text = _watch.Condition == NotchWatchCondition.Reaches ? NumberPlaceholder : TextPlaceholder;
        }
        _settlesHint.Visibility = _watch.Condition == NotchWatchCondition.Settles ? Visibility.Visible : Visibility.Collapsed;

        _finishedRow.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        _again.Visibility = _watch.CanWatchAgain ? Visibility.Visible : Visibility.Collapsed;
        _since.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        _since.Text = _watch.StartedAt is { } started ? $"Watching since {started.ToString("t", CultureInfo.CurrentCulture)}" : "";
    }

    private void Apply()
    {
        if (_watch.Condition == NotchWatchCondition.Reaches) _watch.SetMatchNumber(_field.Text);
        else _watch.SetMatchText(_field.Text);
    }

    // MARK: Menus

    private ContextMenu Menu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, Style = (Style)_window.Resources["IslandMenu"] };
        menu.Resources[typeof(MenuItem)] = _window.Resources["IslandMenuItem"];
        _modal(true);
        menu.Closed += (_, _) => _modal(false);
        return menu;
    }

    /// <summary>What the island shows, picked automatically or chosen line by line.</summary>
    private void ShowHeadlineMenu()
    {
        var menu = Menu(_headlineButton);
        menu.Items.Add(new MenuItem { Header = InIsland, IsEnabled = false });
        var automatic = new MenuItem { Header = Automatic, IsChecked = _watch.HeadlineLine is null };
        automatic.Click += (_, _) => _watch.SetHeadlineLine(null);
        menu.Items.Add(automatic);
        var lines = NotchWatchSupport.Lines(_watch.Text);
        for (var i = 0; i < Math.Min(12, lines.Count); i++)
        {
            var index = i;
            var item = new MenuItem { Header = lines[i], IsChecked = _watch.HeadlineLine == i };
            item.Click += (_, _) => _watch.SetHeadlineLine(index);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ShowConditionMenu()
    {
        var menu = Menu(_conditionButton);
        foreach (var condition in Enum.GetValues<NotchWatchCondition>())
        {
            var item = new MenuItem { Header = NotchWatchSupport.Title(condition), IsChecked = condition == _watch.Condition };
            item.Click += (_, _) => _watch.SetCondition(condition);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
