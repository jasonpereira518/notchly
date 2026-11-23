// SPDX-License-Identifier: GPL-3.0-or-later
// The top strip as a Mac-style menu bar (Windows-only): the app in front in bold and its menus on the
// left, stopping before the notch, and the clock on the right. Classic menus drop down under the bar;
// UI Automation menus open the app's own menu; apps without menus get the usual File, Edit, View,
// Window and Help, whose items send the usual shortcut. The strip never takes focus from the app.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Shell;
using Notchly.App.Platform.Windowing;
using Notchly.Core.MenuBar;
using Notchly.Core.Notch;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Serilog;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Island.MenuBar;

public sealed class MenuBarController : IDisposable
{
    private const string TitleStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Foreground" Value="{DynamicResource MenuBarText}" />
          <Setter Property="FontSize" Value="13" />
          <Setter Property="Padding" Value="9,0" />
          <Setter Property="Focusable" Value="False" />
          <Setter Property="VerticalAlignment" Value="Stretch" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Back" CornerRadius="5" Background="Transparent"
                        Margin="0,2" Padding="{TemplateBinding Padding}">
                  <ContentPresenter VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Back" Property="Background" Value="{DynamicResource MenuBarHover}" /></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Back" Property="Background" Value="{DynamicResource MenuBarPressed}" /></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private static readonly Style TitleStyle = (Style)XamlReader.Parse(TitleStyleXaml);

    private readonly SettingsStore _settings;
    private readonly Grid _root;
    /// <summary>The bar's colour, animated between apps; black until the first app is read.</summary>
    private readonly SolidColorBrush _background = new(Colors.Black);
    private Rgb _tint = Rgb.Black;
    private readonly AppMenus _menus = new();
    private readonly StackPanel _titles;
    private readonly Button _clock;
    private readonly TextBlock _clockText;
    private readonly DispatcherTimer _clockTimer;
    private ForegroundApp? _app;
    private IReadOnlyList<MenuTitle> _bar = [];
    private int _generation;
    private double _available = double.PositiveInfinity;
    private ContextMenu? _open;

    public MenuBarController(AppBarStrip strip, SettingsStore settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _titles = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(MenuBarLayout.LeftInset - 4, 0, 0, 0), ClipToBounds = true,
        };
        _clockText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _clock = new Button { Style = TitleStyle, Content = _clockText, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
        _clock.ToolTip = "Notifications and calendar";
        // Windows 11's notification centre with the calendar (Win+N), like the Mac's Notification Center.
        _clock.Click += async (_, _) => await KeySender.SendAsync(IntPtr.Zero, new Shortcut('N', ShortcutModifiers.Win));
        _root = new Grid { Background = _background, Children = { _titles, _clock } };
        ApplyText(Rgb.Black);
        strip.Content = _root;
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _clockTimer.Tick += (_, _) => TickClock();
        OnForeground(GetForegroundWindow());
    }

