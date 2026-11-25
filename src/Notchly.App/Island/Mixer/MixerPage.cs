// SPDX-License-Identifier: GPL-3.0-or-later
// The island's volume mixer: the Windows counterpart of upstream UI/Notch/NotchMixerView.swift. A master column
// and a microphone column, a divider, then a rail of app columns (icon with its menu, a vertical slider with a live
// meter in place of upstream's playing dot, and a percent footer with reset and mute). A band above holds the
// output menu and an options menu (upstream's options view, as one menu). Pinned rows lead; the menu moves rows.
// Windows plays an app up to 100%, so the slider tops out there (no boost).
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Platform.Audio;
using Notchly.App.Platform.Shell;
using Notchly.App.Sound;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Sound;

namespace Notchly.App.Island.Mixer;

public sealed class MixerPage
{
    private const double MasterWidth = 72, ColumnWidth = 96, FooterHeight = 28, HeaderHeight = 32, TrackThickness = 28, BandHeight = 26;
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TrackBack = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Fill = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Meter = Frozen(Color.FromArgb(0xB0, 0x30, 0xD1, 0x58));
    private static readonly SolidColorBrush Divider = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    private readonly NotchWindow _window;
    private readonly Func<MixerService?> _source;
    private readonly SettingsStore _settings;
    private readonly Action<bool> _modal;
    private readonly Grid _root;
    private readonly Grid _band;
    private readonly Button _outputButton;
    private readonly StackPanel _rail = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _railScroll;
    private readonly StackPanel _fixedColumns = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _empty;
    private readonly Dictionary<string, Column> _columns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private MixerService? _service;
    private IDisposable? _observing;
    private Column? _master, _mic;
    private bool _visible, _dragging;
    private double _height = NotchLayout.MixerMinimumHeight;

