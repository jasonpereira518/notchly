// SPDX-License-Identifier: GPL-3.0-or-later
// The panel's volume mixer: the Windows counterpart of upstream UI/MenuPanel/MixerSection.swift. The output with
// its level, the microphone with its level, then a row per app (icon, name, level, percent, reset, mute, and a
// menu for pinning, order, output and hiding). Rows update while the panel is open.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Notchly.App.Platform.Audio;
using Notchly.App.Platform.Shell;
using Notchly.App.Sound;
using Notchly.Core.Sound;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Notchly.App.Shell;

internal sealed class PanelMixer
{
    private readonly StackPanel _host;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private MixerService? _service;
    private IDisposable? _observing;
    private bool _dragging;

    public PanelMixer(StackPanel host) => _host = host;

    public void Show(MixerService? service)
    {
        Hide();
        _service = service;
        if (service is null) return;
        service.Changed += OnChanged;
        _observing = service.Observe();
        Rebuild();
    }

    public void Hide()
    {
        if (_service is not null) _service.Changed -= OnChanged;
        _observing?.Dispose();
        _observing = null;
        _service = null;
    }

    private void OnChanged()
    {
        if (!_dragging) Rebuild();
    }

    private static FontFamily IconFont => (FontFamily)Application.Current.Resources["IconFont"];

