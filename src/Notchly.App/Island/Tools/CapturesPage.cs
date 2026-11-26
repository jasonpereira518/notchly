// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the island's Captures page (upstream's NotchCapturesView): the capture actions as tiles (screenshot, scrolling, copy text,
// colour) and a strip of the latest screenshots to open in the editor. The actions are the same services the shortcuts run.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Platform.Capture;
using Notchly.App.Screenshots;
using Notchly.App.Shell;
using Notchly.App.Tools;
using Notchly.Core.Capture;
using Notchly.Core.Features;
using Notchly.Core.Settings;
using Image = System.Windows.Controls.Image;

namespace Notchly.App.Island.Tools;

internal sealed class CapturesPage : IDisposable
{
    private readonly Grid _root;
    private readonly SettingsStore _settings;
    private readonly Func<CaptureHistory> _history;
    private readonly Action _collapse;
    private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
    private readonly WrapPanel _recent = new();
    private bool _visible;
    private bool _subscribed;

    public CapturesPage(NotchWindow window, SettingsStore settings, Func<CaptureHistory> history, Action collapse)
    {
        _root = window.CapturesPage;
        _settings = settings;
        _history = history;
        _collapse = collapse;
        var stack = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
        stack.Children.Add(Title("Capture"));
        stack.Children.Add(_tiles);
        stack.Children.Add(Title("Recent"));
        stack.Children.Add(_recent);
        _root.Children.Add(stack);
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) Render();
        }
    }

    public void Layout(double budgetHeight, double width) => Render();

    private static TextBlock Title(string text) =>
        new() { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), Margin = new Thickness(2, 0, 0, 2) };

    private static T? Service<T>(AppFeature feature) where T : class, IFeatureService => App.Current.Features.Get<T>(feature);

    private void Render()
    {
        if (!_subscribed) { _history().Changed += () => _root.Dispatcher.BeginInvoke(() => { if (_visible) Render(); }); _subscribed = true; }
        _tiles.Children.Clear();
        void Tile(string label, string glyph, AppFeature feature, Action run)
        {
            if (!_settings.IsAvailable(feature)) return;
            var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            text.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            var tile = new Border
            {
                Width = 92, Height = 62, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)), Child = text,
            };
            tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x44, 255, 255, 255));
            tile.MouseLeave += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
            // The island closes first so it is not in the picture, then the action runs.
            tile.MouseLeftButtonUp += (_, _) => { _collapse(); _root.Dispatcher.BeginInvoke(run, System.Windows.Threading.DispatcherPriority.Background); };
            _tiles.Children.Add(tile);
        }
        Tile("Area", "", AppFeature.Screenshot, () => Service<ScreenshotService>(AppFeature.Screenshot)?.CaptureArea());
        Tile("Scrolling", "", AppFeature.Screenshot, () => Service<ScreenshotService>(AppFeature.Screenshot)?.CaptureArea(false, ScreenCaptureTool.Scrolling));
        Tile("Copy text", "", AppFeature.ScreenOCR, () => Service<ScreenTextService>(AppFeature.ScreenOCR)?.Capture());
        Tile("Color", "", AppFeature.ColorPicker, () => Service<ColorPickerService>(AppFeature.ColorPicker)?.Pick());

        _recent.Children.Clear();
        var service = Service<ScreenshotService>(AppFeature.Screenshot);
        var entries = _settings.IsAvailable(AppFeature.Screenshot) ? _history().Entries().Where(e => e.Kind == RecentCaptureKind.Screenshot).Take(6).ToList() : [];
        if (entries.Count == 0)
        {
            _recent.Children.Add(new TextBlock { Text = "No screenshots yet", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)), Margin = new Thickness(2, 4, 0, 0) });
            return;
        }
        foreach (var entry in entries)
        {
            var picture = new Image { Width = 78, Height = 50, Stretch = Stretch.UniformToFill };
            if (_history().ThumbnailPathOf(entry) is { } thumb && ImageCodec.DecodeFile(thumb) is { } image) picture.Source = ImageCodec.ToBitmap(image);
            var frame = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Child = picture, ToolTip = entry.CreatedAt.LocalDateTime.ToString("g") };
            frame.MouseLeftButtonUp += (_, _) =>
            {
                if (_history().PathOf(entry) is not { } path || ImageCodec.DecodeFile(path) is not { } full) return;
                _collapse();
                service?.OpenEditor(full, entry.Scale ?? 1);
            };
            _recent.Children.Add(frame);
        }
    }

    public void Dispose() { }
}
