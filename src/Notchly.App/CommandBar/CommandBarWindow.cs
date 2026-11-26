// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream UI/CommandBar/CommandBarView.swift: the palette. A 560 wide acrylic window with one field and, under it, the rows the
// service offers (up to twelve while searching, a longer browsable list while empty). Keys: Up/Down or Ctrl+P/N move (and wrap), Return
// runs, Tab completes, Esc steps back (a prompt, then the text, then the window), Alt+1…9 runs the row at that position. A click on
// another window closes it. It opens with its top at 28% of the work area of the display the pointer is on, where you left it.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Notchly.App.Platform;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Windowing;
using Notchly.Core.CommandBar;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;

namespace Notchly.App.CommandBar;

internal sealed class CommandBarWindow : FluentWindow
{
    public const double PanelWidth = 560, FieldHeight = 50, ListCeiling = 452, RowHeight = 46;

    private static readonly Brush Soft = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)), Plate = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                                  Hover = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Accent = Frozen(Color.FromRgb(0x4C, 0x8D, 0xFF)),
                                  Warn = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private readonly CommandBarService _service;
    private readonly TextBox _field = new() { FontSize = 18, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
    private readonly TextBlock _placeholder = new() { Text = CommandBarStrings.SearchPlaceholder, FontSize = 18, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _prompt = new() { FontSize = 12, Margin = new Thickness(18, 2, 18, 8), Foreground = Warn, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = ListCeiling };
    private readonly Border _separator = new() { Height = 1, Background = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed };
    private IReadOnlyList<CommandBarEntry>? _shown;
    private IReadOnlyDictionary<int, string>? _shownTitles;
    private readonly List<Border> _rowViews = [];
    private bool _updating, _closing;
    private Point _lastPointer;

    public CommandBarWindow(CommandBarService service)
    {
        _service = service;
        Title = CommandBarStrings.PageTitle;
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        MaxHeight = FieldHeight + ListCeiling + 60;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Acrylic;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        _field.Style = null;
        _field.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _field.SetResourceReference(TextBox.CaretBrushProperty, "TextFillColorPrimaryBrush");
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var glyph = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 16, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Drag to move · Double-click to recenter" };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        glyph.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Recenter(); else DragWindow(); e.Handled = true; };
        var input = new Grid { Height = FieldHeight, Margin = new Thickness(18, 0, 18, 0) };
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_field, 1);
        Grid.SetColumn(_placeholder, 1);
        input.Children.Add(glyph);
        input.Children.Add(_placeholder);
        input.Children.Add(_field);

        var root = new StackPanel { Margin = new Thickness(0, 28, 0, 8) };
        root.Children.Add(input);
        root.Children.Add(_prompt);
        root.Children.Add(_separator);
        root.Children.Add(_scroll);
        _scroll.Content = _list;
        Content = root;

        _field.TextChanged += (_, _) =>
        {
            if (_updating) return;
            _placeholder.Visibility = _field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _service.SetQuery(_field.Text);
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (!_closing && IsVisible) Close(); };
        Closing += (_, _) => _closing = true;
        _service.Changed += OnChanged;
        Closed += (_, _) => _service.Changed -= OnChanged;
        SourceInitialized += (_, _) => Place();
    }

    public void Open()
    {
        Show();
        Activate();
        _field.Focus();
        Render();
    }

    public void SetQueryText(string text)
    {
        _field.Text = text;
        _field.CaretIndex = text.Length;
        _field.Focus();
    }

    // MARK: Position

    private (RectD Work, double Scale) CurrentWork()
    {
        Native.GetCursorPos(out var p);
        var display = Displays.AtPoint(p.X, p.Y) ?? Displays.All().FirstOrDefault();
        var scale = display?.Scale ?? 1;
        var work = display?.WorkArea ?? new Native.RECT { Right = 1920, Bottom = 1080 };
        return (new RectD(work.Left / scale, work.Top / scale, work.Width / scale, work.Height / scale), scale);
    }

    private void Place()
    {
        var (work, scale) = CurrentWork();
        var offset = CommandBarPreferences.DecodePositionOffset(_service.Settings.GetString(DefaultsKey.CommandBarPositionOffset));
        var origin = CommandBarPreferences.ClampedPanelOrigin(new SizeD(PanelWidth, FieldHeight + 80), work, offset);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale), 0, 0, 0x0001 | 0x0010);
    }

    private void DragWindow()
    {
        try { DragMove(); } catch (InvalidOperationException) { return; }
        // The distance from where it opens by itself is what is kept.
        var (work, scale) = CurrentWork();
        var home = CommandBarPreferences.ClampedPanelOrigin(new SizeD(PanelWidth, FieldHeight + 80), work, new SizeD(0, 0));
        _service.Settings.Set(DefaultsKey.CommandBarPositionOffset, CommandBarPreferences.EncodePositionOffset(new SizeD(Left - home.X, Top - home.Y)));
        _field.Focus();
    }

    private void Recenter()
    {
        _service.Settings.Set(DefaultsKey.CommandBarPositionOffset, "");
        var (work, _) = CurrentWork();
        var origin = CommandBarPreferences.ClampedPanelOrigin(new SizeD(PanelWidth, FieldHeight + 80), work, new SizeD(0, 0));
        Left = origin.X;
        Top = origin.Y;
        _field.Focus();
    }

    // MARK: Keys

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed) return;   // an input method is composing: its keys are not ours
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        switch (key)
        {
            case Key.Escape:
                if (!_service.StepBack())
                {
                    if (_field.Text.Length > 0) _field.Clear();
                    else Close();
                }
                e.Handled = true;
                return;
            case Key.Down: _service.Move(1); e.Handled = true; return;
            case Key.Up: _service.Move(-1); e.Handled = true; return;
            case Key.N when ctrl: _service.Move(1); e.Handled = true; return;
            case Key.P when ctrl: _service.Move(-1); e.Handled = true; return;
            case Key.Enter: _service.RunSelected(ctrl); e.Handled = true; return;
            case Key.K when ctrl: _service.OpenActions(); e.Handled = true; return;
            case Key.Tab: Complete(); e.Handled = true; return;
        }
        if (alt && key is >= Key.D1 and <= Key.D9)
        {
            var index = key - Key.D1;
            if (index < _service.Rows.Count) { _service.Select(index); _service.RunSelected(); }
            e.Handled = true;
        }
    }

    /// <summary>Tab reuses a calculator answer, or completes the field to the selected row's name.</summary>
    private void Complete()
    {
        if (_service.Selected is not { } entry || entry.IsAnswer && entry.Id != "math.result") return;
        string completion;
        if (entry.Id == "math.result" && Notchly.Core.CommandBar.CommandBarMath.Evaluate(_service.Query, System.Globalization.CultureInfo.CurrentCulture) is { } result)
            completion = Notchly.Core.CommandBar.CommandBarMath.ReusableExpression(result, System.Globalization.CultureInfo.CurrentCulture);
        else completion = CommandBarCompletion.CompletedQuery(_service.Query, entry.Title, entry.MatchTitle);
        SetQueryText(completion);
    }

    // MARK: Drawing

    private void OnChanged()
    {
        Render();
    }

    private CommandBarMode _shownMode;

    private void Render()
    {
        // While a name is typed the field holds it; when that ends the field holds the query again.
        if (_service.Mode == CommandBarMode.Alias && _shownMode != CommandBarMode.Alias)
        {
            _updating = true;
            _field.Text = _service.AliasDraft;
            _field.CaretIndex = _field.Text.Length;
            _updating = false;
        }
        else if (_shownMode == CommandBarMode.Alias && _service.Mode != CommandBarMode.Alias)
        {
            _updating = true;
            _field.Text = _service.Query;
            _field.CaretIndex = _field.Text.Length;
            _updating = false;
        }
        _shownMode = _service.Mode;
        _placeholder.Visibility = _field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var prompt = _service.Prompt;
        _prompt.Text = prompt;
        _prompt.Visibility = prompt.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var rows = _service.Rows;
        if (!ReferenceEquals(rows, _shown) || !ReferenceEquals(_service.Titles, _shownTitles)) Rebuild(rows);
        for (var i = 0; i < _rowViews.Count; i++) Paint(i);
        if (_service.SelectedIndex >= 0 && _service.SelectedIndex < _rowViews.Count) _rowViews[_service.SelectedIndex].BringIntoView();
        var any = rows.Count > 0 || _service.Query.Length > 0;
        _separator.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Rebuild(IReadOnlyList<CommandBarEntry> rows)
    {
        _shown = rows;
        _shownTitles = _service.Titles;
        _rowViews.Clear();
        _list.Children.Clear();
        if (rows.Count == 0 && _service.Query.Trim().Length > 0)
        {
            _list.Children.Add(new TextBlock { Text = CommandBarStrings.NoResultsTitle, Margin = new Thickness(18, 14, 18, 14), Foreground = Soft, FontSize = 14 });
            return;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (_service.Titles.TryGetValue(i, out var heading))
                _list.Children.Add(new TextBlock { Text = heading.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Soft, Margin = new Thickness(18, i == 0 ? 6 : 12, 18, 4) });
            var row = Row(rows[i], i);
            _rowViews.Add(row);
            _list.Children.Add(row);
        }
    }

    private void Paint(int index)
    {
        var selected = index == _service.SelectedIndex;
        _rowViews[index].Background = selected ? Hover : Brushes.Transparent;
    }

    private Border Row(CommandBarEntry entry, int index)
    {
        var iconHost = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        FillIcon(iconHost, entry.Icon);
        var title = new TextBlock { Text = entry.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        var subtitle = entry.Trouble switch
        {
            CommandBarTrouble.NeedsSetup t => CommandBarStrings.Format(CommandBarStrings.NeedsSetupFormat, t.FeatureTitle),
            CommandBarTrouble.NotInstalled t => CommandBarStrings.Format(CommandBarStrings.NotInstalledFormat, t.FeatureTitle),
            _ => entry.Subtitle,
        };
        if (subtitle.Length > 0)
            text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = Soft, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        if (entry.IsActive) right.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Accent, Margin = new Thickness(0, 0, 8, 0) });
        if (entry.AnswerValue is { } answer) right.Children.Add(new TextBlock { Text = answer, FontSize = 13, Foreground = Accent, FontWeight = FontWeights.SemiBold });
        else if (entry.MenuShortcut is { } shortcut) right.Children.Add(new TextBlock { Text = shortcut, FontSize = 11, Foreground = Soft });
        else if (index < 9 && !_service.Titles.ContainsKey(0) || index < 9) right.Children.Add(new TextBlock { Text = "Alt+" + (index + 1), FontSize = 10, Foreground = Frozen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)) });

        var grid = new Grid { Margin = new Thickness(10, 0, 10, 0), MinHeight = RowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(iconHost);
        grid.Children.Add(text);
        grid.Children.Add(right);
        var row = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(8, 1, 8, 1), Child = grid, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        row.MouseMove += (_, e) =>
        {
            var at = e.GetPosition(this);
            if (at == _lastPointer) return;
            _lastPointer = at;
            _service.Select(index);
        };
        row.MouseLeftButtonUp += (_, _) => { _service.Select(index); _service.RunSelected(); };
        return row;
    }

    private static void FillIcon(Border host, CommandBarIcon icon)
    {
        switch (icon.Kind)
        {
            case CommandBarIconKind.Glyph:
                host.Background = Plate;
                var mark = new TextBlock { Text = icon.Value, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                mark.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                host.Child = mark;
                break;
            case CommandBarIconKind.Color when icon.Color is { } color:
                host.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.Alpha * 255), (byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255)));
                host.BorderBrush = Plate;
                host.BorderThickness = new Thickness(1);
                break;
            default:
                if (icon.Value.Length == 0) { host.Background = Plate; break; }
                var image = new Image { Stretch = Stretch.Uniform, Width = 28, Height = 28 };
                host.Child = image;
                _ = LoadIcon(image, icon.Value);
                break;
        }
    }

    private static async Task LoadIcon(Image image, string path)
    {
        try
        {
            var source = await ShellThumbnails.GetAsync(path, 64);
            image.Dispatcher.Invoke(() => image.Source = source);
        }
        catch (Exception) { }
    }
}
