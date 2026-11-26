// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream UI/RadialMenu/RadialMenuView.swift: the wheel. A 400 pixel transparent window centred on the pointer holds a glass disc
// of 300, slices as chips on a ring of 112, a hairline between slices, a wedge on the highlighted one and a hub in the middle that names
// it. The service owns the logic; this draws a list of items with a highlight and reports clicks and keys.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Notchly.App.Platform;
using Notchly.Core.Radial;

namespace Notchly.App.Radial;

public static class RadialColors
{
    public static Color Of(RadialMenuColor color) => color switch
    {
        RadialMenuColor.Blue => Color.FromRgb(0x0A, 0x84, 0xFF), RadialMenuColor.Purple => Color.FromRgb(0xBF, 0x5A, 0xF2),
        RadialMenuColor.Pink => Color.FromRgb(0xFF, 0x37, 0x5F), RadialMenuColor.Red => Color.FromRgb(0xFF, 0x45, 0x3A),
        RadialMenuColor.Orange => Color.FromRgb(0xFF, 0x9F, 0x0A), RadialMenuColor.Yellow => Color.FromRgb(0xFF, 0xD6, 0x0A),
        RadialMenuColor.Green => Color.FromRgb(0x30, 0xD1, 0x58), RadialMenuColor.Mint => Color.FromRgb(0x63, 0xE6, 0xE2),
        RadialMenuColor.Cyan => Color.FromRgb(0x64, 0xD2, 0xFF), RadialMenuColor.Indigo => Color.FromRgb(0x5E, 0x5C, 0xE6),
        RadialMenuColor.Graphite => Color.FromRgb(0x8E, 0x8E, 0x93), _ => Color.FromRgb(0x4C, 0x8D, 0xFF),
    };

    public static SolidColorBrush Brush(RadialMenuColor color, byte alpha = 255)
    {
        var c = Of(color);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>What a slice is called when its name was left empty (upstream's automatic title).</summary>
public static class RadialLabels
{
    public static string Of(RadialMenuItem item)
    {
        if (item.Name.Length > 0) return item.Name;
        switch (item.Kind)
        {
            case RadialMenuKind.App:
            case RadialMenuKind.File:
                return System.IO.Path.GetFileNameWithoutExtension(item.Payload.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : item.Payload;
            case RadialMenuKind.Url:
                return Uri.TryCreate(item.Payload, UriKind.Absolute, out var uri) ? uri.Host : item.Payload;
            case RadialMenuKind.Shortcut:
                return Notchly.Core.Shortcuts.Shortcut.Parse(item.Payload)?.DisplayText ?? "Shortcut";
            case RadialMenuKind.Submenu:
                return "Submenu";
            default:
                return Spaced(item.Payload);
        }
    }

    private static string Spaced(string id)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(id, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}

internal sealed class RadialMenuWindow : Window
{
    private const double Half = RadialMenuLayout.PanelSize / 2, WedgeOuter = RadialMenuLayout.WheelDiameter / 2 - 4;

    private readonly Canvas _canvas = new() { Width = RadialMenuLayout.PanelSize, Height = RadialMenuLayout.PanelSize, Background = Brushes.Transparent };
    private readonly TextBlock _hubText = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = 64 };
    private RadialMenuColor _color = RadialMenuColor.Accent;
    private IReadOnlyList<RadialMenuItem> _items = [];
    private string _hubDefault = "";
    private readonly List<Border> _chips = [];
    private System.Windows.Shapes.Path? _wedge;
    private int? _highlight;

    public RadialMenuWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Width = RadialMenuLayout.PanelSize;
        Height = RadialMenuLayout.PanelSize;
        Left = -32000;
        Top = -32000;
        Title = "Radial menu";
        Content = _canvas;
        Closed += (_, _) => { };
    }

    public event Action<int>? PointerReleased;
    public event Action<Key>? KeyPressed;
    public event Action? Dismissed;

    /// <summary>Handle for placing the window in pixels.</summary>
    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>The pixel box of the window (its centre is the wheel's centre).</summary>
    public void PlaceCentredAt(int centerX, int centerY, double scale)
    {
        var side = (int)Math.Round(RadialMenuLayout.PanelSize * scale);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, centerX - side / 2, centerY - side / 2, side, side, 0x0010);
    }

    public void Present(IReadOnlyList<RadialMenuItem> items, RadialMenuColor color, string hubDefault)
    {
        _color = color;
        _items = items;
        _hubDefault = hubDefault;
        _highlight = null;
        Rebuild();
    }

    public void SetItems(IReadOnlyList<RadialMenuItem> items, string hubDefault)
    {
        _items = items;
        _hubDefault = hubDefault;
        _highlight = null;
        Rebuild();
    }

    public void SetHighlight(int? index)
    {
        if (_highlight == index) return;
        _highlight = index;
        Rebuild();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        KeyPressed?.Invoke(e.Key == Key.System ? e.SystemKey : e.Key);
        e.Handled = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Dismissed?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        PointerReleased?.Invoke(0);
    }

    // MARK: Drawing

    private static Point OnRing(double radius, double angle) => new(Half + radius * Math.Sin(angle), Half - radius * Math.Cos(angle));

    private void Rebuild()
    {
        _canvas.Children.Clear();
        _chips.Clear();
        var disc = new Ellipse
        {
            Width = RadialMenuLayout.WheelDiameter, Height = RadialMenuLayout.WheelDiameter,
            Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)), Stroke = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), StrokeThickness = 1,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.5, ShadowDepth = 0 },
        };
        Canvas.SetLeft(disc, Half - RadialMenuLayout.WheelDiameter / 2);
        Canvas.SetTop(disc, Half - RadialMenuLayout.WheelDiameter / 2);
        _canvas.Children.Add(disc);