    /// <summary>The colour along the top of the app in front; the text follows so it stays readable.</summary>
    public void SetTint(Rgb tint)
    {
        if (tint == _tint) return;
        _tint = tint;
        var target = Color.FromRgb(tint.R, tint.G, tint.B);
        _background.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.HoldEnd });
        ApplyText(tint);
    }

    private void ApplyText(Rgb bar)
    {
        var text = MenuBarTint.Text(bar);
        var color = Color.FromRgb(text.R, text.G, text.B);
        _root.Resources["MenuBarText"] = Frozen(color);
        _root.Resources["MenuBarHover"] = Frozen(Color.FromArgb(0x33, color.R, color.G, color.B));
        _root.Resources["MenuBarPressed"] = Frozen(Color.FromArgb(0x4D, color.R, color.G, color.B));
        _clockText.Foreground = (Brush)_root.Resources["MenuBarText"];
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _open?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _menus.Dispose();
    }

    /// <summary>Applies the menu and clock switches and fits the titles beside the island.</summary>
    public void Apply(DisplayInfo display, NotchGeometry geometry, double offset)
    {
        _available = MenuBarLayout.Available(display.LocalDips.Width, offset, geometry.CameraWidth);
        _titles.MaxWidth = _available + 4;
        _titles.Visibility = _settings.GetBool(DefaultsKey.NotchStripMenus) ? Visibility.Visible : Visibility.Collapsed;
        var clock = _settings.GetBool(DefaultsKey.NotchStripClock);
        _clock.Visibility = clock ? Visibility.Visible : Visibility.Collapsed;
        if (clock) TickClock(); else _clockTimer.Stop();
        Fit();
    }

    private void TickClock()
    {
        var now = DateTime.Now;
        _clockText.Text = StripClock.Format(now, CultureInfo.CurrentCulture);
        _clock.ToolTip = now.ToString("D", CultureInfo.CurrentCulture);
        _clockTimer.Stop();
        _clockTimer.Interval = StripClock.UntilNextMinute(now);
        _clockTimer.Start();
    }

    // MARK: The app in front

    public void OnForeground(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        var app = ForegroundApps.Describe(window);
        if (app is null) return;
        switch (ForegroundRules.Classify(app.ClassName, app.ProcessName))
        {
            case ForegroundKind.KeepPrevious:
                return;
            case ForegroundKind.Desktop:
                _app = null;
                _generation++;
                Show([new MenuTitle("Desktop", MenuSource.Standard) { Bold = true }]);
                return;
        }
        if (_app is { } current && current.Window == app.Window && current.Name == app.Name) return;
        _app = app;
        var generation = ++_generation;
        Show(StandardMenus.Compose(app.Name, app.Elevated, app.Maximized, null));
        LoadOwnMenus(app, generation);
    }

    /// <summary>The standard menus show at once; the app's own replace them when found.</summary>
    private async void LoadOwnMenus(ForegroundApp app, int generation)
    {
        var own = AppMenus.NativeTitles(app.Window);
        if (own is null && ForegroundRules.UsesAutomation(app.ClassName, app.ProcessName))
            own = await _menus.AutomationTitlesAsync(app.Window);
        if (own is null || generation != _generation) return;
        Show(StandardMenus.Compose(app.Name, app.Elevated, app.Maximized, own));
    }

    private void Show(IReadOnlyList<MenuTitle> bar)
    {
        _bar = bar;
        _titles.Children.Clear();
        foreach (var title in bar)
        {
            var text = new TextBlock
            {
                Text = title.Title, FontWeight = title.Bold ? FontWeights.Bold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Button { Style = TitleStyle, Content = text, Tag = title };
            button.Click += (_, _) => OpenTitle(title, button);
            _titles.Children.Add(button);
        }
        Fit();
    }

    /// <summary>Titles that would reach the notch are hidden, as on a Mac; the app's name always shows.</summary>
    private void Fit()
    {
        var widths = new List<double>();
        foreach (Button button in _titles.Children)
        {
            button.Visibility = Visibility.Visible;
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths.Add(button.DesiredSize.Width);
        }
        var shown = MenuBarLayout.Fit(widths, _available, 0);
        for (var i = 0; i < _titles.Children.Count; i++)
            _titles.Children[i].Visibility = i < shown ? Visibility.Visible : Visibility.Collapsed;
        if (_titles.Children.Count > 0 && _titles.Children[0] is Button first)
            first.MaxWidth = Math.Max(40, _available);
    }

    // MARK: Menus

    private void OpenTitle(MenuTitle title, Button anchor)
    {
        if (_app is not { } app) return;
        switch (title.Source)
        {
            case MenuSource.Automation:
                _menus.Expand(app.Window, title.Index);
                return;
            case MenuSource.Native:
                Drop(AppMenus.NativeItems(app.Window, title.Index), anchor, app);
                return;
            default:
                // The Window menu's Maximize/Restore follows the window as it is now.
                var items = title.Title == "Window" && title.Source == MenuSource.Standard
                    ? StandardMenus.WindowMenu(IsZoomed(app.Window), app.Elevated).Items
                    : title.Items;
                Drop(items, anchor, app);
                return;
        }
    }

    private void Drop(IReadOnlyList<MenuNode> items, FrameworkElement anchor, ForegroundApp app)
    {
        if (items.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, HorizontalOffset = 0 };
        foreach (var node in items) menu.Items.Add(Entry(node, app));
        menu.Closed += (_, _) => { if (_open == menu) _open = null; };
        _open = menu;
        menu.IsOpen = true;
    }

    private object Entry(MenuNode node, ForegroundApp app)
    {
        if (node.Kind == MenuNodeKind.Separator) return new Separator();
        var item = new MenuItem
        {
            Header = node.Title, InputGestureText = node.Shortcut ?? "", IsEnabled = node.Enabled, IsChecked = node.Checked,
            ToolTip = node.Note,
        };
        ToolTipService.SetShowOnDisabled(item, node.Note is not null);
        if (node.Kind == MenuNodeKind.Submenu)
            foreach (var child in node.Children) item.Items.Add(Entry(child, app));
        else if (node.Command is { } command)
            item.Click += (_, _) => Run(command, app);
        return item;
    }

    private async void Run(MenuCommand command, ForegroundApp app)
    {
        // Let the menu close and the app's window take its keys back first.
        await Task.Delay(60);
        try
        {
            switch (command)
            {
                case MenuCommand.SendKeys keys: await KeySender.SendAsync(app.Window, keys.Chord); break;
                case MenuCommand.Window window: WindowActions.Perform(window.Action, app); break;
                case MenuCommand.NativeItem native: AppMenus.Choose(app.Window, native.Id); break;
            }
        }
        catch (Exception e) { Log.Warning(e, "Menu command failed"); }
    }
}
