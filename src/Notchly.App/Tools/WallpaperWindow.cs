// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream UI/MenuPanel/WallpaperSection (the gallery): a paged grid of pictures (24 a page) with the All / Your pictures /
// Windows filter, Add image and Add folder, a Remove mode, and whether a pick goes on every display or only the one with the pointer.
// A click applies the picture. Opened from Settings and the tray menu.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Notchly.App.Platform.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Tools;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;

namespace Notchly.App.Tools;

internal sealed class WallpaperWindow : FluentWindow
{
    public const string FilterAll = "All", FilterOwn = "Your pictures", FilterSystem = "Windows", AddImage = "Add image", AddFolder = "Add folder",
                        Remove = "Remove", DoneRemoving = "Done", ApplyAll = "Show on all displays", OpenSettings = "Open Windows wallpaper settings",
                        EmptyAll = "No wallpapers found", EmptyOwn = "No pictures added yet", Previous = "Previous", Next = "Next";

    private readonly WallpaperService _service;
    private readonly SettingsStore _settings;
    private readonly Guid _viewer = Guid.NewGuid();
    private readonly UniformGrid _grid = new() { Columns = 4 };
    private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), MinWidth = 90, TextAlignment = TextAlignment.Center };
    private readonly Button _previous = new() { Content = Previous }, _next = new() { Content = Next };
    private readonly List<System.Windows.Controls.Primitives.ToggleButton> _chips = [];
    private readonly Button _removeButton = new();
    private IReadOnlyList<WallpaperEntry> _all = [];
    private IReadOnlySet<string> _current = new HashSet<string>();
    private WallpaperFilter _filter;
    private int _page = 1;
    private bool _removing;

    public WallpaperWindow(WallpaperService service, SettingsStore settings)
    {
        _service = service;
        _settings = settings;
        Title = WallpaperService.Title;
        Width = 760;
        Height = 560;
        MinWidth = 560;
        MinHeight = 420;
        ShowInTaskbar = true;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _filter = WallpaperSupport.FilterFromRaw(settings.GetString(DefaultsKey.WallpaperFilter));
        foreach (var label in new[] { _pageLabel, _empty }) label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (filter, label) in new[] { (WallpaperFilter.All, FilterAll), (WallpaperFilter.Own, FilterOwn), (WallpaperFilter.System, FilterSystem) })
        {
            var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = label, Tag = filter, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), IsChecked = filter == _filter };
            chip.Click += (_, _) => { _filter = filter; _page = 1; _settings.Set(DefaultsKey.WallpaperFilter, WallpaperSupport.FilterRaw(filter)); Rebuild(); };
            _chips.Add(chip);
            chips.Children.Add(chip);
        }
        var addImage = new Button { Content = AddImage, Margin = new Thickness(0, 0, 6, 0) };
        addImage.Click += (_, _) => PickImages();
        var addFolder = new Button { Content = AddFolder, Margin = new Thickness(0, 0, 6, 0) };
        addFolder.Click += (_, _) => PickFolder();
        _removeButton.Content = Remove;
        _removeButton.Click += (_, _) => { _removing = !_removing; Rebuild(); };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { addImage, addFolder, _removeButton } };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        header.Children.Add(chips);

        var all = new ToggleSwitch { Content = ApplyAll, IsChecked = settings.GetBool(DefaultsKey.WallpaperApplyAllDisplays), VerticalAlignment = VerticalAlignment.Center };
        all.Click += (_, _) => settings.Set(DefaultsKey.WallpaperApplyAllDisplays, all.IsChecked == true);
        var open = new Button { Content = OpenSettings, Margin = new Thickness(12, 0, 0, 0) };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:personalization-background") { UseShellExecute = true });
        _previous.Click += (_, _) => { _page--; Rebuild(); };
        _next.Click += (_, _) => { _page++; Rebuild(); };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _previous, _pageLabel, _next } };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(pager, Dock.Right);
        footer.Children.Add(pager);
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { all, open } });

        var body = new Grid();
        body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grid });
        body.Children.Add(_empty);
        var root = new DockPanel { Margin = new Thickness(18, 44, 18, 16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        Loaded += async (_, _) => await Reload();
        Closed += (_, _) => _service.Gallery.End(_viewer);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        _service.Gallery.Begin(_viewer);
    }

    private async Task Reload()
    {
        var token = _service.Gallery.Invalidate();
        var entries = await _service.LoadAsync(token);
        if (entries is null || !_service.Gallery.Accepts(token)) return;
        _all = entries;
        _current = _service.CurrentPictures();
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var chip in _chips) chip.IsChecked = (WallpaperFilter)chip.Tag == _filter;
        _removeButton.Content = _removing ? DoneRemoving : Remove;
        var shown = WallpaperSupport.Filtered(_all, _filter);
        _page = WallpaperSupport.ClampedPage(_page, shown.Count);
        var pages = WallpaperSupport.PageCount(shown.Count);
        _pageLabel.Text = pages == 0 ? "" : $"{_page} of {pages}";
        _previous.IsEnabled = _page > 1;
        _next.IsEnabled = _page < pages;
        _empty.Text = shown.Count > 0 ? "" : _filter == WallpaperFilter.Own ? EmptyOwn : EmptyAll;
        _grid.Children.Clear();
        foreach (var entry in WallpaperSupport.PageSlice(shown, _page)) _grid.Children.Add(Tile(entry));
    }

    private FrameworkElement Tile(WallpaperEntry entry)
    {
        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var picture = new Image { Stretch = Stretch.UniformToFill, Height = 84 };
        _ = Load(picture, entry);
        var current = _current.Contains(entry.Id);
        var title = new TextBlock { Text = entry.Title, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 2, 0), Opacity = 0.85 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var tile = new Border
        {
            Margin = new Thickness(4), Padding = new Thickness(6), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, ToolTip = entry.ImagePath,
            Background = new SolidColorBrush(dark ? Color.FromArgb(0x22, 255, 255, 255) : Color.FromArgb(0x14, 0, 0, 0)),
            BorderThickness = new Thickness(2), BorderBrush = current ? (Brush)new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)) : Brushes.Transparent,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = picture },
                    title,
                },
            },
        };
        if (_removing && entry.Source == WallpaperSource.Own)
            tile.Child = new Grid
            {
                Children =
                {
                    (UIElement)tile.Child,
                    new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
                                 Child = new TextBlock { Text = Remove, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold } },
                },
            };
        tile.MouseLeftButtonUp += async (_, _) =>
        {
            if (_removing)
            {
                if (entry.Source == WallpaperSource.Own) { _service.Remove(entry); await Reload(); }
                return;
            }
            if (_service.Apply(entry)) { _current = _service.CurrentPictures(); Rebuild(); }
            else QuickToolHud.Show(WallpaperService.ApplyFailed);
        };
        return tile;
    }

    private static async Task Load(Image image, WallpaperEntry entry)
    {
        try
        {
            var source = await ShellThumbnails.GetAsync(entry.PreviewPath, 256);
            image.Dispatcher.Invoke(() => image.Source = source);
        }
        catch (Exception) { }
    }

    private async void PickImages()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true, Title = "Choose images to keep in the wallpaper list",
            Filter = "Pictures|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        _service.AddPaths(dialog.FileNames.Where(WallpaperSupport.IsStillImage));
        await Reload();
    }

    private async void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder of images to keep in the wallpaper list" };
        if (dialog.ShowDialog(this) != true) return;
        _service.AddPaths([dialog.FolderName]);
        await Reload();
    }
}
