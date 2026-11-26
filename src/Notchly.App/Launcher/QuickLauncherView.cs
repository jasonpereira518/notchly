// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream UI/MenuPanel/QuickLauncherView.swift: the tiles, drawn once and shown in two places, the floating panel (three
// columns, with a header) and the island's Tools page (a rail of small tiles). It paints the Core model and sends clicks and keys to
// it. Edit mode reorders (drag) and hides tiles and opens the options cards; hosted panels (quick toggles, window layout, clean URL)
// replace the grid with a back button.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Tools;
using Notchly.Core.Features;
using Notchly.Core.Launcher;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Notchly.Core.Windowing;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Launcher;

internal sealed class QuickLauncherView
{
    private static readonly Brush Soft = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)), Plate = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                                  Hover = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Accent = Frozen(Color.FromRgb(0x4C, 0x8D, 0xFF)),
                                  Green = Frozen(Color.FromRgb(0x30, 0xD1, 0x58)), Red = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));

    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private readonly QuickLauncherService _service;
    private readonly bool _rail;
    private readonly Grid _root = new();
    private bool _built;

    public QuickLauncherView(QuickLauncherService service, bool rail)
    {
        _service = service;
        _rail = rail;
        Model.PropertyChanged += (_, _) => Rebuild();
    }

    public FrameworkElement Root => _root;
    private QuickLauncherModel Model => _service.Model;
    private SettingsStore Settings => _service.App.Settings;

    /// <summary>The columns the grid has right now (the keyboard flow reads it).</summary>
    public GridFlow Flow => _rail && !Model.IsEditing ? GridFlow.ByColumns(RailRows) : GridFlow.ByRows(Model.IsEditing && _rail ? QuickLauncherLayout.EditColumns : QuickLauncherLayout.Columns);

    /// <summary>Rows the rail has room for (set by the page that hosts it).</summary>
    public int RailRows { get; set; } = 2;

    public void Rebuild()
    {
        _built = true;
        _root.Children.Clear();
        _root.Children.Add(Content());
    }

    public void EnsureBuilt() { if (!_built) Rebuild(); }

    // MARK: Content

    private UIElement Content()
    {
        if (Model.ActiveUtility is { } utility) return Hosted(utility);
        if (Model.EditingOptionsItem is { } options) return OptionsCard(options);
        var stack = new StackPanel();
        stack.Children.Add(Grid());
        if (Model.IsEditing && Model.HiddenItems.Count > 0) stack.Children.Add(HiddenTray());
        return stack;
    }

    private UIElement Grid()
    {
        var items = Model.VisibleItems;
        if (items.Count == 0)
            return new TextBlock { Text = "No tools are turned on. Install tools in Features to see them here.", Foreground = Soft, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 16, 8, 16) };
        if (_rail && !Model.IsEditing)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Vertical, ItemHeight = QuickLauncherLayout.RailTileHeight + QuickLauncherLayout.RailSpacing, ItemWidth = QuickLauncherLayout.RailTileWidth + QuickLauncherLayout.RailSpacing };
            for (var i = 0; i < items.Count; i++) wrap.Children.Add(Tile(items[i], i, small: true));
            wrap.Height = RailRows * (QuickLauncherLayout.RailTileHeight + QuickLauncherLayout.RailSpacing);
            return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = wrap };
        }
        var columns = _rail ? QuickLauncherLayout.EditColumns : QuickLauncherLayout.Columns;
        var grid = new UniformGrid { Columns = columns };
        for (var i = 0; i < items.Count; i++) grid.Children.Add(Tile(items[i], i, small: _rail));
        return grid;
    }

    private static string TitleOf(QuickLauncherItem item) => item switch
    {
        QuickLauncherItem.KeepAwake => "Keep awake", QuickLauncherItem.Cleaner => "Cleaner", QuickLauncherItem.Toggles => "Quick toggles",
        QuickLauncherItem.MicMute => "Microphone", QuickLauncherItem.ScreenOcr => "Copy text", QuickLauncherItem.ColorPicker => "Color picker",
        QuickLauncherItem.Clipboard => "Clipboard", QuickLauncherItem.WindowLayout => "Window layout", QuickLauncherItem.Cleaning => "Cleaning mode",
        QuickLauncherItem.Homebrew => "Packages", QuickLauncherItem.Media => "Media", QuickLauncherItem.UrlCleaner => "Clean URL",
        QuickLauncherItem.Uninstaller => "Uninstaller", QuickLauncherItem.Screenshot => "Screenshot", QuickLauncherItem.ScreenRecorder => "Recorder",
        QuickLauncherItem.CameraPreview => "Camera", _ => "Scratchpad",
    };

    private bool IsLit(QuickLauncherItem item) => item switch
    {
        QuickLauncherItem.KeepAwake => _service.App.Features.Get<Energy.KeepAwakeService>(AppFeature.KeepAwake)?.Active == true,
        QuickLauncherItem.MicMute => _service.App.Features.Get<Sound.MicMuteService>(AppFeature.MicMute)?.Active == true,
        _ => false,
    };

    private static bool HasOptions(QuickLauncherItem item) =>
        item is QuickLauncherItem.KeepAwake or QuickLauncherItem.MicMute or QuickLauncherItem.ColorPicker or QuickLauncherItem.Clipboard;

    private FrameworkElement Tile(QuickLauncherItem item, int index, bool small)
    {
        var lit = IsLit(item);
        var selected = Model.SelectedIndex == index && Model.KeyboardIndex is not null;
        var glyph = FeatureCatalog.Info(item.Feature()).Glyph;
        var iconSize = small ? 30 : 46;
        var plate = new Border
        {
            Width = iconSize, Height = iconSize, CornerRadius = new CornerRadius(small ? 9 : 13), HorizontalAlignment = HorizontalAlignment.Center,
            Background = lit ? (item == QuickLauncherItem.MicMute ? Red : Accent) : Plate,
            Child = Icon(glyph, small ? 15 : 21),
        };
        var title = new TextBlock { Text = TitleOf(item), FontSize = small ? 10.5 : 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = small ? 28 : 34, Margin = new Thickness(0, small ? 4 : 6, 0, 0) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Width = small ? QuickLauncherLayout.RailTileWidth - 8 : double.NaN };
        content.Children.Add(plate);
        content.Children.Add(title);
        var layers = new Grid();
        layers.Children.Add(content);
        if (lit && !Model.IsEditing)
            layers.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Green, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 8, 0) });
        if (!small && !Model.IsEditing && index < 9)
            layers.Children.Add(new TextBlock { Text = (index + 1).ToString(), FontSize = 9, Foreground = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 4, 0, 0) });
        if (Model.IsEditing)
        {
            var minus = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Red, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 4, 0, 0), Cursor = Cursors.Hand,
                                     Child = new TextBlock { Text = "−", Foreground = Brushes.White, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) } };
            minus.MouseLeftButtonUp += (_, e) => { Model.SetHidden(item, true); e.Handled = true; };
            layers.Children.Add(minus);
        }
        var tile = new Border
        {
            Margin = new Thickness(small ? 3 : 5), Padding = new Thickness(small ? 2 : 6, small ? 6 : 10, small ? 2 : 6, small ? 4 : 8), CornerRadius = new CornerRadius(14), Child = layers, Cursor = Cursors.Hand,
            Background = selected ? Hover : Brushes.Transparent, MinHeight = small ? QuickLauncherLayout.RailTileHeight - 6 : 96, AllowDrop = Model.IsEditing,
        };
        tile.MouseEnter += (_, _) => { if (Model.SelectedIndex != index || Model.KeyboardIndex is not null) Model.Select(item); };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (Model.IsEditing) { if (HasOptions(item)) Model.EditingOptionsItem = item; }
            else Model.Run(item);
        };
        if (Model.IsEditing)
        {
            Point? start = null;
            tile.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(tile);
            tile.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || start is null) return;
                var at = e.GetPosition(tile);
                if (Math.Abs(at.X - start.Value.X) + Math.Abs(at.Y - start.Value.Y) < 8) return;
                start = null;
                DragDrop.DoDragDrop(tile, new DataObject("notchly-tile", item.Id()), DragDropEffects.Move);
            };
            tile.Drop += (_, e) =>
            {
                if (e.Data.GetData("notchly-tile") is not string id || QuickLauncherItems.FromId(id) is not { } moved || moved == item) return;
                var order = Model.OrderedItems.ToList();
                order.Remove(moved);
                order.Insert(Math.Max(0, order.IndexOf(item)), moved);
                Model.SetItemOrder(order);
            };
        }
        return tile;
    }

    private static TextBlock Icon(string glyph, double size)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return icon;
    }

    private UIElement HiddenTray()
    {
        var wrap = new WrapPanel { Margin = new Thickness(6, 8, 6, 0) };
        wrap.Children.Add(new TextBlock { Text = "Hidden", Foreground = Soft, FontSize = 11, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var item in Model.HiddenItems)
        {
            var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 2, 6, 2), Background = Plate, Cursor = Cursors.Hand, Child = new TextBlock { Text = "+  " + TitleOf(item), FontSize = 11.5, Foreground = Brushes.White } };
            var captured = item;
            chip.MouseLeftButtonUp += (_, _) => Model.SetHidden(captured, false);
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    // MARK: Options cards

    private UIElement OptionsCard(QuickLauncherItem item)
    {
        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(Header(TitleOf(item), () => Model.EditingOptionsItem = null));
        switch (item)
        {
            case QuickLauncherItem.KeepAwake:
                stack.Children.Add(Switch("Keep the PC awake with the lid closed", DefaultsKey.ClamshellPreferred));
                stack.Children.Add(Choice("Default duration", DefaultsKey.DefaultDurationMinutes, [(15, "15 minutes"), (30, "30 minutes"), (60, "1 hour"), (120, "2 hours"), (240, "4 hours"), (480, "8 hours"), (0, "Until stopped")]));
                break;
            case QuickLauncherItem.MicMute:
                stack.Children.Add(Switch("Show an indicator in the tray", DefaultsKey.MicMuteMenuBarIndicator));
                break;
            case QuickLauncherItem.ColorPicker:
                stack.Children.Add(Choice("Copied format", DefaultsKey.ColorPickerFormat, [("hex", "HEX"), ("rgb", "RGB"), ("hsl", "HSL"), ("swiftui", "SwiftUI")]));
                if (ColorCopyFormats.Sanitized(Settings.GetString(DefaultsKey.ColorPickerFormat)) == ColorCopyFormat.Hex)
                    stack.Children.Add(Switch("Copy without the # prefix", DefaultsKey.ColorPickerBareHex));
                break;
            case QuickLauncherItem.Clipboard:
                stack.Children.Add(Switch("Keep a clipboard history", DefaultsKey.ClipboardHistoryEnabled));
                stack.Children.Add(Choice("Items kept", DefaultsKey.ClipboardHistoryLimit, [(20, "20"), (50, "50"), (100, "100"), (250, "250"), (500, "500"), (1000, "1,000")]));
                break;
        }
        return stack;
    }

    private UIElement Switch(string text, string key)
    {
        var toggle = new ToggleSwitch { Content = text, IsChecked = Settings.GetBool(key), Margin = new Thickness(0, 8, 0, 0) };
        toggle.Click += (_, _) => Settings.Set(key, toggle.IsChecked == true);
        return toggle;
    }

    private UIElement Choice<T>(string label, string key, (T Value, string Text)[] options) where T : notnull
    {
        var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Foreground = Soft, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        var combo = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (value, text) in options) combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        var current = Settings.Get<T>(key, default!);
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, current));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: T value }) Settings.Set(key, value); };
        row.Children.Add(combo);
        return row;
    }

    // MARK: Hosted panels

    private UIElement Header(string title, Action back)
    {
        var button = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Plate, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 10, 0), Child = Icon("", 13) };
        button.MouseLeftButtonUp += (_, _) => back();
        var text = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { button, text } };
    }

    private UIElement Hosted(QuickLauncherItem utility)
    {
        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(Header(TitleOf(utility), Model.CloseUtility));
        switch (utility)
        {
            case QuickLauncherItem.Toggles: stack.Children.Add(Toggles()); break;
            case QuickLauncherItem.WindowLayout: stack.Children.Add(Layouts()); break;
            case QuickLauncherItem.UrlCleaner: stack.Children.Add(UrlCleaner()); break;
        }
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 470, Content = stack };
    }

    private UIElement Toggles()
    {
        var grid = new UniformGrid { Columns = 3 };
        if (_service.App.Features.Get<QuickTogglesService>(AppFeature.QuickToggles) is not { } service) return grid;
        service.Refresh();
        foreach (var id in QuickToggleSupport.VisibleRows(Settings).Where(service.IsSupported))
        {
            var info = QuickTogglesService.All.First(i => i.Id == id);
            var on = service.IsOn(id) == true;
            var plate = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, Background = on ? Accent : Plate, Child = Icon(info.Glyph, 18) };
            var title = new TextBlock { Text = info.Title, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            var tile = new Border { Margin = new Thickness(4), Padding = new Thickness(4, 8, 4, 6), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = new StackPanel { Children = { plate, title } } };
            tile.MouseEnter += (_, _) => tile.Background = Hover;
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
            var captured = id;
            tile.MouseLeftButtonUp += (_, _) => { service.Run(captured); Rebuild(); };
            grid.Children.Add(tile);
        }
        return grid;
    }

    private static readonly (WindowLayoutAction Action, string Title)[] LayoutChoices =
    [
        (WindowLayoutAction.LeftHalf, "Left half"), (WindowLayoutAction.RightHalf, "Right half"), (WindowLayoutAction.TopHalf, "Top half"), (WindowLayoutAction.BottomHalf, "Bottom half"),
        (WindowLayoutAction.LeftThird, "Left third"), (WindowLayoutAction.CenterThird, "Center third"), (WindowLayoutAction.RightThird, "Right third"), (WindowLayoutAction.CenterHalf, "Center half"),
        (WindowLayoutAction.TopLeft, "Top left"), (WindowLayoutAction.TopRight, "Top right"), (WindowLayoutAction.BottomLeft, "Bottom left"), (WindowLayoutAction.BottomRight, "Bottom right"),
        (WindowLayoutAction.Maximize, "Maximize"), (WindowLayoutAction.Center, "Center"), (WindowLayoutAction.PreviousDisplay, "Previous display"), (WindowLayoutAction.NextDisplay, "Next display"),
        (WindowLayoutAction.Restore, "Restore"),
    ];

    private UIElement Layouts()
    {
        var grid = new UniformGrid { Columns = 4 };
        foreach (var (action, titleText) in LayoutChoices)
        {
            var title = new TextBlock { Text = titleText, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            var tile = new Border { Margin = new Thickness(3), MinHeight = 48, Padding = new Thickness(4), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Plate, Child = title };
            tile.MouseEnter += (_, _) => tile.Background = Hover;
            tile.MouseLeave += (_, _) => tile.Background = Plate;
            var captured = action;
            tile.MouseLeftButtonUp += async (_, _) =>
            {
                Model.Hide();
                if (_service.App.Features.Get<Layout.WindowLayoutService>(AppFeature.WindowLayout) is not { } layout) return;
                await _service.RestoreFocusAsync();
                await layout.ApplyAsync(captured);
            };
            grid.Children.Add(tile);
        }
        return grid;
    }

    private UIElement UrlCleaner()
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Takes tracking parameters out of a link you copied. Copy a link, then clean it here, or turn on the cleaner to do it as you copy.", Foreground = Soft, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(Switch("Clean links as I copy them", DefaultsKey.UrlCleanerEnabled));
        var clean = new Button { Content = "Clean the copied link now", Margin = new Thickness(0, 10, 0, 0) };
        clean.Click += (_, _) =>
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (UrlCleaning.Clean(text) is { } cleaned && cleaned.Url != text.Trim() && ClipboardWrite.TrySetText(cleaned.Url)) QuickToolHud.Show("Link cleaned");
            else QuickToolHud.Show("Nothing to clean");
        };
        stack.Children.Add(clean);
        return stack;
    }
}