        var count = _items.Count;
        if (count > 1)
            for (var i = 0; i < count; i++)
            {
                var angle = 2 * Math.PI * (i + 0.5) / count;
                var a = OnRing(RadialMenuLayout.DeadZoneRadius, angle);
                var b = OnRing(WedgeOuter, angle);
                _canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)), StrokeThickness = 1 });
            }
        if (_highlight is { } hi && count > 0)
        {
            _wedge = Wedge(hi, count);
            _canvas.Children.Add(_wedge);
        }
        for (var i = 0; i < count; i++) _canvas.Children.Add(Chip(i, count));

        var hub = new Ellipse { Width = RadialMenuLayout.HubDiameter, Height = RadialMenuLayout.HubDiameter, Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x34)), Stroke = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)) };
        Canvas.SetLeft(hub, Half - RadialMenuLayout.HubDiameter / 2);
        Canvas.SetTop(hub, Half - RadialMenuLayout.HubDiameter / 2);
        _canvas.Children.Add(hub);
        _hubText.Text = _highlight is { } h && h < count ? RadialLabels.Of(_items[h]) : _hubDefault;
        _hubText.Measure(new Size(64, double.PositiveInfinity));
        Canvas.SetLeft(_hubText, Half - 32);
        Canvas.SetTop(_hubText, Half - Math.Min(_hubText.DesiredSize.Height, 52) / 2);
        _canvas.Children.Add(_hubText);
    }

    private System.Windows.Shapes.Path Wedge(int index, int count)
    {
        var step = 2 * Math.PI / count;
        var center = step * index;
        var from = center - step / 2;
        var to = center + step / 2;
        var small = step <= Math.PI;
        var figure = new PathFigure { StartPoint = OnRing(RadialMenuLayout.DeadZoneRadius, from), IsClosed = true };
        figure.Segments.Add(new LineSegment(OnRing(WedgeOuter, from), true));
        figure.Segments.Add(new ArcSegment(OnRing(WedgeOuter, to), new Size(WedgeOuter, WedgeOuter), 0, !small, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(OnRing(RadialMenuLayout.DeadZoneRadius, to), true));
        figure.Segments.Add(new ArcSegment(OnRing(RadialMenuLayout.DeadZoneRadius, from), new Size(RadialMenuLayout.DeadZoneRadius, RadialMenuLayout.DeadZoneRadius), 0, !small, SweepDirection.Counterclockwise, true));
        return new System.Windows.Shapes.Path { Data = new PathGeometry([figure]), Fill = RadialColors.Brush(_color, 0x66), IsHitTestVisible = false };
    }

    private Border Chip(int index, int count)
    {
        var item = _items[index];
        var lit = _highlight == index;
        var (dx, dyUp) = RadialMenuGeometry.UnitPosition(index, count);
        var size = RadialMenuLayout.ChipSize;
        var chip = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), IsHitTestVisible = false,
            Background = lit ? RadialColors.Brush(_color) : new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(lit ? 1.14 : 1, lit ? 1.14 : 1),
            Child = Icon(item),
        };
        Canvas.SetLeft(chip, Half + dx * RadialMenuLayout.RingRadius - size / 2);
        Canvas.SetTop(chip, Half - dyUp * RadialMenuLayout.RingRadius - size / 2);
        _chips.Add(chip);
        return chip;
    }

    private static UIElement Icon(RadialMenuItem item)
    {
        if (item.CustomIconData is { Length: > 0 } data)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(data);
                bitmap.EndInit();
                bitmap.Freeze();
                return new Image { Source = bitmap, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException) { }
        }
        return new TextBlock
        {
            Text = RadialMenuGlyphs.EffectiveGlyph(item), FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 22, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
