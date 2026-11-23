// SPDX-License-Identifier: GPL-3.0-or-later
// The island's window: the Windows counterpart of upstream's NotchPanel (NotchWindowHost.swift).
// A layered, topmost, non-activating tool window sized to the largest animation envelope. Its
// fully transparent pixels let clicks through, so only the black shape takes the pointer.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Notchly.App.Platform.Windowing;
using Notchly.Core.Notch;
using static Notchly.App.Platform.Native;

namespace Notchly.App.Island;

public partial class NotchWindow : Window
{
    private IntPtr _handle;
    private bool _noActivate = true;

    /// <summary>A horizontal wheel/touchpad delta over the island (WM_MOUSEHWHEEL).</summary>
    public event Action<int>? HorizontalWheel;

    public NotchWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            AddExStyle(_handle, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, WS_EX_APPWINDOW);
            HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
        };
        KeepTransparent();
        // Bars of the closed music strip, as thin and as close as upstream draws them
        // (compactMusicBarWidth, with 0.85 of a bar between two).
        const double barWidth = NotchLayout.CompactMusicBarWidth;
        for (var i = 0; i < NotchLayout.CompactMusicBarCount; i++)
        {
            StripBars.Children.Add(new Border
            {
                Width = barWidth, Height = 4, CornerRadius = new CornerRadius(barWidth / 2), Background = Brushes.White,
                Margin = new Thickness(0, 0, i == NotchLayout.CompactMusicBarCount - 1 ? 0 : barWidth * 0.85, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    public IntPtr Handle => _handle;

    private Brush? _edge;
    private Brush? _warning;

    /// <summary>The island's light edge: `alpha` of `color`, none at zero.</summary>
    public void SetEdge(Color color, double alpha)
    {
        var argb = Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
        if (_edge is SolidColorBrush current ? current.Color == argb : argb.A == 0) return;
        _edge = argb.A == 0 ? null : Frozen(argb);
        ApplyStroke();
    }

    /// <summary>A low battery's tint around the island, over its usual edge; null for none.</summary>
    public void SetWarning(Color? color)
    {
        if (_warning is SolidColorBrush current ? current.Color == color : color is null) return;
        _warning = color is { } c ? Frozen(c) : null;
        ApplyStroke();
    }

    private void ApplyStroke() => Shape.Stroke = _warning ?? _edge;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// WPF-UI restyles every window in the app when the theme is applied (opening Settings
    /// triggers it): it sets the themed window background and a Mica backdrop, which painted this
    /// window's whole envelope as an opaque grey rectangle that also swallowed clicks. The island
    /// must stay fully transparent, so its Background is coerced to Transparent whoever sets it
    /// (no change handler, so nothing can loop), and any backdrop is removed when the theme moves.
    /// </summary>
    static NotchWindow()
    {
        BackgroundProperty.OverrideMetadata(typeof(NotchWindow),
            new FrameworkPropertyMetadata(Brushes.Transparent, null, (_, _) => Brushes.Transparent));
    }

    private void KeepTransparent()
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnThemeChanged;
        SourceInitialized += (_, _) => RemoveBackdrop();
    }

    private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, Color accent) =>
        Dispatcher.BeginInvoke(RemoveBackdrop);

    private bool _removingBackdrop;

    private void RemoveBackdrop()
    {
        if (_removingBackdrop || _handle == IntPtr.Zero) return;
        _removingBackdrop = true;
        try
        {
            Wpf.Ui.Controls.WindowBackdrop.RemoveBackdrop(this);
            // The layered window's own surface must stay clear, whatever the backdrop code left.
            if (HwndSource.FromHwnd(_handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        }
        finally { _removingBackdrop = false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == WM_MOUSEHWHEEL)
        {
            HorizontalWheel?.Invoke((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            handled = true;
        }
        return IntPtr.Zero;
    }

    // MARK: Placement

    /// <summary>Sizes the window to the envelope and centres it at the top of `display`, in pixels.</summary>
    public void Place(DisplayInfo display, SizeD envelope, double cameraOffset)
    {
        var width = (int)Math.Ceiling(envelope.Width * display.Scale);
        var height = (int)Math.Ceiling(envelope.Height * display.Scale);
        var left = display.Bounds.Left + (int)Math.Round((display.Bounds.Width - width) / 2.0 + cameraOffset * display.Scale);
        var top = display.Bounds.Top;
        if (_handle == IntPtr.Zero) return;
        SetWindowPos(_handle, HWND_TOPMOST, left, top, width, height, SWP_NOACTIVATE);
        Root.Width = envelope.Width;
        Root.Height = envelope.Height;
    }

    public void ReassertTopmost()
    {
        if (_handle != IntPtr.Zero)
            SetWindowPos(_handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void SetShownInCaptures(bool shown)
    {
        if (_handle != IntPtr.Zero) SetWindowDisplayAffinity(_handle, shown ? WDA_NONE : WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Opened by a click, the island takes keyboard focus so a click elsewhere closes it.</summary>
    public void SetActivatable(bool activatable)
    {
        if (_handle == IntPtr.Zero || activatable == !_noActivate) return;
        _noActivate = !activatable;
        if (activatable) AddExStyle(_handle, 0, WS_EX_NOACTIVATE);
        else AddExStyle(_handle, WS_EX_NOACTIVATE);
    }

    public void TakeFocus()
    {
        SetActivatable(true);
        SetForegroundWindow(_handle);
        Activate();
    }

    // MARK: Frame

    /// <summary>Draws the island at `size`, `shift` points right of centre: outline, clip and wings.</summary>
    public void ApplyFrame(SizeD size, double shift, NotchGeometry geometry, double cameraWidth)
    {
        var envelopeWidth = Root.Width is double w && double.IsFinite(w) ? w : ActualWidth;
        Surface.Width = Math.Max(0, size.Width);
        Surface.Height = Math.Max(0, size.Height);
        Canvas.SetLeft(Surface, (envelopeWidth - size.Width) / 2 + shift);
        Canvas.SetTop(Surface, geometry.FloatingDrop);

        var outline = ToGeometry(NotchLayout.IslandPath(size, geometry.FloatingGap));
        Shape.Data = outline;
        ContentLayer.Clip = outline;
        // Only the stroke's inner half shows. A hanging island's top is the screen's edge, where an
        // edge would only draw a line along the top: it is left out there.
        Shape.Clip = geometry.Floats ? outline
            : new CombinedGeometry(GeometryCombineMode.Intersect, outline, new RectangleGeometry(new Rect(0, 1.5, size.Width, size.Height)));

        // The strip keeps the camera's width in the middle; wings take what is left on each side.
        var wing = Math.Max(0, (size.Width - cameraWidth) / 2);
        LeftWingColumn.Width = new GridLength(wing);
        RightWingColumn.Width = new GridLength(wing);
        Strip.Margin = new Thickness(0, geometry.FloatingGap ?? 0, 0, 0);
        Strip.Height = geometry.StripBodyHeight;
    }

    public static Geometry ToGeometry(NotchPath path)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var segment in path.Segments)
            {
                switch (segment)
                {
                    case NotchPath.MoveTo m:
                        context.BeginFigure(new Point(m.Point.X, m.Point.Y), isFilled: true, isClosed: true);
                        break;
                    case NotchPath.LineTo l:
                        context.LineTo(new Point(l.Point.X, l.Point.Y), isStroked: true, isSmoothJoin: true);
                        break;
                    case NotchPath.CubicTo c:
                        context.BezierTo(new Point(c.Control1.X, c.Control1.Y), new Point(c.Control2.X, c.Control2.Y),
                                         new Point(c.Point.X, c.Point.Y), isStroked: true, isSmoothJoin: true);
                        break;
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    // MARK: Layers

    /// <summary>Content departs quickly and arrives once the island has mostly reached its size.</summary>
    /// <summary>The open page keeps its final size while the island springs around it.</summary>
    public void LayoutOpenLayer(SizeD expanded, NotchGeometry geometry)
    {
        OpenLayer.Width = Math.Max(0, expanded.Width - NotchLayout.HorizontalInset * 2);
        OpenLayer.Height = Math.Max(0, expanded.Height - geometry.HeaderTopInset - NotchLayout.BottomInset);
        OpenLayer.Margin = new Thickness(0, geometry.HeaderTopInset, 0, 0);
    }

    /// <summary>The height the visible page needs at the island's content width.</summary>
    public double MeasurePage(FrameworkElement page, double width)
    {
        page.Measure(new Size(width, double.PositiveInfinity));
        return page.DesiredSize.Height;
    }

    public void ShowOpenLayer(bool open)
    {
        if (open)
        {
            OpenLayer.Visibility = Visibility.Visible;
            Fade(OpenLayer, 1, 0.22, delay: 0.12);
            Fade(Strip, 0, 0.1);
        }
        else
        {
            Fade(OpenLayer, 0, 0.1, then: () => { if (OpenLayer.Opacity == 0) OpenLayer.Visibility = Visibility.Collapsed; });
            Fade(Strip, 1, 0.2, delay: 0.14);
        }
    }

    public static void Fade(UIElement element, double to, double seconds, double delay = 0, Action? then = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (then is not null) animation.Completed += (_, _) => then();
        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
