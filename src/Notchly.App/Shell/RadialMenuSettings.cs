// SPDX-License-Identifier: GPL-3.0-or-later
// Settings → Tools → Radial menu: the Windows counterpart of upstream's UI/Settings/RadialMenuSettings.swift. Switch it on, choose how it
// opens, keep several wheels (name, colour, shortcut, mouse button), and edit a wheel on a live preview: click a slice to edit it, drag
// one onto another to swap, add and remove slices, step into a submenu; or use the list below it. Each slice opens a sheet
// (RadialItemEditor). Everything is saved as you go.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Notchly.App.Platform.Input;
using Notchly.App.Radial;
using Notchly.Core.Features;
using Notchly.Core.Radial;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Ellipse = System.Windows.Shapes.Ellipse;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using WheelCanvas = System.Windows.Controls.Canvas;

namespace Notchly.App.Shell;

internal sealed partial class SettingsPages
{
    private const string RadialNote = "A wheel of apps, files, links and tools around the pointer. Hold the shortcut or the mouse button, move toward a slice and let go to run it; tap and it stays open to click. Apps running as administrator cannot be sent keys.";

    private Guid _radialProfile;
    /// <summary>The submenu being edited, as the ids of the slices stepped into (empty at the top).</summary>
    private readonly List<Guid> _radialPath = [];

    private IReadOnlyList<RadialMenuProfile> RadialProfiles() =>
        RadialMenuSupport.DecodeProfiles(app.Settings.GetString(DefaultsKey.RadialMenuProfiles), new StoreRadialSettings(app.Settings));

    private void SaveRadialProfiles(IReadOnlyList<RadialMenuProfile> profiles) =>
        app.Settings.Set(DefaultsKey.RadialMenuProfiles, RadialMenuSupport.EncodeProfiles(RadialMenuSupport.SanitizedProfiles(profiles)));

    private void EditRadialProfile(Guid id, Func<RadialMenuProfile, RadialMenuProfile> edit)
    {
        SaveRadialProfiles(RadialProfiles().Select(p => p.Id == id ? edit(p) : p).ToList());
        Refresh();
    }

    // MARK: Items at a path

    private static IReadOnlyList<RadialMenuItem> ItemsAt(IReadOnlyList<RadialMenuItem> root, IReadOnlyList<Guid> path)
    {
        var items = root;
        foreach (var id in path) items = items.FirstOrDefault(i => i.Id == id)?.Children ?? [];
        return items;
    }

    private static IReadOnlyList<RadialMenuItem> ReplaceAt(IReadOnlyList<RadialMenuItem> root, IReadOnlyList<Guid> path, int depth,
                                                           Func<IReadOnlyList<RadialMenuItem>, IReadOnlyList<RadialMenuItem>> edit)
    {
        if (depth >= path.Count) return edit(root);
        return root.Select(i => i.Id == path[depth] ? i with { Children = ReplaceAt(i.Children, path, depth + 1, edit) } : i).ToList();
    }

    private void EditItems(RadialMenuProfile profile, Func<IReadOnlyList<RadialMenuItem>, IReadOnlyList<RadialMenuItem>> edit) =>
        EditRadialProfile(profile.Id, p => p with { Items = ReplaceAt(p.Items, _radialPath, 0, edit) });

    // MARK: The section

