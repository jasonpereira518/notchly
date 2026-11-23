// SPDX-License-Identifier: GPL-3.0-or-later
// The island's Camera page: the Windows counterpart of upstream UI/Notch/NotchCameraView.swift (the hint and
// Open camera, NotchCameraChooser, NotchCameraStopButton) and the embedded half of CameraPreviewView.swift.
// Windows-specific: the mirror is a WriteableBitmap on a flipped image brush, the chosen camera is remembered
// in Notchly's settings by device id, and a denied camera points at Windows' camera privacy page.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform.Camera;
using Notchly.App.Platform.Shell;
using Notchly.Core.Camera;
using Notchly.Core.Notch;
using Notchly.Core.Settings;

namespace Notchly.App.Island.Camera;

public sealed class CameraPage : IDisposable
{
    private const string WebcamGlyph = "", WarningGlyph = "";
    private static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Soft = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TileBack = Frozen(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Edge = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

    private readonly NotchWindow _window;
    private readonly SettingsStore _settings;
    private readonly CameraCapture _capture;
    private readonly Func<bool> _canPresent;
    private readonly Action<bool> _modal;
    private readonly Grid _root;
    private readonly ImageBrush _mirror;
    private bool _visible, _presented, _locked;
    private double _width = 500, _height = 264;

    /// <param name="canPresent">The island is open on this page with the page turned on (upstream canPresent).</param>
    /// <param name="modal">Keeps the island open while the camera menu, outside it, is up.</param>
    public CameraPage(NotchWindow window, SettingsStore settings, Dispatcher dispatcher, Func<bool> canPresent, Action<bool> modal)
    {
        _window = window;
        _settings = settings;
        _canPresent = canPresent;
        _modal = modal;
        _root = window.CameraPage;
        _capture = new CameraCapture(dispatcher);
        _capture.StateChanged += Render;
        _capture.FrameShown += OnFrameShown;
        // Mirrored like a real mirror, filling its frame (upstream's preview layer, aspect-fill and flipped).
        _mirror = new ImageBrush
        {
            Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
            RelativeTransform = new ScaleTransform(-1, 1, 0.5, 0.5),
        };
        RenderOptions.SetBitmapScalingMode(_mirror, BitmapScalingMode.Linear);
        window.CameraStop.Click += (_, _) => Hide();
        window.CameraMenu.Click += (_, _) => ShowMenu();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];

    /// <summary>The page fills the preset's height, at least upstream's 144 points so messages stay reachable.</summary>
    public void Layout(double width, double height)
    {
        _width = width;
        _height = Math.Max(NotchLayout.CameraMinimumHeight, height);
        _root.Height = _height;
        if (_visible) Render();
    }

    /// <summary>Showing the page shows the hint; leaving it (or the island closing) stops the camera at once.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (!value) Hide();
            else Render();
        }
    }

    /// <summary>Open camera: the camera starts only from this explicit click (upstream showEmbedded).</summary>
    private void Show()
    {
        if (_presented || _locked || !_canPresent()) return;
        _presented = true;
        _ = _capture.StartAsync(_settings.GetString(DefaultsKey.NotchCameraDevice));
        Render();
    }

    /// <summary>Stop camera, leaving the page, closing the island, locking or sleeping (upstream hideEmbedded).</summary>
    public void Hide()
    {
        if (!_presented) { Render(); return; }
        _presented = false;
        _ = _capture.StopAsync();
        Render();
    }

    private void Retry()
    {
        if (!_presented) return;
        _ = _capture.StartAsync(_settings.GetString(DefaultsKey.NotchCameraDevice));
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => _window.Dispatcher.BeginInvoke(() =>
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
        {
            _locked = true;
            Hide();
        }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _locked = false;
    });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _window.Dispatcher.BeginInvoke(Hide);
    }

    private void OnFrameShown()
    {
        if (!ReferenceEquals(_mirror.ImageSource, _capture.Frame)) _mirror.ImageSource = _capture.Frame;
    }

    // MARK: Drawing

    public void Render()
    {
        var running = _presented && _capture.State == CameraPreviewState.Running;
        _window.CameraStop.Visibility = _visible && _presented ? Visibility.Visible : Visibility.Collapsed;
        var chooser = _visible && running && _capture.Devices.Count > 1;
        _window.CameraMenu.Visibility = chooser ? Visibility.Visible : Visibility.Collapsed;
        if (chooser)
        {
            var name = CurrentName();
            _window.CameraMenuName.Text = name;
            _window.CameraMenu.ToolTip = name;
        }
        if (!_visible) { _root.Children.Clear(); _mirror.ImageSource = null; return; }
        _root.Children.Clear();
        _root.Children.Add(_presented ? Preview() : HintView());
    }

    private string CurrentName() =>
        _capture.Devices.FirstOrDefault(d => d.Id == _capture.DeviceId)?.Name ?? CameraPreviewSupport.MenuLabel;

    /// <summary>Upstream's idle page: the camera glyph in a tile beside the hint and Open camera.</summary>
    private FrameworkElement HintView()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 440, Margin = new Thickness(12, 0, 12, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(18), Background = TileBack, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = WebcamGlyph, FontFamily = IconFont, FontSize = 26, Foreground = Dim, TextTrimming = TextTrimming.None,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = CameraPreviewSupport.Hint, FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
        });
        var open = new Button
        {
            Style = (Style)_window.Resources["ChipButton"], Content = CameraPreviewSupport.Start, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        open.Click += (_, _) => Show();
        text.Children.Add(open);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>The mirror's frame at upstream's preview size: the image, or what stands in for it.</summary>
    private FrameworkElement Preview()
    {
        var size = NotchLayout.CameraPreviewSize(_width, _height);
        var running = _capture.State == CameraPreviewState.Running;
        if (running && _capture.Frame is { } frame) _mirror.ImageSource = frame;
        var frameBorder = new Border
        {
            Width = size.Width, Height = size.Height, CornerRadius = new CornerRadius(NotchLayout.CameraPreviewRadius),
            Background = running ? _mirror : Brushes.Black, BorderBrush = Edge, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(frameBorder, CameraPreviewSupport.Title);
        if (!running) frameBorder.Child = Status();
        return frameBorder;
    }

    /// <summary>Upstream CameraPreviewView's states: a quiet wait, or a message with what to do about it.</summary>
    private FrameworkElement Status()
    {
        switch (_capture.State)
        {
            case CameraPreviewState.Denied:
                return Message(WarningGlyph, CameraPreviewSupport.Denied, CameraPreviewSupport.DeniedDetail, "Open Windows settings",
                    () => ShellActions.Open("ms-settings:privacy-webcam"));
            case CameraPreviewState.Unavailable:
                return Message(WarningGlyph, CameraPreviewSupport.Unavailable, null, CameraPreviewSupport.Start, Retry);
            case CameraPreviewState.NoCamera:
                return Message(WebcamGlyph, CameraPreviewSupport.NoCamera, null, null, null);
            default:
                return new TextBlock
                {
                    Text = "Starting the camera…", FontSize = 12, Foreground = Faint,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
        }
    }

    private FrameworkElement Message(string glyph, string text, string? detail, string? action, Action? act)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 28, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = 24, Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.None,
        });
        stack.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Foreground = Soft, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.None, Margin = new Thickness(0, 10, 0, 0),
        });
        if (detail is not null)
            stack.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.None, Margin = new Thickness(0, 4, 0, 0),
            });
        if (action is not null && act is not null)
        {
            var button = new Button
            {
                Style = (Style)_window.Resources["ChipButton"], Content = action, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
            };
            button.Click += (_, _) => act();
            stack.Children.Add(button);
        }
        return stack;
    }

    // MARK: The camera menu (upstream NotchCameraChooser)

    /// <summary>Every camera, a tick on the one showing; a pick is remembered for next time.</summary>
    private void ShowMenu()
    {
        if (!_presented || _capture.Devices.Count < 2) return;
        var window = _window;
        var menu = new ContextMenu { PlacementTarget = window.CameraMenu, Placement = PlacementMode.Bottom, Style = (Style)window.Resources["IslandMenu"] };
        menu.Resources[typeof(MenuItem)] = window.Resources["IslandMenuItem"];
        foreach (var device in _capture.Devices)
        {
            var item = new MenuItem { Header = device.Name, IsChecked = device.Id == _capture.DeviceId };
            item.Click += (_, _) => Select(device);
            menu.Items.Add(item);
        }
        _modal(true);
        menu.Closed += (_, _) => _modal(false);
        menu.IsOpen = true;
    }

    private void Select(CameraDevice device)
    {
        if (!_presented || device.Id == _capture.DeviceId) return;
        _settings.Set(DefaultsKey.NotchCameraDevice, device.Id);
        _ = _capture.StartAsync(device.Id);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _capture.StateChanged -= Render;
        _capture.FrameShown -= OnFrameShown;
        _presented = false;
        _capture.Dispose();
    }
}
