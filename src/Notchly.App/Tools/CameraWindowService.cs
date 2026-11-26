// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/CameraPreviewService.swift and UI/CameraPreview/CameraPreviewView.swift for the
// floating mirror: the shortcut opens a small flipped preview near the top of the screen with the pointer, where the camera usually
// is; Esc, a click elsewhere or another app coming forward closes it, and the camera is on only while it is open. The capture itself is
// the one the island's Camera page uses. Windows differences: closing on a click elsewhere is the window losing the focus (which also
// covers another app coming forward), and the chosen camera is kept by Notchly since Windows has no per-app default.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchly.App.Platform;
using Notchly.App.Platform.Camera;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.Core.Camera;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Serilog;

namespace Notchly.App.Tools;

public sealed class CameraWindowService(SettingsStore settings, HotkeyService hotkeys, Dispatcher dispatcher) : IFeatureService
{
    private IDisposable? _registration;
    private string? _registered;
    private CameraWindow? _window;

    public bool ShortcutUnavailable { get; private set; }

    public bool IsOpen => _window is { IsVisible: true };

    public void Sync()
    {
        var wanted = settings.GetBool(DefaultsKey.CameraPreviewShortcutEnabled) ? settings.GetString(DefaultsKey.CameraPreviewShortcut) : "";
        if (wanted == _registered) return;
        _registration?.Dispose();
        _registration = null;
        _registered = wanted;
        ShortcutUnavailable = false;
        if (wanted.Length == 0 || Shortcut.Parse(wanted) is not { } shortcut) return;
        _registration = hotkeys.Register(shortcut, () => dispatcher.BeginInvoke(Toggle));
        ShortcutUnavailable = _registration is null;
    }

    /// <summary>Opens the mirror, or closes it when it is open.</summary>
    public void Toggle()
    {
        if (_window is { IsVisible: true } open) { open.Close(); return; }
        _window = new CameraWindow(settings, dispatcher);
        _window.Closed += (_, _) => _window = null;
        _window.Open();
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _window?.Close();
    }
}

internal sealed class CameraWindow : Window
{
    private static readonly SolidColorBrush Soft = new(Color.FromArgb(0xB3, 255, 255, 255));