    private void AddRadialSection(StackPanel page)
    {
        var settings = app.Settings;
        // A migrated or first-run wheel gets its ids saved once, so editing it keeps them.
        var profiles = RadialProfiles();
        if (settings.GetString(DefaultsKey.RadialMenuProfiles).Length == 0) SaveRadialProfiles(profiles);
        if (!profiles.Any(p => p.Id == _radialProfile)) { _radialProfile = profiles[0].Id; _radialPath.Clear(); }
        var profile = profiles.First(p => p.Id == _radialProfile);

        page.Children.Add(Section("Radial menu"));
        page.Children.Add(Caption(RadialNote));
        if (!settings.IsAvailable(AppFeature.RadialMenu)) page.Children.Add(Caption("Install Radial menu in Features to use it."));
        page.Children.Add(Toggle("Turn on the radial menu", null, DefaultsKey.RadialMenuEnabled, ""));
        page.Children.Add(Choice("How it opens", "Hold: let go on a slice to run it. Press: it stays open until you click. Either: a quick tap stays open, holding runs on release.", DefaultsKey.RadialMenuActivationMode,
            [("pressOrHold", "Hold or press"), ("press", "Press"), ("hold", "Hold")]));
        var position = new ComboBox { MinWidth = 180 };
        position.Items.Add(new ComboBoxItem { Content = "At the pointer", Tag = true });
        position.Items.Add(new ComboBoxItem { Content = "In the middle of the screen", Tag = false });
        position.SelectedIndex = settings.GetBool(DefaultsKey.RadialMenuAtPointer) ? 0 : 1;
        position.SelectionChanged += (_, _) => settings.Set(DefaultsKey.RadialMenuAtPointer, (bool)((ComboBoxItem)position.SelectedItem!).Tag);
        page.Children.Add(Card("Where it opens", null, "", position));
        page.Children.Add(ActionButton("Try it", () => app.Features.Get<Radial.RadialMenuService>(AppFeature.RadialMenu)?.Open(profile)));

        AddRadialProfiles(page, profiles, profile);
        AddRadialWheel(page, profile);
    }

