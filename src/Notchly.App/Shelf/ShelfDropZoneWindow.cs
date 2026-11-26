// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream UI/Shelf/ShelfDropZoneView.swift: the docked drop zone. While something is being dragged, a small pill sits at the
// corner above the tray (or under the top edge when the island is not there); dropping on it shelves the content, and holding the pointer
// over it for a moment opens the shelf. It never takes the focus. Placed in physical pixels like the card.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Notchly.App.Island.Files;
using Notchly.App.Platform;
using Notchly.Core.Shelf;
using Notchly.Core.Windowing;

namespace Notchly.App.ShelfCard;

internal sealed class ShelfDropZoneWindow : Window
{
    private static readonly SolidColorBrush Back = Frozen(Color.FromArgb(0xEE, 0x1C, 0x1C, 0x1E));
    private static readonly SolidColorBrush Idle = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Accent = Frozen(Color.FromRgb(0x4C, 0x8D, 0xFF));

    private readonly ShelfService _shelf;
    private readonly Border _pill;
    private readonly TextBlock _label;
    private readonly DispatcherTimer _caught;
    private IntPtr _handle;

    public bool IsShown { get; private set; }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public ShelfDropZoneWindow(ShelfService shelf)
    {
        _shelf = shelf;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = ShelfSummonSupport.DockWidth;
        Height = ShelfSummonSupport.DockHeight;
        Left = -32000;
        Top = -32000;
        AllowDrop = true;
        Focusable = false;

        var icon = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)(Application.Current.TryFindResource("IconFont") ?? new FontFamily("Segoe Fluent Icons")),
            FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        _label = new TextBlock { Text = "Drop to shelve", FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(_label);
        _pill = new Border
        {
            Background = Back, BorderBrush = Idle, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(ShelfSummonSupport.DockHeight / 2), Child = row,
        };
        Content = _pill;

        DragEnter += OnDrag;
        DragOver += OnDrag;
        DragLeave += (_, _) => _pill.BorderBrush = Idle;
        Drop += (_, e) =>
        {
            e.Handled = true;
            _pill.BorderBrush = Idle;
            if (_shelf.CanAccept(e.Data) && _shelf.AddDrop(e.Data) is not null) Caught();
        };
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_handle, 0x80 | 0x08000000, 0x40000);
        };
        _caught = new DispatcherTimer(TimeSpan.FromSeconds(ShelfSummonSupport.CaughtTick), DispatcherPriority.Background, (_, _) =>
        {
            _caught!.Stop();
            _label.Text = "Drop to shelve";
        }, Dispatcher) { IsEnabled = false };
    }

    private void OnDrag(object sender, DragEventArgs e)
    {
        e.Effects = _shelf.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        _pill.BorderBrush = e.Effects == DragDropEffects.None ? Idle : Accent;
        e.Handled = true;
    }

    /// <summary>A brief "caught" tick after a drop.</summary>
    private void Caught()
    {
        _label.Text = "Shelved";
        _caught.Stop();
        _caught.Start();
    }

    /// <summary>Shows the pill over a frame in physical pixels.</summary>
    public void ShowAt(WindowRect frame)
    {
        if (_handle == IntPtr.Zero) _handle = new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible) Show();
        SetWindowPos(_handle, new IntPtr(-1), (int)frame.X, (int)frame.Y, (int)frame.Width, (int)frame.Height, 0x0010 | 0x0040);
        IsShown = true;
    }

    public void HidePill()
    {
        if (!IsShown) return;
        IsShown = false;
        Hide();
        if (_handle != IntPtr.Zero) SetWindowPos(_handle, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
