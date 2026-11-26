// SPDX-License-Identifier: GPL-3.0-or-later
// Port of the island's Tools page (upstream UI/Notch's QuickLauncherView at island size): the same tiles and model as the floating
// quick launcher, laid out as a rail of small tiles that fills as many rows as the page is tall and scrolls sideways; edit mode
// becomes a five-column grid, and a hosted panel replaces the rail with a back button. Hover selects; the keys are the launcher's.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchly.App.Launcher;
using Notchly.Core.Features;
using Notchly.Core.Launcher;

namespace Notchly.App.Island.Tools;

internal sealed class ToolsPage : IDisposable
{
    private readonly NotchWindow _window;
    private readonly Func<QuickLauncherService?> _service;
    private readonly Grid _root;
    private QuickLauncherView? _view;
    private QuickLauncherService? _bound;
    private Border? _customize;
    private bool _visible;
    private double _width = 500, _height = 264;

    public ToolsPage(NotchWindow window, Func<QuickLauncherService?> service)
    {
        _window = window;
        _service = service;
        _root = window.ToolsPage;
        window.PreviewKeyDown += OnKey;
    }

    /// <summary>The island is open on this page.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) { Bind(); _bound?.Model.PrepareForPresentation(); Render(); }
        }
    }

    /// <summary>Lays the rail out for the room the page has (the island's content width and height budget).</summary>
    public void Layout(double budgetHeight, double width)
    {
        _width = width;
        _height = budgetHeight;
        Bind();
        Render();
    }

    private void Bind()
    {
        var service = _service();
        if (ReferenceEquals(service, _bound)) return;
        _bound = service;
        _view = service is null ? null : new QuickLauncherView(service, rail: true);
        _root.Children.Clear();
        if (_view is null) return;
        _customize = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = "Customize", Background = Brushes.Transparent,
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _customize.MouseLeftButtonUp += (_, _) => { service!.Model.IsEditing = !service.Model.IsEditing; service.Model.EditingOptionsItem = null; };
        _view.Root.Margin = new Thickness(0, 28, 0, 0);
        _root.Children.Add(_view.Root);
        _root.Children.Add(_customize);
    }

    private void Render()
    {
        if (_view is null || _bound is null) return;
        var count = _bound.Model.VisibleItems.Count;
        _view.RailRows = Math.Max(1, RailLayout.ToolRows(count, _width, Math.Max(40, _height - 30)));
        _view.Rebuild();
        if (_customize is not null) _customize.Background = _bound.Model.IsEditing ? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) : Brushes.Transparent;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_visible || _bound is null || _view is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = QuickLauncherModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= QuickLauncherModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= QuickLauncherModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= QuickLauncherModifiers.Shift;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (QuickLauncherKeys.FromVirtualKey(vk) is var mapped && mapped != QuickLauncherKey.Other
            && _bound.Model.HandlePanelKey(new QuickLauncherKeyEvent(mapped, modifiers, e.Key == Key.ImeProcessed), _view.Flow)) e.Handled = true;
    }

    public void Dispose() => _window.PreviewKeyDown -= OnKey;
}