    public MixerPage(NotchWindow window, Func<MixerService?> source, SettingsStore settings, Action<bool> modal)
    {
        _window = window;
        _source = source;
        _settings = settings;
        _modal = modal;
        _root = window.MixerPage;
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BandHeight) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _band = new Grid { Margin = new Thickness(2, 0, 2, 2) };
        _outputButton = new Button { Style = (Style)window.Resources["ChipButton"], HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Content = "Output", Padding = new Thickness(10, 2, 10, 2), Height = 22, FontSize = 11 };
        _outputButton.Click += (_, _) => ShowOutputMenu(_outputButton);
        _band.Children.Add(_outputButton);
        var options = new Button { Style = (Style)window.Resources["IconButton"], Content = "", FontSize = 12, ToolTip = "Mixer options", HorizontalAlignment = HorizontalAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetName(options, "Mixer options");
        options.Click += (_, _) => ShowOptionsMenu(options);
        _band.Children.Add(options);
        Grid.SetRow(_band, 0);
        _root.Children.Add(_band);

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_fixedColumns, Dock.Left);
        body.Children.Add(_fixedColumns);
        _railScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rail,
            Focusable = false, Tag = "MixerRail",
        };
        _railScroll.PreviewMouseWheel += (_, e) =>
        {
            // The wheel moves the rail sideways; over a slider it moves the slider instead (handled there).
            if (e.Handled) return;
            _railScroll.ScrollToHorizontalOffset(_railScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        body.Children.Add(_railScroll);
        _empty = new TextBlock
        {
            Text = "Apps that use audio show up here", FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(body, 1);
        _root.Children.Add(body);
        Grid.SetRow(_empty, 1);
        _root.Children.Add(_empty);
    }

    public void Layout(double height)
    {
        _height = Math.Max(NotchLayout.MixerMinimumHeight + BandHeight, height);
        _root.Height = _height;
        Rebuild();
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value)
            {
                _service = _source();
                if (_service is null) { Rebuild(); return; }
                _service.Changed += OnChanged;
                _service.Metered += OnMetered;
                _observing = _service.Observe();
                Rebuild();
            }
            else
            {
                if (_service is not null) { _service.Changed -= OnChanged; _service.Metered -= OnMetered; }
                _observing?.Dispose();
                _observing = null;
                _service = null;
            }
        }
    }

    private void OnChanged()
    {
        if (_dragging) return;
        Rebuild();
    }

    private void OnMetered(MixerMeters meters)
    {
        if (_dragging) return;
        foreach (var (id, column) in _columns)
            if (meters.Peaks.TryGetValue(id, out var peak)) column.SetPeak(peak);
        _master?.SetLevel(meters.OutputVolume, meters.OutputMuted);
        _mic?.SetLevel(meters.InputVolume ?? 0, meters.InputMuted);
    }

    // MARK: Columns

    private static double TrackHeight(double pageHeight) => Math.Min(160, Math.Max(32, pageHeight - BandHeight - HeaderHeight - FooterHeight - 12));

    private void Rebuild()
    {
        _fixedColumns.Children.Clear();
        _rail.Children.Clear();
        _columns.Clear();
        _master = _mic = null;
        if (_service is null)
        {
            _empty.Text = "Turn on the Volume mixer in Settings → Features.";
            _empty.Visibility = Visibility.Visible;
            return;
        }
        var track = TrackHeight(_height);
        var current = _service.Outputs.FirstOrDefault(o => o.IsDefault);
        _outputButton.Content = current?.Name ?? "No output";
        _outputButton.MaxWidth = 220;

        _master = Column.Master(this, track, "", "Output volume", _service.OutputVolume, _service.OutputMuted,
            v => _service.SetOutputVolume(v), () => _service.SetOutputMuted(!_service.OutputMuted));
        _fixedColumns.Children.Add(_master.Element);
        if (_service.InputVolume is { } input)
        {
            _mic = Column.Master(this, track, "", "Microphone level", input, _service.InputMuted,
                v => _service.SetInputVolume(v), () => _service.SetInputMuted(!_service.InputMuted));
            _fixedColumns.Children.Add(_mic.Element);
        }
        _fixedColumns.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 8, 5, 8), Background = Divider });

        foreach (var row in _service.Rows)
        {
            var column = Column.App(this, row, track);
            _columns[row.Id] = column;
            _rail.Children.Add(column.Element);
            if (row.ExePath is { } path) LoadIcon(path, column);
        }
        _empty.Visibility = _service.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = "Apps that use audio show up here";
        _empty.Margin = new Thickness(MasterWidth * 2 + 20, 0, 0, 0);
    }

    private async void LoadIcon(string path, Column column)
    {
        if (!_icons.TryGetValue(path, out var icon))
            _icons[path] = icon = await ShellThumbnails.GetAsync(path, 64);
        column.SetIcon(icon);
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

    private void ShowOutputMenu(FrameworkElement anchor)
    {
        if (_service is null) return;
        var menu = Menu(anchor);
        foreach (var output in _service.Outputs)
        {
            var item = new MenuItem { Header = output.Name, IsChecked = output.IsDefault };
            item.Click += (_, _) => _service?.SetSystemOutput(output.Id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ShowOptionsMenu(FrameworkElement anchor)
    {
        if (_service is null) return;
        var service = _service;
        var menu = Menu(anchor);
        AddDeviceMenu(menu, "Output", service.Outputs, id => service.SetSystemOutput(id));
        AddDeviceMenu(menu, "System sounds", service.Outputs, id => service.SetSoundsOutput(id));
        AddDeviceMenu(menu, "Microphone", service.Inputs, id => service.SetInput(id));
        menu.Items.Add(new Separator());
        AddToggle(menu, "Hide inactive apps", DefaultsKey.MixerHideInactiveApps);
        AddToggle(menu, "Lower volume when headphones disconnect", DefaultsKey.MixerLowerVolumeOnHeadphonesDisconnect);
        AddToggle(menu, "Show system sounds", DefaultsKey.MixerShowSystemSounds);
        var hidden = service.HiddenApps;
        if (hidden.Count > 0)
        {
            menu.Items.Add(new Separator());
            var parent = new MenuItem { Header = $"Hidden apps ({hidden.Count})" };
            foreach (var (id, name) in hidden.OrderBy(h => h.Value, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new MenuItem { Header = $"Show {name}" };
                item.Click += (_, _) => service.Unhide(id);
                parent.Items.Add(item);
            }
            menu.Items.Add(parent);
        }
        menu.IsOpen = true;
    }

    private void AddToggle(ContextMenu menu, string title, string key)
    {
        var item = new MenuItem { Header = title, IsChecked = _settings.GetBool(key) };
        item.Click += (_, _) => { _settings.Set(key, !_settings.GetBool(key)); _service?.Refresh(); };
        menu.Items.Add(item);
    }

    private static void AddDeviceMenu(ContextMenu menu, string title, IReadOnlyList<AudioEndpoint> devices, Action<string> choose)
    {
        var parent = new MenuItem { Header = title };
        foreach (var device in devices)
        {
            var item = new MenuItem { Header = device.Name, IsChecked = device.IsDefault };
            item.Click += (_, _) => choose(device.Id);
            parent.Items.Add(item);
        }
        menu.Items.Add(parent);
    }

    private void ShowRowMenu(FrameworkElement anchor, MixerRow row)
    {
        if (_service is null) return;
        var service = _service;
        var menu = Menu(anchor);
        if (row.PersistenceId is not null && !row.IsSystemSounds)
        {
            var pin = new MenuItem { Header = row.Pinned ? "Unpin" : "Pin to Front" };
            pin.Click += (_, _) => service.TogglePin(row.Id);
            menu.Items.Add(pin);
            var left = new MenuItem { Header = "Move Left" };
            left.Click += (_, _) => service.Move(row.Id, -1);
            menu.Items.Add(left);
            var right = new MenuItem { Header = "Move Right" };
            right.Click += (_, _) => service.Move(row.Id, 1);
            menu.Items.Add(right);
        }
        if (service.PerAppOutputAvailable && !row.IsSystemSounds && row.PersistenceId is not null && !row.BypassesControl)
        {
            var output = new MenuItem { Header = "Output" };
            var system = new MenuItem { Header = "Default", IsChecked = row.SelectedOutputId is null };
            system.Click += (_, _) => service.SetOutput(row.Id, null);
            output.Items.Add(system);
            var listed = service.Outputs.Select(o => o.Id).ToList();
            foreach (var device in service.Outputs)
            {
                var item = new MenuItem { Header = device.Name + (device.IsDefault ? " (current)" : ""), IsChecked = row.SelectedOutputId == device.Id };
                item.Click += (_, _) => service.SetOutput(row.Id, device.Id);
                output.Items.Add(item);
            }
            if (MixerSupport.NeedsUnavailableOutputRow(row.SelectedOutputId, row.OutputUnavailable, listed))
                output.Items.Add(new MenuItem { Header = "Output unavailable", IsChecked = true, IsEnabled = false });
            menu.Items.Add(output);
        }
        if (!row.BypassesControl)
        {
            var reset = new MenuItem { Header = "Reset to 100%", IsEnabled = !MixerSupport.IsUnity(row.Volume) };
            reset.Click += (_, _) => service.ResetVolume(row.Id);
            menu.Items.Add(reset);
        }
        if (!row.IsSystemSounds && row.PersistenceId is not null)
        {
            menu.Items.Add(new Separator());
            var hide = new MenuItem { Header = "Hide from the list" };
            hide.Click += (_, _) => service.Hide(row.Id);
            menu.Items.Add(hide);
        }
        menu.IsOpen = true;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];

    private static TextBlock Glyph(string glyph, double size, Brush brush) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.None,
    };

    /// <summary>One column: header, a vertical slider and the percent footer.</summary>
    private sealed class Column
    {
        private readonly MixerPage _page;
        private readonly Border _fill = new() { Background = Fill, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(TrackThickness / 2) };
        private readonly Border _peak = new() { Background = Meter, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Width = 4, CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
        private readonly Border _track;
        private readonly TextBlock _percent;
        private readonly Button _reset, _mute;
        private readonly TextBlock _muteGlyph;
        private readonly Image? _icon;
        private readonly double _trackHeight;
        private readonly Action<double> _set;
        private readonly bool _isApp;
        private double _value;
        private bool _muted;

        public FrameworkElement Element { get; }

        private Column(MixerPage page, double trackHeight, double width, Action<double> set, bool isApp)
        {
            _page = page;
            _trackHeight = trackHeight;
            _set = set;
            _isApp = isApp;
            _track = new Border { Width = TrackThickness, Height = trackHeight, CornerRadius = new CornerRadius(TrackThickness / 2), Background = TrackBack, ClipToBounds = true };
            _track.Child = new Grid { Children = { _fill, _peak } };
            _percent = new TextBlock { FontSize = 11, FontWeight = FontWeights.Medium, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _reset = new Button { Style = (Style)page._window.Resources["IconButton"], Content = "", FontSize = 10, Width = 24, Height = 24, ToolTip = "Reset to 100%" };
            _muteGlyph = Glyph("", 11, Brushes.White);
            _mute = new Button { Style = (Style)page._window.Resources["IconButton"], Content = _muteGlyph, Width = 24, Height = 24, ToolTip = "Mute" };
            Element = new Grid { Width = width };
            _icon = isApp ? new Image { Width = 28, Height = 28, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center } : null;
            Wire();
        }

        private Grid Frame(UIElement header)
        {
            var grid = (Grid)Element;
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FooterHeight) });
            Grid.SetRow(header, 0);
            grid.Children.Add(header);
            var sliderHost = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            sliderHost.Children.Add(_track);
            Grid.SetRow(sliderHost, 1);
            grid.Children.Add(sliderHost);
            sliderHost.MouseLeftButtonDown += (_, e) => { _page._dragging = true; sliderHost.CaptureMouse(); Drag(e); e.Handled = true; };
            sliderHost.MouseMove += (_, e) => { if (sliderHost.IsMouseCaptured) Drag(e); };
            sliderHost.MouseLeftButtonUp += (_, e) => { sliderHost.ReleaseMouseCapture(); _page._dragging = false; e.Handled = true; };
            sliderHost.LostMouseCapture += (_, _) => _page._dragging = false;
            sliderHost.PreviewMouseWheel += (_, e) => { Apply(Math.Clamp(_value + Math.Sign(e.Delta) * 0.02, 0, 1)); e.Handled = true; };

            var footer = new Grid { Width = Element.Width };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _reset.HorizontalAlignment = HorizontalAlignment.Left;
            _mute.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_percent, 1);
            Grid.SetColumn(_mute, 2);
            footer.Children.Add(_reset);
            footer.Children.Add(_percent);
            footer.Children.Add(_mute);
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);
            return grid;
        }

        private void Wire()
        {
            _reset.Click += (_, _) => Apply(1);
        }

        private void Drag(MouseEventArgs e)
        {
            var y = e.GetPosition(_track).Y;
            Apply(Math.Clamp(1 - y / _trackHeight, 0, 1));
        }

        private void Apply(double value)
        {
            // A device slider reaches 100% only; an app's stored scale is held to what Windows can play.
            value = Math.Round(value * 100) / 100;
            SetLevel(value, value <= MixerSupport.MutedThreshold);
            _set(value);
        }

        public void SetLevel(double value, bool muted)
        {
            _value = Math.Clamp(value, 0, 1);
            _muted = muted;
            _fill.Height = _trackHeight * _value;
            _percent.Text = $"{MixerSupport.DisplayPercent(_value)}%";
            _percent.Opacity = muted ? 0.5 : 1;
            _reset.Visibility = _isApp && !MixerSupport.IsUnity(_value) ? Visibility.Visible : Visibility.Hidden;
            _muteGlyph.Text = muted || _value <= MixerSupport.MutedThreshold ? "" : "";
            _mute.ToolTip = muted ? "Unmute" : "Mute";
        }

        public void SetPeak(double peak) => _peak.Height = Math.Min(_trackHeight, _trackHeight * Math.Clamp(peak, 0, 1) * _value);

        public void SetIcon(ImageSource? icon)
        {
            if (_icon is not null && icon is not null) _icon.Source = icon;
        }

        /// <summary>The output or microphone column: a glyph header, no menu.</summary>
        public static Column Master(MixerPage page, double track, string glyph, string tip, double value, bool muted, Action<double> set, Action toggleMute)
        {
            var column = new Column(page, track, MasterWidth, set, isApp: false);
            var header = Glyph(glyph, 16, Brushes.White);
            header.ToolTip = tip;
            column.Frame(header);
            column._mute.Click += (_, _) => toggleMute();
            column.SetLevel(value, muted);
            return column;
        }

        public static Column App(MixerPage page, MixerRow row, double track)
        {
            var column = new Column(page, track, ColumnWidth, v => page._service?.SetVolume(row.Id, v), isApp: true);
            var header = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = row.IsSystemSounds ? (UIElement)Glyph("", 18, Brushes.White) : column._icon!;
            ((FrameworkElement)icon).ToolTip = row.Name + (row.Pinned ? " · pinned" : "") + (row.OutputUnavailable ? " · using default until its output returns" : "");
            header.Children.Add(icon);
            var menu = new Button { Style = (Style)page._window.Resources["IconButton"], Content = "", FontSize = 11, Width = 22, Height = 24, ToolTip = "More", Margin = new Thickness(0, 0, 2, 0) };
            menu.Click += (_, _) => page.ShowRowMenu(menu, row);
            Grid.SetColumn(menu, 1);
            header.Children.Add(menu);
            if (row.BypassesControl)
            {
                column.Frame(header);
                column._track.Opacity = 0.35;
                column._track.IsHitTestVisible = false;
                column._mute.IsEnabled = false;
                column._reset.Visibility = Visibility.Collapsed;
                column._percent.Text = "Own volume";
                column._percent.FontSize = 10;
                return column;
            }
            column.Frame(header);
            column._mute.Click += (_, _) => page._service?.ToggleMute(row.Id);
            column.SetLevel(Math.Min(row.Volume, MixerSupport.PlayableMaximum), row.Muted);
            return column;
        }
    }
}