    private void AddRadialProfiles(StackPanel page, IReadOnlyList<RadialMenuProfile> profiles, RadialMenuProfile profile)
    {
        page.Children.Add(Section("Wheels"));
        var picker = new ComboBox { MinWidth = 200 };
        foreach (var p in profiles) picker.Items.Add(new ComboBoxItem { Content = p.DisplayName(), Tag = p.Id, IsSelected = p.Id == profile.Id });
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: Guid id } && id != _radialProfile) { _radialProfile = id; _radialPath.Clear(); Refresh(); }
        };
        var add = new Button { Content = "Add a wheel…", Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = add };
            foreach (var preset in RadialMenuPresets.All)
            {
                var item = new MenuItem { Header = RadialMenuPresets.Title(preset) };
                var captured = preset;
                item.Click += (_, _) =>
                {
                    var created = RadialMenuPresets.CreateProfile(captured, RadialMenuPresets.Title(captured), RadialMenuPresets.DefaultColor(captured), "", RadialMouseTrigger.Off.RawValue);
                    SaveRadialProfiles([.. profiles, created]);
                    _radialProfile = created.Id;
                    _radialPath.Clear();
                    Refresh();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        var duplicate = new Button { Content = "Duplicate", Margin = new Thickness(8, 0, 0, 0) };
        duplicate.Click += (_, _) =>
        {
            var copy = profile.Duplicate(profile.DisplayName() + " 2");
            SaveRadialProfiles([.. profiles, copy]);
            _radialProfile = copy.Id;
            _radialPath.Clear();
            Refresh();
        };
        var delete = new Button { Content = "Delete", Margin = new Thickness(8, 0, 0, 0), IsEnabled = profiles.Count > 1 };
        delete.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show($"Delete the wheel \"{profile.DisplayName()}\"?", "Radial menu", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            var result = RadialMenuSupport.DeleteProfile(profiles, profile.Id, profile.Id);
            if (!result.Changed) return;
            SaveRadialProfiles(result.Profiles);
            _radialProfile = result.SelectedId;
            _radialPath.Clear();
            Refresh();
        };
        page.Children.Add(Card("Wheel", null, "", new StackPanel { Orientation = Orientation.Horizontal, Children = { picker, add, duplicate, delete } }));

        var name = new TextBox { Text = profile.Name, PlaceholderText = "General", MinWidth = 220, MaxLength = 60 };
        name.LostFocus += (_, _) => { if (name.Text != profile.Name) EditRadialProfile(profile.Id, p => p with { Name = name.Text.Trim() }); };
        page.Children.Add(Card("Name", null, "", name));

        var swatches = new WrapPanel { MaxWidth = 300 };
        foreach (var color in RadialMenuColors.All)
        {
            var captured = color;
            var dot = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(3), Cursor = Cursors.Hand, ToolTip = color.Title(),
                Background = RadialColors.Brush(color), BorderThickness = new Thickness(2), BorderBrush = color == profile.Color ? Brushes.White : Brushes.Transparent,
            };
            dot.MouseLeftButtonUp += (_, _) => EditRadialProfile(profile.Id, p => p with { Color = captured });
            swatches.Children.Add(dot);
        }
        page.Children.Add(Card("Color", null, "", swatches));

        page.Children.Add(Card("Shortcut", "Click, then press the keys (with Ctrl, Alt or the Windows key). Backspace clears it.", "", new ShortcutRecorder(() => profile.Shortcut, value =>
        {
            if (value.Length > 0)
            {
                if (RadialMenuSupport.ParseShortcut(value) is not { } parsed) { System.Windows.MessageBox.Show("Use Ctrl, Alt or the Windows key with a key.", "Radial menu"); return; }
                if (RadialMenuSupport.ProfileUsing(parsed, profiles, profile.Id) is { } other)
                {
                    System.Windows.MessageBox.Show($"\"{other.DisplayName()}\" already opens with that shortcut.", "Radial menu");
                    return;
                }
            }
            SaveRadialProfiles(profiles.Select(p => p.Id == profile.Id ? p with { Shortcut = value } : p).ToList());
        })));

        var button = new ComboBox { MinWidth = 180 };
        var current = RadialMouseTrigger.Sanitized(profile.MouseButton);
        void Option(string label, string raw) => button.Items.Add(new ComboBoxItem { Content = label, Tag = raw, IsSelected = raw == current.RawValue });
        Option("None", "off");
        Option("Back button", "back");
        Option("Forward button", "forward");
        if (current.ButtonNumber is > 4) Option("Button " + current.ButtonNumber, current.RawValue);
        button.SelectionChanged += (_, _) =>
        {
            if (button.SelectedItem is not ComboBoxItem { Tag: string raw } || raw == current.RawValue) return;
            // A button belongs to one wheel: choosing it here takes it away from another.
            SaveRadialProfiles(RadialMenuSupport.Assigning(raw, profile.Id, profiles));
            Refresh();
        };
        var seen = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        var test = new Button { Content = "Test a button", Margin = new Thickness(8, 0, 0, 0) };
        test.Click += (_, _) => DetectButton(seen);
        page.Children.Add(Card("Mouse button", "Holds the wheel open while pressed. It takes the button away from Mouse Button Shortcuts.", "",
            new StackPanel { Orientation = Orientation.Horizontal, Children = { button, test, seen } }));
        if (!RadialMenuSupport.HasTrigger(profile)) page.Children.Add(Caption("This wheel has no shortcut or mouse button, so it can only be opened with Try it."));
    }

    /// <summary>Listens for a few seconds and says which extra mouse button was pressed.</summary>
    private void DetectButton(TextBlock label)
    {
        label.Text = "Press a mouse button…";
        IDisposable? subscription = null;
        var found = "";
        subscription = app.Input.SubscribeMouse((in MouseEvent e) =>
        {
            if (e.Message == 0x020B) found = (e.XButton + 2) switch { 3 => "Back button", 4 => "Forward button", var n => "Button " + n };
            return false;
        });
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var until = DateTime.UtcNow.AddSeconds(6);
        timer.Tick += (_, _) =>
        {
            if (found.Length > 0 || DateTime.UtcNow > until)
            {
                timer.Stop();
                subscription?.Dispose();
                label.Text = found.Length > 0 ? "Seen: " + found : "No button seen.";
            }
        };
        timer.Start();
    }

    // MARK: The wheel preview and the list

    private void AddRadialWheel(StackPanel page, RadialMenuProfile profile)
    {
        page.Children.Add(Section("Slices"));
        var items = ItemsAt(profile.Items, _radialPath);
        var path = new List<RadialMenuItem>();
        var level = profile.Items;
        foreach (var id in _radialPath) { var step = level.FirstOrDefault(i => i.Id == id); if (step is null) break; path.Add(step); level = step.Children; }
        var crumb = path.Count == 0 ? profile.DisplayName() : profile.DisplayName() + " › " + string.Join(" › ", path.Select(RadialLabels.Of));
        page.Children.Add(Caption(crumb + " · " + items.Count + " of " + RadialMenuSupport.MaxItemsPerWheel + " slices. Click a slice to edit it, drag one onto another to swap."));

        page.Children.Add(Card("Preview", null, "", PreviewCanvas(profile, items)));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (_radialPath.Count > 0) actions.Children.Add(ActionButton("‹ Back", () => { _radialPath.RemoveAt(_radialPath.Count - 1); Refresh(); }));
        if (items.Count < RadialMenuSupport.MaxItemsPerWheel) actions.Children.Add(ActionButton("Add a slice", () => EditSlice(profile, null, items)));
        if (profile.Preset is { } presetName && RadialMenuPresets.Parse(presetName) is { } preset && _radialPath.Count == 0)
            actions.Children.Add(ActionButton("Reset to " + RadialMenuPresets.Title(preset), () =>
            {
                if (System.Windows.MessageBox.Show("Replace this wheel's slices with the starting set?", "Radial menu", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                    EditRadialProfile(profile.Id, p => p with { Items = RadialMenuPresets.MakeItems(preset) });
            }));
        page.Children.Add(actions);

        var expander = new Expander { Header = "List", IsExpanded = false, Margin = new Thickness(0, 0, 0, 8) };
        var list = new StackPanel();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var index = i;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(SmallButton("", "Move up", index > 0, () => EditItems(profile, l => Swap(l, index, index - 1))));
            buttons.Children.Add(SmallButton("", "Move down", index < items.Count - 1, () => EditItems(profile, l => Swap(l, index, index + 1))));
            buttons.Children.Add(SmallButton("", "Edit", true, () => EditSlice(profile, item, items)));
            buttons.Children.Add(SmallButton("", "Remove", true, () => EditItems(profile, l => l.Where(x => x.Id != item.Id).ToList())));
            list.Children.Add(Card(RadialLabels.Of(item), RadialItemEditor.KindTitle(item.Kind), RadialMenuGlyphs.EffectiveGlyph(item), buttons));
        }
        if (items.Count == 0) list.Children.Add(Caption("This wheel has no slices yet."));
        expander.Content = list;
        page.Children.Add(expander);
    }

    private static IReadOnlyList<RadialMenuItem> Swap(IReadOnlyList<RadialMenuItem> items, int a, int b)
    {
        var copy = items.ToList();
        (copy[a], copy[b]) = (copy[b], copy[a]);
        return copy;
    }

    private static Button SmallButton(string glyph, string tip, bool enabled, Action click)
    {
        var button = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 }, ToolTip = tip, Appearance = ControlAppearance.Transparent, Padding = new Thickness(8), IsEnabled = enabled };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The slice sheet. A new slice goes at the end; an edited one keeps its place. A submenu is offered only at the top.</summary>
    private void EditSlice(RadialMenuProfile profile, RadialMenuItem? existing, IReadOnlyList<RadialMenuItem> items)
    {
        var window = app.SettingsHost ?? Application.Current.MainWindow;
        var start = existing ?? RadialMenuItem.Create(RadialMenuKind.Url);
        var editor = new RadialItemEditor(window, app.Settings, start, allowSubmenu: _radialPath.Count == 0, saved: edited =>
        {
            EditItems(profile, l => existing is null
                ? [.. l, edited]
                : l.Select(i => i.Id == existing.Id ? edited with { Id = existing.Id, Children = edited.Kind == RadialMenuKind.Submenu ? existing.Children : [] } : i).ToList());
        });
        editor.ShowDialog();
    }

    /// <summary>The 250 pixel preview: the ring of slices, which can be clicked, dragged onto each other, and stepped into.</summary>
    private FrameworkElement PreviewCanvas(RadialMenuProfile profile, IReadOnlyList<RadialMenuItem> items)
    {
        const double size = 250, ring = 92, chip = 42, hub = 68;
        var canvas = new WheelCanvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
        var disc = new Ellipse { Width = size - 6, Height = size - 6, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) };
        WheelCanvas.SetLeft(disc, 3);
        WheelCanvas.SetTop(disc, 3);
        canvas.Children.Add(disc);
        var hubShape = new Ellipse { Width = hub, Height = hub, Fill = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), Stroke = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)) };
        WheelCanvas.SetLeft(hubShape, size / 2 - hub / 2);
        WheelCanvas.SetTop(hubShape, size / 2 - hub / 2);
        canvas.Children.Add(hubShape);
        var hubText = new TextBlock { Text = items.Count == 0 ? "Empty" : _radialPath.Count > 0 ? "‹ Back" : profile.DisplayName(), FontSize = 11, TextAlignment = TextAlignment.Center, Width = hub - 8, TextWrapping = TextWrapping.Wrap, Cursor = _radialPath.Count > 0 ? Cursors.Hand : Cursors.Arrow };
        hubText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        WheelCanvas.SetLeft(hubText, size / 2 - (hub - 8) / 2);
        WheelCanvas.SetTop(hubText, size / 2 - 14);
        if (_radialPath.Count > 0) hubText.MouseLeftButtonUp += (_, _) => { _radialPath.RemoveAt(_radialPath.Count - 1); Refresh(); };
        canvas.Children.Add(hubText);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var (dx, dyUp) = RadialMenuGeometry.UnitPosition(i, items.Count);
            var glyph = new TextBlock { Text = RadialMenuGlyphs.EffectiveGlyph(item), FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            UIElement face = glyph;
            if (item.CustomIconData is { Length: > 0 } data)
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new System.IO.MemoryStream(data);
                    bitmap.EndInit();
                    face = new System.Windows.Controls.Image { Source = bitmap, Width = 24, Height = 24 };
                }
                catch (Exception e) when (e is NotSupportedException or System.IO.IOException) { }
            }
            var node = new Border
            {
                Width = chip, Height = chip, CornerRadius = new CornerRadius(chip / 2), Cursor = Cursors.Hand, AllowDrop = true, ToolTip = RadialLabels.Of(item) + (item.Kind == RadialMenuKind.Submenu ? " (right-click to open)" : ""),
                Background = RadialColors.Brush(profile.Color, 0xB0), Child = face,
            };
            WheelCanvas.SetLeft(node, size / 2 + dx * ring - chip / 2);
            WheelCanvas.SetTop(node, size / 2 - dyUp * ring - chip / 2);
            Point? pressed = null;
            node.PreviewMouseLeftButtonDown += (_, e) => pressed = e.GetPosition(node);
            node.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || pressed is null) return;
                var at = e.GetPosition(node);
                if (Math.Abs(at.X - pressed.Value.X) + Math.Abs(at.Y - pressed.Value.Y) < 6) return;
                pressed = null;
                DragDrop.DoDragDrop(node, new DataObject("notchly-slice", item.Id.ToString()), DragDropEffects.Move);
            };
            node.MouseLeftButtonUp += (_, _) => { if (pressed is not null) { pressed = null; EditSlice(profile, item, items); } };
            node.Drop += (_, e) =>
            {
                if (e.Data.GetData("notchly-slice") is not string text || !Guid.TryParse(text, out var from) || from == item.Id) return;
                EditItems(profile, l =>
                {
                    var a = l.ToList().FindIndex(x => x.Id == from);
                    var b = l.ToList().FindIndex(x => x.Id == item.Id);
                    return a < 0 || b < 0 ? l : Swap(l, a, b);
                });
            };
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Edit…" };
            edit.Click += (_, _) => EditSlice(profile, item, items);
            menu.Items.Add(edit);
            if (item.Kind == RadialMenuKind.Submenu)
            {
                var open = new MenuItem { Header = "Edit its slices" };
                open.Click += (_, _) => { _radialPath.Add(item.Id); Refresh(); };
                menu.Items.Add(open);
            }
            var remove = new MenuItem { Header = "Remove" };
            remove.Click += (_, _) => EditItems(profile, l => l.Where(x => x.Id != item.Id).ToList());
            menu.Items.Add(remove);
            node.ContextMenu = menu;
            canvas.Children.Add(node);
        }
        return canvas;
    }
}