    private readonly SettingsStore _settings;
    private readonly CameraCapture _capture;
    private readonly Image _mirror = new() { Stretch = Stretch.UniformToFill, Width = CameraWindowSupport.Width - 2, Height = CameraWindowSupport.Height - 2, RenderTransform = new ScaleTransform(-1, 1, (CameraWindowSupport.Width - 2) / 2, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Grid _stage = new() { Background = Brushes.Black };
    private readonly Border _picker;
    private readonly TextBlock _pickerLabel;
    private DateTime? _permissionResolvedAt;
    private CameraPreviewState _lastState = CameraPreviewState.Idle;
    private bool _menuOpen, _closing;

    public CameraWindow(SettingsStore settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _capture = new CameraCapture(dispatcher);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Title = CameraPreviewSupport.Title;
        Width = CameraWindowSupport.Width;
        Height = CameraWindowSupport.Height;
        Left = -32000;
        Top = -32000;
        // Aspect-fill mirror, under a hairline white border, with a black plate for the states without a picture.
        var radius = new CornerRadius(CameraWindowSupport.Radius);
        // Inside the one-pixel border, so the picture is rounded to match it.
        _stage.Clip = new RectangleGeometry(new Rect(0, 0, CameraWindowSupport.Width - 2, CameraWindowSupport.Height - 2), CameraWindowSupport.Radius - 1, CameraWindowSupport.Radius - 1);
        _stage.Children.Add(_mirror);

        _pickerLabel = new TextBlock { FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
        _picker = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12, Foreground = Brushes.White, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    _pickerLabel,
                    new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 9, Foreground = Soft, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        _picker.MouseLeftButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };

        Content = new Border
        {
            Margin = new Thickness(0), CornerRadius = radius, Background = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)), BorderThickness = new Thickness(1),
            Child = new Grid { Children = { _stage, _picker } },
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } };
        MouseEnter += (_, _) => UpdatePicker(true);
        MouseLeave += (_, _) => UpdatePicker(false);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += OnDeactivated;
        _capture.StateChanged += Render;
        _capture.FrameShown += OnFrameShown;
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSession;
        SourceInitialized += (_, _) => Place();
    }

    public void Open()
    {
        Show();
        Activate();
        Render();
        // The stored camera when it is still there, else the first (the capture falls back by itself).
        _ = _capture.StartAsync(_settings.GetString(DefaultsKey.CameraPreviewDeviceId));
    }

    /// <summary>Near the top centre of the display with the pointer (upstream <c>position</c>), in pixels.</summary>
    private void Place()
    {
        Native.GetCursorPos(out var p);
        var display = Displays.AtPoint(p.X, p.Y) ?? Displays.All().FirstOrDefault();
        if (display is null) return;
        var scale = display.Scale;
        var work = display.WorkArea;
        var screen = new RectD(work.Left / scale, work.Top / scale, work.Width / scale, work.Height / scale);
        var origin = CameraWindowSupport.Origin(screen, new SizeD(CameraWindowSupport.Width, CameraWindowSupport.Height));
        var handle = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
                            (int)Math.Round(CameraWindowSupport.Width * scale), (int)Math.Round(CameraWindowSupport.Height * scale), 0x0010);
    }

    private void OnFrameShown()
    {
        if (!ReferenceEquals(_mirror.Source, _capture.Frame)) _mirror.Source = _capture.Frame;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closing || _menuOpen) return;
        double? ago = _permissionResolvedAt is { } at ? (DateTime.UtcNow - at).TotalSeconds : null;
        // Let a click on the picker's menu settle first: the menu is ours, so its opening is not "another app".
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || _menuOpen || IsActive) return;
            if (CameraWindowSupport.HidesWhenAnotherAppActivates(_capture.State, ago)) Close();
        }, DispatcherPriority.Background);
    }

    private void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.BeginInvoke(Close);
    }

    private void OnSession(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) Dispatcher.BeginInvoke(Close);
    }

    // MARK: States

    private void Render()
    {
        if (_lastState == CameraPreviewState.WaitingPermission && _capture.State != CameraPreviewState.WaitingPermission)
            _permissionResolvedAt = DateTime.UtcNow;
        _lastState = _capture.State;
        // Keep the picture; add the message layer over it for the states that have none.
        for (var i = _stage.Children.Count - 1; i >= 1; i--) _stage.Children.RemoveAt(i);
        switch (_capture.State)
        {
            case CameraPreviewState.Running:
                if (_capture.Frame is { } frame) _mirror.Source = frame;
                break;
            case CameraPreviewState.Denied:
                _stage.Children.Add(Message(CameraPreviewSupport.Denied, CameraPreviewSupport.DeniedDetail, "Open Windows settings",
                    () => Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true })));
                break;
            case CameraPreviewState.Unavailable:
                _stage.Children.Add(Message(CameraPreviewSupport.Unavailable, null, CameraPreviewSupport.Start, Retry));
                break;
            case CameraPreviewState.NoCamera:
                _stage.Children.Add(Message(CameraPreviewSupport.NoCamera, null, null, null));
                break;
            default:
                _stage.Children.Add(new Wpf.Ui.Controls.ProgressRing { IsIndeterminate = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                break;
        }
        UpdatePicker(IsMouseOver);
    }

    private FrameworkElement Message(string title, string? detail, string? action, Action? click)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 24, 0) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        if (detail is not null)
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Soft, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        if (action is not null && click is not null)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
                Child = new TextBlock { Text = action, FontSize = 13, Foreground = Brushes.White },
            };
            chip.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
            stack.Children.Add(chip);
        }
        return stack;
    }

    private void Retry() => _ = _capture.StartAsync(_settings.GetString(DefaultsKey.CameraPreviewDeviceId));

    // MARK: Picker

    private void UpdatePicker(bool hovering)
    {
        var show = hovering && _capture.State == CameraPreviewState.Running && CameraWindowSupport.ShowsPicker(_capture.Devices.Count);
        _picker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _pickerLabel.Text = _capture.Devices.FirstOrDefault(d => d.Id == _capture.DeviceId)?.Name ?? CameraPreviewSupport.MenuLabel;
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _picker, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var device in _capture.Devices)
        {
            var item = new MenuItem { Header = device.Name, IsChecked = device.Id == _capture.DeviceId };
            item.Click += (_, _) =>
            {
                if (device.Id == _capture.DeviceId) return;
                // An explicit pick is remembered; a fallback when a camera leaves is not.
                _settings.Set(DefaultsKey.CameraPreviewDeviceId, device.Id);
                _ = _capture.StartAsync(device.Id);
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; if (!IsActive && !_closing) Activate(); };
        menu.IsOpen = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionSwitch -= OnSession;
        _capture.StateChanged -= Render;
        _capture.FrameShown -= OnFrameShown;
        _capture.Dispose();
        base.OnClosed(e);
    }
}
