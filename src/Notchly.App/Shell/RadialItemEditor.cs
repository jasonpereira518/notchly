// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the item sheet of upstream UI/Settings/RadialMenuSettings.swift: what one slice is. A kind, what it targets (a picker or field
// for each kind), a name (empty means automatic), an icon from a grid of Segoe Fluent glyphs or an image of your own, and for a link a
// button that fetches the site's icon. Save is enabled only when the target is valid for the kind.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Notchly.App.Platform.Apps;
using Notchly.App.Radial;
using Notchly.Core.Radial;
using Notchly.Core.Settings;
using Notchly.Core.Windowing;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed class RadialItemEditor : FluentWindow
{
    private readonly bool _allowSubmenu;
    private readonly SettingsStore _settings;
    private RadialMenuItem _item;
    private readonly StackPanel _payload = new();
    private readonly Button _save;
    private readonly TextBlock _hint = new() { FontSize = 12, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly WrapPanel _icons = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _iconNote = new() { FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0) };
    private bool _loading;

    public RadialItemEditor(Window owner, SettingsStore settings, RadialMenuItem item, bool allowSubmenu, Action<RadialMenuItem> saved)
    {
        Owner = owner;
        _settings = settings;
        _allowSubmenu = allowSubmenu;
        _item = item;
        Title = "Slice";
        Width = 460;
        Height = 640;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        var kind = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var k in Enum.GetValues<RadialMenuKind>().Where(k => allowSubmenu || k != RadialMenuKind.Submenu))
            kind.Items.Add(new ComboBoxItem { Content = KindTitle(k), Tag = k });
        kind.SelectedItem = kind.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (RadialMenuKind)i.Tag == item.Kind);
        kind.SelectionChanged += (_, _) =>
        {
            if (_loading || kind.SelectedItem is not ComboBoxItem { Tag: RadialMenuKind chosen } || chosen == _item.Kind) return;
            _item = _item with { Kind = chosen, Payload = DefaultPayload(chosen), Children = chosen == RadialMenuKind.Submenu ? _item.Children : [] };
            BuildPayload();
            Validate();
        };

        var name = new TextBox { PlaceholderText = "Automatic", Text = item.Name, MaxLength = 60 };
        name.TextChanged += (_, _) => _item = _item with { Name = name.Text };

        var custom = new Button { Content = "Choose an image…", Margin = new Thickness(0, 8, 8, 0) };
        custom.Click += (_, _) => ChooseImage();
        var clear = new Button { Content = "Remove it", Margin = new Thickness(0, 8, 0, 0) };
        clear.Click += (_, _) => { _item = _item with { CustomIconData = null }; BuildIcons(); };

        _save = new Button { Content = "Save", Appearance = ControlAppearance.Primary, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        _save.Click += (_, _) => { saved(_item); Close(); };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close();

        var form = new StackPanel { Margin = new Thickness(24, 40, 24, 12) };
        form.Children.Add(Label("What it does"));
        form.Children.Add(kind);
        form.Children.Add(_payload);
        form.Children.Add(_hint);
        form.Children.Add(Label("Name", 14));
        form.Children.Add(name);
        form.Children.Add(Label("Icon", 14));
        form.Children.Add(new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _icons });
        form.Children.Add(_iconNote);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { custom, clear } });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 0, 24, 18), Children = { cancel, _save } };
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form });
        Content = root;
        BuildPayload();
        BuildIcons();
        Validate();
    }

    private static TextBlock Label(string text, double top = 0) => new() { Text = text, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, top, 0, 4) };

    public static string KindTitle(RadialMenuKind kind) => kind switch
    {
        RadialMenuKind.App => "Open an app", RadialMenuKind.File => "Open a file or folder", RadialMenuKind.Url => "Open a link",
        RadialMenuKind.Shortcut => "Press a key combination", RadialMenuKind.Tool => "Run a Notchly tool", RadialMenuKind.QuickToggle => "Flip a quick toggle",
        RadialMenuKind.WindowLayout => "Arrange the window", RadialMenuKind.Media => "Media key", _ => "Open a submenu",
    };

    private static string DefaultPayload(RadialMenuKind kind) => kind switch
    {
        RadialMenuKind.Tool => RadialMenuTools.Ids[2], RadialMenuKind.QuickToggle => "darkMode", RadialMenuKind.WindowLayout => "maximize",
        RadialMenuKind.Media => "playPause", _ => "",
    };

    private void Validate()
    {
        var valid = RadialMenuSupport.IsValidPayload(_item);
        _save.IsEnabled = valid || _item.Kind == RadialMenuKind.Submenu;
        _hint.Text = valid || _item.Kind == RadialMenuKind.Submenu || _item.Payload.Length == 0 ? "" : _item.Kind switch
        {
            RadialMenuKind.Url => "That does not look like a web address.", RadialMenuKind.Shortcut => "Use at least Ctrl, Alt or the Windows key with a key.", _ => "That is not valid for this kind.",
        };
    }

    private void SetPayload(string payload)
    {
        _item = _item with { Payload = payload };
        Validate();
    }

    // MARK: Payload editors

    private void BuildPayload()
    {
        _payload.Children.Clear();
        switch (_item.Kind)
        {
            case RadialMenuKind.App: AppEditor(); break;
            case RadialMenuKind.File: FileEditor(); break;
            case RadialMenuKind.Url: UrlEditor(); break;
            case RadialMenuKind.Shortcut:
                _payload.Children.Add(Label("Keys"));
                _payload.Children.Add(new ShortcutRecorder(() => _item.Payload, value => { SetPayload(value); }));
                break;
            case RadialMenuKind.Tool: Choose("Tool", RadialMenuTools.Ids, id => Spaced(id)); break;
            case RadialMenuKind.QuickToggle: Choose("Toggle", RadialMenuToggles.Ids, id => Spaced(id)); break;
            case RadialMenuKind.Media: Choose("Key", RadialMenuMedia.Ids, id => Spaced(id)); break;
            case RadialMenuKind.WindowLayout:
                Choose("Placement", Enum.GetValues<WindowLayoutAction>().Select(WindowLayoutSupport.RawName).ToList(), id => Spaced(id));
                break;
            case RadialMenuKind.Submenu:
                _payload.Children.Add(new TextBlock { Text = "A slice that opens another ring. Add its slices from the wheel preview.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                break;
        }
    }

    private static string Spaced(string id)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(id, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private void Choose(string label, IReadOnlyList<string> ids, Func<string, string> title)
    {
        _payload.Children.Add(Label(label));
        var combo = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var id in ids) combo.Items.Add(new ComboBoxItem { Content = title(id), Tag = id });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _item.Payload) ?? combo.Items[0];
        if (combo.SelectedItem is ComboBoxItem { Tag: string first } && first != _item.Payload) SetPayload(first);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string id }) SetPayload(id); };
        _payload.Children.Add(combo);
    }

    private TextBox PathBox(string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = _item.Payload };
        box.TextChanged += (_, _) => SetPayload(box.Text.Trim());
        return box;
    }

    private void AppEditor()
    {
        _payload.Children.Add(Label("App"));
        var box = PathBox(@"C:\Program Files\App\app.exe");
        _payload.Children.Add(box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var choose = new Button { Content = "Choose a program…", Margin = new Thickness(0, 0, 8, 0) };
        choose.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Programs|*.exe;*.lnk|All files|*.*" };
            if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
        };
        var start = new ComboBox { MinWidth = 200, MaxWidth = 240 };
        start.Items.Add(new ComboBoxItem { Content = "Or pick from Start…", Tag = "" });
        start.SelectedIndex = 0;
        _ = Task.Run(() => InstalledApps.List()).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            foreach (var app in task.Result) start.Items.Add(new ComboBoxItem { Content = app.Name, Tag = app.ShellPath });
        }));
        start.SelectionChanged += (_, _) => { if (start.SelectedItem is ComboBoxItem { Tag: string path } && path.Length > 0) box.Text = path; };
        row.Children.Add(choose);
        row.Children.Add(start);
        _payload.Children.Add(row);
    }

    private void FileEditor()
    {
        _payload.Children.Add(Label("File or folder"));
        var box = PathBox(@"~\Downloads");
        _payload.Children.Add(box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var file = new Button { Content = "Choose a file…", Margin = new Thickness(0, 0, 8, 0) };
        file.Click += (_, _) => { var dialog = new OpenFileDialog(); if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName; };
        var folder = new Button { Content = "Choose a folder…" };
        folder.Click += (_, _) => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName; };
        row.Children.Add(file);
        row.Children.Add(folder);
        _payload.Children.Add(row);
    }

    private void UrlEditor()
    {
        _payload.Children.Add(Label("Address"));
        _payload.Children.Add(PathBox("example.com/page"));
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var fetch = new Button { Content = "Use the site's icon", Margin = new Thickness(0, 8, 0, 0) };
        fetch.Click += async (_, _) =>
        {
            fetch.IsEnabled = false;
            status.Text = "Fetching…";
            var png = await FaviconFetcher.FetchAsync(_item.Payload);
            fetch.IsEnabled = true;
            if (png is null) { status.Text = "Could not get an icon from that site."; return; }
            _item = _item with { CustomIconData = png };
            status.Text = "Icon set.";
            BuildIcons();
        };
        _payload.Children.Add(fetch);
        _payload.Children.Add(status);
    }

    // MARK: The icon

    private void BuildIcons()
    {
        _icons.Children.Clear();
        var automatic = _item.SymbolName.Length == 0 && _item.CustomIconData is null;
        _icons.Children.Add(IconButton("Auto", automatic, () => { _item = _item with { SymbolName = "", CustomIconData = null }; BuildIcons(); }, text: true));
        foreach (var choice in RadialMenuGlyphs.Choices)
        {
            var name = choice.Name;
            _icons.Children.Add(IconButton(choice.Glyph, _item.CustomIconData is null && _item.SymbolName == name, () => { _item = _item with { SymbolName = name, CustomIconData = null }; BuildIcons(); }));
        }
        _iconNote.Text = _item.CustomIconData is { } data ? $"Your own image ({data.Length / 1024.0:0.#} KB)." : "";
    }

    private static Border IconButton(string glyph, bool selected, Action click, bool text = false)
    {
        var label = new TextBlock
        {
            Text = glyph, FontSize = text ? 11 : 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = text ? new FontFamily("Segoe UI") : (FontFamily)Application.Current.Resources["IconFont"],
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var border = new Border
        {
            Width = 34, Height = 34, Margin = new Thickness(2), CornerRadius = new CornerRadius(8), Cursor = System.Windows.Input.Cursors.Hand, Child = label,
            Background = new SolidColorBrush(selected ? Color.FromArgb(0x66, 0x4C, 0x8D, 0xFF) : Color.FromArgb(0x1E, 255, 255, 255)),
            BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)) : Brushes.Transparent, BorderThickness = new Thickness(1.5),
        };
        border.MouseLeftButtonUp += (_, _) => click();
        return border;
    }

    private void ChooseImage()
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.ico|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            if (bytes.Length > 8 * 1024 * 1024) { _iconNote.Text = "That image is too large."; return; }
            if (FaviconFetcher.Scaled(bytes) is not { } png) { _iconNote.Text = "That image could not be used."; return; }
            _item = _item with { CustomIconData = png };
            BuildIcons();
        }
        catch (IOException) { _iconNote.Text = "That image could not be read."; }
    }
}