    private void Rebuild()
    {
        _host.Children.Clear();
        if (_service is not { } service) return;
        _host.Children.Add(new TextBlock { Text = "Volume mixer", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 6) });
        var card = new Card { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
        var stack = new StackPanel();
        card.Content = stack;
        _host.Children.Add(card);

        stack.Children.Add(DeviceBlock("Output", "", service.Outputs, service.SetSystemOutput, service.OutputVolume, service.OutputMuted,
            v => service.SetOutputVolume(v), () => service.SetOutputMuted(!service.OutputMuted)));
        if (service.InputVolume is { } input)
            stack.Children.Add(DeviceBlock("Microphone", "", service.Inputs, service.SetInput, input, service.InputMuted,
                v => service.SetInputVolume(v), () => service.SetInputMuted(!service.InputMuted)));
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), Margin = new Thickness(0, 6, 0, 6) });
        if (service.Rows.Count == 0)
            stack.Children.Add(new TextBlock { Text = "Apps that use audio show up here", FontSize = 12, Opacity = 0.7 });
        foreach (var row in service.Rows) stack.Children.Add(AppRow(service, row));
    }

    private FrameworkElement DeviceBlock(string title, string glyph, IReadOnlyList<AudioEndpoint> devices, Func<string, bool> choose, double level,
                                         bool muted, Action<double> setLevel, Action toggleMute)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var device in devices) combo.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id, IsSelected = device.IsDefault });
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string id } && !devices.First(d => d.Id == id).IsDefault) choose(id); };
        block.Children.Add(new TextBlock { Text = title, FontSize = 11, Opacity = 0.7 });
        block.Children.Add(combo);
        block.Children.Add(LevelRow(glyph, level, muted, setLevel, toggleMute, reset: null));
        return block;
    }

    private Grid LevelRow(string glyph, double level, bool muted, Action<double> set, Action toggleMute, Action? reset)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mute = new Button
        {
            Appearance = ControlAppearance.Transparent, Padding = new Thickness(6),
            Content = new TextBlock { Text = muted || level <= MixerSupport.MutedThreshold ? "" : glyph, FontFamily = IconFont, FontSize = 14 },
            ToolTip = muted ? "Unmute" : "Mute",
        };
        mute.Click += (_, _) => toggleMute();
        row.Children.Add(mute);
        var percent = new TextBlock { Text = $"{MixerSupport.DisplayPercent(level)}%", FontSize = 11, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = muted ? 0.5 : 1 };
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = Math.Clamp(level, 0, 1), TickFrequency = 0.01, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        slider.PreviewMouseLeftButtonDown += (_, _) => _dragging = true;
        slider.PreviewMouseLeftButtonUp += (_, _) => _dragging = false;
        slider.LostMouseCapture += (_, _) => _dragging = false;
        slider.ValueChanged += (_, e) =>
        {
            if (!slider.IsMouseCaptureWithin && !slider.IsKeyboardFocusWithin) return;
            percent.Text = $"{MixerSupport.DisplayPercent(e.NewValue)}%";
            set(Math.Round(e.NewValue * 100) / 100);
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(percent, 2);
        row.Children.Add(percent);
        if (reset is not null)
        {
            var button = new Button { Appearance = ControlAppearance.Transparent, Padding = new Thickness(6), ToolTip = "Reset to 100%",
                Content = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12 }, Visibility = MixerSupport.IsUnity(level) ? Visibility.Hidden : Visibility.Visible };
            button.Click += (_, _) => reset();
            Grid.SetColumn(button, 3);
            row.Children.Add(button);
        }
        return row;
    }

    private FrameworkElement AppRow(MixerService service, MixerRow row)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (row.IsSystemSounds) head.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        else
        {
            head.Children.Add(icon);
            if (row.ExePath is { } path) LoadIcon(path, icon);
        }
        var name = new TextBlock { Text = row.Name + (row.Pinned ? "  📌" : ""), FontSize = 12, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var menu = new Button { Appearance = ControlAppearance.Transparent, Padding = new Thickness(6), Content = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12 }, ToolTip = "More" };
        menu.Click += (_, _) => ShowMenu(menu, service, row);
        Grid.SetColumn(menu, 2);
        head.Children.Add(menu);
        block.Children.Add(head);
        if (row.BypassesControl)
            block.Children.Add(new TextBlock { Text = "Keeps its own volume", FontSize = 11, Opacity = 0.6, Margin = new Thickness(28, 0, 0, 0) });
        else
        {
            block.Children.Add(LevelRow("", Math.Min(row.Volume, MixerSupport.PlayableMaximum), row.Muted, v => service.SetVolume(row.Id, v),
                () => service.ToggleMute(row.Id), () => service.ResetVolume(row.Id)));
            if (row.OutputUnavailable)
                block.Children.Add(new TextBlock { Text = "Using default until this device returns.", FontSize = 11, Opacity = 0.6, Margin = new Thickness(28, 0, 0, 0) });
        }
        return block;
    }

    private async void LoadIcon(string path, Image image)
    {
        if (!_icons.TryGetValue(path, out var icon)) _icons[path] = icon = await ShellThumbnails.GetAsync(path, 40);
        image.Source = icon;
    }

    private static void ShowMenu(FrameworkElement anchor, MixerService service, MixerRow row)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (row.PersistenceId is not null && !row.IsSystemSounds)
        {
            var pin = new MenuItem { Header = row.Pinned ? "Unpin" : "Pin to Top" };
            pin.Click += (_, _) => service.TogglePin(row.Id);
            menu.Items.Add(pin);
            var up = new MenuItem { Header = "Move Up" };
            up.Click += (_, _) => service.Move(row.Id, -1);
            menu.Items.Add(up);
            var down = new MenuItem { Header = "Move Down" };
            down.Click += (_, _) => service.Move(row.Id, 1);
            menu.Items.Add(down);
        }
        if (service.PerAppOutputAvailable && !row.IsSystemSounds && row.PersistenceId is not null && !row.BypassesControl)
        {
            var output = new MenuItem { Header = "Output" };
            var system = new MenuItem { Header = "Default", IsChecked = row.SelectedOutputId is null };
            system.Click += (_, _) => service.SetOutput(row.Id, null);
            output.Items.Add(system);
            foreach (var device in service.Outputs)
            {
                var item = new MenuItem { Header = device.Name + (device.IsDefault ? " (current)" : ""), IsChecked = row.SelectedOutputId == device.Id };
                item.Click += (_, _) => service.SetOutput(row.Id, device.Id);
                output.Items.Add(item);
            }
            if (MixerSupport.NeedsUnavailableOutputRow(row.SelectedOutputId, row.OutputUnavailable, service.Outputs.Select(o => o.Id).ToList()))
                output.Items.Add(new MenuItem { Header = "Output unavailable", IsChecked = true, IsEnabled = false });
            menu.Items.Add(output);
        }
        if (!row.IsSystemSounds && row.PersistenceId is not null)
        {
            var hide = new MenuItem { Header = "Hide from the list" };
            hide.Click += (_, _) => service.Hide(row.Id);
            menu.Items.Add(hide);
        }
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }
}
