// SPDX-License-Identifier: GPL-3.0-or-later
// The panel's keep-awake card and display brightness sliders: the Windows counterparts of upstream's
// KeepAwakeCard (MenuPanelView.swift) and UI/MenuPanel/BrightnessSection.swift. They update while the panel is open.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Notchly.App.Energy;
using Notchly.Core.Energy;
using Notchly.Core.Settings;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Notchly.App.Shell;

internal sealed class PanelEnergy(StackPanel keepAwakeHost, StackPanel brightnessHost, SettingsStore settings)
{
    private KeepAwakeService? _keepAwake;
    private BrightnessService? _brightness;
    private TextBlock? _status;
    private bool _dragging, _untilOpen;

    public void Show(KeepAwakeService? keepAwake, BrightnessService? brightness)
    {
        Hide();
        _keepAwake = keepAwake;
        _brightness = brightness;
        if (keepAwake is not null)
        {
            keepAwake.Changed += RebuildKeepAwake;
            keepAwake.Tick += UpdateStatus;
        }
        if (brightness is not null) brightness.Changed += OnBrightnessChanged;
        RebuildKeepAwake();
        RebuildBrightness();
    }

    public void Hide()
    {
        if (_keepAwake is not null) { _keepAwake.Changed -= RebuildKeepAwake; _keepAwake.Tick -= UpdateStatus; }
        if (_brightness is not null) _brightness.Changed -= OnBrightnessChanged;
        _keepAwake = null;
        _brightness = null;
        _untilOpen = false;
    }

    // MARK: Keep awake

    private string StatusText()
    {
        if (_keepAwake is not { } k) return "";
        if (!k.Active)
            return k.BatteryProtectionPercent() is { } percent ? $"Battery at {percent}%. Plug in or lower the battery limit to start." : "Off";
        var prefix = k.IsAutomatic ? "Automatic · " : "";
        if (k.Paused) return prefix + "Paused while locked";
        return k.RemainingSeconds is { } secs ? $"{prefix}{KeepAwakeSupport.ChipCountdown(secs)} left" : prefix + "Until you turn it off";
    }

    private void UpdateStatus()
    {
        if (_status is not null) _status.Text = StatusText();
    }

    private void RebuildKeepAwake()
    {
        keepAwakeHost.Children.Clear();
        if (_keepAwake is not { } service || !settings.GetBool(DefaultsKey.PanelShowKeepAwake)) return;
        keepAwakeHost.Children.Add(new TextBlock { Text = "Keep awake", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6) });
        var card = new Card { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
        var stack = new StackPanel();
        card.Content = stack;
        keepAwakeHost.Children.Add(card);

        var head = new Grid();
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Keep this PC awake", FontSize = 14 });
        _status = new TextBlock { Text = StatusText(), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        titles.Children.Add(_status);
        head.Children.Add(titles);
        var toggle = new ToggleSwitch { IsChecked = service.Active, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        toggle.Click += (_, _) => service.Toggle();
        head.Children.Add(toggle);
        stack.Children.Add(head);

        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var minutes in KeepAwakeSupport.ChipDurations)
        {
            var selected = service.Active && !_untilOpen && service.Minutes == minutes;
            var chip = new Button
            {
                Content = minutes == 0 ? "∞" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h",
                Margin = new Thickness(0, 0, 6, 6),
                Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary,
                ToolTip = minutes == 0 ? "Until you turn it off" : null,
            };
            var chosen = minutes;
            chip.Click += (_, _) => service.Activate(chosen);
            chips.Children.Add(chip);
        }
        var until = new Button { Content = "Until…", Margin = new Thickness(0, 0, 6, 6), Appearance = _untilOpen ? ControlAppearance.Primary : ControlAppearance.Secondary };
        until.Click += (_, _) => { _untilOpen = !_untilOpen; RebuildKeepAwake(); };
        chips.Children.Add(until);
        stack.Children.Add(chips);

        if (_untilOpen) stack.Children.Add(UntilRow(service));

        if (service.Active && service.End is not null)
        {
            var extend = new WrapPanel();
            foreach (var minutes in KeepAwakeSupport.ExtendChoices)
            {
                var more = new Button { Content = $"+{minutes}m", Margin = new Thickness(0, 0, 6, 0), Appearance = ControlAppearance.Transparent };
                var chosen = minutes;
                more.Click += (_, _) => service.Extend(chosen);
                extend.Children.Add(more);
            }
            stack.Children.Add(extend);
        }
    }

    private FrameworkElement UntilRow(KeepAwakeService service)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { PlaceholderText = "Time, like 18:30", Text = "" };
        Grid.SetColumn(box, 0);
        var go = new Button { Content = "Start", Margin = new Thickness(6, 0, 0, 0), Appearance = ControlAppearance.Primary };
        go.Click += (_, _) =>
        {
            if (!TimeSpan.TryParse(box.Text.Trim(), CultureInfo.CurrentCulture, out var time) && !TimeSpan.TryParse(box.Text.Trim(), CultureInfo.InvariantCulture, out time)) return;
            _untilOpen = false;
            service.ActivateUntil(KeepAwakeSupport.ResolveUntil(DateTime.Now, time.Hours, time.Minutes));
        };
        Grid.SetColumn(go, 1);
        row.Children.Add(box);
        row.Children.Add(go);
        return row;
    }

    // MARK: Brightness

    private void OnBrightnessChanged()
    {
        if (!_dragging) RebuildBrightness();
    }

    private void RebuildBrightness()
    {
        brightnessHost.Children.Clear();
        if (_brightness is not { } service || !settings.GetBool(DefaultsKey.PanelShowBrightness) || service.Displays.Count == 0) return;
        brightnessHost.Children.Add(new TextBlock { Text = "Displays", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6) });
        var card = new Card { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
        var stack = new StackPanel();
        card.Content = stack;
        brightnessHost.Children.Add(card);
        foreach (var display in service.Displays) stack.Children.Add(DisplayRow(service, display));
    }

    private FrameworkElement DisplayRow(BrightnessService service, BrightnessDisplay display)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var caption = display.Kind == DisplayRouteKind.Software ? $"{display.Name} · software dim" : display.Name;
        block.Children.Add(new TextBlock { Text = caption, FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        var icon = new TextBlock
        {
            Text = "", FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"], FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0),
        };
        row.Children.Add(icon);
        var percent = new TextBlock { Text = $"{BrightnessSupport.WholePercent(display.Level)}%", FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = display.Level, TickFrequency = 0.01, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        slider.PreviewMouseLeftButtonDown += (_, _) => _dragging = true;
        slider.PreviewMouseLeftButtonUp += (_, _) => _dragging = false;
        slider.LostMouseCapture += (_, _) => _dragging = false;
        slider.ValueChanged += (_, e) =>
        {
            if (!slider.IsMouseCaptureWithin && !slider.IsKeyboardFocusWithin) return;
            percent.Text = $"{BrightnessSupport.WholePercent(e.NewValue)}%";
            service.SetLevel(display.Id, Math.Round(e.NewValue * 100) / 100);
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(percent, 2);
        row.Children.Add(percent);
        block.Children.Add(row);
        return block;
    }
}
